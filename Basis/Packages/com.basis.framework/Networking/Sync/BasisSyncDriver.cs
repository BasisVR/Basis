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
        private static readonly List<BasisPickupSyncNetworking> _pickupReweld = new List<BasisPickupSyncNetworking>();

        private static bool _initialized;
        /// <summary>Set whenever <see cref="_owned"/> changes, so the transmit snapshot is rebuilt only then.</summary>
        private static bool _ownedDirty;
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

        // ── Batched distance/relevance reduction ──
        // Every owned object used to compute its own nearest-observer distance inside TransmitIfDue,
        // walking the whole receiver snapshot and calling GetLatestNetworkPose per player. That made
        // the pose gather alone O(objects x players), and because each object's timer started at 0
        // and was stamped with the current time on fire, every object that first ticked on the same
        // frame stayed phase-locked to every other one — the entire cost landed on one frame, twice
        // a second. Now: gather each player's pose ONCE, run the cross product as a parallel Burst
        // job, scatter the results back. Scheduled in ScheduleRemote (Update) and completed in
        // TransmitOwned (LateUpdate) so it overlaps the rest of the frame.
        private const double ReductionUpdateInterval = 0.5;
        private static double _lastReductionPassTime = double.NegativeInfinity;
        private static bool _reductionScheduled;
        private static JobHandle _reductionHandle;
        private static int _reductionCount;
        private static int _reductionMasksPerObject = 1;
        private static int _reductionRawPlayerCount;
        private static readonly List<BasisSyncedObject> _reductionParticipants = new List<BasisSyncedObject>();
        private static NativeArray<float3> _reductionObjectPositions;
        private static NativeArray<float> _reductionRadiusSq;
        private static NativeArray<float3> _reductionPlayerPositions;
        private static NativeArray<float> _reductionNearestSq;
        private static NativeArray<ulong> _reductionMask;
        private static ushort[] _reductionPlayerIds = new ushort[0];
        private static ushort[] _reductionRecipientScratch = new ushort[0];

        // Receive decode fan-out. Each receiver touches only its own state and packets are only
        // produced by the main-thread action drain, which cannot run while this pass blocks.
        private static float _advanceDelta;
        private static readonly System.Action<int> _advanceBody = AdvanceRemoteBody;
        private static readonly System.Threading.Tasks.ParallelOptions _advanceOptions = new System.Threading.Tasks.ParallelOptions
        {
            MaxDegreeOfParallelism = System.Math.Max(1, System.Environment.ProcessorCount - 2)
        };

        private static void AdvanceRemoteBody(int i)
        {
            BasisSyncedObject o = _remote[i];
            if (o is null) return;
            o.AdvanceReceiver(_advanceDelta);
        }

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
            if (_reductionScheduled) { _reductionHandle.Complete(); _reductionScheduled = false; }

            DisposeIf(ref _reductionObjectPositions); DisposeIf(ref _reductionRadiusSq);
            DisposeIf(ref _reductionPlayerPositions); DisposeIf(ref _reductionNearestSq);
            DisposeIf(ref _reductionMask);
            _reductionParticipants.Clear();
            _reductionCount = 0;
            _lastReductionPassTime = double.NegativeInfinity;

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
            _ownedScratch.Clear();
            _ownedDirty = false;
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

        /// <summary>
        /// Live set of locally-owned (transmitting) synced objects, for debug visualisation.
        /// Do not mutate — Register/UnregisterOwned are what keep the transmit snapshot in sync;
        /// an outside Add/Remove would not be seen until the set's count next changes.
        /// </summary>
        public static HashSet<BasisSyncedObject> OwnedObjects => _owned;

        public static void RegisterOwned(BasisSyncedObject obj)
        {
            if (obj != null && _owned.Add(obj)) _ownedDirty = true;
        }

        public static void UnregisterOwned(BasisSyncedObject obj)
        {
            if (obj != null && _owned.Remove(obj)) _ownedDirty = true;
        }

        public static void MarkLayoutDirty() => _dirtyLayout = true;

        public static void ScheduleRemote(float deltaTime)
        {
            if (!_initialized) return;
            if (_scheduled) { _jobHandle.Complete(); _scheduled = false; }

            // Ahead of the _remote early-out: owned-object reduction is independent of whether any
            // remote objects exist, and this is the earliest point in the frame it can be kicked.
            ScheduleReductionPass(Time.timeAsDouble);

            if (_remote.Count == 0) return;
            if (_dirtyLayout) { RebuildLayout(); _forceFullCopy = true; }

            int n = _remote.Count;
            _advanceDelta = deltaTime;
            if (n > 8)
            {
                System.Threading.Tasks.Parallel.For(0, n, _advanceOptions, _advanceBody);
            }
            else
            {
                for (int i = 0; i < n; i++) AdvanceRemoteBody(i);
            }
            for (int i = 0; i < n; i++)
            {
                BasisSyncedObject o = _remote[i];
                if (o == null) { _active[i] = 0; continue; }

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

        /// <summary>
        /// Rebuilds the owned-object snapshot if the set changed. The snapshot exists so
        /// TransmitIfDue can register or unregister owned objects without invalidating the
        /// enumerator; it used to be rebuilt every frame — a full HashSet walk (which visits free
        /// slots, so it costs the high-water capacity rather than the live count) plus a List.Add
        /// per entry — for a set that only changes when something is picked up, dropped, spawned or
        /// despawned. Rebuilding on change is behaviourally identical: a mutation from inside the
        /// transmit loop already only took effect on the following frame, and still does. The count
        /// comparison is the cheap guard against a mutation that bypassed Register/UnregisterOwned.
        /// </summary>
        private static void RefreshOwnedSnapshot()
        {
            if (!_ownedDirty && _ownedScratch.Count == _owned.Count) return;
            _ownedScratch.Clear();
            foreach (BasisSyncedObject o in _owned) _ownedScratch.Add(o);
            _ownedDirty = false;
        }

        internal static void QueuePickupReweld(BasisPickupSyncNetworking pickup) => _pickupReweld.Add(pickup);

        /// <summary>
        /// Re-welds every hand-attached remote pickup against the holder's freshly posed skeleton.
        /// Their <c>ApplyInterpolated</c> runs in <see cref="CompleteRemote"/>, before the remote bone jobs
        /// write this frame's pose; BasisEventDriver calls this after CompleteRemoteBoneJobSystemJobs.
        /// </summary>
        public static void ReweldAttachedPickups()
        {
            int count = _pickupReweld.Count;
            if (count == 0) return;
            for (int i = 0; i < count; i++)
            {
                BasisPickupSyncNetworking pickup = _pickupReweld[i];
                if (pickup != null) pickup.ReweldAfterRemoteBones();
            }
            _pickupReweld.Clear();
        }

        public static void TransmitOwned(double timeAsDouble)
        {
            if (!_initialized) return;

            // Joined before the empty-set early-out: everything owned can be unregistered between
            // the schedule in ScheduleRemote and here (a destroy or an ownership change during
            // network apply), and a scheduled job must never be left unjoined.
            CompleteReductionPass();

            if (_owned.Count == 0) return;
            RefreshOwnedSnapshot();

            for (int i = 0; i < _ownedScratch.Count; i++)
            {
                BasisSyncedObject o = _ownedScratch[i];
                if (o == null) continue;

                // Fenced per object. TransmitIfDue reaches content-authored OnBeforeTransmit overrides
                // and the transport, and this pass sits mid-LateUpdate ahead of CompleteRemote — the
                // join for the interpolation jobs ScheduleRemote kicked. An escape here unwinds all the
                // way to BasisEventDriver.LateUpdate's catch, skipping that join plus every later stage,
                // so one object's bad frame becomes a frame-wide outage that repeats silently.
                try
                {
                    o.TransmitIfDue(timeAsDouble);
                }
                catch (System.Exception ex)
                {
                    BasisDebug.LogErrorOnce($"sync-transmit-{o.NetworkID}",
                        $"BasisSyncedObject '{o.name}' (NetID {o.NetworkID}) transmit failed and was skipped: {ex}",
                        BasisDebug.LogTag.Networking);
                }
            }

            BasisSyncBatchCollector.Flush();
        }

        /// <summary>
        /// Gathers this pass's inputs and kicks the reduction job. Runs on the shared
        /// <see cref="ReductionUpdateInterval"/> cadence, plus immediately whenever the owned set
        /// changed so a freshly-owned object is never left un-reduced for up to an interval.
        /// </summary>
        private static void ScheduleReductionPass(double time)
        {
            if (_owned.Count == 0) return;

            bool ownedChanged = _ownedDirty || _ownedScratch.Count != _owned.Count;
            if (!ownedChanged && time - _lastReductionPassTime < ReductionUpdateInterval) return;

            RefreshOwnedSnapshot();
            _lastReductionPassTime = time;

            // Player poses: read ONCE for the whole pass rather than once per object per player.
            var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            _reductionRawPlayerCount = BasisNetworkPlayers.ReceiverCount;
            int playerCount = 0;
            EnsureManaged(ref _reductionPlayerIds, _reductionRawPlayerCount);
            EnsureNative(ref _reductionPlayerPositions, _reductionRawPlayerCount);
            for (int i = 0; i < _reductionRawPlayerCount; i++)
            {
                var rec = snapshot[i];
                if (rec == null) continue;
                rec.GetLatestNetworkPose(out float3 pp, out _, out _);
                _reductionPlayerPositions[playerCount] = pp;
                _reductionPlayerIds[playerCount] = rec.playerId;
                playerCount++;
            }

            // Object anchors. TryGetReductionPosition is a live transform read, so it stays on the
            // main thread — but it is O(objects), not O(objects x players), and only on pass frames.
            _reductionParticipants.Clear();
            int scratchCount = _ownedScratch.Count;
            EnsureNative(ref _reductionObjectPositions, scratchCount);
            EnsureNative(ref _reductionRadiusSq, scratchCount);
            for (int i = 0; i < scratchCount; i++)
            {
                BasisSyncedObject o = _ownedScratch[i];
                if (o == null || !o.WantsReduction) continue;
                if (!o.TryGetReductionPosition(out Vector3 worldPos))
                {
                    // No spatial channel — the object keeps full rate, as it did before.
                    o.ApplyReduction(false, 0f, null, 0);
                    continue;
                }
                int slot = _reductionParticipants.Count;
                _reductionObjectPositions[slot] = worldPos;
                _reductionRadiusSq[slot] = o.ReductionRadiusSq;
                _reductionParticipants.Add(o);
            }

            _reductionCount = _reductionParticipants.Count;
            if (_reductionCount == 0) return;

            _reductionMasksPerObject = playerCount > 0 ? ((playerCount + 63) / 64) : 1;
            EnsureNative(ref _reductionNearestSq, _reductionCount);
            EnsureNative(ref _reductionMask, _reductionCount * _reductionMasksPerObject);

            var job = new BasisSyncReductionJob
            {
                ObjectPositions = _reductionObjectPositions,
                RelevanceRadiusSq = _reductionRadiusSq,
                PlayerPositions = _reductionPlayerPositions,
                PlayerCount = playerCount,
                MasksPerObject = _reductionMasksPerObject,
                NearestSq = _reductionNearestSq,
                RelevanceMask = _reductionMask,
            };

            _reductionHandle = job.Schedule(_reductionCount, 32);
            _reductionScheduled = true;

            // The complete sits over in TransmitOwned (LateUpdate); without a kick the job would
            // idle in the queue through everything it is meant to overlap.
            JobHandle.ScheduleBatchedJobs();
        }

        /// <summary>Joins the reduction job and scatters its results back onto the participating objects.</summary>
        private static void CompleteReductionPass()
        {
            if (!_reductionScheduled) return;
            _reductionHandle.Complete();
            _reductionScheduled = false;

            for (int i = 0; i < _reductionCount; i++)
            {
                BasisSyncedObject o = _reductionParticipants[i];
                if (o == null) continue;

                // Matches the previous per-object rule: an empty instance reads as "nobody is far",
                // i.e. full send rate, rather than maximum throttle.
                float nearestSq = _reductionRawPlayerCount == 0 ? 0f : _reductionNearestSq[i];

                if (!o.RelevanceCulling)
                {
                    o.ApplyReduction(true, nearestSq, null, 0);
                    continue;
                }

                int count = ExpandRecipients(i);
                o.ApplyReduction(true, nearestSq, _reductionRecipientScratch, count);
            }
        }

        /// <summary>Expands one object's player-slot bitmask into the shared recipient scratch. Returns the count.</summary>
        private static int ExpandRecipients(int objectIndex)
        {
            EnsureManaged(ref _reductionRecipientScratch, _reductionPlayerIds.Length);
            return ExpandRecipientMask(_reductionMask, objectIndex * _reductionMasksPerObject, _reductionMasksPerObject,
                                       _reductionPlayerIds, _reductionRecipientScratch);
        }

        /// <summary>
        /// Turns one object's span of the job's player-slot bitmask into player ids, writing them
        /// into <paramref name="destination"/> and returning how many were written. Bit b of word m
        /// is gathered player slot (m * 64 + b), which indexes <paramref name="playerIds"/>.
        /// Pure — takes everything it reads so it can be exercised directly.
        /// </summary>
        internal static int ExpandRecipientMask(NativeArray<ulong> mask, int maskBase, int masksPerObject,
                                                ushort[] playerIds, ushort[] destination)
        {
            int count = 0;
            for (int m = 0; m < masksPerObject; m++)
            {
                ulong bits = mask[maskBase + m];
                while (bits != 0ul)
                {
                    int bit = math.tzcnt(bits);
                    bits &= bits - 1ul;
                    destination[count++] = playerIds[(m << 6) + bit];
                }
            }
            return count;
        }

        /// <summary>Number of objects in the current owned snapshot — the list TransmitOwned iterates.</summary>
        internal static int OwnedSnapshotCount => _ownedScratch.Count;

        /// <summary>Forces the owned snapshot up to date; normally driven by the transmit/reduction passes.</summary>
        internal static void RefreshOwnedSnapshotForTests() => RefreshOwnedSnapshot();

        private static void EnsureNative<T>(ref NativeArray<T> array, int length) where T : struct
        {
            int want = length < 1 ? 1 : length;
            if (array.IsCreated && array.Length >= want) return;
            if (array.IsCreated) array.Dispose();
            array = new NativeArray<T>(want, Allocator.Persistent);
        }

        private static void EnsureManaged(ref ushort[] array, int length)
        {
            int want = length < 1 ? 1 : length;
            if (array != null && array.Length >= want) return;
            array = new ushort[want];
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
