using System;
using System.Collections.Generic;
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

        // Double-buffered frames: _backFrame written by batch task, consumed by Apply()
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

        private bool _initialized;
        private bool _disposed;
        private bool _faceVisible = true;

        private const int AudioBufferSize = 48000; // 1 second at 48kHz
        private const float BlendShapeWriteEps = 0.25f;

        // Reusable buffer for batch task audio — eliminates per-frame float[] allocation
        private float[] _audioChunk = new float[AudioBufferSize];

        // ────────────────────────────────────────────────────────────
        //  Batch inference state (per-instance, set by Simulate, read by batch task)
        // ──────────────────���─────────────────────────────────────────
        private volatile bool _readyForInference;
        private int _frozenSampleCount;

        // ───────��────────────────────────────────────────────────────
        //  Static batch processing — replaces per-context Task.Run()
        //
        //  Instead of each context spawning its own background task
        //  (which causes thread pool saturation when 30+ contexts
        //  all contend on the single-threaded ONNX session), all
        //  ready contexts are collected into a batch and processed
        //  sequentially in one background task.
        //
        //  This scales to any number of active contexts because:
        //  - Only 1 thread pool task runs at a time
        //  - No ONNX session lock contention
        //  - Fair round-robin scheduling for all contexts
        //  - Per-frame budget limits work per batch
        // ──────────────────────────────────��─────────────────────��───
        private static readonly List<BasisOpenLipSyncContext> _pendingInference = new List<BasisOpenLipSyncContext>(64);
        private static Task _batchTask;
        private static BasisOpenLipSyncContext[] _cachedBatch;
        private static int _cachedBatchLen;
        // Cached delegate — avoids per-frame closure/display-class allocation in Task.Run.
        private static readonly Action _runBatchInference = RunBatchInference;

        /// <summary>
        /// Maximum number of contexts to process per batch task.
        /// Contexts beyond this are deferred to the next frame.
        /// With ~1ms per ONNX inference, 32 contexts = ~32ms background work.
        /// </summary>
        public static int MaxContextsPerBatch = 32;

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
        public bool DebugTaskRunning => _batchTask != null && !_batchTask.IsCompleted;
        public static int DebugPendingCount => _pendingInference.Count;
        public static bool DebugBatchRunning => _batchTask != null && !_batchTask.IsCompleted;

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
                    if (w >= cap)
                    {
                        w = 0;
                    }
                }
            }

            if (buf == 0) Volatile.Write(ref _writeIndexA, w);
            else Volatile.Write(ref _writeIndexB, w);
#pragma warning restore CS0420

            Interlocked.Exchange(ref _hasNewAudio, 1);
        }

        /// <summary>
        /// Called on the main thread. Swaps audio buffers and enqueues this context
        /// for batch inference. Does NOT spawn its own background task.
        /// </summary>
        public void Simulate(float deltaTime)
        {
            if (!_initialized || _disposed || !_faceVisible) return;

            // Already queued for batch processing
            if (_readyForInference) return;

            // Don't queue new work until Apply() has consumed previous results
            if (_hasNewResults) return;

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

            // Copy frozen audio into reusable buffer (zero GC allocation)
            Array.Copy(frozenBuffer, 0, _audioChunk, 0, frozenCount);

            // Reset write index for the now-frozen buffer
#pragma warning disable CS0420 // Volatile.Write provides correct semantics for volatile fields
            if (oldActive == 0) Volatile.Write(ref _writeIndexA, 0);
            else Volatile.Write(ref _writeIndexB, 0);
#pragma warning restore CS0420

            _frozenSampleCount = frozenCount;
            _readyForInference = true;

            lock (_pendingInference)
            {
                _pendingInference.Add(this);
            }
        }

        /// <summary>
        /// Call after all individual Simulate() calls complete.
        /// Collects all pending contexts and processes them sequentially in a single
        /// background task. This avoids thread pool saturation and ONNX session contention
        /// that occurs when each context spawns its own Task.Run().
        ///
        /// Scales to 1000+ audio sources because:
        /// - Only in-range contexts with new audio are processed
        /// - Single background task, no thread pool flooding
        /// - Per-batch budget caps work per frame
        /// </summary>
        public static void ProcessAllPending()
        {
            // Check for faulted previous batch
            if (_batchTask?.IsFaulted == true)
            {
                Debug.LogWarning($"[OpenLipSync] Batch inference faulted: {_batchTask.Exception?.InnerException?.Message}");
                _batchTask = null;
            }

            // Don't start new batch while previous is still running
            if (_batchTask != null && !_batchTask.IsCompleted) return;

            int take;
            lock (_pendingInference)
            {
                int batchCount = _pendingInference.Count;
                if (batchCount == 0) return;

                // Cap batch size to spread work across frames
                take = Math.Min(batchCount, MaxContextsPerBatch);
                // Reuse cached batch array to avoid per-frame allocation.
                if (_cachedBatch == null || _cachedBatch.Length < take)
                    _cachedBatch = new BasisOpenLipSyncContext[Math.Max(take, MaxContextsPerBatch)];
                _pendingInference.CopyTo(0, _cachedBatch, 0, take);
                _pendingInference.RemoveRange(0, take);
            }

            _cachedBatchLen = take;
            _batchTask = Task.Run(_runBatchInference);
        }

        private static void RunBatchInference()
        {
            var batch = _cachedBatch;
            int batchLen = _cachedBatchLen;
            for (int i = 0; i < batchLen; i++)
            {
                var ctx = batch[i];
                if (ctx._disposed)
                {
                    ctx._readyForInference = false;
                    continue;
                }

                try
                {
                    var result = BasisOpenLipSyncDriver.ProcessFrame(
                        ctx._contextHandle, ctx._audioChunk, ctx._frozenSampleCount, ctx._backFrame);

                    if (result == Result.Success)
                    {
                        ctx._hasNewResults = true;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Expected during teardown
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OpenLipSync] Batch inference error for context {ctx._contextHandle}: {ex.Message}");
                }
                finally
                {
                    ctx._readyForInference = false;
                }
            }
        }

        /// <summary>
        /// Called on the main thread. If background inference completed, picks up new
        /// viseme weights. Applies cached weights to blendshapes. Never stalls.
        /// </summary>
        public void Apply()
        {
            if (!_initialized || _disposed || _meshRenderer == null || !_faceVisible) return;

            // Pick up completed results from batch task
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
            if (_meshRenderer == null || _hasViseme == null || _visemeToBlendShape == null) return;

            // During teardown the SkinnedMeshRenderer can outlive its sharedMesh, leaving
            // blendShapeCount at 0; SetBlendShapeWeight then throws "index out of bounds (size=0)".
            var sharedMesh = _meshRenderer.sharedMesh;
            if (sharedMesh == null) return;
            int blendShapeCount = sharedMesh.blendShapeCount;
            if (blendShapeCount == 0) return;

            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;
                int bsIndex = _visemeToBlendShape[i];
                if (bsIndex < 0 || bsIndex >= blendShapeCount) continue;
                _meshRenderer.SetBlendShapeWeight(bsIndex, 0f);
                _lastApplied[i] = 0f;
            }
            Array.Clear(_cachedVisemeWeights, 0, _cachedVisemeWeights.Length);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _initialized = false;
                _disposed = true;
                _readyForInference = false;

                // Remove from pending list if queued
                lock (_pendingInference)
                {
                    _pendingInference.Remove(this);
                }

                _audioBufferA = null;
                _audioBufferB = null;
                _audioChunk = null;
                _backFrame = null;
            }
        }
    }
}
