using System;
using System.Collections.Generic;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// Generic networked value container. Declare typed fields (position/rotation/scale/float/int/ushort)
    /// with the Register* calls in Awake, then on the owner call LocalSet each frame and on every other
    /// client read RemoteGet — values arrive smoothed through a playback buffer with interpolation.
    /// Owner-authoritative (grab via TakeOwnership); only the owner's writes go on the wire.
    /// Optionally BindTransform so a remote transform is driven automatically by the Burst apply job.
    /// </summary>
    public struct BasisSyncGizmoSample
    {
        public bool HasSpatial;
        public Vector3 FromWorld;
        public Vector3 ToWorld;
        public Vector3 AnchorWorld;
        public float InterpT;
        public bool Extrapolating;
        public int BufferDepth;
        public float DesiredDepth;
        public float BytesPerSecond;
        public float PacketsPerSecond;
        public ushort NetworkID;
    }

    public class BasisSyncedObject : BasisNetworkBehaviour, ISerializationCallbackReceiver
    {
        public float SendIntervalSeconds = 0.05f;
        public float KeyframeIntervalSeconds = 0.5f;
        public float ContinuousEpsilon = 1e-4f;
        public float RotationSendThresholdDegrees = 0.5f;
        public bool UseDirectP2P = true;
        public bool ForceP2POnly = false;
        public bool OverrideP2PRate = false;
        public float P2PSendIntervalSeconds = 0.033f;
        public float P2PKeyframeIntervalSeconds = 0.5f;
        public DeliveryMethod Delivery = DeliveryMethod.Unreliable;
        public DeliveryMethod KeyframeDelivery = DeliveryMethod.ReliableOrdered;
        public bool Extrapolate = false;
        public float MaxExtrapolationSeconds = 0.2f;

        /// <summary>
        /// Playback buffer depth target, in send intervals (1-4). The remote holds roughly this many staged
        /// frames ahead of the interpolation window — the dominant, fixed share of perceived latency. 2 (default)
        /// matches the historical smoothing; 1 roughly halves the buffer latency (snappier, less tolerant of
        /// jitter/loss). Re-apply with ApplySyncConfig() if changed at runtime.
        /// </summary>
        [Range(1f, 4f)] public float JitterBufferDepth = 2f;
        public bool UseTeleportThreshold = false;
        public float TeleportThreshold = 3f;
        public bool DistanceReduction = true;
        public bool RelevanceCulling = false;
        public float RelevanceRadius = 50f;
        public bool UseChecksum = true;

        // Stays 0 in anything serialized before this field existed (legacy bundles/mods), so it reliably
        // tells pre-refactor content apart from freshly-authored content regardless of field-initializer behaviour.
        [SerializeField, HideInInspector] private int _serializedVersion;
        protected const int CurrentSerializedVersion = 3;

        private readonly BasisSyncSchema _schema = new BasisSyncSchema();
        private BasisSyncValues _local;
        private BasisSyncValues _lastSent;
        private BasisSyncReceiver _receiver;
        private byte[] _dirtyMask;
        private byte[] _scratch;
        private byte[] _sendBuffer;
        private ushort[] _snapshotRecipient;

        private bool _schemaLocked;
        private bool _buffersReady;
        private bool _isRemoteRegistered;
        private bool _isOwnedRegistered;
        private byte _seq;
        private double _lastSendTime;
        private double _lastKeyframeTime;
        private bool _forceKeyframe = true;
        private float _rotDotThreshold = 0.99999f;
        private int _idleKeyframeBackoff;
        private const int MaxKeyframeBackoffShift = 4;
        private int _idleKeyframesAtCap;
        private const int MaxIdleKeyframesAtCap = 2;
        private bool _lastSendWasIdle;
        private ushort _lastKnownOwnerId;
        private bool _haveKnownOwner;

        // Transmit-side wire metering — the owner's mirror of the receiver's rx counters,
        // windowed over the same 0.5 s so the debug gizmos read comparably on both ends.
        private const double TxRateWindow = 0.5;
        private int _txBytes;
        private int _txPackets;
        private double _txWindowStart;

        /// <summary>Serialized bytes handed to the transport per second while this client owns the object.</summary>
        public float TxBytesPerSecond { get; private set; }
        /// <summary>Packets handed to the transport per second while this client owns the object.</summary>
        public float TxPacketsPerSecond { get; private set; }

        internal BasisSyncSchema Schema => _schema;
        internal BasisSyncReceiver Receiver => _receiver;
        internal int SyncSlot;
        internal int OutContBase;
        internal int OutRotBase;
        internal int OutDiscBase;
        internal bool HasTransformBinding;
        internal Transform BoundTransform;
        internal int BindPosFieldIndex = -1;
        internal int BindRotFieldIndex = -1;
        internal int BindScaleFieldIndex = -1;
        internal bool BindWorldSpace;

        internal void AdvanceReceiver(float dt) => _receiver?.Advance(dt);

        internal bool WantsMainThreadApply;
        internal bool JobApplied;
        internal int TeleportWatchStart;
        internal int TeleportWatchCount;

        internal void DriverApply()
        {
            if (_receiver != null && _receiver.HasData && !IsOwnedLocallyOnClient)
                ApplyInterpolated();
        }

        /// <summary>
        /// Describes how the Burst apply job should drive this object's transform, with indices as this
        /// object's own schema offsets (the driver rebases them into the shared pools). Returning false
        /// leaves the object entirely on the main-thread <see cref="ApplyInterpolated"/> path;
        /// <paramref name="replacesMainThreadApply"/> additionally suppresses the main-thread call when
        /// the binding covers everything it would do (a BindTransform binding does not — both ran before
        /// and still do). Evaluated at every layout rebuild; call
        /// <see cref="BasisSyncDriver.MarkLayoutDirty"/> after changing what it depends on.
        /// </summary>
        internal virtual bool TryGetJobApplyBinding(out BasisSyncApplyBinding binding, out Transform target, out bool replacesMainThreadApply)
        {
            binding = BasisSyncApplyBinding.Empty;
            target = BoundTransform;
            replacesMainThreadApply = false;
            if (!HasTransformBinding || BoundTransform == null) return false;

            if (BindPosFieldIndex >= 0)
            {
                int off = _schema.GetField(BindPosFieldIndex).Offset;
                binding.PosX = off;
                binding.PosY = off + 1;
                binding.PosZ = off + 2;
            }
            if (BindRotFieldIndex >= 0) binding.RotQuat = _schema.GetField(BindRotFieldIndex).Offset;
            if (BindScaleFieldIndex >= 0)
            {
                int off = _schema.GetField(BindScaleFieldIndex).Offset;
                binding.ScaleX = off;
                binding.ScaleY = off + 1;
                binding.ScaleZ = off + 2;
            }
            binding.World = (byte)(BindWorldSpace ? 1 : 0);
            return binding.HasAny;
        }

        /// <summary>Driver-driven (post-interpolation, main thread) apply hook for remote objects that compose their own output.</summary>
        protected virtual void ApplyInterpolated() { }

        /// <summary>
        /// Snapshot of this object's live interpolation state for the debug gizmos
        /// (<see cref="BasisSyncGizmos"/>). Remote, receiving objects only — returns false on the owner.
        /// </summary>
        public bool TryGetSyncGizmoSample(out BasisSyncGizmoSample sample)
        {
            sample = default;
            BasisSyncReceiver r = _receiver;
            if (r == null || !r.HasData || IsOwnedLocallyOnClient) return false;

            sample.NetworkID = NetworkID;
            sample.InterpT = r.InterpTime;
            sample.Extrapolating = sample.InterpT > 1f;
            sample.BufferDepth = r.BufferedFrameCount;
            sample.DesiredDepth = r.DynamicDepth;
            sample.BytesPerSecond = r.BytesPerSecond;
            sample.PacketsPerSecond = r.PacketsPerSecond;
            sample.HasSpatial = TryGetSyncGizmoSpatial(r.CurrentValues, r.NextValues, out Vector3 fromWorld, out Vector3 toWorld);
            sample.FromWorld = fromWorld;
            sample.ToWorld = toWorld;
            sample.AnchorWorld = TryGetSyncWorldPosition(out Vector3 worldPos) ? worldPos : transform.position;
            return true;
        }

        /// <summary>
        /// Snapshot for the debug gizmos while this client owns the object. The owner has no
        /// receive pipeline to visualise, so this carries only the anchor position and the
        /// transmit rates; interpolation fields stay zero.
        /// </summary>
        public bool TryGetOwnedSyncGizmoSample(out BasisSyncGizmoSample sample)
        {
            sample = default;
            if (!IsOwnedLocallyOnClient || !HasNetworkID) return false;

            sample.NetworkID = NetworkID;
            sample.BytesPerSecond = TxBytesPerSecond;
            sample.PacketsPerSecond = TxPacketsPerSecond;
            sample.AnchorWorld = TryGetSyncWorldPosition(out Vector3 worldPos) ? worldPos : transform.position;
            return true;
        }

        /// <summary>Override to expose the from/to keyframe positions (world space) for the sync gizmos. Return false if the object has no spatial channel.</summary>
        protected virtual bool TryGetSyncGizmoSpatial(BasisSyncValues from, BasisSyncValues to, out Vector3 fromWorld, out Vector3 toWorld)
        {
            fromWorld = default;
            toWorld = default;
            return false;
        }

        private List<ushort> _recipientScratch;
        private ushort[] _recipientArray;
        private const double ReductionUpdateInterval = 0.5;
        private double _lastReductionTime;
        private bool _haveReduction;
        private float _cachedNearestSq;
        private ushort[] _cachedRecipients;

        /// <summary>Override to supply the object's world position for distance/relevance reduction. Return false to disable.</summary>
        protected virtual bool TryGetSyncWorldPosition(out Vector3 position)
        {
            position = default;
            return false;
        }

        private void ComputeReduction(Vector3 objPos, out float nearestSq, out ushort[] recipients)
        {
            nearestSq = float.MaxValue;
            recipients = null;

            var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            int count = BasisNetworkPlayers.ReceiverCount;

            bool cull = RelevanceCulling;
            if (cull)
            {
                if (_recipientScratch == null) _recipientScratch = new List<ushort>();
                _recipientScratch.Clear();
            }

            float radiusSq = RelevanceRadius * RelevanceRadius;
            float3 op = objPos;
            for (int i = 0; i < count; i++)
            {
                var rec = snapshot[i];
                if (rec == null) continue;
                rec.GetLatestNetworkPose(out float3 pp, out _, out _);
                float d2 = math.lengthsq(pp - op);
                if (d2 < nearestSq) nearestSq = d2;
                if (cull && d2 <= radiusSq) _recipientScratch.Add(rec.playerId);
            }

            if (count == 0) nearestSq = 0f;

            if (cull)
            {
                int n = _recipientScratch.Count;
                if (_recipientArray == null || _recipientArray.Length != n) _recipientArray = new ushort[n];
                for (int i = 0; i < n; i++) _recipientArray[i] = _recipientScratch[i];
                recipients = _recipientArray;
            }
        }

        // ── Field declaration (call in Awake, before the object is network-ready) ──
        public BasisSyncHandle RegisterPosition() => Add(BasisSyncFieldType.Position);
        public BasisSyncHandle RegisterRotation() => Add(BasisSyncFieldType.Rotation);
        public BasisSyncHandle RegisterRotation(int magnitudeBits, bool interpolate = true)
            => new BasisSyncHandle(_schema.AddRotation(interpolate, magnitudeBits), BasisSyncFieldType.Rotation);
        public BasisSyncHandle RegisterScale() => Add(BasisSyncFieldType.Scale);
        public BasisSyncHandle RegisterFloat(bool interpolate = true, bool quantize = false) => Add(BasisSyncFieldType.Float, interpolate, quantize);
        public BasisSyncHandle RegisterInt() => Add(BasisSyncFieldType.Int);
        public BasisSyncHandle RegisterUShort() => Add(BasisSyncFieldType.UShort);
        public BasisSyncHandle RegisterVector2(bool interpolate = true, bool quantize = false) => Add(BasisSyncFieldType.Vector2, interpolate, quantize);
        public BasisSyncHandle RegisterVector4(bool interpolate = true, bool quantize = false) => Add(BasisSyncFieldType.Vector4, interpolate, quantize);
        public BasisSyncHandle RegisterColor(bool interpolate = true, bool quantize = false) => Add(BasisSyncFieldType.Color, interpolate, quantize);
        public BasisSyncHandle RegisterBool() => Add(BasisSyncFieldType.Bool);
        public BasisSyncHandle RegisterByte() => Add(BasisSyncFieldType.Byte);
        public BasisSyncHandle RegisterUInt() => Add(BasisSyncFieldType.UInt);
        public BasisSyncHandle RegisterAngle(bool interpolate = true, bool quantize = false) => Add(BasisSyncFieldType.Angle, interpolate, quantize);

        // ── Field declaration with explicit per-component compression (Raw / Half / N-bit Ranged) ──
        /// <summary>Declare any field with per-component compression. componentSpecs length should match the type's component count (1 float, 2 Vector2, 3 Position/Scale, 4 Vector4/Color); ignored for discrete/rotation.</summary>
        public BasisSyncHandle Register(BasisSyncFieldType type, bool interpolate, BasisQuantSpec[] componentSpecs)
            => new BasisSyncHandle(_schema.AddField(type, interpolate, componentSpecs), type);

        public BasisSyncHandle RegisterFloat(BasisQuantSpec spec, bool interpolate = true)
            => new BasisSyncHandle(_schema.AddField(BasisSyncFieldType.Float, interpolate, new[] { spec }), BasisSyncFieldType.Float);
        public BasisSyncHandle RegisterAngle(BasisQuantSpec spec, bool interpolate = true)
            => new BasisSyncHandle(_schema.AddField(BasisSyncFieldType.Angle, interpolate, new[] { spec }), BasisSyncFieldType.Angle);

        private BasisSyncHandle Add(BasisSyncFieldType type, bool interpolate = true, bool quantize = false)
        {
            int index = _schema.AddField(type, interpolate, quantize);
            return new BasisSyncHandle(index, type);
        }

        // ── Local writes (owner) ──
        public void LocalSet(BasisSyncHandle h, Vector3 v)
        {
            RequireContinuous(h);
            EnsureBuffers();
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            _local.Cont[f.Offset] = v.x;
            _local.Cont[f.Offset + 1] = v.y;
            _local.Cont[f.Offset + 2] = v.z;
        }

        public void LocalSet(BasisSyncHandle h, Quaternion q)
        {
            Require(h, BasisSyncFieldType.Rotation);
            EnsureBuffers();
            _local.Rot[_schema.GetField(h.FieldIndex).Offset] = new quaternion(q.x, q.y, q.z, q.w);
        }

        public void LocalSet(BasisSyncHandle h, float v)
        {
            if (h.Type != BasisSyncFieldType.Float && h.Type != BasisSyncFieldType.Angle)
                throw new ArgumentException($"BasisSyncHandle is {h.Type}, expected Float or Angle.");
            EnsureBuffers();
            _local.Cont[_schema.GetField(h.FieldIndex).Offset] = v;
        }

        public void LocalSet(BasisSyncHandle h, int v)
        {
            Require(h, BasisSyncFieldType.Int);
            EnsureBuffers();
            _local.Disc[_schema.GetField(h.FieldIndex).Offset] = v;
        }

        public void LocalSet(BasisSyncHandle h, ushort v)
        {
            Require(h, BasisSyncFieldType.UShort);
            EnsureBuffers();
            _local.Disc[_schema.GetField(h.FieldIndex).Offset] = v;
        }

        public void LocalSet(BasisSyncHandle h, Vector2 v)
        {
            Require(h, BasisSyncFieldType.Vector2);
            EnsureBuffers();
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            _local.Cont[f.Offset] = v.x;
            _local.Cont[f.Offset + 1] = v.y;
        }

        public void LocalSet(BasisSyncHandle h, Vector4 v)
        {
            Require(h, BasisSyncFieldType.Vector4);
            EnsureBuffers();
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            _local.Cont[f.Offset] = v.x;
            _local.Cont[f.Offset + 1] = v.y;
            _local.Cont[f.Offset + 2] = v.z;
            _local.Cont[f.Offset + 3] = v.w;
        }

        public void LocalSet(BasisSyncHandle h, Color c)
        {
            Require(h, BasisSyncFieldType.Color);
            EnsureBuffers();
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            _local.Cont[f.Offset] = c.r;
            _local.Cont[f.Offset + 1] = c.g;
            _local.Cont[f.Offset + 2] = c.b;
            _local.Cont[f.Offset + 3] = c.a;
        }

        public void LocalSet(BasisSyncHandle h, bool value)
        {
            Require(h, BasisSyncFieldType.Bool);
            EnsureBuffers();
            _local.Disc[_schema.GetField(h.FieldIndex).Offset] = value ? 1 : 0;
        }

        public void LocalSet(BasisSyncHandle h, byte value)
        {
            Require(h, BasisSyncFieldType.Byte);
            EnsureBuffers();
            _local.Disc[_schema.GetField(h.FieldIndex).Offset] = value;
        }

        public void LocalSet(BasisSyncHandle h, uint value)
        {
            Require(h, BasisSyncFieldType.UInt);
            EnsureBuffers();
            _local.Disc[_schema.GetField(h.FieldIndex).Offset] = (int)value;
        }

        // ── Remote reads (any client; returns owner's authoritative value on the owner) ──
        public Vector3 GetVector3(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null)
                return new Vector3(_local.Cont[f.Offset], _local.Cont[f.Offset + 1], _local.Cont[f.Offset + 2]);
            return BasisSyncDriver.ReadFloat3(OutContBase + f.Offset);
        }

        public Quaternion GetQuaternion(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            quaternion q = (IsOwnedLocallyOnClient && _local != null) ? _local.Rot[f.Offset] : BasisSyncDriver.ReadRot(OutRotBase + f.Offset);
            return new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
        }

        public float GetFloat(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null) return _local.Cont[f.Offset];
            return BasisSyncDriver.ReadCont(OutContBase + f.Offset);
        }

        public int GetInt(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null) return _local.Disc[f.Offset];
            return BasisSyncDriver.ReadDisc(OutDiscBase + f.Offset);
        }

        public ushort GetUShort(BasisSyncHandle h) => (ushort)GetInt(h);

        public Vector2 GetVector2(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null)
                return new Vector2(_local.Cont[f.Offset], _local.Cont[f.Offset + 1]);
            int b = OutContBase + f.Offset;
            return new Vector2(BasisSyncDriver.ReadCont(b), BasisSyncDriver.ReadCont(b + 1));
        }

        public Vector4 GetVector4(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null)
                return new Vector4(_local.Cont[f.Offset], _local.Cont[f.Offset + 1], _local.Cont[f.Offset + 2], _local.Cont[f.Offset + 3]);
            int b = OutContBase + f.Offset;
            return new Vector4(BasisSyncDriver.ReadCont(b), BasisSyncDriver.ReadCont(b + 1), BasisSyncDriver.ReadCont(b + 2), BasisSyncDriver.ReadCont(b + 3));
        }

        public Color GetColor(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null)
                return new Color(_local.Cont[f.Offset], _local.Cont[f.Offset + 1], _local.Cont[f.Offset + 2], _local.Cont[f.Offset + 3]);
            int b = OutContBase + f.Offset;
            return new Color(BasisSyncDriver.ReadCont(b), BasisSyncDriver.ReadCont(b + 1), BasisSyncDriver.ReadCont(b + 2), BasisSyncDriver.ReadCont(b + 3));
        }

        public bool GetBool(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null) return _local.Disc[f.Offset] != 0;
            return BasisSyncDriver.ReadDisc(OutDiscBase + f.Offset) != 0;
        }

        public byte GetByte(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null) return (byte)_local.Disc[f.Offset];
            return (byte)BasisSyncDriver.ReadDisc(OutDiscBase + f.Offset);
        }

        public uint GetUInt(BasisSyncHandle h)
        {
            BasisSyncField f = _schema.GetField(h.FieldIndex);
            if (IsOwnedLocallyOnClient && _local != null) return (uint)_local.Disc[f.Offset];
            return (uint)BasisSyncDriver.ReadDisc(OutDiscBase + f.Offset);
        }

        public float GetAngle(BasisSyncHandle h) => GetFloat(h);

        // ── Transform binding (remote transforms only; owner keeps authority over its own) ──
        public void BindTransform(Transform target, BasisSyncHandle position, BasisSyncHandle rotation, BasisSyncHandle scale, bool worldSpace = false)
        {
            BoundTransform = target;
            HasTransformBinding = target != null;
            BindWorldSpace = worldSpace;
            BindPosFieldIndex = position.IsValid ? position.FieldIndex : -1;
            BindRotFieldIndex = rotation.IsValid ? rotation.FieldIndex : -1;
            BindScaleFieldIndex = scale.IsValid ? scale.FieldIndex : -1;
            BasisSyncDriver.MarkLayoutDirty();
        }

        /// <summary>Force the next outgoing packet to be a full keyframe (e.g. after a teleport).</summary>
        public void ForceKeyframe() => _forceKeyframe = true;

        /// <summary>
        /// Remote copies: collapse the interpolation buffer to the freshest received frame on the next tick,
        /// skipping interpolation. Call after a discontinuity in the meaning of the synced values (teleport,
        /// or a pickup toggling between world and hand-relative encoding) so the copy snaps instead of sliding.
        /// </summary>
        public void SnapReceiver() => _receiver?.ForceSnap();

        /// <summary>Owner hook fired right before serialization; push live source values into LocalSet here.</summary>
        protected virtual void OnBeforeTransmit() { }

        /// <summary>
        /// Override to skip distance-based send-rate reduction for this tick even when DistanceReduction is on
        /// — e.g. while a pickup is actively held, so the thing the player is manipulating and watching stays
        /// full-rate instead of being throttled (and then buffered) by its distance to the nearest viewer.
        /// Relevance culling, if enabled, still applies.
        /// </summary>
        protected virtual bool ShouldSuppressDistanceReduction() => false;

        public override void OnNetworkReady()
        {
            EnsureBuffers();
            RefreshRole();
        }

        public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
        {
            // A new owner is a new packet stream: its sequence numbering has no relation to the previous
            // owner's, and everything buffered belongs to the old stream. Without this reset the sequence
            // high-water-mark silently drops the new owner's packets (up to ~127 of them) about half the
            // time, and playback first has to drain the previous owner's stale frames — together the
            // "grabbed pickup freezes, then rushes to catch up" bug. Resetting re-seeds from the new
            // owner's forced keyframe instead. Owner-id tracking keeps re-fires (e.g. late owner-player
            // resolution) from resetting a healthy stream.
            bool ownerChanged = !_haveKnownOwner || CurrentOwnerId != _lastKnownOwnerId;
            _haveKnownOwner = true;
            _lastKnownOwnerId = CurrentOwnerId;
            if (ownerChanged && !IsOwnedLocallyOnClient) _receiver?.Reset();
            RefreshRole();
        }

        public override void OnServerOwnershipDestroyed()
        {
            _haveKnownOwner = false;
            RefreshRole();
        }

        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (IsOwnedLocallyOnClient)
            {
                _forceKeyframe = true;
                return;
            }

            if (player == null || _receiver == null || !_receiver.HasData) return;
            if (HasPresentOwner()) return;
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) && localId == player.playerId) return;
            SendStateSnapshotTo(player.playerId);
        }

        private bool HasPresentOwner()
            => BasisNetworkPlayers.OwnershipPairing.TryGetValue(clientIdentifier, out ushort ownerId)
               && BasisNetworkPlayers.GetPlayerById(ownerId, out _);

        private void SendStateSnapshotTo(ushort playerId)
        {
            if (!HasNetworkID) return;
            EnsureBuffers();
            OnBeforeTransmit();

            unchecked { _seq++; }
            ushort intervalMs = (ushort)math.clamp((int)math.round(SendIntervalSeconds * 1000.0), 1, 65535);
            int len = BasisSyncCodec.Serialize(_schema, _local, true, _dirtyMask, _seq, intervalMs, _scratch, UseChecksum);

            if (_sendBuffer == null || _sendBuffer.Length != len) _sendBuffer = new byte[len];
            Array.Copy(_scratch, 0, _sendBuffer, 0, len);
            if (_snapshotRecipient == null) _snapshotRecipient = new ushort[1];
            _snapshotRecipient[0] = playerId;
            SendCustomNetworkEvent(_sendBuffer, KeyframeDelivery, _snapshotRecipient);
        }

        public override void OnNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (IsOwnedLocallyOnClient) return;
            EnsureBuffers();
            _receiver.OnPacket(buffer, buffer != null ? buffer.Length : 0);
        }

        public override void OnDirectNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (IsOwnedLocallyOnClient) return;
            EnsureBuffers();
            _receiver.OnPacket(buffer, buffer != null ? buffer.Length : 0);
        }

        public override void OnDestroy()
        {
            if (_isRemoteRegistered) { BasisSyncDriver.UnregisterRemote(this); _isRemoteRegistered = false; }
            if (_isOwnedRegistered) { BasisSyncDriver.UnregisterOwned(this); _isOwnedRegistered = false; }
            base.OnDestroy();
        }

        internal void TransmitIfDue(double time)
        {
            if (!IsOwnedLocallyOnClient || !HasNetworkID) return;
            EnsureBuffers();
            OnBeforeTransmit();

            // Runs before the send-gating below so an idle stretch still closes the window
            // and the published rates decay to zero instead of freezing at the last burst.
            if (_txWindowStart <= 0.0)
            {
                _txWindowStart = time;
            }
            else if (time - _txWindowStart >= TxRateWindow)
            {
                float inv = (float)(1.0 / (time - _txWindowStart));
                TxBytesPerSecond = _txBytes * inv;
                TxPacketsPerSecond = _txPackets * inv;
                _txBytes = 0;
                _txPackets = 0;
                _txWindowStart = time;
            }

            float baseInterval = SendIntervalSeconds;
            float keyframeInterval = KeyframeIntervalSeconds;
            if (UseDirectP2P && OverrideP2PRate && BasisP2PManager.HasAnyConnectedSession())
            {
                baseInterval = P2PSendIntervalSeconds;
                keyframeInterval = P2PKeyframeIntervalSeconds;
            }

            float effectiveInterval = baseInterval;
            ushort[] recipients = null;
            if (DistanceReduction || RelevanceCulling)
            {
                if (time - _lastReductionTime >= ReductionUpdateInterval)
                {
                    _lastReductionTime = time;
                    _haveReduction = TryGetSyncWorldPosition(out Vector3 worldPos);
                    if (_haveReduction) ComputeReduction(worldPos, out _cachedNearestSq, out _cachedRecipients);
                }
                if (_haveReduction)
                {
                    recipients = RelevanceCulling ? _cachedRecipients : null;
                    if (RelevanceCulling && recipients != null && recipients.Length == 0) return;
                    if (DistanceReduction && !ShouldSuppressDistanceReduction())
                    {
                        var meta = BasisNetworkManagement.ServerMetaDataMessage;
                        if (meta.SlowestSendRate > 0f)
                        {
                            float scaled = baseInterval * (meta.BaseMultiplier + _cachedNearestSq * meta.IncreaseRate);
                            effectiveInterval = Mathf.Clamp(scaled, baseInterval, meta.SlowestSendRate);
                        }
                    }
                }
            }

            bool intervalElapsed = _lastSendTime <= 0 || (time - _lastSendTime) >= effectiveInterval;
            if (!intervalElapsed) return;

            int dirtyBytes = _schema.DirtyMaskBytes;
            for (int i = 0; i < dirtyBytes; i++) _dirtyMask[i] = 0;

            bool anyChange = false;
            bool discreteChange = false;
            int fieldCount = _schema.FieldCount;
            for (int fi = 0; fi < fieldCount; fi++)
            {
                if (!FieldChanged(fi)) continue;
                _dirtyMask[fi >> 3] |= (byte)(1 << (fi & 7));
                anyChange = true;
                if (_schema.GetField(fi).Pool == BasisSyncPool.Discrete) discreteChange = true;
            }

            double effectiveKeyframe = KeyframeBackoffInterval(keyframeInterval, _idleKeyframeBackoff, MaxKeyframeBackoffShift);
            bool periodicDue = (time - _lastKeyframeTime) >= effectiveKeyframe
                && (anyChange || !IdleKeyframesExhausted(_idleKeyframeBackoff, MaxKeyframeBackoffShift, _idleKeyframesAtCap, MaxIdleKeyframesAtCap));
            bool keyframe = _forceKeyframe || _lastSendTime <= 0 || periodicDue;

            if (discreteChange) keyframe = true;
            if (!keyframe && !anyChange) return;
            if (anyChange) { _idleKeyframeBackoff = 0; _idleKeyframesAtCap = 0; }

            double elapsed = _lastSendTime > 0 ? StampInterval(time - _lastSendTime, effectiveInterval, _lastSendWasIdle) : baseInterval;
            ushort intervalMs = (ushort)math.clamp((int)math.round(elapsed * 1000.0), 1, 65535);

            _lastSendTime = time;
            unchecked { _seq++; }

            int len = BasisSyncCodec.Serialize(_schema, _local, keyframe, _dirtyMask, _seq, intervalMs, _scratch, UseChecksum);
            _txBytes += len;
            _txPackets++;

            DeliveryMethod dm = keyframe ? KeyframeDelivery : Delivery;
            if (!BasisSyncBatchCollector.TryEnqueue(NetworkID, _scratch, len, dm, recipients, UseDirectP2P))
            {
                if (_sendBuffer == null || _sendBuffer.Length != len) _sendBuffer = new byte[len];
                Array.Copy(_scratch, 0, _sendBuffer, 0, len);
                if (UseDirectP2P) SendCustomNetworkEventDirect(_sendBuffer, dm, recipients, !ForceP2POnly);
                else SendCustomNetworkEvent(_sendBuffer, dm, recipients);
            }

            _lastSent.CopyFrom(_local);
            _lastSendWasIdle = !anyChange;
            if (keyframe)
            {
                _lastKeyframeTime = time;
                _forceKeyframe = false;
                if (!anyChange)
                {
                    if (_idleKeyframeBackoff < MaxKeyframeBackoffShift) _idleKeyframeBackoff++;
                    else if (_idleKeyframesAtCap < MaxIdleKeyframesAtCap) _idleKeyframesAtCap++;
                }
            }
        }

        /// <summary>
        /// True once an idle owner has delivered the whole backoff ladder plus <paramref name="maxAtCap"/>
        /// keyframes at the capped interval. Keyframes are reliable, so remotes hold the converged state
        /// without further re-sends; late joiners are covered by OnPlayerJoined (forced keyframe/snapshot)
        /// and any change resets both counters.
        /// </summary>
        public static bool IdleKeyframesExhausted(int idleCount, int maxShift, int atCapCount, int maxAtCap)
            => idleCount >= maxShift && atCapCount >= maxAtCap;

        /// <summary>
        /// Keyframe interval stretched by an idle backoff: after <paramref name="idleCount"/> consecutive keyframes
        /// with nothing changed, the interval is doubled each step (capped at 2^<paramref name="maxShift"/>×). A
        /// late-joiner still gets an immediate keyframe (OnPlayerJoined forces one) and any change resets the
        /// backoff, so recovery cadence is unchanged for moving objects — only idle objects stop re-sending.
        /// </summary>
        public static double KeyframeBackoffInterval(double baseInterval, int idleCount, int maxShift)
        {
            int shift = idleCount < 0 ? 0 : (idleCount > maxShift ? maxShift : idleCount);
            return baseInterval * (1 << shift);
        }

        /// <summary>
        /// Interval stamp for the next outgoing packet. While streaming it's the real elapsed time (that's the
        /// motion's true pacing, including distance-reduced cadences), but a quiet stretch — the last send was
        /// an unchanged idle keyframe, or the gap dwarfs the current cadence — is dead time, not motion. Those
        /// stamp the cadence instead, so remotes step across the gap rather than replaying it in real time
        /// (the "grabbed pickup lags for seconds after sitting idle" bug).
        /// </summary>
        public static double StampInterval(double elapsed, double effectiveInterval, bool lastSendWasIdle)
        {
            if (lastSendWasIdle || elapsed > effectiveInterval * 8.0) return effectiveInterval;
            return elapsed;
        }

        private bool FieldChanged(int fi)
        {
            BasisSyncField f = _schema.GetField(fi);
            switch (f.Pool)
            {
                case BasisSyncPool.Continuous:
                    for (int c = 0; c < f.ContComponents; c++)
                    {
                        if (math.abs(_local.Cont[f.Offset + c] - _lastSent.Cont[f.Offset + c]) > ContinuousEpsilon) return true;
                    }
                    return false;
                case BasisSyncPool.Rotation:
                    return math.abs(math.dot(_local.Rot[f.Offset].value, _lastSent.Rot[f.Offset].value)) < _rotDotThreshold;
                case BasisSyncPool.Discrete:
                    return _local.Disc[f.Offset] != _lastSent.Disc[f.Offset];
            }
            return false;
        }

        private void RefreshRole()
        {
            if (!HasNetworkID) return;
            EnsureBuffers();

            if (IsOwnedLocallyOnClient)
            {
                if (_isRemoteRegistered) { BasisSyncDriver.UnregisterRemote(this); _isRemoteRegistered = false; _receiver.Reset(); }
                if (!_isOwnedRegistered) { BasisSyncDriver.RegisterOwned(this); _isOwnedRegistered = true; }
                _forceKeyframe = true;
                // Stale from any previous tenure as owner — left alone, the first packet after re-grabbing
                // would stamp the whole ownerless gap (up to 65.5 s) as its interval and remotes would
                // interpolate across it.
                _lastSendTime = 0;
                _idleKeyframeBackoff = 0;
                _idleKeyframesAtCap = 0;
                _lastSendWasIdle = false;
                _txBytes = 0;
                _txPackets = 0;
                _txWindowStart = 0.0;
                TxBytesPerSecond = 0f;
                TxPacketsPerSecond = 0f;
            }
            else
            {
                if (_isOwnedRegistered) { BasisSyncDriver.UnregisterOwned(this); _isOwnedRegistered = false; }
                if (!_isRemoteRegistered) { BasisSyncDriver.RegisterRemote(this); _isRemoteRegistered = true; }
            }
        }

        private void EnsureBuffers()
        {
            if (_buffersReady) return;
            if (!_schemaLocked) { _schema.Lock(); _schemaLocked = true; }
            _local = new BasisSyncValues(); _local.Allocate(_schema);
            _lastSent = new BasisSyncValues(); _lastSent.Allocate(_schema);
            _dirtyMask = new byte[_schema.DirtyMaskBytes < 1 ? 1 : _schema.DirtyMaskBytes];
            _scratch = new byte[BasisSyncCodec.MaxSerializedSize(_schema)];
            _receiver = new BasisSyncReceiver(_schema);
            ApplySyncConfig();
            _buffersReady = true;
        }

        /// <summary>Re-pushes extrapolation/teleport settings into the receiver. Call after changing them at runtime.</summary>
        public void ApplySyncConfig()
        {
            _rotDotThreshold = Mathf.Cos(Mathf.Deg2Rad * Mathf.Max(0f, RotationSendThresholdDegrees) * 0.5f);
            _receiver?.Configure(Extrapolate, MaxExtrapolationSeconds, UseTeleportThreshold, TeleportThreshold * TeleportThreshold, TeleportWatchStart, TeleportWatchCount, UseChecksum, Mathf.Clamp(JitterBufferDepth, 1f, 4f));
        }

        // ── Serialized-data migration (keeps legacy bundle / mod content working after a refactor) ──
        public void OnBeforeSerialize()
        {
            // Freshly-authored content gets stamped on its first serialize so it's never treated as legacy.
            if (_serializedVersion == 0) _serializedVersion = CurrentSerializedVersion;
        }

        public void OnAfterDeserialize()
        {
            if (_serializedVersion < CurrentSerializedVersion)
            {
                MigrateSerialized(_serializedVersion);
                _serializedVersion = CurrentSerializedVersion;
            }
        }

        /// <summary>
        /// Convert content serialized before a field existed. fromVersion 0 = pre-sync-system content (e.g. a
        /// legacy pickup/vehicle from a mod bundle that never had these fields). Runs at deserialize, before
        /// Awake, so the schema is built from the converted values. Override + call base for subclass fields.
        /// </summary>
        protected virtual void MigrateSerialized(int fromVersion)
        {
            if (fromVersion < 1)
            {
                if (SendIntervalSeconds <= 0f) SendIntervalSeconds = 0.05f;
                if (KeyframeIntervalSeconds <= 0f) KeyframeIntervalSeconds = 0.5f;
                if (P2PSendIntervalSeconds <= 0f) P2PSendIntervalSeconds = 0.033f;
                if (P2PKeyframeIntervalSeconds <= 0f) P2PKeyframeIntervalSeconds = 0.5f;
                if (MaxExtrapolationSeconds <= 0f) MaxExtrapolationSeconds = 0.2f;
                if (RelevanceRadius <= 0f) RelevanceRadius = 50f;
                if (ContinuousEpsilon <= 0f) ContinuousEpsilon = 1e-4f;
                UseDirectP2P = true;
                DistanceReduction = true;
                Delivery = DeliveryMethod.Unreliable;
                KeyframeDelivery = DeliveryMethod.ReliableOrdered;
            }

            if (fromVersion < 2)
            {
                UseChecksum = true;
            }

            if (fromVersion < 3)
            {
                if (RotationSendThresholdDegrees <= 0f) RotationSendThresholdDegrees = 0.5f;
            }
        }

        private void Require(BasisSyncHandle h, BasisSyncFieldType expected)
        {
            if (h.Type != expected) throw new ArgumentException($"BasisSyncHandle is {h.Type}, expected {expected}.");
        }

        private void RequireContinuous(BasisSyncHandle h)
        {
            if (h.Type != BasisSyncFieldType.Position && h.Type != BasisSyncFieldType.Scale)
                throw new ArgumentException($"BasisSyncHandle is {h.Type}, expected Position or Scale for a Vector3.");
        }
    }
}
