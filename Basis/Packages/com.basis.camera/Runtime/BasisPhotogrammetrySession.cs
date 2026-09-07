using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Basis
{
    /// <summary>
    /// One photogrammetry session's capture, encode and manifest write. Unlike
    /// <see cref="BasisCameraFrameRecorder"/> this is event-triggered rather than paced, and every
    /// capture is an independent file rather than a frame in one continuous stream — so frames
    /// carry their own sequence index and are free to land in any order, and there is no segment
    /// rolling to coordinate.
    ///
    /// <para>Capture reuses the same "blit the live feed into our own small target, then read that
    /// back" mechanism <see cref="BasisCameraFrameRecorder"/> uses, so a session never touches the
    /// shared capture resolution or render texture and can run alongside a GIF or video
    /// recording.</para>
    /// </summary>
    public sealed class BasisPhotogrammetrySession
    {
        /// <summary>
        /// Readbacks allowed in flight. Far fewer than a paced recorder needs: capture here is
        /// sparse and event-triggered rather than running at a fixed frame rate.
        /// </summary>
        private const int MaxPendingReadbacks = 4;

        /// <summary>Bound on how long <see cref="Shutdown"/> will wait for a handful of in-flight PNG encodes.</summary>
        private const int ShutdownEncodeWaitMilliseconds = 2000;

        private struct PendingReadback
        {
            public AsyncGPUReadbackRequest Request;
            public int Index;
            public Matrix4x4 TransformMatrix;
            public float VerticalFieldOfViewDegrees;
        }

        private readonly List<PendingReadback> pendingReadbacks = new List<PendingReadback>();
        private readonly List<BasisPhotogrammetryFrame> frames = new List<BasisPhotogrammetryFrame>();
        private readonly object frameLock = new object();

        private RenderTexture target;
        private string imagesFolder;
        private string manifestPath;
        private int width;
        private int height;
        private int nextIndex;
        private int pendingEncodeCount;
        private int framesEncoded;
        private volatile string lastEncodeFailure;

        public BasisCameraRecordingState State { get; private set; } = BasisCameraRecordingState.Idle;

        /// <summary>Frames handed to the GPU for readback this session.</summary>
        public int FramesCaptured { get; private set; }

        /// <summary>Frames that have finished encoding to disk.</summary>
        public int FramesEncoded => Volatile.Read(ref framesEncoded);

        /// <summary>Filename of the last photo this session saved, or null.</summary>
        public string LastFileName { get; private set; }

        /// <summary>Why the session failed, or null. Cleared when a new one starts.</summary>
        public string LastFailure { get; private set; }

        /// <summary>Where <c>transforms.json</c> was (or will be) written, once a session has started.</summary>
        public string ManifestPath => manifestPath;

        /// <summary>Starts a session writing into <paramref name="sessionFolder"/>/images, at the given target size.</summary>
        public bool Start(int width, int height, string sessionFolder)
        {
            if (State != BasisCameraRecordingState.Idle) return false;

            this.width = width;
            this.height = height;
            imagesFolder = Path.Combine(sessionFolder, "images");
            manifestPath = Path.Combine(sessionFolder, "transforms.json");

            try
            {
                Directory.CreateDirectory(imagesFolder);
            }
            catch (Exception e)
            {
                LastFailure = $"{e.GetType().Name}: {e.Message}";
                return false;
            }

            target = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) { sRGB = true })
            {
                name = "BasisPhotogrammetryCapture"
            };
            target.Create();

            pendingReadbacks.Clear();
            lock (frameLock) frames.Clear();
            nextIndex = 0;
            pendingEncodeCount = 0;
            framesEncoded = 0;
            FramesCaptured = 0;
            lastEncodeFailure = null;
            LastFileName = null;
            LastFailure = null;
            State = BasisCameraRecordingState.Recording;
            return true;
        }

        /// <summary>Ends capture and lets the frames already taken drain into their files and the manifest.</summary>
        public void Stop()
        {
            if (State != BasisCameraRecordingState.Recording) return;
            State = BasisCameraRecordingState.Saving;
        }

        /// <summary>
        /// Blits the live feed into this session's target and kicks off a readback, tagged with
        /// the pose it was captured from. False when the in-flight cap is full or the session is
        /// not recording — callers use this to decide whether to advance their own "since last
        /// capture" baseline, so a refused capture is retried rather than silently skipped.
        /// </summary>
        public bool TryCapture(RenderTexture source, Vector3 position, Quaternion rotation, float verticalFieldOfViewDegrees)
        {
            if (State != BasisCameraRecordingState.Recording) return false;
            if (source == null || target == null) return false;
            if (pendingReadbacks.Count >= MaxPendingReadbacks) return false;

            BasisHandHeldCamera.GetStreamBlitCrop(source, target, out Vector2 scale, out Vector2 offset);
            Graphics.Blit(source, target, scale, offset);

            pendingReadbacks.Add(new PendingReadback
            {
                Request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32),
                Index = nextIndex++,
                TransformMatrix = BasisPhotogrammetryPose.BuildNerfTransformMatrix(position, rotation),
                VerticalFieldOfViewDegrees = verticalFieldOfViewDegrees,
            });
            FramesCaptured++;
            return true;
        }

        /// <summary>Per-frame upkeep, run from the camera's render-phase tick regardless of state — a stopped session still has frames draining.</summary>
        public void Tick()
        {
            DrainReadbacks(blocking: false);

            if (State == BasisCameraRecordingState.Saving && pendingReadbacks.Count == 0 && Volatile.Read(ref pendingEncodeCount) == 0)
            {
                Finish();
            }
        }

        /// <summary>
        /// Teardown for a camera that closes mid-session. The readbacks already in flight are
        /// waited for synchronously — they need the render thread, which will not outlive the
        /// camera — but the encode tasks that follow hold no Unity objects, so they are only
        /// waited for up to a short bound rather than trusted to have finished instantly.
        /// </summary>
        public void Shutdown()
        {
            if (State == BasisCameraRecordingState.Idle) return;

            DrainReadbacks(blocking: true);

            int waited = 0;
            while (Volatile.Read(ref pendingEncodeCount) > 0 && waited < ShutdownEncodeWaitMilliseconds)
            {
                Thread.Sleep(5);
                waited += 5;
            }

            Finish();
        }

        private void DrainReadbacks(bool blocking)
        {
            for (int index = pendingReadbacks.Count - 1; index >= 0; index--)
            {
                PendingReadback pending = pendingReadbacks[index];
                if (blocking) pending.Request.WaitForCompletion();
                if (!pending.Request.done) continue;

                pendingReadbacks.RemoveAt(index);
                if (!pending.Request.hasError) DispatchEncode(pending);
            }
        }

        private void DispatchEncode(PendingReadback pending)
        {
            NativeArray<byte> rgba = pending.Request.GetData<byte>();
            byte[] copy = new byte[rgba.Length];
            rgba.CopyTo(copy);

            int frameWidth = width;
            int frameHeight = height;
            string folder = imagesFolder;
            int index = pending.Index;
            Matrix4x4 transformMatrix = pending.TransformMatrix;
            float verticalFieldOfViewDegrees = pending.VerticalFieldOfViewDegrees;

            Interlocked.Increment(ref pendingEncodeCount);
            Task.Run(() => EncodeAndAppend(copy, frameWidth, frameHeight, folder, index, transformMatrix, verticalFieldOfViewDegrees));
        }

        private void EncodeAndAppend(byte[] rgba, int frameWidth, int frameHeight, string folder, int index, Matrix4x4 transformMatrix, float verticalFieldOfViewDegrees)
        {
            try
            {
                byte[] png = ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_SRGB, (uint)frameWidth, (uint)frameHeight, 0);
                string fileName = $"{index + 1:00000}.png";
                File.WriteAllBytes(Path.Combine(folder, fileName), png);

                BasisPhotogrammetryPose.ComputeIntrinsics(verticalFieldOfViewDegrees, frameWidth, frameHeight,
                    out float focalLengthX, out float focalLengthY, out float principalPointX, out float principalPointY, out _);

                BasisPhotogrammetryFrame frame = new BasisPhotogrammetryFrame(index, "images/" + fileName, transformMatrix,
                    focalLengthX, focalLengthY, principalPointX, principalPointY, frameWidth, frameHeight);
                lock (frameLock) frames.Add(frame);

                Interlocked.Increment(ref framesEncoded);
            }
            catch (Exception e)
            {
                lastEncodeFailure = $"{e.GetType().Name}: {e.Message}";
                BasisDebug.LogError($"Photogrammetry frame {index} failed to save: {lastEncodeFailure}", BasisDebug.LogTag.Camera);
            }
            finally
            {
                Interlocked.Decrement(ref pendingEncodeCount);
            }
        }

        private void Finish()
        {
            List<BasisPhotogrammetryFrame> snapshot;
            lock (frameLock) snapshot = new List<BasisPhotogrammetryFrame>(frames);
            snapshot.Sort((a, b) => a.Index.CompareTo(b.Index));

            LastFailure = lastEncodeFailure;
            if (snapshot.Count > 0) LastFileName = Path.GetFileName(snapshot[snapshot.Count - 1].RelativeFilePath);

            try
            {
                File.WriteAllText(manifestPath, BasisPhotogrammetryManifest.BuildJson(snapshot));
                BasisDebug.Log($"Photogrammetry session saved: {snapshot.Count} photos to {manifestPath}.", BasisDebug.LogTag.Camera);
            }
            catch (Exception e)
            {
                LastFailure = $"{e.GetType().Name}: {e.Message}";
                BasisDebug.LogError($"Photogrammetry manifest failed to save: {LastFailure}", BasisDebug.LogTag.Camera);
            }

            ReleaseTarget();
            State = BasisCameraRecordingState.Idle;
        }

        private void ReleaseTarget()
        {
            if (target == null) return;
            target.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
            target = null;
        }
    }
}
