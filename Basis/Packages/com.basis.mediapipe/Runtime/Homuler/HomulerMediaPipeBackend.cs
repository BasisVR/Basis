#if BASIS_MEDIAPIPE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Mediapipe;
using NormalizedLandmark = Mediapipe.Tasks.Components.Containers.NormalizedLandmark;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.MediaPipe.Homuler
{
    public sealed class HomulerMediaPipeBackend : IBasisMediaPipeBackend
    {
        private const string FaceModelAddress = "Packages/com.basis.mediapipe/Models/face_landmarker.task.bytes";
        private const string HandModelAddress = "Packages/com.basis.mediapipe/Models/hand_landmarker.task.bytes";
        private const string PoseLiteModelAddress = "Packages/com.basis.mediapipe/Models/pose_landmarker_lite.task.bytes";
        private const string PoseFullModelAddress = "Packages/com.basis.mediapipe/Models/pose_landmarker_full.task.bytes";
        private const string PoseHeavyModelAddress = "Packages/com.basis.mediapipe/Models/pose_landmarker_heavy.task.bytes";
        private const int ModelCount = 3, JoinMilliseconds = 3000;
        private enum Model { Face = 0, Hand = 1, Pose = 2 }
        private enum LoadState { None, Loading, Ready }

        public bool IsAvailable { get; private set; }
        public bool IsReady => _activeModels > 0;
        public string BackendName => "homuler MediaPipe Unity Plugin";

        private volatile bool _mirror, _lowLight, _swapHands;
        private readonly LoadState[] _loadState = new LoadState[ModelCount];
        private readonly int[] _loadToken = new int[ModelCount];
        private string _requestedPoseModel = string.Empty;

        private FaceLandmarker _face;
        private HandLandmarker _hand;
        private PoseLandmarker _pose;
        private volatile int _activeModels;
        private readonly ConcurrentQueue<Action> _coordinatorActions = new ConcurrentQueue<Action>();

        private readonly MediaPipeResultSlots _slots = new MediaPipeResultSlots();
        private long _lastTimestamp;

        private Thread _coordinator, _poseThread, _handThread;
        private AutoResetEvent _signal, _poseSignal, _handSignal;
        private ManualResetEventSlim _poseDone, _handDone;
        private volatile bool _running, _busy;
        private FaceLandmarkerResult _faceRaw;
        private PoseLandmarkerResult _poseRaw;
        private HandLandmarkerResult _handRaw;
        private bool _faceOk, _poseOk, _handOk;

        private Color32[] _pixels;
        private byte[] _srcRgba, _rgba;
        private NativeArray<byte> _native;
        private int _w, _h;
        private long _ts;
        private bool _useAsyncReadback;
        private volatile bool _readbackPending;
        private int _pendingW, _pendingH;
        private long _pendingTs;
        private RenderTexture _readbackRT;
        private NativeArray<byte> _readbackNative;

        private MediaPipeSideLatch _sides;
        private readonly MediaPipeHandSideResolver _handSides = new MediaPipeHandSideResolver();
        private readonly MediaPipeExposure _exposure = new MediaPipeExposure();
        private bool _hadFace;
        private Vector2 _faceCenter;
        private float _faceSize;

        // Stage timings, in ms, for the diagnostics readout. Written by the workers, read by the main thread --
        // deliberately unsynchronised, because they steer a human reading a menu, never any control flow.
        private long _submitTicks;
        private volatile float _readbackMs, _flipMs, _faceMs, _poseMs, _handMs, _inferMs, _worstPeriodMs;

        private static float MsSince(long startTicks) =>
            (float)((System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        public string TimingBreakdown()
        {
            float period = _readbackMs + _flipMs + _inferMs;
            if (!(period > 0f)) return string.Empty;
            return $"readback {_readbackMs:F0} + flip {_flipMs:F0} + inference {_inferMs:F0} (face {_faceMs:F0} | pose {_poseMs:F0} | hands {_handMs:F0}, in parallel) = {period:F0}ms (worst {_worstPeriodMs:F0}ms)";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() =>
            BasisMediaPipeBackendRegistry.Register(() => new HomulerMediaPipeBackend());

        public void Initialize(BasisMediaPipeConfig config)
        {
            _useAsyncReadback = SystemInfo.supportsAsyncGPUReadback;
            IsAvailable = true;
            _signal = new AutoResetEvent(false);
            _poseSignal = new AutoResetEvent(false);
            _handSignal = new AutoResetEvent(false);
            _poseDone = new ManualResetEventSlim(true);
            _handDone = new ManualResetEventSlim(true);
            _running = true;
            _coordinator = new Thread(CoordinatorLoop) { IsBackground = true, Name = "BasisMediaPipe" };
            _poseThread = new Thread(() => HelperLoop(_poseSignal, _poseDone, RunPose)) { IsBackground = true, Name = "BasisMediaPipe.Pose" };
            _handThread = new Thread(() => HelperLoop(_handSignal, _handDone, RunHands)) { IsBackground = true, Name = "BasisMediaPipe.Hands" };
            _coordinator.Start();
            _poseThread.Start();
            _handThread.Start();
            Reconfigure(config);
        }

        // Main thread. Landmarkers are created and closed on the coordinator between frames, from model bytes the
        // main thread fetches asynchronously, so neither the camera nor the worker threads restart and nothing
        // blocks the frame.
        public void Reconfigure(BasisMediaPipeConfig config)
        {
            if (!_running) return;
            _mirror = config.MirrorHorizontally;
            _lowLight = config.LowLightBoost;
            _swapHands = config.SwapHands;

            Sync(Model.Face, config.EnableFace, FaceModelAddress);
            Sync(Model.Hand, config.EnableHands, HandModelAddress);

            string poseModel = BasisMediaPipeConfig.NormalizePoseModel(config.PoseModel);
            if (config.EnablePose && _loadState[(int)Model.Pose] != LoadState.None && !string.Equals(_requestedPoseModel, poseModel, StringComparison.Ordinal))
            {
                Sync(Model.Pose, false, null);
            }
            _requestedPoseModel = poseModel;
            Sync(Model.Pose, config.EnablePose, PoseModelAddress(poseModel));
        }

        private static string PoseModelAddress(string model)
        {
            switch (model)
            {
                case BasisMediaPipeConfig.PoseModelFull: return PoseFullModelAddress;
                case BasisMediaPipeConfig.PoseModelHeavy: return PoseHeavyModelAddress;
                default: return PoseLiteModelAddress;
            }
        }

        private void Sync(Model model, bool want, string address)
        {
            int index = (int)model;
            if (want)
            {
                if (_loadState[index] != LoadState.None) return;
                _loadState[index] = LoadState.Loading;
                int token = ++_loadToken[index];
                LoadModelAsync(address, bytes => OnModelLoaded(model, token, address, bytes));
            }
            else
            {
                if (_loadState[index] == LoadState.None) return;
                _loadState[index] = LoadState.None;
                _loadToken[index]++;
                _coordinatorActions.Enqueue(() => Close(model));
                _signal.Set();
            }
        }

        private void OnModelLoaded(Model model, int token, string address, byte[] bytes)
        {
            int index = (int)model;
            if (!_running || token != _loadToken[index]) return;
            if (bytes == null || bytes.Length == 0)
            {
                _loadState[index] = LoadState.None;
                BasisDebug.LogError($"BasisMediaPipe(homuler): model '{address}' could not be loaded.");
                if (model == Model.Pose && !string.Equals(_requestedPoseModel, BasisMediaPipeConfig.PoseModelLite, StringComparison.Ordinal))
                {
                    _requestedPoseModel = BasisMediaPipeConfig.PoseModelLite;
                    Sync(Model.Pose, true, PoseLiteModelAddress);
                }
                return;
            }
            _loadState[index] = LoadState.Ready;
            _coordinatorActions.Enqueue(() => Create(model, bytes));
            _signal.Set();
        }

        private static void LoadModelAsync(string address, Action<byte[]> onLoaded)
        {
            AsyncOperationHandle<TextAsset> handle;
            try
            {
                handle = Addressables.LoadAssetAsync<TextAsset>(address);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"BasisMediaPipe(homuler): addressable '{address}' failed: {e.Message}");
                onLoaded(null);
                return;
            }
            handle.Completed += op =>
            {
                byte[] bytes = null;
                try
                {
                    if (op.Status == AsyncOperationStatus.Succeeded && op.Result != null) bytes = op.Result.bytes;
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"BasisMediaPipe(homuler): reading '{address}' failed: {e.Message}");
                }
                finally
                {
                    Addressables.Release(handle);
                }
                onLoaded(bytes);
            };
        }

        // Coordinator thread, between frames: the helper threads are idle, so swapping a landmarker is safe.
        private void Create(Model model, byte[] bytes)
        {
            try
            {
                switch (model)
                {
                    case Model.Face:
                        _face?.Close();
                        _face = CreateFaceLandmarker(bytes);
                        break;
                    case Model.Hand:
                        _hand?.Close();
                        _hand = CreateHandLandmarker(bytes);
                        break;
                    case Model.Pose:
                        _pose?.Close();
                        _pose = CreatePoseLandmarker(bytes);
                        break;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"BasisMediaPipe(homuler): {model} landmarker init failed: {e.Message}");
            }
            CountModels();
        }

        private void Close(Model model)
        {
            try
            {
                switch (model)
                {
                    case Model.Face:
                        _face?.Close();
                        _face = null;
                        break;
                    case Model.Hand:
                        _hand?.Close();
                        _hand = null;
                        break;
                    case Model.Pose:
                        _pose?.Close();
                        _pose = null;
                        break;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"BasisMediaPipe(homuler): closing the {model} landmarker failed: {e.Message}");
            }
            CountModels();
        }

        private void CountModels() => _activeModels = (_face != null ? 1 : 0) + (_hand != null ? 1 : 0) + (_pose != null ? 1 : 0);

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

        // Main thread: copy pixels (Unity API) and hand the frame to the coordinator.
        public void SubmitFrame(WebCamTexture frame, double timestampMs)
        {
            if (!_running || _activeModels == 0 || _busy || _readbackPending || frame == null || frame.width <= 16) return;

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
                AsyncGPUReadback.RequestIntoNativeArray(ref _readbackNative, _readbackRT, 0, TextureFormat.RGBA32, OnReadback);
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
                if (_readbackNative.IsCreated) _readbackNative.Dispose();
                _readbackNative = new NativeArray<byte>(len * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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

            if (_srcRgba == null || !_readbackNative.IsCreated || _readbackNative.Length != _srcRgba.Length) return;

            _w = _pendingW;
            _h = _pendingH;
            _ts = _pendingTs;
            _busy = true;
            _signal.Set();
        }

        private void CoordinatorLoop()
        {
            while (_running)
            {
                _signal.WaitOne();
                if (!_running) break;

                while (_coordinatorActions.TryDequeue(out Action action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogError($"BasisMediaPipe(homuler): reconfigure failed: {e}");
                    }
                }
                if (!_busy) continue;

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

        private void HelperLoop(AutoResetEvent signal, ManualResetEventSlim done, Action run)
        {
            while (_running)
            {
                signal.WaitOne();
                if (!_running) break;
                try
                {
                    run();
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"BasisMediaPipe(homuler): inference failed: {e}");
                }
                finally
                {
                    done.Set();
                }
            }
        }

        private void RunFace()
        {
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();
            _faceRaw = _face.DetectForVideo(NewImage(_w, _h), _ts);
            _faceOk = true;
            _faceMs = MsSince(stage);
        }

        private void RunPose()
        {
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();
            _poseRaw = _pose.DetectForVideo(NewImage(_w, _h), _ts);
            _poseOk = true;
            _poseMs = MsSince(stage);
        }

        private void RunHands()
        {
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();
            _handRaw = _hand.DetectForVideo(NewImage(_w, _h), _ts);
            _handOk = true;
            _handMs = MsSince(stage);
        }

        private void ProcessFrame()
        {
            int w = _w;
            int h = _h;
            bool mirror = _mirror;
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();

            // WebCamTexture origin is bottom-left; MediaPipe expects top-left. Flip rows,
            // and mirror columns for a selfie-style camera.
            if (_useAsyncReadback) _readbackNative.CopyTo(_srcRgba);
            MeterLight(w, h);
            _exposure.Update(_lowLight);
            byte[] lut = _exposure.Lut;
            if (_useAsyncReadback)
            {
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (h - 1 - y) * w;
                    int dstRow = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int src = (srcRow + (mirror ? (w - 1 - x) : x)) * 4;
                        int dst = (dstRow + x) * 4;
                        _rgba[dst + 0] = lut[_srcRgba[src + 0]];
                        _rgba[dst + 1] = lut[_srcRgba[src + 1]];
                        _rgba[dst + 2] = lut[_srcRgba[src + 2]];
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
                        int src = srcRow + (mirror ? (w - 1 - x) : x);
                        int dst = (dstRow + x) * 4;
                        Color32 c = _pixels[src];
                        _rgba[dst + 0] = lut[c.r];
                        _rgba[dst + 1] = lut[c.g];
                        _rgba[dst + 2] = lut[c.b];
                        _rgba[dst + 3] = c.a;
                    }
                }
            }

            _native.CopyFrom(_rgba);
            _flipMs = MsSince(stage);

            MediaPipeResultSlot slot = _slots.BeginWrite();
            slot.Result = new BasisMediaPipeResult
            {
                TimestampMs = _ts,
                ImageAspect = (float)w / h,
                LightLevel = _exposure.Level,
                LightBoost = _exposure.Boost,
            };

            // The three models read the same frame buffer at once, each on its own thread; the buffer is only
            // rewritten after the next submit, which the busy flag holds back until everything here is done.
            bool runFace = _face != null, runPose = _pose != null, runHand = _hand != null;
            _faceOk = false;
            _poseOk = false;
            _handOk = false;
            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            if (runPose)
            {
                _poseDone.Reset();
                _poseSignal.Set();
            }
            if (runHand)
            {
                _handDone.Reset();
                _handSignal.Set();
            }
            if (runFace)
            {
                try
                {
                    RunFace();
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"BasisMediaPipe(homuler): face inference failed: {e}");
                }
            }
            if (runPose) _poseDone.Wait();
            if (runHand) _handDone.Wait();
            _inferMs = MsSince(stage);

            if (_faceOk) ParseFace(_faceRaw, slot, mirror, w, h);
            // Pose before hands: the hand landmarker's left/right label is a guess, and the pose (whose sides
            // are repaired from geometry) is what the hands get matched against to settle it.
            if (_poseOk) ParsePose(_poseRaw, slot, mirror);
            if (_handOk) ParseHands(_handRaw, slot, mirror);
            _faceRaw = default;
            _poseRaw = default;
            _handRaw = default;

            float period = _readbackMs + _flipMs + _inferMs;
            if (period > _worstPeriodMs) _worstPeriodMs = period;

            _slots.Publish();
        }

        private static Vector2 Point(List<NormalizedLandmark> landmarks, int index) => new Vector2(landmarks[index].x, landmarks[index].y);

        private void ParseFace(FaceLandmarkerResult result, MediaPipeResultSlot slot, bool mirror, int w, int h)
        {
            ref BasisMediaPipeResult output = ref slot.Result;
            if (result.faceLandmarks != null && result.faceLandmarks.Count > 0)
            {
                List<NormalizedLandmark> fl = result.faceLandmarks[0].landmarks;
                if (fl != null && fl.Count > 152)
                {
                    output.HasFace = true;
                    Vector3 head = MediaPipeSpace.Image(new Vector3(fl[1].x, fl[1].y, 0f), mirror);
                    output.HeadImagePosition = new Vector2(head.x, head.y);
                    output.FaceImageSize = Mathf.Abs(fl[152].y - fl[10].y);
                    if (fl.Count >= MediaPipeGaze.LandmarkCount)
                    {
                        float aspect = (float)w / h;
                        output.HasRightGaze = MediaPipeGaze.TryMeasure(Point(fl, MediaPipeGaze.RightImageLeftCorner), Point(fl, MediaPipeGaze.RightImageRightCorner), Point(fl, MediaPipeGaze.RightUpperLid), Point(fl, MediaPipeGaze.RightLowerLid), Point(fl, MediaPipeGaze.RightIris), aspect, out output.RightEyeGaze);
                        output.HasLeftGaze = MediaPipeGaze.TryMeasure(Point(fl, MediaPipeGaze.LeftImageLeftCorner), Point(fl, MediaPipeGaze.LeftImageRightCorner), Point(fl, MediaPipeGaze.LeftUpperLid), Point(fl, MediaPipeGaze.LeftLowerLid), Point(fl, MediaPipeGaze.LeftIris), aspect, out output.LeftEyeGaze);
                    }
                }
            }

            if (result.faceBlendshapes != null && result.faceBlendshapes.Count > 0)
            {
                var categories = result.faceBlendshapes[0].categories;
                if (categories != null)
                {
                    float[] shapes = slot.Blendshapes;
                    Array.Clear(shapes, 0, shapes.Length);
                    for (int i = 0; i < categories.Count; i++)
                    {
                        int index = categories[i].index;
                        if (index >= 0 && index < shapes.Length)
                        {
                            shapes[index] = categories[i].score;
                        }
                    }
                    output.FaceBlendshapes = shapes;
                }
            }

            if (result.facialTransformationMatrixes != null && result.facialTransformationMatrixes.Count > 0)
            {
                output.FaceTransform = result.facialTransformationMatrixes[0];
            }

            if (output.HasFace) output.TongueOut = ComputeTongueOut(result, w, h);

            _hadFace = output.HasFace && output.FaceImageSize > 0f;
            if (_hadFace)
            {
                _faceCenter = output.HeadImagePosition;
                _faceSize = output.FaceImageSize;
            }
        }

        private void ParseHands(HandLandmarkerResult result, MediaPipeResultSlot slot, bool mirror)
        {
            if (result.handLandmarks == null) return;
            ref BasisMediaPipeResult output = ref slot.Result;

            bool firstLabelLeft = true;
            int found = 0;
            for (int h = 0; h < result.handLandmarks.Count && found < 2; h++)
            {
                List<NormalizedLandmark> landmarks = result.handLandmarks[h].landmarks;
                if (landmarks == null || landmarks.Count < MediaPipeSpace.HandCount) continue;

                Vector3[] image = slot.HandImage[found], world = slot.HandWorld[found];
                for (int i = 0; i < MediaPipeSpace.HandCount; i++)
                {
                    image[i] = MediaPipeSpace.Image(new Vector3(landmarks[i].x, landmarks[i].y, landmarks[i].z), mirror);
                }

                bool hasWorld = false;
                if (result.handWorldLandmarks != null && h < result.handWorldLandmarks.Count)
                {
                    var metric = result.handWorldLandmarks[h].landmarks;
                    if (metric != null && metric.Count >= MediaPipeSpace.HandCount)
                    {
                        for (int i = 0; i < MediaPipeSpace.HandCount; i++)
                        {
                            world[i] = MediaPipeSpace.World(new Vector3(metric[i].x, metric[i].y, metric[i].z), mirror);
                        }
                        hasWorld = true;
                    }
                }
                slot.HandHasWorld[found] = hasWorld;

                if (found == 0) firstLabelLeft = LabelSaysLeft(result, h);
                found++;
            }

            if (found == 0) return;
            if (found == 2 && MediaPipeHandSideResolver.IsDuplicate(slot.HandImage[0], slot.HandImage[1], output.ImageAspect)) found = 1;

            bool firstIsLeft = _handSides.Resolve(output.PoseLandmarks, slot.HandImage[0], found == 2 ? slot.HandImage[1] : null, firstLabelLeft, found);
            Assign(ref output, slot, 0, firstIsLeft);
            if (found == 2)
            {
                Assign(ref output, slot, 1, !firstIsLeft);
            }
        }

        private static void Assign(ref BasisMediaPipeResult output, MediaPipeResultSlot slot, int index, bool left)
        {
            Vector3[] image = slot.HandImage[index], world = slot.HandHasWorld[index] ? slot.HandWorld[index] : null;
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

        private void ParsePose(PoseLandmarkerResult result, MediaPipeResultSlot slot, bool mirror)
        {
            ref BasisMediaPipeResult output = ref slot.Result;
            if (result.poseWorldLandmarks != null && result.poseWorldLandmarks.Count > 0)
            {
                var world = result.poseWorldLandmarks[0].landmarks;
                if (world != null && world.Count >= MediaPipeSpace.PoseCount)
                {
                    for (int i = 0; i < MediaPipeSpace.PoseCount; i++)
                    {
                        slot.PoseWorld[i] = MediaPipeSpace.World(new Vector3(world[i].x, world[i].y, world[i].z), mirror);
                    }
                    output.PoseWorldLandmarks = slot.PoseWorld;
                    output.HasPose = true;
                }
            }

            if (result.poseLandmarks != null && result.poseLandmarks.Count > 0)
            {
                List<NormalizedLandmark> image = result.poseLandmarks[0].landmarks;
                if (image != null && image.Count >= MediaPipeSpace.PoseCount)
                {
                    for (int i = 0; i < MediaPipeSpace.PoseCount; i++)
                    {
                        slot.Pose[i] = MediaPipeSpace.Image(new Vector3(image[i].x, image[i].y, image[i].z), mirror);
                        slot.PoseVisibility[i] = image[i].visibility ?? -1f;
                    }
                    output.PoseLandmarks = slot.Pose;
                    output.PoseVisibility = slot.PoseVisibility;
                    output.HasPose = true;
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
            bool swapped = _sides.Update(decision);

            if (swapped)
            {
                MediaPipeSpace.SwapPoseSidesInPlace(output.PoseWorldLandmarks);
                MediaPipeSpace.SwapPoseSidesInPlace(output.PoseLandmarks);
                MediaPipeSpace.SwapPoseSidesInPlace(output.PoseVisibility);
            }
            output.PoseSidesSwapped = swapped;
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

        private void MeterLight(int w, int h)
        {
            int x0 = 0, y0 = 0, x1 = w, y1 = h;
            if (_hadFace && _faceSize > 0.02f)
            {
                int half = Mathf.RoundToInt(_faceSize * h * 0.7f), cx = Mathf.RoundToInt(_faceCenter.x * w), cy = Mathf.RoundToInt(_faceCenter.y * h);
                x0 = Mathf.Clamp(cx - half, 0, w - 1);
                x1 = Mathf.Clamp(cx + half, x0 + 1, w);
                y0 = Mathf.Clamp(cy - half, 0, h - 1);
                y1 = Mathf.Clamp(cy + half, y0 + 1, h);
            }
            int step = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt((x1 - x0) * (y1 - y0) / 3000f)));
            _exposure.Begin();
            if (_useAsyncReadback)
            {
                for (int y = y0; y < y1; y += step)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x += step)
                    {
                        int i = (row + x) * 4;
                        _exposure.Add(_srcRgba[i], _srcRgba[i + 1], _srcRgba[i + 2]);
                    }
                }
            }
            else
            {
                for (int y = y0; y < y1; y += step)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x += step)
                    {
                        Color32 c = _pixels[row + x];
                        _exposure.Add(c.r, c.g, c.b);
                    }
                }
            }
            _exposure.End();
        }

        private Image NewImage(int w, int h) =>
            new Image(ImageFormat.Types.Format.Srgba, w, h, w * 4, _native);

        public bool TryGetLatestResult(out BasisMediaPipeResult result) => _slots.TryTake(out result);

        public void Shutdown()
        {
            _running = false;
            for (int i = 0; i < ModelCount; i++)
            {
                _loadState[i] = LoadState.None;
                _loadToken[i]++;
            }
            _signal?.Set();
            _poseSignal?.Set();
            _handSignal?.Set();
            Join(_coordinator);
            Join(_poseThread);
            Join(_handThread);
            _coordinator = null;
            _poseThread = null;
            _handThread = null;

            _face?.Close();
            _hand?.Close();
            _pose?.Close();
            _face = null;
            _hand = null;
            _pose = null;
            _activeModels = 0;
            while (_coordinatorActions.TryDequeue(out _)) { }

            _signal?.Dispose();
            _poseSignal?.Dispose();
            _handSignal?.Dispose();
            _poseDone?.Dispose();
            _handDone?.Dispose();
            _signal = null;
            _poseSignal = null;
            _handSignal = null;
            _poseDone = null;
            _handDone = null;

            if (_native.IsCreated) _native.Dispose();
            if (_readbackNative.IsCreated)
            {
                if (_readbackPending) AsyncGPUReadback.WaitAllRequests();
                _readbackNative.Dispose();
            }
            if (_readbackRT != null)
            {
                _readbackRT.Release();
                _readbackRT = null;
            }
            IsAvailable = false;
            _busy = false;
            _slots.Clear();
        }

        private static void Join(Thread thread)
        {
            if (thread != null && thread.IsAlive && !thread.Join(JoinMilliseconds))
            {
                BasisDebug.LogError($"BasisMediaPipe(homuler): thread '{thread.Name}' did not stop in time.");
            }
        }
    }
}
#endif
