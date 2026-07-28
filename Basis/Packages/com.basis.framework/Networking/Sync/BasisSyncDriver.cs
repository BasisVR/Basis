using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// Central engine for generic value sync. Owns the SoA interpolation pools shared by every
    /// remote object, schedules the Burst interpolation + transform-apply jobs once per frame, and
    /// drives owned objects' transmit cadence. Ticked from BasisEventDriver alongside the pickup
    /// driver: Initialize/OnDestroy, ScheduleRemote (Update), TransmitOwned + CompleteRemote (LateUpdate).
    /// </summary>
    public static class BasisSyncDriver
    {
        private static readonly List<BasisSyncedObject> _remote = new List<BasisSyncedObject>();
        private static readonly HashSet<BasisSyncedObject> _owned = new HashSet<BasisSyncedObject>();
        private static readonly List<BasisSyncedObject> _ownedScratch = new List<BasisSyncedObject>();
        private static readonly List<BasisSyncedObject> _mainThreadApply = new List<BasisSyncedObject>();

        private static bool _initialized;
        private static bool _dirtyLayout;
        private static bool _scheduled;
        private static bool _hasBindings;
        private static bool _forceFullCopy;
        private static JobHandle _jobHandle;

        // Per-slot (indexed by remote list position).
        private static NativeArray<byte> _active;
        private static NativeArray<float> _t;
        private static NativeArray<int> _contBase, _contCount, _rotBase, _rotCount, _discBase, _discCount;

        // Pools.
        private static NativeArray<float> _contCur, _contNext, _contOut;
        private static NativeArray<byte> _contMode;
        private static NativeArray<quaternion> _rotCur, _rotNext, _rotOut;
        private static NativeArray<byte> _rotMode;
        private static NativeArray<int> _discNext, _discOut;

        // Transform bindings.
        private static readonly List<Transform> _bindTransforms = new List<Transform>();
        private static readonly List<BasisSyncApplyBinding> _bindings = new List<BasisSyncApplyBinding>();
        private static TransformAccessArray _taa;
        private static NativeArray<BasisSyncApplyBinding> _bindingsArr;

        public static void Initialize()
        {
            if (_initialized) return;
            AllocSlots(1);
            AllocCont(1);
            AllocRot(1);
            AllocDisc(1);
            _taa = new TransformAccessArray(0);
            AllocBindings(1);
            _initialized = true;
            _dirtyLayout = true;
        }

        public static void OnDestroy()
        {
            if (!_initialized) return;
            if (_scheduled) { _jobHandle.Complete(); _scheduled = false; }

            DisposeIf(ref _active); DisposeIf(ref _t);
            DisposeIf(ref _contBase); DisposeIf(ref _contCount);
            DisposeIf(ref _rotBase); DisposeIf(ref _rotCount);
            DisposeIf(ref _discBase); DisposeIf(ref _discCount);
            DisposeIf(ref _contCur); DisposeIf(ref _contNext); DisposeIf(ref _contOut); DisposeIf(ref _contMode);
            DisposeIf(ref _rotCur); DisposeIf(ref _rotNext); DisposeIf(ref _rotOut); DisposeIf(ref _rotMode);
            DisposeIf(ref _discNext); DisposeIf(ref _discOut);
            DisposeIf(ref _bindingsArr);
            if (_taa.isCreated) _taa.Dispose();

            _remote.Clear();
            _owned.Clear();
            _initialized = false;
        }

        public static void RegisterRemote(BasisSyncedObject obj)
        {
            if (obj == null || _remote.Contains(obj)) return;
            _remote.Add(obj);
            if (obj.WantsMainThreadApply && !_mainThreadApply.Contains(obj)) _mainThreadApply.Add(obj);
            _dirtyLayout = true;
        }

        public static void UnregisterRemote(BasisSyncedObject obj)
        {
            if (obj == null) return;
            _mainThreadApply.Remove(obj);
            if (_remote.Remove(obj)) _dirtyLayout = true;
        }

        /// <summary>Live list of remote (interpolated) synced objects, for debug visualisation. Do not mutate.</summary>
        public static IReadOnlyList<BasisSyncedObject> RemoteObjects => _remote;

        /// <summary>Live set of locally-owned (transmitting) synced objects, for debug visualisation. Do not mutate.</summary>
        public static HashSet<BasisSyncedObject> OwnedObjects => _owned;

        public static void RegisterOwned(BasisSyncedObject obj)
        {
            if (obj != null) _owned.Add(obj);
        }

        public static void UnregisterOwned(BasisSyncedObject obj)
        {
            if (obj != null) _owned.Remove(obj);
        }

        public static void MarkLayoutDirty() => _dirtyLayout = true;

        public static void ScheduleRemote(float deltaTime)
        {
            if (!_initialized) return;
            if (_scheduled) { _jobHandle.Complete(); _scheduled = false; }

            if (_remote.Count == 0) return;
            if (_dirtyLayout) { RebuildLayout(); _forceFullCopy = true; }

            int n = _remote.Count;
            for (int i = 0; i < n; i++)
            {
                BasisSyncedObject o = _remote[i];
                if (o == null) { _active[i] = 0; continue; }

                o.AdvanceReceiver(deltaTime);
                BasisSyncReceiver recv = o.Receiver;
                if (recv == null || !recv.HasData) { _active[i] = 0; continue; }

                if (recv.ConsumeValuesDirty() || _forceFullCopy)
                {
                    int cb = _contBase[i], cc = _contCount[i];
                    int rb = _rotBase[i], rc = _rotCount[i];
                    int db = _discBase[i], dc = _discCount[i];
                    BasisSyncValues cur = recv.CurrentValues;
                    BasisSyncValues nxt = recv.NextValues;

                    for (int c = 0; c < cc; c++)
                    {
                        _contCur[cb + c] = cur.Cont[c];
                        _contNext[cb + c] = nxt.Cont[c];
                    }
                    for (int r = 0; r < rc; r++)
                    {
                        _rotCur[rb + r] = cur.Rot[r];
                        _rotNext[rb + r] = nxt.Rot[r];
                    }
                    for (int d = 0; d < dc; d++)
                    {
                        _discNext[db + d] = nxt.Disc[d];
                    }
                }

                _t[i] = recv.InterpTime;
                _active[i] = 1;
            }

            _forceFullCopy = false;

            var interp = new InterpolateSyncObjectsJob
            {
                Active = _active,
                T = _t,
                ContBase = _contBase,
                ContCount = _contCount,
                RotBase = _rotBase,
                RotCount = _rotCount,
                DiscBase = _discBase,
                DiscCount = _discCount,
                ContCur = _contCur,
                ContNext = _contNext,
                ContMode = _contMode,
                RotCur = _rotCur,
                RotNext = _rotNext,
                RotMode = _rotMode,
                DiscNext = _discNext,
                ContOut = _contOut,
                RotOut = _rotOut,
                DiscOut = _discOut,
            };

            JobHandle h = interp.Schedule(n, 16);

            if (_hasBindings && _taa.isCreated && _taa.length > 0)
            {
                var apply = new ApplySyncTransformsJob
                {
                    ContOut = _contOut,
                    RotOut = _rotOut,
                    Active = _active,
                    Bindings = _bindingsArr,
                };
                h = apply.Schedule(_taa, h);
            }

            _jobHandle = h;
            _scheduled = true;

            // Kick the batch now: the complete sits far away in the LateUpdate network-apply
            // block, and without a flush the interp chain idles in the queue through the
            // eye/comms/transmit stages it is supposed to overlap.
            JobHandle.ScheduleBatchedJobs();
        }

        public static void CompleteRemote()
        {
            if (_scheduled)
            {
                _jobHandle.Complete();
                _scheduled = false;
            }

            for (int i = 0; i < _mainThreadApply.Count; i++)
            {
                BasisSyncedObject o = _mainThreadApply[i];
                if (o != null && !o.JobApplied) o.DriverApply();
            }
        }

        public static void TransmitOwned(double timeAsDouble)
        {
            if (!_initialized || _owned.Count == 0) return;

            _ownedScratch.Clear();
            foreach (BasisSyncedObject o in _owned) _ownedScratch.Add(o);
            for (int i = 0; i < _ownedScratch.Count; i++)
            {
                BasisSyncedObject o = _ownedScratch[i];
                if (o != null) o.TransmitIfDue(timeAsDouble);
            }

            BasisSyncBatchCollector.Flush();
        }

        internal static float ReadCont(int idx)
            => (idx >= 0 && _contOut.IsCreated && idx < _contOut.Length) ? _contOut[idx] : 0f;

        internal static float3 ReadFloat3(int idx)
        {
            if (idx < 0 || !_contOut.IsCreated || idx + 2 >= _contOut.Length) return float3.zero;
            return new float3(_contOut[idx], _contOut[idx + 1], _contOut[idx + 2]);
        }

        internal static quaternion ReadRot(int idx)
            => (idx >= 0 && _rotOut.IsCreated && idx < _rotOut.Length) ? _rotOut[idx] : quaternion.identity;

        internal static int ReadDisc(int idx)
            => (idx >= 0 && _discOut.IsCreated && idx < _discOut.Length) ? _discOut[idx] : 0;

        private static void RebuildLayout()
        {
            if (_scheduled) { _jobHandle.Complete(); _scheduled = false; }

            int n = _remote.Count;
            int contTotal = 0, rotTotal = 0, discTotal = 0;
            for (int i = 0; i < n; i++)
            {
                BasisSyncSchema s = _remote[i].Schema;
                contTotal += s.ContCount;
                rotTotal += s.RotCount;
                discTotal += s.DiscCount;
            }

            AllocSlots(n);
            AllocCont(contTotal);
            AllocRot(rotTotal);
            AllocDisc(discTotal);

            _bindTransforms.Clear();
            _bindings.Clear();

            int cb = 0, rb = 0, db = 0;
            for (int i = 0; i < n; i++)
            {
                BasisSyncedObject o = _remote[i];
                BasisSyncSchema s = o.Schema;

                o.SyncSlot = i;
                o.OutContBase = cb;
                o.OutRotBase = rb;
                o.OutDiscBase = db;

                _contBase[i] = cb; _contCount[i] = s.ContCount;
                _rotBase[i] = rb; _rotCount[i] = s.RotCount;
                _discBase[i] = db; _discCount[i] = s.DiscCount;

                for (int f = 0; f < s.FieldCount; f++)
                {
                    BasisSyncField fld = s.GetField(f);
                    if (fld.Pool == BasisSyncPool.Continuous)
                    {
                        byte mode = fld.Type == BasisSyncFieldType.Angle
                            ? (fld.Interpolate ? (byte)2 : (byte)0)
                            : (fld.Interpolate ? (byte)1 : (byte)0);
                        for (int c = 0; c < fld.ContComponents; c++)
                            _contMode[cb + fld.Offset + c] = mode;
                    }
                    else if (fld.Pool == BasisSyncPool.Rotation)
                    {
                        _rotMode[rb + fld.Offset] = (byte)(fld.Interpolate ? 1 : 0);
                    }
                }

                o.JobApplied = false;
                if (o.TryGetJobApplyBinding(out BasisSyncApplyBinding binding, out Transform target, out bool replacesMainThreadApply) && target != null)
                {
                    binding.Slot = i;
                    binding.OffsetPools(cb, rb);
                    _bindTransforms.Add(target);
                    _bindings.Add(binding);
                    o.JobApplied = replacesMainThreadApply;
                }

                cb += s.ContCount;
                rb += s.RotCount;
                db += s.DiscCount;
            }

            RebuildBindings();
            _dirtyLayout = false;
        }

        private static void RebuildBindings()
        {
            int count = _bindTransforms.Count;
            _hasBindings = count > 0;

            if (_taa.isCreated) _taa.Dispose();
            _taa = new TransformAccessArray(count);
            for (int i = 0; i < count; i++) _taa.Add(_bindTransforms[i]);

            AllocBindings(count < 1 ? 1 : count);
            for (int i = 0; i < count; i++) _bindingsArr[i] = _bindings[i];
        }

        private static void AllocSlots(int n)
        {
            if (n < 1) n = 1;
            if (_active.IsCreated && _active.Length == n) return;
            DisposeIf(ref _active); DisposeIf(ref _t);
            DisposeIf(ref _contBase); DisposeIf(ref _contCount);
            DisposeIf(ref _rotBase); DisposeIf(ref _rotCount);
            DisposeIf(ref _discBase); DisposeIf(ref _discCount);
            _active = new NativeArray<byte>(n, Allocator.Persistent);
            _t = new NativeArray<float>(n, Allocator.Persistent);
            _contBase = new NativeArray<int>(n, Allocator.Persistent);
            _contCount = new NativeArray<int>(n, Allocator.Persistent);
            _rotBase = new NativeArray<int>(n, Allocator.Persistent);
            _rotCount = new NativeArray<int>(n, Allocator.Persistent);
            _discBase = new NativeArray<int>(n, Allocator.Persistent);
            _discCount = new NativeArray<int>(n, Allocator.Persistent);
        }

        private static void AllocCont(int total)
        {
            if (total < 1) total = 1;
            if (_contCur.IsCreated && _contCur.Length == total) return;
            DisposeIf(ref _contCur); DisposeIf(ref _contNext); DisposeIf(ref _contOut); DisposeIf(ref _contMode);
            _contCur = new NativeArray<float>(total, Allocator.Persistent);
            _contNext = new NativeArray<float>(total, Allocator.Persistent);
            _contOut = new NativeArray<float>(total, Allocator.Persistent);
            _contMode = new NativeArray<byte>(total, Allocator.Persistent);
        }

        private static void AllocRot(int total)
        {
            if (total < 1) total = 1;
            if (_rotCur.IsCreated && _rotCur.Length == total) return;
            DisposeIf(ref _rotCur); DisposeIf(ref _rotNext); DisposeIf(ref _rotOut); DisposeIf(ref _rotMode);
            _rotCur = new NativeArray<quaternion>(total, Allocator.Persistent);
            _rotNext = new NativeArray<quaternion>(total, Allocator.Persistent);
            _rotOut = new NativeArray<quaternion>(total, Allocator.Persistent);
            _rotMode = new NativeArray<byte>(total, Allocator.Persistent);
            for (int i = 0; i < total; i++)
            {
                _rotCur[i] = quaternion.identity;
                _rotNext[i] = quaternion.identity;
                _rotOut[i] = quaternion.identity;
                _rotMode[i] = 1;
            }
        }

        private static void AllocDisc(int total)
        {
            if (total < 1) total = 1;
            if (_discNext.IsCreated && _discNext.Length == total) return;
            DisposeIf(ref _discNext); DisposeIf(ref _discOut);
            _discNext = new NativeArray<int>(total, Allocator.Persistent);
            _discOut = new NativeArray<int>(total, Allocator.Persistent);
        }

        private static void AllocBindings(int n)
        {
            if (n < 1) n = 1;
            if (_bindingsArr.IsCreated && _bindingsArr.Length == n) return;
            DisposeIf(ref _bindingsArr);
            _bindingsArr = new NativeArray<BasisSyncApplyBinding>(n, Allocator.Persistent);
        }

        private static void DisposeIf<T>(ref NativeArray<T> arr) where T : struct
        {
            if (arr.IsCreated) arr.Dispose();
            arr = default;
        }
    }
}
