#if BASIS_MEDIAPIPE
using System;
using System.Threading;
using Mediapipe;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;

namespace Basis.MediaPipe.Homuler
{
    /// <summary>
    /// MediaPipe Unity Plugin (homuler) backend. Compiled only when the plugin is present
    /// (BASIS_MEDIAPIPE). Runs FaceLandmarker + HandLandmarker + PoseLandmarker in VIDEO mode
    /// on a background worker thread; the main thread only does GetPixels32 (Unity API) and
    /// signals the worker. A busy flag drops frames instead of racing the shared buffer.
    /// </summary>
    public sealed class HomulerMediaPipeBackend : IBasisMediaPipeBackend
    {
        private const string FaceModelAddress = "Packages/com.basis.mediapipe/Models/face_landmarker.task.bytes";
        private const string HandModelAddress = "Packages/com.basis.mediapipe/Models/hand_landmarker.task.bytes";
        private const string PoseModelAddress = "Packages/com.basis.mediapipe/Models/pose_landmarker_lite.task.bytes";

        public bool IsAvailable { get; private set; }
        public string BackendName => "homuler MediaPipe Unity Plugin";
        private bool _swapHands;
        private bool _poseSidesSwapped;

        private FaceLandmarker _face;
        private HandLandmarker _hand;
        private PoseLandmarker _pose;

        private readonly object _resultLock = new object();
        private BasisMediaPipeResult _latest;
        private bool _hasLatest;
        private long _lastTimestamp;

        private Thread _worker;
        private AutoResetEvent _signal;
        private volatile bool _running;
        private volatile bool _busy;

        private Color32[] _pixels;
        private byte[] _srcRgba;
        private byte[] _rgba;
        private NativeArray<byte> _native;
        private int _w;
        private int _h;
        private long _ts;
        private bool _mirror;
        private bool _useAsyncReadback;
        private volatile bool _readbackPending;
        private int _pendingW;
        private int _pendingH;
        private long _pendingTs;
        private RenderTexture _readbackRT;

        // Stage timings, in ms, for the diagnostics readout. Written by the worker, read by the main thread --
        // deliberately unsynchronised, because they steer a human reading a menu, never any control flow.
        //
        // The stages are strictly SERIAL, which is the point of measuring them apart. A submit blocks on
        // _readbackPending until the GPU hands the frame back (1-3 render frames), and then on _busy until all
        // three models have run on the single worker thread. Nothing overlaps, so the tracking period really is
        // the sum of these, and whichever one is biggest is the one worth attacking.
        private long _submitTicks;
        private volatile float _readbackMs;
        private volatile float _flipMs;
        private volatile float _faceMs;
        private volatile float _poseMs;
        private volatile float _handMs;
        private volatile float _worstPeriodMs;

        private static float MsSince(long startTicks) =>
            (float)((System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        public string TimingBreakdown()
        {
            float infer = _faceMs + _poseMs + _handMs;
            float period = _readbackMs + _flipMs + infer;
            if (!(period > 0f)) return string.Empty;

            return $"readback {_readbackMs:F0} + flip {_flipMs:F0} + face {_faceMs:F0} + pose {_poseMs:F0} + hands {_handMs:F0} = {period:F0}ms (worst {_worstPeriodMs:F0}ms)";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() =>
            BasisMediaPipeBackendRegistry.Register(() => new HomulerMediaPipeBackend());

        public void Initialize(BasisMediaPipeConfig config)
        {
            _mirror = config.MirrorHorizontally;
            _swapHands = config.SwapHands;
            _useAsyncReadback = SystemInfo.supportsAsyncGPUReadback;
            TryCreateLandmarkers(config);
            IsAvailable = _face != null || _hand != null || _pose != null;

            if (IsAvailable)
            {
                _signal = new AutoResetEvent(false);
                _running = true;
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BasisMediaPipe" };
                _worker.Start();
            }
        }

        private bool TryCreateLandmarkers(BasisMediaPipeConfig config)
        {
            try
            {
                if (config.EnableFace) _face = CreateFaceLandmarker(LoadModelBuffer(FaceModelAddress));
                if (config.EnableHands) _hand = CreateHandLandmarker(LoadModelBuffer(HandModelAddress));
                if (config.EnablePose) _pose = CreatePoseLandmarker(LoadModelBuffer(PoseModelAddress));
                return _face != null || _hand != null || _pose != null;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"BasisMediaPipe(homuler): landmarker init failed: {e.Message}");
                _face?.Close();
                _hand?.Close();
                _pose?.Close();
                _face = null;
                _hand = null;
                _pose = null;
                return false;
            }
        }

        private static FaceLandmarker CreateFaceLandmarker(byte[] modelBuffer)
        {
            BaseOptions baseOptions = new BaseOptions(modelAssetBuffer: modelBuffer);
            FaceLandmarkerOptions options = new FaceLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.VIDEO,
                numFaces: 1,
                minFaceDetectionConfidence: 0.5f,
                minFacePresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f,
                outputFaceBlendshapes: true,
                outputFaceTransformationMatrixes: true);
            return FaceLandmarker.CreateFromOptions(options);
        }

        private static HandLandmarker CreateHandLandmarker(byte[] modelBuffer)
        {
            BaseOptions baseOptions = new BaseOptions(modelAssetBuffer: modelBuffer);
            HandLandmarkerOptions options = new HandLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.VIDEO,
                numHands: 2,
                minHandDetectionConfidence: 0.5f,
                minHandPresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f);
            return HandLandmarker.CreateFromOptions(options);
        }

        private static PoseLandmarker CreatePoseLandmarker(byte[] modelBuffer)
        {
            BaseOptions baseOptions = new BaseOptions(modelAssetBuffer: modelBuffer);
            PoseLandmarkerOptions options = new PoseLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.VIDEO,
                numPoses: 1,
                minPoseDetectionConfidence: 0.5f,
                minPosePresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f,
                outputSegmentationMasks: false);
            return PoseLandmarker.CreateFromOptions(options);
        }

        // Main thread: copy pixels (Unity API) and hand the frame to the worker.
        public void SubmitFrame(WebCamTexture frame, double timestampMs)
        {
            if (!IsAvailable || _busy || _readbackPending || frame == null || frame.width <= 16) return;

            int w = frame.width;
            int h = frame.height;
            EnsureBuffers(w, h);

            long ts = (long)timestampMs;
            if (ts <= _lastTimestamp) ts = _lastTimestamp + 1;
            _lastTimestamp = ts;

            _submitTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_useAsyncReadback)
            {
                // WebCamTexture's GPU format usually can't be read back directly, so blit it into a
                // plain RGBA RenderTexture and async-read that instead (no main-thread GPU stall).
                Graphics.Blit(frame, _readbackRT);
                _pendingW = w;
                _pendingH = h;
                _pendingTs = ts;
                _readbackPending = true;
                AsyncGPUReadback.Request(_readbackRT, 0, TextureFormat.RGBA32, OnReadback);
            }
            else
            {
                frame.GetPixels32(_pixels);
                _w = w;
                _h = h;
                _ts = ts;
                _busy = true;
                _signal.Set();
            }
        }

        private void EnsureBuffers(int w, int h)
        {
            int len = w * h;
            if (_rgba != null && _rgba.Length == len * 4) return;
            _pixels = new Color32[len];
            _srcRgba = new byte[len * 4];
            _rgba = new byte[len * 4];
            if (_native.IsCreated) _native.Dispose();
            _native = new NativeArray<byte>(len * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (_useAsyncReadback)
            {
                if (_readbackRT != null) _readbackRT.Release();
                _readbackRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            }
        }

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            _readbackPending = false;
            _readbackMs = MsSince(_submitTicks);
            if (!_running) return;
            if (req.hasError)
            {
                // GPU readback not supported for this texture/platform; fall back to the CPU path.
                _useAsyncReadback = false;
                return;
            }

            NativeArray<byte> data = req.GetData<byte>();
            if (_srcRgba == null || data.Length != _srcRgba.Length) return;
            data.CopyTo(_srcRgba);

            _w = _pendingW;
            _h = _pendingH;
            _ts = _pendingTs;
            _busy = true;
            _signal.Set();
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                _signal.WaitOne();
                if (!_running) break;

                try
                {
                    ProcessFrame();
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"BasisMediaPipe(homuler): inference failed: {e}");
                }
                finally
                {
                    _busy = false;
                }
            }
        }

        private void ProcessFrame()
        {
            int w = _w;
            int h = _h;
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();

            // WebCamTexture origin is bottom-left; MediaPipe expects top-left. Flip rows,
            // and mirror columns for a selfie-style camera.
            if (_useAsyncReadback)
            {
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (h - 1 - y) * w;
                    int dstRow = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int src = (srcRow + (_mirror ? (w - 1 - x) : x)) * 4;
                        int dst = (dstRow + x) * 4;
                        _rgba[dst + 0] = _srcRgba[src + 0];
                        _rgba[dst + 1] = _srcRgba[src + 1];
                        _rgba[dst + 2] = _srcRgba[src + 2];
                        _rgba[dst + 3] = _srcRgba[src + 3];
                    }
                }
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (h - 1 - y) * w;
                    int dstRow = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int src = srcRow + (_mirror ? (w - 1 - x) : x);
                        int dst = (dstRow + x) * 4;
                        Color32 c = _pixels[src];
                        _rgba[dst + 0] = c.r;
                        _rgba[dst + 1] = c.g;
                        _rgba[dst + 2] = c.b;
                        _rgba[dst + 3] = c.a;
                    }
                }
            }

            _native.CopyFrom(_rgba);
            _flipMs = MsSince(stage);

            BasisMediaPipeResult result = new BasisMediaPipeResult { TimestampMs = _ts };
            // DetectForVideo consumes (disposes) the Image it is given, so build a fresh one per call.
            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_face != null)
            {
                FaceLandmarkerResult faceResult = _face.DetectForVideo(NewImage(w, h), _ts);
                ParseFace(faceResult, ref result);
                if (result.HasFace) result.TongueOut = ComputeTongueOut(faceResult, w, h);
            }
            _faceMs = _face != null ? MsSince(stage) : 0f;

            // Pose before hands: the hand landmarker's left/right label is a guess, and the pose (whose sides
            // are repaired from geometry) is what the hands get matched against to settle it.
            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_pose != null) ParsePose(_pose.DetectForVideo(NewImage(w, h), _ts), ref result);
            _poseMs = _pose != null ? MsSince(stage) : 0f;

            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_hand != null) ParseHands(_hand.DetectForVideo(NewImage(w, h), _ts), ref result);
            _handMs = _hand != null ? MsSince(stage) : 0f;

            float period = _readbackMs + _flipMs + _faceMs + _poseMs + _handMs;
            if (period > _worstPeriodMs) _worstPeriodMs = period;

            lock (_resultLock)
            {
                _latest = result;
                _hasLatest = true;
            }
        }

        private void ParseFace(FaceLandmarkerResult result, ref BasisMediaPipeResult output)
        {
            if (result.faceLandmarks != null && result.faceLandmarks.Count > 0)
            {
                output.HasFace = true;
                var fl = result.faceLandmarks[0].landmarks;
                if (fl != null && fl.Count > 152)
                {
                    Vector3 head = MediaPipeSpace.Image(new Vector3(fl[1].x, fl[1].y, 0f), _mirror);
                    output.HeadImagePosition = new Vector2(head.x, head.y);
                    output.FaceImageSize = Mathf.Abs(fl[152].y - fl[10].y);
                }
            }

            if (result.faceBlendshapes != null && result.faceBlendshapes.Count > 0)
            {
                var categories = result.faceBlendshapes[0].categories;
                if (categories != null)
                {
                    output.FaceBlendshapes = new float[(int)MediaPipeArkitBlendshape.Count];
                    for (int i = 0; i < categories.Count; i++)
                    {
                        int index = categories[i].index;
                        if (index >= 0 && index < output.FaceBlendshapes.Length)
                        {
                            output.FaceBlendshapes[index] = categories[i].score;
                        }
                    }
                }
            }

            if (result.facialTransformationMatrixes != null && result.facialTransformationMatrixes.Count > 0)
            {
                output.FaceTransform = result.facialTransformationMatrixes[0];
            }
        }

        private void ParseHands(HandLandmarkerResult result, ref BasisMediaPipeResult output)
        {
            if (result.handLandmarks == null) return;

            Vector3[] firstImage = null, firstWorld = null, secondImage = null, secondWorld = null;
            bool firstLabelLeft = true;
            int found = 0;

            for (int h = 0; h < result.handLandmarks.Count && found < 2; h++)
            {
                var landmarks = result.handLandmarks[h].landmarks;
                if (landmarks == null) continue;

                Vector3[] image = new Vector3[landmarks.Count];
                for (int i = 0; i < landmarks.Count; i++)
                {
                    image[i] = MediaPipeSpace.Image(new Vector3(landmarks[i].x, landmarks[i].y, landmarks[i].z), _mirror);
                }

                Vector3[] world = null;
                if (result.handWorldLandmarks != null && h < result.handWorldLandmarks.Count)
                {
                    var metric = result.handWorldLandmarks[h].landmarks;
                    if (metric != null)
                    {
                        world = new Vector3[metric.Count];
                        for (int i = 0; i < metric.Count; i++)
                        {
                            world[i] = MediaPipeSpace.World(new Vector3(metric[i].x, metric[i].y, metric[i].z), _mirror);
                        }
                    }
                }

                if (found == 0)
                {
                    firstImage = image;
                    firstWorld = world;
                    firstLabelLeft = LabelSaysLeft(result, h);
                }
                else
                {
                    secondImage = image;
                    secondWorld = world;
                }
                found++;
            }

            if (found == 0) return;

            bool firstIsLeft = ResolveHandSide(output.PoseLandmarks, firstImage, secondImage, firstLabelLeft, found);
            Assign(ref output, firstImage, firstWorld, firstIsLeft);
            if (found == 2)
            {
                Assign(ref output, secondImage, secondWorld, !firstIsLeft);
            }
        }

        private static void Assign(ref BasisMediaPipeResult output, Vector3[] image, Vector3[] world, bool left)
        {
            if (left)
            {
                output.LeftHandLandmarks = image;
                output.LeftHandWorldLandmarks = world;
                output.HasLeftHand = true;
            }
            else
            {
                output.RightHandLandmarks = image;
                output.RightHandWorldLandmarks = world;
                output.HasRightHand = true;
            }
        }

        // Which physical hand this is decides whether TryPalmFrame negates the palm normal, so getting it wrong
        // rolls the avatar's wrist 180 degrees while the position still looks right. The handedness label is a
        // guess that depends on the mirror and on the model, so settle it against the pose wrists instead —
        // those sides are already repaired from geometry. Falls back to the label when there is no pose.
        private bool ResolveHandSide(Vector3[] pose, Vector3[] first, Vector3[] second, bool labelLeft, int found)
        {
            if (pose == null || pose.Length < MediaPipeSpace.PoseCount) return labelLeft;

            float firstToLeft = WristGap(first, pose, true);
            float firstToRight = WristGap(first, pose, false);
            if (firstToLeft < 0f || firstToRight < 0f) return labelLeft;

            if (found == 2)
            {
                float secondToLeft = WristGap(second, pose, true);
                float secondToRight = WristGap(second, pose, false);
                if (secondToLeft >= 0f && secondToRight >= 0f)
                {
                    return firstToLeft + secondToRight <= firstToRight + secondToLeft;
                }
            }

            if (Mathf.Abs(firstToLeft - firstToRight) < 0.02f) return labelLeft;
            return firstToLeft < firstToRight;
        }

        private static float WristGap(Vector3[] hand, Vector3[] pose, bool left)
        {
            if (hand == null || hand.Length <= MediaPipeSpace.HandWrist) return -1f;

            Vector3 wrist = hand[MediaPipeSpace.HandWrist];
            Vector3 poseWrist = pose[left ? MediaPipeSpace.LeftWrist : MediaPipeSpace.RightWrist];
            return new Vector2(wrist.x - poseWrist.x, wrist.y - poseWrist.y).magnitude;
        }

        private bool LabelSaysLeft(HandLandmarkerResult result, int index)
        {
            bool isLeft = index == 0;
            if (result.handedness != null && index < result.handedness.Count)
            {
                var categories = result.handedness[index].categories;
                if (categories != null && categories.Count > 0)
                {
                    isLeft = categories[0].categoryName == "Left";
                }
            }
            return _swapHands ? !isLeft : isLeft;
        }

        private void ParsePose(PoseLandmarkerResult result, ref BasisMediaPipeResult output)
        {
            if (result.poseWorldLandmarks != null && result.poseWorldLandmarks.Count > 0)
            {
                var world = result.poseWorldLandmarks[0].landmarks;
                if (world != null)
                {
                    output.HasPose = true;
                    Vector3[] arr = new Vector3[world.Count];
                    for (int i = 0; i < world.Count; i++)
                    {
                        arr[i] = MediaPipeSpace.World(new Vector3(world[i].x, world[i].y, world[i].z), _mirror);
                    }
                    output.PoseWorldLandmarks = arr;
                }
            }

            if (result.poseLandmarks != null && result.poseLandmarks.Count > 0)
            {
                var image = result.poseLandmarks[0].landmarks;
                if (image != null)
                {
                    output.HasPose = true;
                    Vector3[] arr = new Vector3[image.Count];
                    float[] visibility = new float[image.Count];
                    for (int i = 0; i < image.Count; i++)
                    {
                        arr[i] = MediaPipeSpace.Image(new Vector3(image[i].x, image[i].y, image[i].z), _mirror);
                        visibility[i] = image[i].visibility ?? -1f;
                    }
                    output.PoseLandmarks = arr;
                    output.PoseVisibility = visibility;
                }
            }

            ResolveSides(ref output);
        }

        // The pose model names landmarks from appearance, so whether its left/right come back reversed depends
        // on the mirror AND on the model, and getting it wrong flips the body frame's forward (arms end up
        // behind the back). Decide it from the shoulder geometry instead, and hold the last call while the user
        // is turned too far side-on to tell.
        private void ResolveSides(ref BasisMediaPipeResult output)
        {
            float decision = MediaPipeSpace.SideSwapNeeded(output.PoseWorldLandmarks);
            if (decision == 0f) decision = MediaPipeSpace.SideSwapNeeded(output.PoseLandmarks);
            if (decision != 0f) _poseSidesSwapped = decision > 0f;

            if (_poseSidesSwapped)
            {
                MediaPipeSpace.SwapPoseSidesInPlace(output.PoseWorldLandmarks);
                MediaPipeSpace.SwapPoseSidesInPlace(output.PoseLandmarks);
                MediaPipeSpace.SwapPoseSidesInPlace(output.PoseVisibility);
            }
            output.PoseSidesSwapped = _poseSidesSwapped;
        }

        // Tongue isn't a landmark; estimate it from pink/red pixels filling the lower mouth
        // interior (a tongue protruding past the lower lip), gated on the mouth being open.
        private float ComputeTongueOut(FaceLandmarkerResult faceResult, int w, int h)
        {
            if (_rgba == null || faceResult.faceLandmarks == null || faceResult.faceLandmarks.Count == 0) return 0f;
            var lm = faceResult.faceLandmarks[0].landmarks;
            if (lm == null || lm.Count < 468) return 0f;

            float openAmount = Mathf.Abs(lm[14].y - lm[13].y);
            if (openAmount < 0.02f) return 0f;

            float x0 = Mathf.Min(lm[78].x, lm[308].x);
            float x1 = Mathf.Max(lm[78].x, lm[308].x);
            float y0 = (lm[13].y + lm[14].y) * 0.5f;
            float y1 = lm[14].y + openAmount * 0.5f;

            int px0 = Mathf.Clamp((int)(x0 * w), 0, w - 1);
            int px1 = Mathf.Clamp((int)(x1 * w), 0, w - 1);
            int py0 = Mathf.Clamp((int)(y0 * h), 0, h - 1);
            int py1 = Mathf.Clamp((int)(y1 * h), 0, h - 1);
            if (px1 <= px0 || py1 <= py0) return 0f;

            int total = 0;
            int tongue = 0;
            int stepX = Mathf.Max(1, (px1 - px0) / 16);
            int stepY = Mathf.Max(1, (py1 - py0) / 16);
            for (int y = py0; y <= py1; y += stepY)
            {
                int row = y * w;
                for (int x = px0; x <= px1; x += stepX)
                {
                    int idx = (row + x) * 4;
                    byte r = _rgba[idx];
                    byte g = _rgba[idx + 1];
                    byte b = _rgba[idx + 2];
                    total++;
                    // pink/red, not too dark, not white (teeth)
                    if (r > 60 && r > g + 12 && r > b + 12 && r + g + b < 600)
                    {
                        tongue++;
                    }
                }
            }
            float fraction = total == 0 ? 0f : (float)tongue / total;
            // Subtract a baseline so lip/gum edges inside the ROI don't read as tongue.
            return Mathf.Clamp01((fraction - 0.25f) / 0.75f);
        }

        private Image NewImage(int w, int h) =>
            new Image(ImageFormat.Types.Format.Srgba, w, h, w * 4, _native);

        public bool TryGetLatestResult(out BasisMediaPipeResult result)
        {
            lock (_resultLock)
            {
                if (_hasLatest)
                {
                    result = _latest;
                    _hasLatest = false;
                    return true;
                }
            }
            result = default;
            return false;
        }

        public void Shutdown()
        {
            _running = false;
            _signal?.Set();
            if (_worker != null && _worker.IsAlive)
            {
                _worker.Join(500);
            }
            _worker = null;
            _signal?.Dispose();
            _signal = null;

            _face?.Close();
            _hand?.Close();
            _pose?.Close();
            _face = null;
            _hand = null;
            _pose = null;
            if (_native.IsCreated) _native.Dispose();
            if (_readbackRT != null)
            {
                _readbackRT.Release();
                _readbackRT = null;
            }
            IsAvailable = false;
            _busy = false;
            lock (_resultLock) { _hasLatest = false; }
        }

        private static byte[] LoadModelBuffer(string address)
        {
            var handle = Addressables.LoadAssetAsync<TextAsset>(address);
            TextAsset asset = handle.WaitForCompletion();
            if (asset == null)
            {
                Addressables.Release(handle);
                throw new InvalidOperationException($"BasisMediaPipe(homuler): model addressable '{address}' could not be loaded.");
            }
            byte[] buffer = asset.bytes;
            Addressables.Release(handle);
            return buffer;
        }
    }
}
#endif
