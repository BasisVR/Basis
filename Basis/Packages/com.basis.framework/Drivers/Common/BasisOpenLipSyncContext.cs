using System;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using OpenLipSync.Inference.OVRCompat;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class BasisOpenLipSyncContext : IDisposable
    {
        public const int VisemeCount = Frame.VisemeCount; // 15

        private uint _contextHandle;

        // Double-buffered frames: _backFrame written by background task, _readyFrame consumed by Apply()
        private Frame _backFrame = new Frame();
        private volatile bool _hasNewResults;

        // Viseme-to-blendshape mapping
        private int[] _visemeToBlendShape;
        private bool[] _hasViseme;
        private SkinnedMeshRenderer _meshRenderer;

        // Audio buffering for thread-safe audio thread -> main thread transfer
        private float[] _audioBufferA;
        private float[] _audioBufferB;
        private volatile int _activeBuffer;
        private volatile int _writeIndexA;
        private volatile int _writeIndexB;
        private volatile int _hasNewAudio;

        // Cached viseme weights for Apply()
        private float[] _cachedVisemeWeights = new float[VisemeCount];
        private float[] _lastApplied = new float[VisemeCount];

        // Background processing
        private Task _processingTask;

        private bool _initialized;
        private bool _disposed;
        private bool _faceVisible = true;

        private const int AudioBufferSize = 48000; // 1 second at 48kHz
        private const float BlendShapeWriteEps = 0.25f;

        public bool IsInitialized => _initialized;

        // Debug accessors for editor window (read-only, no hot-path cost)
        public uint DebugContextHandle => _contextHandle;
        public bool DebugFaceVisible => _faceVisible;
        public int DebugActiveBuffer => _activeBuffer;
        public int DebugWriteIndexA => _writeIndexA;
        public int DebugWriteIndexB => _writeIndexB;
        public bool[] DebugHasViseme => _hasViseme;
        public int[] DebugVisemeToBlendShape => _visemeToBlendShape;
        public float[] DebugVisemeWeights => _cachedVisemeWeights;
        public float[] DebugLastApplied => _lastApplied;
        public bool DebugTaskRunning => _processingTask != null && !_processingTask.IsCompleted;

        public void Initialize(BasisAvatar avatar, uint contextHandle)
        {
            _contextHandle = contextHandle;
            _meshRenderer = avatar.FaceVisemeMesh;

            int count = Math.Min(avatar.FaceVisemeMovement.Length, VisemeCount);
            _visemeToBlendShape = new int[VisemeCount];
            _hasViseme = new bool[VisemeCount];

            for (int i = 0; i < VisemeCount; i++)
            {
                if (i < count && avatar.FaceVisemeMovement[i] != -1)
                {
                    _visemeToBlendShape[i] = avatar.FaceVisemeMovement[i];
                    _hasViseme[i] = true;
                }
                else
                {
                    _visemeToBlendShape[i] = -1;
                    _hasViseme[i] = false;
                }
            }

            _audioBufferA = new float[AudioBufferSize];
            _audioBufferB = new float[AudioBufferSize];
            _activeBuffer = 0;
            _writeIndexA = 0;
            _writeIndexB = 0;
            _hasNewAudio = 0;

            Array.Clear(_cachedVisemeWeights, 0, _cachedVisemeWeights.Length);
            Array.Clear(_lastApplied, 0, _lastApplied.Length);

            BasisOpenLipSyncDriver.SendSignal(_contextHandle, Signals.VisemeSmoothing, 70);

            _initialized = true;
        }

        /// <summary>
        /// Called from the audio thread. Buffers raw PCM samples for later processing.
        /// </summary>
        public void ProcessAudioSamples(float[] data, int channels, int length)
        {
            if (!_initialized || _disposed || !_faceVisible) return;
            if (data == null || length <= 0) return;

#pragma warning disable CS0420 // Volatile.Read/Write provide correct semantics for volatile fields
            int ch = Math.Max(channels, 1);
            int buf = Volatile.Read(ref _activeBuffer);
            float[] dstArr = (buf == 0) ? _audioBufferA : _audioBufferB;
            int w = (buf == 0) ? Volatile.Read(ref _writeIndexA) : Volatile.Read(ref _writeIndexB);
            int cap = AudioBufferSize;

            for (int s = 0; s < length; s += ch)
            {
                if (s < data.Length)
                {
                    dstArr[w] = data[s];
                    w++;
                    if (w >= cap) w = 0;
                }
            }

            if (buf == 0) Volatile.Write(ref _writeIndexA, w);
            else Volatile.Write(ref _writeIndexB, w);
#pragma warning restore CS0420

            Interlocked.Exchange(ref _hasNewAudio, 1);
        }

        /// <summary>
        /// Called on the main thread. Swaps audio buffers and kicks off background
        /// ONNX inference via Task.Run(). Does not block.
        /// </summary>
        public void Simulate(float deltaTime)
        {
            if (!_initialized || _disposed || !_faceVisible) return;

            // Don't start new work if previous task still running
            if (_processingTask != null && !_processingTask.IsCompleted) return;

            // Don't start new work until Apply() has consumed previous results
            if (_hasNewResults) return;

            // Check for faulted task from previous frame
            if (_processingTask?.IsFaulted == true)
            {
                Debug.LogWarning($"[OpenLipSync] Background task faulted: {_processingTask.Exception?.InnerException?.Message}");
                _processingTask = null;
            }

            if (Interlocked.Exchange(ref _hasNewAudio, 0) != 1) return;

            // Swap buffers
            int oldActive = _activeBuffer;
            int newActive = oldActive ^ 1;
            Volatile.Write(ref _activeBuffer, newActive);

            int frozenCount = oldActive == 0
                ? Volatile.Read(ref _writeIndexA)
                : Volatile.Read(ref _writeIndexB);

            float[] frozenBuffer = oldActive == 0 ? _audioBufferA : _audioBufferB;

            if (frozenCount <= 0) return;

            // Copy frozen audio for the background task
            float[] audioChunk = new float[frozenCount];
            Array.Copy(frozenBuffer, 0, audioChunk, 0, frozenCount);

            // Reset write index for the now-frozen buffer
#pragma warning disable CS0420 // Volatile.Write provides correct semantics for volatile fields
            if (oldActive == 0) Volatile.Write(ref _writeIndexA, 0);
            else Volatile.Write(ref _writeIndexB, 0);
#pragma warning restore CS0420

            // Schedule background processing
            uint handle = _contextHandle;
            Frame targetFrame = _backFrame;
            _processingTask = Task.Run(() =>
            {
                var result = BasisOpenLipSyncDriver.ProcessFrame(handle, audioChunk, targetFrame);
                if (result == Result.Success)
                {
                    _hasNewResults = true;
                }
            });
        }

        /// <summary>
        /// Called on the main thread. If background inference completed, picks up new
        /// viseme weights. Applies cached weights to blendshapes. Never stalls.
        /// </summary>
        public void Apply()
        {
            if (!_initialized || _disposed || _meshRenderer == null || !_faceVisible) return;

            // Pick up completed results from background task
            if (_hasNewResults)
            {
                _hasNewResults = false;
                int visemeCount = Math.Min(_backFrame.Visemes.Length, _cachedVisemeWeights.Length);
                Array.Copy(_backFrame.Visemes, _cachedVisemeWeights, visemeCount);
            }

            // Apply cached weights (new or stale from last frame - no stall)
            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;

                int bsIndex = _visemeToBlendShape[i];
                float weight = _cachedVisemeWeights[i] * 100f;
                weight = Math.Clamp(weight, 0f, 100f);

                float prev = _lastApplied[i];
                float diff = weight - prev;

                if (diff * diff > BlendShapeWriteEps * BlendShapeWriteEps)
                {
                    _meshRenderer.SetBlendShapeWeight(bsIndex, weight);
                    _lastApplied[i] = weight;
                }
            }
        }

        public void SetFaceVisible(bool visible)
        {
            _faceVisible = visible;
        }

        public void ZeroVisemes()
        {
            if (_meshRenderer == null) return;

            for (int i = 0; i < VisemeCount; i++)
            {
                if (_hasViseme[i])
                {
                    _meshRenderer.SetBlendShapeWeight(_visemeToBlendShape[i], 0f);
                    _lastApplied[i] = 0f;
                }
            }
            Array.Clear(_cachedVisemeWeights, 0, _cachedVisemeWeights.Length);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _initialized = false;
                _disposed = true;

                // Wait for any in-flight task to finish before releasing buffers
                if (_processingTask != null && !_processingTask.IsCompleted)
                {
                    try { _processingTask.Wait(500); } catch { }
                }

                _audioBufferA = null;
                _audioBufferB = null;
                _backFrame = null;
            }
        }
    }
}
