using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Basis;
using Basis.ImagePickup;
using Basis.Scripts.Audio;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Shareable-GIF recording for the handheld camera. Records whatever the preview shows — the
/// same feed the video output streams — at a picked size and frame rate for a fixed duration,
/// and encodes it into an animated GIF beside the photos. The capture side is the shared
/// <see cref="BasisCameraFrameRecorder"/>; the encode side is a worker thread quantising and
/// LZW-encoding incrementally, so the render loop pays for neither and a recording never holds
/// more than a handful of raw frames.
/// </summary>
public partial class BasisHandHeldCamera
{
    public const int MinGifFrameRate = 5;
    public const int MaxGifFrameRate = 30;
    public const float MinGifDurationSeconds = 1f;
    public const float MaxGifDurationSeconds = 15f;
    public const int MinGifWidth = 160;
    public const int MaxGifWidth = 1280;

    /// <summary>Widths the panel offers. The height always follows the photo aspect.</summary>
    public static readonly int[] GifWidthPresets = { 320, 480, 640, 960 };

    /// <summary>Whether the finished GIF repeats forever, or plays once and holds its last frame.</summary>
    [NonSerialized]
    public bool GifLoop = true;

    /// <summary>Whether frames are dithered. Smoother gradients for a little grain and encode cost.</summary>
    [NonSerialized]
    public bool GifDither = true;

    private int gifFrameRate = 15;
    private float gifDurationSeconds = 5f;
    private int gifWidth = 480;

    private readonly BasisCameraFrameRecorder gifRecorder = new BasisCameraFrameRecorder("GIF");

    public int GifFrameRate => gifFrameRate;
    public float GifDurationSeconds => gifDurationSeconds;
    public int GifWidth => gifWidth;

    public void SetGifFrameRate(int framesPerSecond) =>
        gifFrameRate = Mathf.Clamp(framesPerSecond, MinGifFrameRate, MaxGifFrameRate);

    public void SetGifDuration(float seconds) =>
        gifDurationSeconds = Mathf.Clamp(seconds, MinGifDurationSeconds, MaxGifDurationSeconds);

    public void SetGifWidth(int width) =>
        gifWidth = Mathf.Clamp(width, MinGifWidth, MaxGifWidth);

    public BasisCameraRecordingState GifState => gifRecorder.State;

    /// <summary>True while frames are still being captured — the phase that needs the feed live.</summary>
    public bool IsGifRecording => gifRecorder.IsRecording;

    /// <summary>Filename of the last GIF saved by this camera, or null.</summary>
    public string LastGifFileName => gifRecorder.LastFileName;

    /// <summary>Why the last recording failed, or null. Cleared when a new one starts.</summary>
    public string LastGifFailure => gifRecorder.LastFailure;

    /// <summary>Frames handed to the GPU for readback this recording.</summary>
    public int GifFramesCaptured => gifRecorder.FramesCaptured;

    /// <summary>Frames the worker has finished encoding into the file.</summary>
    public int GifFramesEncoded => gifRecorder.FramesEncoded;

    /// <summary>Seconds of recording time left, for the panel's stop-button label.</summary>
    public float GifSecondsRemaining => gifRecorder.SecondsRemaining;

    /// <summary>
    /// Starts a recording with the current GIF settings. Refused while one is already running
    /// or saving, and while an admin has locked capture — the same gate photos go through.
    /// </summary>
    public bool StartGifRecording()
    {
        if (gifRecorder.State != BasisCameraRecordingState.Idle) return false;
        if (!TryBeginClipRecording("GIF", gifWidth, MinGifWidth, MaxGifWidth, out int width, out int height, out string timestamp)) return false;

        string finalPath = GetSavePath($"Gif_{timestamp}_{width}x{height}.gif");
        var session = new BasisGifRecorderSession(width, height, GifLoop, GifDither, gifFrameRate, finalPath);
        if (!session.Start()) return false;

        gifRecorder.Saved = SpawnGifInWorldIfEnabled;

        // Rows top-down: that is what the GIF format wants, unlike the JPEG path.
        gifRecorder.Start(session, width, height, gifFrameRate,
            Mathf.Clamp(gifDurationSeconds, MinGifDurationSeconds, MaxGifDurationSeconds), flip: true);
        UpdateRenderGate();
        AnnounceClipRecording();

        BasisDebug.Log($"GIF recording started: {width}x{height} @ {gifFrameRate}fps for up to {gifDurationSeconds:0.#}s.", BasisDebug.LogTag.Camera);
        return true;
    }

    private void SpawnGifInWorldIfEnabled(string path)
    {
        if (!printPhotoEnabled) return;
        BasisImagePickupManager.SpawnFromFile(path);
    }

    /// <summary>Ends the capture phase and lets the frames already taken drain into the file.</summary>
    public void StopGifRecording()
    {
        gifRecorder.Stop();
        UpdateRenderGate();
    }

    /// <summary>Per-frame recorder upkeep, run from <see cref="SimulateLate"/>.</summary>
    private void TickGifRecorder() =>
        gifRecorder.Tick(renderTexture, BasisNetworkModeration.CameraCaptureBlockedLocally);

    private void ShutdownGifRecorder() => gifRecorder.Shutdown();

    /// <summary>
    /// The checks and geometry every clip recorder starts with: a live feed, the admin capture
    /// lock, a reachable output folder, and a size clamped to the format's range with the height
    /// following the photo aspect.
    /// </summary>
    private bool TryBeginClipRecording(string label, int requestedWidth, int minWidth, int maxWidth, out int width, out int height, out string timestamp)
    {
        width = 0;
        height = 0;
        timestamp = null;

        if (captureCamera == null || renderTexture == null) return false;

        if (BasisNetworkModeration.CameraCaptureBlockedLocally)
        {
            BasisDebug.LogWarning($"{label} recording blocked: camera capture is locked by an admin.", BasisDebug.LogTag.Camera);
            return false;
        }

        width = Mathf.Clamp(requestedWidth, minWidth, maxWidth);
        height = captureWidth > 0 && captureHeight > 0
            ? Mathf.RoundToInt(width * (float)captureHeight / captureWidth)
            : width * 9 / 16;
        height = Mathf.Clamp(height, 16, maxWidth);

        string folder = PhotosDirectory;
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"{label} recording could not reach {folder}: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
            return false;
        }

        timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return true;
    }

    /// <summary>
    /// Announced like a photo: the shutter plays here and for everyone nearby, so being
    /// recorded is as audible as being photographed.
    /// </summary>
    private void AnnounceClipRecording()
    {
        if (BasisDeviceManagement.Instance.CameraShutterSound != null)
        {
            BasisUISounds.PlayAt(BasisUISoundEvent.CameraShutter, BasisDeviceManagement.Instance.CameraShutterSound, captureCamera.transform.position, SMModuleAudio.ActivePropVolume);
        }
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            BasisNetworkPIPCameraDriver.SendShutterSound();
        }
    }
}

namespace Basis
{
    /// <summary>
    /// One GIF recording: a worker thread that quantises and LZW-encodes raw frames into a
    /// temporary file as they arrive, and renames it to its final name when the last frame has
    /// landed. Frame delays come from capture timestamps rather than the nominal rate, with the
    /// rounding remainder carried forward, so dropped or late frames slow the moment they cover
    /// instead of speeding the whole clip up. The main thread only copies bytes and flips
    /// counters; the per-frame quantisation — the expensive half — fans out across a few pool
    /// threads, each with its own scratch, while the write stage keeps the frames in capture
    /// order. The delay carry is sequential by nature and stays on the write stage.
    /// </summary>
    public sealed class BasisGifRecorderSession : IBasisFrameRecorderSession
    {
        /// <summary>
        /// Parallel quantisations in flight. Half the machine, capped: the encode must not
        /// starve the render loop or Unity's own workers of cores mid-session.
        /// </summary>
        private static readonly int EncodeThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

        /// <summary>One in-flight frame: the raw input, its own quantiser scratch, and the result.</summary>
        private sealed class EncodeSlot
        {
            public readonly BasisGifQuantizer Quantizer = new BasisGifQuantizer();
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public byte[] Indices;
            public byte[] Rgba;
            public double Timestamp;
            public volatile string Error;
        }

        private readonly ConcurrentStack<EncodeSlot> slotPool = new ConcurrentStack<EncodeSlot>();
        private readonly int width;
        private readonly int height;
        private readonly bool loop;
        private readonly bool dither;
        private readonly int fallbackDelayCentiseconds;
        private readonly string temporaryPath;

        private readonly ConcurrentQueue<QueuedFrame> pendingFrames = new ConcurrentQueue<QueuedFrame>();
        private readonly ConcurrentStack<byte[]> bufferPool = new ConcurrentStack<byte[]>();
        private readonly AutoResetEvent frameReady = new AutoResetEvent(false);

        private Thread worker;
        private volatile bool completeAdding;
        private int framesQueued;
        private int framesEncoded;
        private volatile bool finished;
        private volatile string failureMessage;

        private struct QueuedFrame
        {
            public byte[] Rgba;
            public double Timestamp;
        }

        public string FinalPath { get; }
        public int FramesQueued => Volatile.Read(ref framesQueued);
        public int FramesEncoded => Volatile.Read(ref framesEncoded);
        public bool IsFinished => finished;
        public string FailureMessage => failureMessage;

        public BasisGifRecorderSession(int width, int height, bool loop, bool dither, int frameRate, string finalPath)
        {
            this.width = width;
            this.height = height;
            this.loop = loop;
            this.dither = dither;
            fallbackDelayCentiseconds = Math.Max(BasisGifWriter.MinDelayCentiseconds,
                (int)Math.Round(100.0 / Math.Max(1, frameRate)));
            FinalPath = finalPath;
            // Beside the final name rather than in a temp directory, so the rename can never
            // cross volumes; the extension keeps a half-written file from ever looking like a GIF.
            temporaryPath = finalPath + ".tmp";
        }

        public bool Start()
        {
            try
            {
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BasisGifEncoder" };
                worker.Start();
                return true;
            }
            catch (Exception e)
            {
                failureMessage = $"Could not start the encode worker ({e.GetType().Name}: {e.Message})";
                finished = true;
                return false;
            }
        }

        /// <summary>Main thread. Copies one readback into a pooled buffer and queues it for encoding.</summary>
        public bool TryAddFrame(NativeArray<byte> rgba, double timestamp)
        {
            if (completeAdding || failureMessage != null) return false;
            if (rgba.Length != width * height * 4) return false;

            if (!bufferPool.TryPop(out byte[] buffer) || buffer.Length != rgba.Length)
            {
                buffer = new byte[rgba.Length];
            }
            rgba.CopyTo(buffer);

            pendingFrames.Enqueue(new QueuedFrame { Rgba = buffer, Timestamp = timestamp });
            Interlocked.Increment(ref framesQueued);
            frameReady.Set();
            return true;
        }

        /// <summary>Main thread. No more frames are coming; the worker finalises once the queue drains.</summary>
        public void CompleteAdding()
        {
            completeAdding = true;
            frameReady.Set();
        }

        private void WorkerLoop()
        {
            FileStream file = null;
            bool success = false;
            var inFlight = new Queue<EncodeSlot>();
            try
            {
                file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var writer = new BasisGifWriter(file, width, height, loop);

                EncodeSlot held = null;
                double delayCarry = 0;

                while (failureMessage == null)
                {
                    bool dispatched = false;
                    while (inFlight.Count < EncodeThreads && pendingFrames.TryDequeue(out QueuedFrame frame))
                    {
                        Interlocked.Decrement(ref framesQueued);
                        inFlight.Enqueue(DispatchEncode(frame));
                        dispatched = true;
                    }

                    if (inFlight.Count == 0)
                    {
                        if (completeAdding && pendingFrames.IsEmpty) break;
                        if (!dispatched) frameReady.WaitOne(100);
                        continue;
                    }

                    // Results are taken oldest-first, so however the pool schedules the
                    // quantisations, the file gets its frames in capture order.
                    EncodeSlot ready = inFlight.Dequeue();
                    ready.Done.Wait();
                    if (ready.Error != null)
                    {
                        failureMessage = ready.Error;
                        break;
                    }

                    // One frame is always held back: a frame's delay is the gap to the frame
                    // after it, which is only known once that frame arrives.
                    if (held != null)
                    {
                        WriteSlot(writer, held, NextDelay(ready.Timestamp - held.Timestamp, ref delayCarry));
                    }
                    held = ready;
                }

                if (failureMessage == null)
                {
                    if (held != null)
                    {
                        WriteSlot(writer, held, fallbackDelayCentiseconds);
                    }

                    writer.Finish();
                    file.Flush();
                    file.Dispose();
                    file = null;

                    if (writer.FrameCount == 0)
                    {
                        failureMessage = "No frames were captured.";
                    }
                    else
                    {
                        File.Move(temporaryPath, FinalPath);
                        success = true;
                    }
                }
            }
            catch (Exception e)
            {
                failureMessage = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                // The pool callbacks write into the slots; every one must land before teardown.
                while (inFlight.Count > 0) inFlight.Dequeue().Done.Wait();
                try { file?.Dispose(); } catch (Exception) { }
                if (!success)
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (Exception) { }
                }
                while (pendingFrames.TryDequeue(out _)) { }
                finished = true;
            }
        }

        private EncodeSlot DispatchEncode(QueuedFrame frame)
        {
            if (!slotPool.TryPop(out EncodeSlot slot)) slot = new EncodeSlot();
            if (slot.Indices == null || slot.Indices.Length != width * height) slot.Indices = new byte[width * height];
            slot.Rgba = frame.Rgba;
            slot.Timestamp = frame.Timestamp;
            slot.Error = null;
            slot.Done.Reset();
            ThreadPool.QueueUserWorkItem(QuantizeSlot, slot);
            return slot;
        }

        private void QuantizeSlot(object state)
        {
            var slot = (EncodeSlot)state;
            try
            {
                slot.Quantizer.Quantize(slot.Rgba, width, height, dither, slot.Indices);
            }
            catch (Exception e)
            {
                slot.Error = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                slot.Done.Set();
            }
        }

        private void WriteSlot(BasisGifWriter writer, EncodeSlot slot, int delayCentiseconds)
        {
            writer.WriteFrame(slot.Indices, slot.Quantizer.PaletteRgb, slot.Quantizer.PaletteCount, delayCentiseconds);
            Interlocked.Increment(ref framesEncoded);
            bufferPool.Push(slot.Rgba);
            slot.Rgba = null;
            slotPool.Push(slot);
        }

        /// <summary>
        /// A frame gap as GIF centiseconds, with the rounding remainder carried into the next
        /// gap so the clip's total length stays true to the capture.
        /// </summary>
        private static int NextDelay(double gapSeconds, ref double carry)
        {
            double target = Math.Max(0, gapSeconds) + carry;
            int centiseconds = Math.Max(BasisGifWriter.MinDelayCentiseconds, (int)Math.Round(target * 100.0));
            carry = target - centiseconds / 100.0;
            return centiseconds;
        }
    }
}
