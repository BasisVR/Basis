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

        // Result handoff, batch task -> Apply(). Two frames and a generation counter rather
        // than one frame and a "new results" flag: the flag had to be cleared by Apply before
        // Simulate would queue more audio, which cost a whole frame of lip-sync latency every
        // time inference did not land inside the Simulate..Apply window. The task writes the
        // slot the last publish did NOT use and only then bumps the generation, so Apply can
        // read the published slot without a lock and without ever blocking the producer.
        // Safe because Simulate and Apply each run once per frame, so at most one publish
        // lands between two reads and the slots cannot be lapped.
        private readonly Frame[] _resultFrames = { new Frame(), new Frame() };
        private volatile int _publishedGeneration;
        private int _consumedGeneration;

        // Viseme-to-blendshape mapping
        private int[] _visemeToBlendShape;
        private bool[] _hasViseme;
        private SkinnedMeshRenderer _meshRenderer;

        // Response shaping baked from BasisAvatar.FaceVisemeProfiles / FaceVisemeDrive.
        // _identityMapping short-circuits every avatar that authored nothing, which is
        // almost all of them, back onto the original probability * 100 write loop.
        private BakedViseme[] _baked;
        private float[] _target;
        private float[] _current;
        private bool _identityMapping = true;
        private BasisVisemeDriveMode _mode;
        private float _winnerMargin;
        private float _winnerHoldSeconds;
        private float _silenceFloor;
        private bool _silIsRest;
        private int _winner = -1;
        private float _winnerHeldSeconds;

        private struct BakedViseme
        {
            public float Gain;
            public float Threshold;
            public float ThresholdSpanInverse;
            public float OutMin;
            public float OutMax;
            public float OutSpan;
            public float AttackRate;
            public float ReleaseRate;
            public bool Binary;
        }

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
        // Not only debug: the HVR comms viseme bridge reads this every frame. The array is
        // allocated once and only ever mutated in place, so callers may cache the reference.
        // These are the weights on the mesh (0..100), so on an avatar that authored a response
        // profile they are post-shaping. Use RawVisemeWeights for the model's own output.
        public float[] LastApplied => _lastApplied;

        /// <summary>
        /// Model output per viseme (0..1), before any avatar response shaping. Allocated once
        /// and mutated in place, so the reference may be cached.
        /// </summary>
        public float[] RawVisemeWeights => _cachedVisemeWeights;
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

            // FaceVisemeMovement is authored against the mesh the creator had in the SDK. A mesh
            // that arrives with fewer shapes than that — a stripped LOD, a failed import, a
            // remapped generic rebuild — leaves indices pointing past the end, so drop them here
            // rather than letting every frame throw. Count 0 is not judged: the shapes may still
            // be landing, and the per-frame guard covers it until they do.
            int mappedCeiling = LiveBlendShapeCount();
            if (mappedCeiling > 0)
            {
                int dropped = 0;
                for (int i = 0; i < VisemeCount; i++)
                {
                    if (!_hasViseme[i]) continue;

                    int bsIndex = _visemeToBlendShape[i];
                    if (bsIndex >= 0 && bsIndex < mappedCeiling) continue;

                    _visemeToBlendShape[i] = -1;
                    _hasViseme[i] = false;
                    dropped++;
                }

                if (dropped > 0)
                {
                    Debug.LogWarning($"[OpenLipSync] {dropped} viseme(s) on '{avatar.name}' map past the face mesh's {mappedCeiling} blendshapes and were disabled.");
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

            int smoothing = BakeProfiles(avatar);
            BasisOpenLipSyncDriver.SendSignal(_contextHandle, Signals.VisemeSmoothing, smoothing);

            _initialized = true;
        }

        /// <summary>
        /// Flattens the avatar's authored profiles into a sanitised per-viseme table so the
        /// per-frame path never has to validate creator input. Returns the backend smoothing
        /// the avatar asks for. Sets <see cref="_identityMapping"/> when nothing was authored,
        /// which keeps untouched avatars on the original write loop.
        /// </summary>
        private int BakeProfiles(BasisAvatar avatar)
        {
            BasisVisemeDriveConfig config = avatar.FaceVisemeDrive;
            if (config == null || config.IsUnset)
            {
                // An avatar built before these fields existed reaches us as null or as an
                // all-zero instance, and zero is not neutral here: BackendSmoothing 0 strips the
                // temporal smoothing the backend used to get unconditionally, leaving the mouth
                // sampling a 100 Hz signal at frame rate. Anything that authored nothing must
                // come out of this exactly as it did before response shaping existed.
                config = new BasisVisemeDriveConfig();
            }

            BasisVisemeProfile[] authored = avatar.FaceVisemeProfiles;
            if (IsUnsetTable(authored))
            {
                // Allocated but never filled in. Baking it would pin every shape to rest and
                // mute the avatar outright, so fall back to the pass-through defaults.
                authored = null;
            }

            _mode = config.Mode;
            _winnerMargin = Math.Max(0f, config.WinnerMargin);
            _winnerHoldSeconds = Math.Max(0f, config.WinnerHoldSeconds);
            _silenceFloor = Math.Clamp(config.SilenceFloor, 0f, 1f);
            _silIsRest = config.SilIsRest;
            _winner = -1;
            _winnerHeldSeconds = 0f;

            bool identity = _mode == BasisVisemeDriveMode.Continuous;
            if (identity && authored != null)
            {
                int authoredCount = Math.Min(authored.Length, VisemeCount);
                for (int i = 0; i < authoredCount; i++)
                {
                    if (!authored[i].IsDefault)
                    {
                        identity = false;
                        break;
                    }
                }
            }

            _identityMapping = identity;
            if (identity)
            {
                _baked = null;
                _target = null;
                _current = null;
                return ResolveBackendSmoothing(config);
            }

            if (_baked == null || _baked.Length != VisemeCount)
            {
                _baked = new BakedViseme[VisemeCount];
                _target = new float[VisemeCount];
                _current = new float[VisemeCount];
            }
            Array.Clear(_target, 0, _target.Length);
            Array.Clear(_current, 0, _current.Length);

            for (int i = 0; i < VisemeCount; i++)
            {
                BasisVisemeProfile profile = authored != null && i < authored.Length
                    ? authored[i]
                    : BasisVisemeProfile.Default;

                if (profile.IsUnset)
                {
                    // An entirely-blank slot in an otherwise authored table. Only the blank ones
                    // are rebuilt: a creator switching a viseme off zeroes its gain or collapses
                    // its range, and rebuilding THAT would drive the shape they silenced.
                    profile = BasisVisemeProfile.Default;
                }

                float threshold = Math.Clamp(profile.Threshold, 0f, 0.99f);
                float outMin = Math.Clamp(profile.OutMin, 0f, 100f);
                float outMax = Math.Clamp(profile.OutMax, 0f, 100f);
                float attack = Math.Max(0f, profile.AttackSeconds);
                float release = Math.Max(0f, profile.ReleaseSeconds);

                _baked[i] = new BakedViseme
                {
                    Gain = Math.Max(0f, profile.Gain),
                    Threshold = threshold,
                    ThresholdSpanInverse = 1f / (1f - threshold),
                    OutMin = outMin,
                    OutMax = outMax,
                    OutSpan = outMax - outMin,
                    AttackRate = attack > 0f ? 100f / attack : 0f,
                    ReleaseRate = release > 0f ? 100f / release : 0f,
                    Binary = profile.Binary,
                };
            }

            return ResolveBackendSmoothing(config);
        }

        /// <summary>
        /// True when a table exists but every entry in it is blank — an allocation nobody ever
        /// filled in, which is the one case the zeroed struct cannot otherwise be told apart from
        /// a creator deliberately silencing every viseme.
        /// </summary>
        private static bool IsUnsetTable(BasisVisemeProfile[] profiles)
        {
            if (profiles == null || profiles.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < profiles.Length; i++)
            {
                if (!profiles[i].IsUnset)
                {
                    return false;
                }
            }

            return true;
        }

        private int ResolveBackendSmoothing(BasisVisemeDriveConfig config)
        {
            int smoothing = config.BackendSmoothing;
            if (smoothing < 0)
            {
                smoothing = _mode == BasisVisemeDriveMode.WinnerTakeAll
                    ? BasisVisemeDriveConfig.WinnerTakeAllBackendSmoothing
                    : BasisVisemeDriveConfig.DefaultBackendSmoothing;
            }
            return Math.Clamp(smoothing, 0, 100);
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
            if (dstArr == null) return;
            int w = (buf == 0) ? Volatile.Read(ref _writeIndexA) : Volatile.Read(ref _writeIndexB);

            if (length > data.Length) length = data.Length;
            int frames = length / ch;

            // Stop at the end rather than wrapping. Simulate drains this linearly from index 0,
            // so a wrap does not cost the oldest samples the way a ring is supposed to — it
            // reorders the buffer and hands the model a second of scrambled audio, which poisons
            // the streaming convolution caches long after the stall that caused it. One second
            // of headroom means only a catastrophic main-thread stall reaches this.
            int room = AudioBufferSize - w;
            if (frames > room) frames = room;

            for (int i = 0, s = 0; i < frames; i++, s += ch)
            {
                dstArr[w + i] = data[s];
            }
            w += frames;

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

            // Already queued for batch processing. This is the only gate left: it protects
            // _audioChunk, which the batch task is reading. Whether Apply() has picked up the
            // PREVIOUS result is deliberately not consulted — waiting on that made the mouth
            // run at half the frame rate and a further frame behind, which is exactly the lag
            // this pipeline is trying not to add.
            if (_readyForInference) return;

            if (Interlocked.Exchange(ref _hasNewAudio, 0) != 1) return;

            // Swap buffers
#pragma warning disable CS0420 // Volatile.Read/Write provide correct semantics for these volatile fields
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
            int processed = 0;

            while (batchLen > 0)
            {
                for (int i = 0; i < batchLen; i++)
                {
                    var ctx = batch[i];
                    batch[i] = null;
                    if (ctx == null) continue;

                    if (ctx._disposed)
                    {
                        ctx._readyForInference = false;
                        continue;
                    }

                    try
                    {
                        // Write the slot the last publish did not use, then publish it. Nothing
                        // reads the new slot until the generation bump makes it visible.
                        int generation = ctx._publishedGeneration + 1;
                        Frame target = ctx._resultFrames[generation & 1];

                        var result = BasisOpenLipSyncDriver.ProcessFrame(
                            ctx._contextHandle, ctx._audioChunk, ctx._frozenSampleCount, target);

                        if (result == Result.Success)
                        {
                            ctx._publishedGeneration = generation;
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

                processed += batchLen;
                batchLen = 0;

                // Contexts that arrived while this task was running would otherwise sit until
                // the next frame boundary, so a room full of speakers accumulates lip-sync lag
                // in whole frames. Keep the thread we already have and drain them now. Bounded
                // so one task cannot monopolise a pool thread indefinitely under sustained load.
                if (processed >= MaxContextsPerBatch * 2) break;

                lock (_pendingInference)
                {
                    int waiting = _pendingInference.Count;
                    if (waiting == 0) break;

                    batchLen = Math.Min(waiting, MaxContextsPerBatch);
                    if (batch.Length < batchLen) batchLen = batch.Length;
                    _pendingInference.CopyTo(0, batch, 0, batchLen);
                    _pendingInference.RemoveRange(0, batchLen);
                }
            }
        }

        /// <summary>
        /// Called on the main thread. If background inference completed, picks up new
        /// viseme weights. Applies cached weights to blendshapes. Never stalls.
        /// </summary>
        public void Apply(float deltaTime)
        {
            if (!_initialized || _disposed || _meshRenderer == null || !_faceVisible) return;

            // Pick up completed results from batch task
            int generation = _publishedGeneration;
            if (generation != _consumedGeneration)
            {
                _consumedGeneration = generation;
                float[] published = _resultFrames[generation & 1].Visemes;
                int visemeCount = Math.Min(published.Length, _cachedVisemeWeights.Length);
                Array.Copy(published, _cachedVisemeWeights, visemeCount);
            }

            if (_identityMapping)
            {
                ApplyDirect();
                return;
            }

            if (_mode == BasisVisemeDriveMode.WinnerTakeAll)
            {
                ResolveWinnerTakeAll(deltaTime);
            }
            else
            {
                ResolveContinuous();
            }

            ApplyShaped(deltaTime);
        }

        /// <summary>
        /// Untouched-avatar path: probability straight to weight, with the same change filter
        /// that has always guarded the skinned mesh from redundant dirtying.
        /// </summary>
        private void ApplyDirect()
        {
            int blendShapeCount = LiveBlendShapeCount();
            if (blendShapeCount == 0) return;

            // Apply cached weights (new or stale from last frame - no stall)
            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;

                int bsIndex = _visemeToBlendShape[i];
                if (bsIndex < 0 || bsIndex >= blendShapeCount) continue;

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

        /// <summary>
        /// Blendshape count of the mesh as it stands this frame, 0 when there is nothing writable.
        /// The mapping is captured at Initialize but the mesh underneath it is not fixed: it drops
        /// to 0 while a SkinnedMeshRenderer outlives its sharedMesh during an avatar swap, and a
        /// generic import can hand over a renderer whose shapes are still being rebuilt. Writing a
        /// stale index into either throws per frame, which the exception notifier then amplifies.
        /// </summary>
        private int LiveBlendShapeCount()
        {
            if (_meshRenderer == null) return 0;

            Mesh sharedMesh = _meshRenderer.sharedMesh;
            return sharedMesh == null ? 0 : sharedMesh.blendShapeCount;
        }

        private void ResolveContinuous()
        {
            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;

                ref BakedViseme baked = ref _baked[i];
                float value = _cachedVisemeWeights[i] * baked.Gain;

                if (value <= baked.Threshold)
                {
                    _target[i] = baked.OutMin;
                    continue;
                }
                if (baked.Binary)
                {
                    _target[i] = baked.OutMax;
                    continue;
                }

                float normalized = (value - baked.Threshold) * baked.ThresholdSpanInverse;
                if (normalized > 1f) normalized = 1f;
                _target[i] = baked.OutMin + baked.OutSpan * normalized;
            }
        }

        /// <summary>
        /// Picks the single strongest viseme and rests everything else. A challenger has to
        /// clear both the dwell time and the margin before it takes over, because the model
        /// emits at a 100 Hz hop and a raw argmax flips on every near-tie — invisible when
        /// shapes blend, but a visible strobe when only one shape is ever shown.
        /// </summary>
        private void ResolveWinnerTakeAll(float deltaTime)
        {
            int best = -1;
            float bestValue = 0f;

            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;

                ref BakedViseme baked = ref _baked[i];
                float value = _cachedVisemeWeights[i] * baked.Gain;
                if (value <= baked.Threshold) continue;

                if (value > bestValue)
                {
                    bestValue = value;
                    best = i;
                }
            }

            _winnerHeldSeconds += deltaTime;

            if (best != _winner)
            {
                float holderValue = _winner >= 0 && _hasViseme[_winner]
                    ? _cachedVisemeWeights[_winner] * _baked[_winner].Gain
                    : 0f;

                if (_winner < 0 || (_winnerHeldSeconds >= _winnerHoldSeconds && bestValue >= holderValue + _winnerMargin))
                {
                    _winner = best;
                    _winnerHeldSeconds = 0f;
                }
            }

            int active = _winner;
            if (active < 0 || bestValue < _silenceFloor || (_silIsRest && active == BasisVisemeDriveConfig.SilVisemeIndex))
            {
                active = -1;
            }

            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;
                _target[i] = i == active ? _baked[i].OutMax : _baked[i].OutMin;
            }
        }

        /// <summary>
        /// Slews toward the resolved targets and writes what changed. The continuous value is
        /// tracked apart from the last written weight so a slow ramp still advances even while
        /// each individual step is below the write threshold.
        /// </summary>
        private void ApplyShaped(float deltaTime)
        {
            int blendShapeCount = LiveBlendShapeCount();
            if (blendShapeCount == 0) return;

            for (int i = 0; i < VisemeCount; i++)
            {
                if (!_hasViseme[i]) continue;

                int bsIndex = _visemeToBlendShape[i];
                if (bsIndex < 0 || bsIndex >= blendShapeCount) continue;

                float value = _current[i];
                float target = _target[i];

                if (value != target)
                {
                    ref BakedViseme baked = ref _baked[i];
                    float rate = target > value ? baked.AttackRate : baked.ReleaseRate;
                    if (rate > 0f)
                    {
                        float step = rate * deltaTime;
                        float delta = target - value;
                        if (delta > step) target = value + step;
                        else if (delta < -step) target = value - step;
                    }
                    _current[i] = target;
                }

                float diff = target - _lastApplied[i];
                bool converged = target == _target[i];

                if (diff * diff > BlendShapeWriteEps * BlendShapeWriteEps || (converged && diff != 0f))
                {
                    _meshRenderer.SetBlendShapeWeight(bsIndex, target);
                    _lastApplied[i] = target;
                }
            }
        }

        public void SetFaceVisible(bool visible)
        {
            _faceVisible = visible;
        }

        public void ZeroVisemes()
        {
            _winner = -1;
            _winnerHeldSeconds = 0f;
            if (_target != null)
            {
                Array.Clear(_target, 0, _target.Length);
                Array.Clear(_current, 0, _current.Length);
            }

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
                // Already driven to rest by a previous zeroing — skip the write. Re-writing a
                // weight that is already 0 still dirties the skinned mesh, which is the whole cost
                // we are trying to avoid; reading _lastApplied does not.
                if (_lastApplied[i] == 0f) continue;
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
                // _resultFrames deliberately survives: it is two 15-float frames, and a batch
                // task that raced past the _disposed check above would otherwise dereference
                // null inside the backend rather than throwing something the catch expects.
            }
        }
    }
}
