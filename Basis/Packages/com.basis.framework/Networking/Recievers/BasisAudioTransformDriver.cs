using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Static transform batch applier.
/// - Call Initialize() once (optionally with capacity).
/// - Per frame, call BeginFrame() then EndFrame() OR just SimulateOneFrame().
/// - Use EnqueueSet / RequestRemove anytime (main thread).
/// - Call Shutdown() on domain unload/editor exit to dispose native containers.
/// 
/// If you prefer automatic driving from Unity's update loop, add the optional
/// BasisAudioTransformDriverHook to a GameObject (below).
/// </summary>
public static class BasisAudioTransformDriver
{
    // ------- State -------
    private static bool _initialized;
    private static int _initialCapacity = 1024;

    // Job wiring
    private static JobHandle _handle;
    private static bool _jobScheduled;

    // Transform storage for the job system
    private static TransformAccessArray _taa;

    // Parallel per-entity arrays (aligned by index with _taa)
    private static NativeList<float3> _positions;     // target world positions
    private static NativeList<quaternion> _rotations; // target world rotations
    private static NativeList<int> _entryGen;         // "dirty" generation per entry

    // Managed mirrors & index map
    private static readonly List<Transform> _mirror = new List<Transform>(4096);
    private static readonly Dictionary<Transform, int> _indexOf = new Dictionary<Transform, int>(4096);

    // Deferred queues (safe to call EnqueueSet/RequestRemove anytime; we flush once per frame)
    private static readonly List<(Transform t, float3 p, quaternion r)> _queuedSetsThisFrame =
        new List<(Transform, float3, quaternion)>(4096);
    private static readonly HashSet<Transform> _queuedRemovals = new HashSet<Transform>();

    // "Dirty" tracking: only entries tagged with currentGen are touched
    private static int _currentGen = 1;
    private static int _dirtyCountThisGen = 0;

    public static bool IsInitialized => _initialized;
    public static int Count => _taa.isCreated ? _taa.length : 0;

    // ---------------- Job ----------------
    [BurstCompile]
    private struct ApplyTRJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<quaternion> rotations;
        [ReadOnly] public NativeArray<int> entryGen;
        [ReadOnly] public int currentGen;

        public void Execute(int index, TransformAccess transform)
        {
            if (entryGen[index] == currentGen)
            {
                transform.SetPositionAndRotation(positions[index], rotations[index]);
            }
        }
    }

    // --------------- Lifecycle ---------------
    /// <summary>Allocate native containers and prepare capacity. Safe to call multiple times; no-ops after the first.</summary>
    public static void Initialize(int initialCapacity = 1024)
    {
        if (_initialized) return;

        _initialCapacity = Mathf.Max(1, initialCapacity);

        _taa = new TransformAccessArray(_initialCapacity);
        _positions = new NativeList<float3>(_initialCapacity, Allocator.Persistent);
        _rotations = new NativeList<quaternion>(_initialCapacity, Allocator.Persistent);
        _entryGen = new NativeList<int>(_initialCapacity, Allocator.Persistent);

        _mirror.Capacity = Mathf.Max(_mirror.Capacity, _initialCapacity);

        _currentGen = 1;
        _dirtyCountThisGen = 0;
        _jobScheduled = false;
        _initialized = true;
    }

    /// <summary>Dispose containers and clear all state.</summary>
    public static void Shutdown()
    {
        if (!_initialized) return;

        CompleteIfScheduled();

        if (_taa.isCreated) _taa.Dispose();
        if (_positions.IsCreated) _positions.Dispose();
        if (_rotations.IsCreated) _rotations.Dispose();
        if (_entryGen.IsCreated) _entryGen.Dispose();

        _queuedSetsThisFrame.Clear();
        _queuedRemovals.Clear();
        _indexOf.Clear();
        _mirror.Clear();

        _currentGen = 1;
        _dirtyCountThisGen = 0;
        _jobScheduled = false;
        _initialized = false;
    }

    // --------------- Frame Simulation API ---------------
    /// <summary>
    /// Call once per frame before you want transforms applied.
    /// This mirrors MonoBehaviour.Update(): completes prior frame's work, flushes queues, and schedules the job.
    /// </summary>
    public static void BeginFrame()
    {
        if (!_initialized) Initialize(_initialCapacity);

        // Make sure last frame’s work is done
        CompleteIfScheduled();

        // Apply queued removals & additions/updates (no main-thread Transform writes)
        FlushRemovals();
        FlushQueuedSets();

        // If anything was marked dirty this frame, schedule the single job
        if (_dirtyCountThisGen > 0 && _taa.length > 0)
        {
            var job = new ApplyTRJob
            {
                positions = _positions.AsDeferredJobArray(),
                rotations = _rotations.AsDeferredJobArray(),
                entryGen = _entryGen.AsDeferredJobArray(),
                currentGen = _currentGen
            };

            _handle = job.Schedule(_taa);
            _jobScheduled = true;
            JobHandle.ScheduleBatchedJobs();
        }
    }

    /// <summary>
    /// Call once per frame after you've allowed other scripts to enqueue work (mirrors LateUpdate).
    /// Completes the job and advances the generation.
    /// </summary>
    public static void EndFrame()
    {
        if (!_initialized) return;

        CompleteIfScheduled();

        // Advance generation and reset dirty count
        if (_dirtyCountThisGen > 0)
        {
            _dirtyCountThisGen = 0;
            _currentGen++;
            if (_currentGen == int.MaxValue) _currentGen = 1; // wrap safely
        }
    }

    /// <summary>Convenience: does BeginFrame() then EndFrame().</summary>
    public static void SimulateOneFrame()
    {
        BeginFrame();
        EndFrame();
    }

    /// <summary>Ensure any scheduled job completes immediately.</summary>
    public static void ForceComplete() => CompleteIfScheduled();

    private static void CompleteIfScheduled()
    {
        if (_jobScheduled)
        {
            _handle.Complete();
            _jobScheduled = false;
        }
    }

    // --------------- Public Work API ---------------
    /// <summary>
    /// Enqueue a world-space position/rotation to be applied this frame to 't'.
    /// Replacement for calling Transform.SetPositionAndRotation thousands of times.
    /// </summary>
    public static void EnqueueSet(Transform t, Vector3 position, Quaternion rotation)
    {
        if (t == null) return;
        if (!_initialized) Initialize(_initialCapacity);
        _queuedSetsThisFrame.Add((t, (float3)position, (quaternion)rotation));
    }

    /// <summary>Stop tracking a Transform; safe to call anytime (main thread).</summary>
    public static void RequestRemove(Transform t)
    {
        if (t == null) return;
        if (!_initialized) Initialize(_initialCapacity);
        _queuedRemovals.Add(t);
    }

    /// <summary>Remove everything and reset the driver.</summary>
    public static void ClearAll()
    {
        if (!_initialized) return;
        CompleteIfScheduled();

        _queuedSetsThisFrame.Clear();
        _queuedRemovals.Clear();
        _indexOf.Clear();
        _mirror.Clear();

        while (_taa.length > 0)
            _taa.RemoveAtSwapBack(_taa.length - 1);

        _positions.Clear();
        _rotations.Clear();
        _entryGen.Clear();

        _dirtyCountThisGen = 0;
        _currentGen = 1;
    }

    // --------------- Internal: flush & bookkeeping ---------------
    private static void EnsureCapacity(int needed)
    {
        if (needed <= _taa.capacity &&
            needed <= _positions.Capacity &&
            needed <= _rotations.Capacity &&
            needed <= _entryGen.Capacity)
            return;

        int current = math.max(_taa.capacity, _positions.Capacity);
        int newCap = math.max(needed, math.max(4, current * 2));

        _taa.capacity = newCap;
        _positions.Capacity = newCap;
        _rotations.Capacity = newCap;
        _entryGen.Capacity = newCap;
        _mirror.Capacity = math.max(_mirror.Capacity, newCap);
    }

    /// <summary>
    /// Rebuild _indexOf from _mirror (and sanity-trim managed/native lists to match _taa.length).
    /// Call this if anything looks out-of-sync.
    /// </summary>
    private static void RebuildIndexMapAndRealign()
    {
        // If the managed lists got longer than _taa (e.g., external destruction or prior errors), trim them.
        int targetLen = _taa.length;

        // Trim managed lists down to targetLen
        while (_mirror.Count > targetLen) _mirror.RemoveAt(_mirror.Count - 1);
        while (_positions.Length > targetLen) _positions.RemoveAtSwapBack(_positions.Length - 1);
        while (_rotations.Length > targetLen) _rotations.RemoveAtSwapBack(_rotations.Length - 1);
        while (_entryGen.Length > targetLen) _entryGen.RemoveAtSwapBack(_entryGen.Length - 1);

        // If managed lists are shorter (shouldn't happen here), we won't try to re-expand;
        // they'll be filled on next EnqueueSet.

        _indexOf.Clear();
        for (int i = 0; i < _mirror.Count; i++)
        {
            var tr = _mirror[i];
            if (tr != null && !_indexOf.ContainsKey(tr))
                _indexOf.Add(tr, i);
        }
    }

    private static void FlushRemovals()
    {
        if (_queuedRemovals.Count == 0) return;

        // Quick pre-check: if anything smells off, fix alignment first.
        // (These should always match during normal operation.)
        if (_taa.length != _mirror.Count ||
            _positions.Length != _taa.length ||
            _rotations.Length != _taa.length ||
            _entryGen.Length != _taa.length)
        {
            RebuildIndexMapAndRealign();
        }

        // Avoid mutating while iterating the set.
        // Also, remove duplicates and nulls just in case.
        _tmpRemovalBuffer.Clear();
        foreach (var t in _queuedRemovals)
        {
            if (t != null) _tmpRemovalBuffer.Add(t);
        }

        for (int n = 0; n < _tmpRemovalBuffer.Count; n++)
        {
            var t = _tmpRemovalBuffer[n];

            // Try fast lookup
            if (!_indexOf.TryGetValue(t, out int idx))
            {
                // Fallback: scan mirror once (robust against any stale map edge cases)
                idx = _mirror.IndexOf(t);
                if (idx < 0) continue; // already gone
            }

            // If the index is out of bounds for TAA, we got desynced; resync and try again.
            if (idx < 0 || idx >= _taa.length)
            {
                RebuildIndexMapAndRealign();

                // Try again after resync
                idx = -1;
                _indexOf.TryGetValue(t, out idx);
                if (idx < 0 || idx >= _taa.length) continue; // still not valid -> skip
            }

            int lastIdx = _taa.length - 1;

            // Remove from TAA first (swap-back)
            _taa.RemoveAtSwapBack(idx);

            // Mirror & arrays swap-back to keep indices consistent
            int lastMirrorIdx = _mirror.Count - 1; // still old count here

            if (idx != lastIdx)
            {
                // Copy the tail entry into idx
                _positions[idx] = _positions[lastMirrorIdx];
                _rotations[idx] = _rotations[lastMirrorIdx];
                _entryGen[idx] = _entryGen[lastMirrorIdx];

                var movedT = _mirror[lastMirrorIdx];
                _mirror[idx] = movedT;

                if (movedT != null)
                    _indexOf[movedT] = idx;
            }

            // Pop the tail from managed/native mirrors
            _positions.RemoveAtSwapBack(lastMirrorIdx);
            _rotations.RemoveAtSwapBack(lastMirrorIdx);
            _entryGen.RemoveAtSwapBack(lastMirrorIdx);
            _mirror.RemoveAt(lastMirrorIdx);

            // Finally remove from the lookup
            _indexOf.Remove(t);
        }

        _queuedRemovals.Clear();

        // Final sanity: ensure everything is aligned after a batch of removals
        if (_taa.length != _mirror.Count ||
            _positions.Length != _taa.length ||
            _rotations.Length != _taa.length ||
            _entryGen.Length != _taa.length)
        {
            RebuildIndexMapAndRealign();
        }
    }

    private static readonly List<Transform> _tmpRemovalBuffer = new List<Transform>(1024);

    private static void FlushQueuedSets()
    {
        if (_queuedSetsThisFrame.Count == 0) return;

        // Pre-ensure capacity (best-effort)
        int needed = _taa.length;
        for (int i = 0; i < _queuedSetsThisFrame.Count; i++)
        {
            var t = _queuedSetsThisFrame[i].t;
            if (t != null && !_indexOf.ContainsKey(t)) needed++;
        }
        EnsureCapacity(needed);

        // Process each queued set
        for (int i = 0; i < _queuedSetsThisFrame.Count; i++)
        {
            var (t, p, r) = _queuedSetsThisFrame[i];
            if (t == null) continue;

            int idx;
            if (!_indexOf.TryGetValue(t, out idx))
            {
                int newIdx = _taa.length;
                _taa.Add(t);
                _mirror.Add(t);

                _positions.Add(p);
                _rotations.Add(r);
                _entryGen.Add(_currentGen); // mark as dirty this frame
                _indexOf[t] = newIdx;

                _dirtyCountThisGen++;
            }
            else
            {
                // Guard against rare desync: if idx is invalid, rebuild, then retry this one.
                if (idx < 0 || idx >= _taa.length)
                {
                    RebuildIndexMapAndRealign();

                    if (!_indexOf.TryGetValue(t, out idx) || idx < 0 || idx >= _taa.length)
                    {
                        // If still invalid after rebuild, treat as a fresh add.
                        int newIdx = _taa.length;
                        _taa.Add(t);
                        _mirror.Add(t);

                        _positions.Add(p);
                        _rotations.Add(r);
                        _entryGen.Add(_currentGen);
                        _indexOf[t] = newIdx;

                        _dirtyCountThisGen++;
                        continue;
                    }
                }

                _positions[idx] = p;
                _rotations[idx] = r;

                if (_entryGen[idx] != _currentGen)
                {
                    _entryGen[idx] = _currentGen;
                    _dirtyCountThisGen++;
                }
            }
        }

        _queuedSetsThisFrame.Clear();
    }
}
