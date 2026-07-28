using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync
{
    public enum BasisTransformAxisMode : byte
    {
        Inherit = 0,
        Raw = 1,
        Half = 2,
        Ranged = 3,
    }

    /// <summary>How a synced transform encodes orientation on the wire.</summary>
    public enum BasisRotationSyncMode : byte
    {
        SmallestThree = 0,
        Euler = 1,
        QuaternionHalf = 2,
        QuaternionRaw = 3,
    }

    /// <summary>Per-axis wire compression for a synced transform component.</summary>
    [System.Serializable]
    public struct BasisTransformAxisCompression
    {
        public BasisTransformAxisMode Mode;
        public float Min;
        public float Max;
        [Range(1, 31)] public int Bits;

        public static BasisTransformAxisCompression PositionDefault => WithRange(-1024f, 1024f, 16);
        public static BasisTransformAxisCompression RotationDefault => WithRange(0f, 360f, 16);
        public static BasisTransformAxisCompression ScaleDefault => WithRange(0f, 64f, 16);

        private static BasisTransformAxisCompression WithRange(float min, float max, int bits) =>
            new BasisTransformAxisCompression { Mode = BasisTransformAxisMode.Inherit, Min = min, Max = max, Bits = bits };

        public BasisQuantSpec ToSpec()
        {
            switch (Mode)
            {
                case BasisTransformAxisMode.Raw: return BasisQuantSpec.Raw;
                case BasisTransformAxisMode.Half: return BasisQuantSpec.Half;
                case BasisTransformAxisMode.Ranged: return BasisQuantSpec.Ranged(Min, Max, Bits < 1 ? 1 : Bits);
                default: return BasisQuantSpec.Raw;
            }
        }
    }

    /// <summary>
    /// Drop-in transform sync built on <see cref="BasisSyncedObject"/>. The owner streams the enabled
    /// axes; every other client's copy is interpolated and composed each frame — off the main thread by
    /// the Burst apply job unless the configuration needs it (RelativeTo, or a subclass overriding
    /// <see cref="ApplyInterpolated"/>). Per-axis toggles let
    /// you sync only what moves (e.g. a door = rotation Y only in Euler mode), the rotation mode picks the
    /// orientation encoding (smallest-three / quaternion / Euler), uniform scale streams one value to all axes,
    /// per-axis compression (Half / Ranged) shrinks the float cost, and teleport-threshold tunes late packets.
    ///
    /// For custom values use the code API on <see cref="BasisSyncedObject"/> directly
    /// (RegisterFloat / RegisterColor / RegisterUShort / ... then LocalSet / RemoteGet).
    /// </summary>
    public class BasisSyncedTransform : BasisSyncedObject
    {
        public Transform Target;

        public bool SyncPosition = true;
        public bool PositionX = true;
        public bool PositionY = true;
        public bool PositionZ = true;

        public bool SyncRotation = true;
        public BasisRotationSyncMode RotationMode = BasisRotationSyncMode.SmallestThree;
        [UnityEngine.Range(6, 16)] public int SmallestThreeBits = 9;
        public bool RotationX = true;
        public bool RotationY = true;
        public bool RotationZ = true;

        public bool SyncScale = false;
        public bool ScaleUniform = false;
        public bool ScaleX = true;
        public bool ScaleY = true;
        public bool ScaleZ = true;

        public bool WorldSpace = false;
        public Transform RelativeTo;

        public bool InterpolatePosition = true;
        public bool InterpolateRotation = true;
        public bool InterpolateScale = true;

        public BasisTransformAxisCompression PosCompX = BasisTransformAxisCompression.PositionDefault;
        public BasisTransformAxisCompression PosCompY = BasisTransformAxisCompression.PositionDefault;
        public BasisTransformAxisCompression PosCompZ = BasisTransformAxisCompression.PositionDefault;
        public BasisTransformAxisCompression RotCompX = BasisTransformAxisCompression.RotationDefault;
        public BasisTransformAxisCompression RotCompY = BasisTransformAxisCompression.RotationDefault;
        public BasisTransformAxisCompression RotCompZ = BasisTransformAxisCompression.RotationDefault;
        public BasisTransformAxisCompression ScaleCompX = BasisTransformAxisCompression.ScaleDefault;
        public BasisTransformAxisCompression ScaleCompY = BasisTransformAxisCompression.ScaleDefault;
        public BasisTransformAxisCompression ScaleCompZ = BasisTransformAxisCompression.ScaleDefault;

        private BasisSyncHandle _posX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _posY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _posZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQuat = BasisSyncHandle.Invalid;
        private BasisSyncHandle _eulerX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _eulerY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _eulerZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQx = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQy = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQz = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQw = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleUniform = BasisSyncHandle.Invalid;
        private bool _rotEuler;
        private Vector3 _heldEuler;
        private bool _hasSyncedPosRot;
        private bool _hasSyncedScale;
        private bool _needsLivePose;
        private bool _needsLiveScale;

        /// <summary>True when at least one scale channel is synced — when false, ComposeSyncedPose leaves scale at Vector3.one and it must not be applied.</summary>
        protected bool HasSyncedScale => _hasSyncedScale;

        private void Reset()
        {
            Target = transform;
        }

        protected virtual void Awake()
        {
            if (Target == null) Target = transform;
            WantsMainThreadApply = true;

            int posAxes = 0;
            if (SyncPosition)
            {
                if (PositionX) { _posX = RegisterFloat(PosCompX.ToSpec(), InterpolatePosition); posAxes++; }
                if (PositionY) { _posY = RegisterFloat(PosCompY.ToSpec(), InterpolatePosition); posAxes++; }
                if (PositionZ) { _posZ = RegisterFloat(PosCompZ.ToSpec(), InterpolatePosition); posAxes++; }
            }

            if (SyncRotation)
            {
                switch (RotationMode)
                {
                    case BasisRotationSyncMode.Euler:
                        if (RotationX || RotationY || RotationZ)
                        {
                            _rotEuler = true;
                            _heldEuler = RelativeTo != null
                                ? (Quaternion.Inverse(RelativeTo.rotation) * Target.rotation).eulerAngles
                                : WorldSpace ? Target.eulerAngles : Target.localEulerAngles;
                            if (RotationX) _eulerX = RegisterAngle(RotCompX.ToSpec(), InterpolateRotation);
                            if (RotationY) _eulerY = RegisterAngle(RotCompY.ToSpec(), InterpolateRotation);
                            if (RotationZ) _eulerZ = RegisterAngle(RotCompZ.ToSpec(), InterpolateRotation);
                        }
                        break;
                    case BasisRotationSyncMode.QuaternionHalf:
                    case BasisRotationSyncMode.QuaternionRaw:
                    {
                        BasisQuantSpec spec = RotationMode == BasisRotationSyncMode.QuaternionHalf ? BasisQuantSpec.Half : BasisQuantSpec.Raw;
                        _rotQx = RegisterFloat(spec, InterpolateRotation);
                        _rotQy = RegisterFloat(spec, InterpolateRotation);
                        _rotQz = RegisterFloat(spec, InterpolateRotation);
                        _rotQw = RegisterFloat(spec, InterpolateRotation);
                        break;
                    }
                    default:
                        _rotQuat = RegisterRotation(SmallestThreeBits, InterpolateRotation);
                        break;
                }
            }

            if (SyncScale)
            {
                if (ScaleUniform)
                {
                    _scaleUniform = RegisterFloat(ScaleCompX.ToSpec(), InterpolateScale);
                }
                else
                {
                    if (ScaleX) _scaleX = RegisterFloat(ScaleCompX.ToSpec(), InterpolateScale);
                    if (ScaleY) _scaleY = RegisterFloat(ScaleCompY.ToSpec(), InterpolateScale);
                    if (ScaleZ) _scaleZ = RegisterFloat(ScaleCompZ.ToSpec(), InterpolateScale);
                }
            }

            // Position axes are registered first, so they occupy the start of the continuous range —
            // that's the window the teleport threshold watches.
            TeleportWatchStart = 0;
            TeleportWatchCount = posAxes;

            bool hasSyncedRot = _rotQuat.IsValid || _rotQw.IsValid || _rotEuler;
            _hasSyncedPosRot = _posX.IsValid || _posY.IsValid || _posZ.IsValid || hasSyncedRot;
            _hasSyncedScale = _scaleUniform.IsValid || _scaleX.IsValid || _scaleY.IsValid || _scaleZ.IsValid;
            _needsLivePose = !(_posX.IsValid && _posY.IsValid && _posZ.IsValid) || !hasSyncedRot;
            _needsLiveScale = _hasSyncedScale && !_scaleUniform.IsValid && !(_scaleX.IsValid && _scaleY.IsValid && _scaleZ.IsValid);
        }

        protected override void OnBeforeTransmit()
        {
            if (Target == null) return;

            Vector3 p;
            Quaternion r;
            if (RelativeTo != null)
            {
                Target.GetPositionAndRotation(out p, out r);
                p = RelativeTo.InverseTransformPoint(p);
                r = Quaternion.Inverse(RelativeTo.rotation) * r;
            }
            else if (WorldSpace) Target.GetPositionAndRotation(out p, out r);
            else Target.GetLocalPositionAndRotation(out p, out r);

            if (_posX.IsValid) LocalSet(_posX, p.x);
            if (_posY.IsValid) LocalSet(_posY, p.y);
            if (_posZ.IsValid) LocalSet(_posZ, p.z);

            if (_rotQuat.IsValid)
            {
                LocalSet(_rotQuat, r);
            }
            else if (_rotQw.IsValid)
            {
                LocalSet(_rotQx, r.x);
                LocalSet(_rotQy, r.y);
                LocalSet(_rotQz, r.z);
                LocalSet(_rotQw, r.w);
            }
            else if (_rotEuler)
            {
                Vector3 e = r.eulerAngles;
                if (_eulerX.IsValid) LocalSet(_eulerX, e.x);
                if (_eulerY.IsValid) LocalSet(_eulerY, e.y);
                if (_eulerZ.IsValid) LocalSet(_eulerZ, e.z);
            }

            if (_scaleUniform.IsValid)
            {
                LocalSet(_scaleUniform, Target.localScale.x);
            }
            else if (_scaleX.IsValid || _scaleY.IsValid || _scaleZ.IsValid)
            {
                Vector3 s = Target.localScale;
                if (_scaleX.IsValid) LocalSet(_scaleX, s.x);
                if (_scaleY.IsValid) LocalSet(_scaleY, s.y);
                if (_scaleZ.IsValid) LocalSet(_scaleZ, s.z);
            }
        }

        /// <summary>
        /// Writes an arbitrary pose into the enabled position / rotation / scale channels (owner side),
        /// honouring the configured axis toggles, rotation mode and scale mode. Exposed so a subclass can
        /// stream a remapped pose — e.g. a hand-relative offset — through the same channels that
        /// <see cref="OnBeforeTransmit"/> normally fills from the Target. Values are written verbatim (no
        /// WorldSpace / RelativeTo handling); <see cref="ComposeSyncedPose"/> returns them unchanged when
        /// every position axis is synced, which is the decode side a subclass pairs with this.
        /// </summary>
        protected void EncodePose(Vector3 p, Quaternion r, Vector3 scale)
        {
            if (_posX.IsValid) LocalSet(_posX, p.x);
            if (_posY.IsValid) LocalSet(_posY, p.y);
            if (_posZ.IsValid) LocalSet(_posZ, p.z);

            if (_rotQuat.IsValid)
            {
                LocalSet(_rotQuat, r);
            }
            else if (_rotQw.IsValid)
            {
                LocalSet(_rotQx, r.x);
                LocalSet(_rotQy, r.y);
                LocalSet(_rotQz, r.z);
                LocalSet(_rotQw, r.w);
            }
            else if (_rotEuler)
            {
                Vector3 e = r.eulerAngles;
                if (_eulerX.IsValid) LocalSet(_eulerX, e.x);
                if (_eulerY.IsValid) LocalSet(_eulerY, e.y);
                if (_eulerZ.IsValid) LocalSet(_eulerZ, e.z);
            }

            if (_scaleUniform.IsValid)
            {
                LocalSet(_scaleUniform, scale.x);
            }
            else
            {
                if (_scaleX.IsValid) LocalSet(_scaleX, scale.x);
                if (_scaleY.IsValid) LocalSet(_scaleY, scale.y);
                if (_scaleZ.IsValid) LocalSet(_scaleZ, scale.z);
            }
        }

        /// <summary>
        /// Hands the whole apply over to the Burst transform job when the configuration is expressible
        /// there — everything except RelativeTo (needs another transform's live pose) and subclasses
        /// that override <see cref="ApplyInterpolated"/> (pickups, vehicles: custom main-thread logic).
        /// Those keep the main-thread path below (plus any explicit BindTransform binding, via base).
        /// </summary>
        internal override bool TryGetJobApplyBinding(out BasisSyncApplyBinding binding, out Transform target, out bool replacesMainThreadApply)
        {
            if (Target == null || RelativeTo != null || HasCustomApply())
                return base.TryGetJobApplyBinding(out binding, out target, out replacesMainThreadApply);

            binding = BasisSyncApplyBinding.Empty;
            target = Target;
            replacesMainThreadApply = true;

            if (_posX.IsValid) binding.PosX = ContOffset(_posX);
            if (_posY.IsValid) binding.PosY = ContOffset(_posY);
            if (_posZ.IsValid) binding.PosZ = ContOffset(_posZ);

            if (_rotQuat.IsValid)
            {
                binding.RotQuat = Schema.GetField(_rotQuat.FieldIndex).Offset;
            }
            else if (_rotQw.IsValid)
            {
                binding.RotCont = ContOffset(_rotQx);
            }
            else if (_rotEuler)
            {
                binding.HeldEuler = _heldEuler;
                if (_eulerX.IsValid) binding.EulerX = ContOffset(_eulerX);
                if (_eulerY.IsValid) binding.EulerY = ContOffset(_eulerY);
                if (_eulerZ.IsValid) binding.EulerZ = ContOffset(_eulerZ);
            }

            if (_scaleUniform.IsValid)
            {
                binding.ScaleUniform = ContOffset(_scaleUniform);
            }
            else
            {
                if (_scaleX.IsValid) binding.ScaleX = ContOffset(_scaleX);
                if (_scaleY.IsValid) binding.ScaleY = ContOffset(_scaleY);
                if (_scaleZ.IsValid) binding.ScaleZ = ContOffset(_scaleZ);
            }

            binding.World = (byte)(WorldSpace ? 1 : 0);
            return binding.HasAny;
        }

        private int ContOffset(BasisSyncHandle h) => Schema.GetField(h.FieldIndex).Offset;

        private static readonly Dictionary<System.Type, bool> _customApplyByType = new Dictionary<System.Type, bool>();

        private bool HasCustomApply()
        {
            System.Type t = GetType();
            if (!_customApplyByType.TryGetValue(t, out bool custom))
            {
                MethodInfo m = t.GetMethod(nameof(ApplyInterpolated), BindingFlags.Instance | BindingFlags.NonPublic);
                custom = m == null || m.DeclaringType != typeof(BasisSyncedTransform);
                _customApplyByType[t] = custom;
            }
            return custom;
        }

        protected override void ApplyInterpolated()
        {
            if (!ComposeSyncedPose(out Vector3 p, out Quaternion r, out Vector3 s)) return;

            if (_hasSyncedPosRot)
            {
                if (RelativeTo != null)
                {
                    Target.SetPositionAndRotation(RelativeTo.TransformPoint(p), RelativeTo.rotation * r);
                }
                else if (WorldSpace) Target.SetPositionAndRotation(p, r);
                else Target.SetLocalPositionAndRotation(p, r);
            }
            if (_hasSyncedScale) Target.localScale = s;
        }

        /// <summary>
        /// Decodes the interpolated pose for the enabled axes (in <see cref="WorldSpace"/> or local space, matching
        /// transmit); a partially-synced channel holds the Target's live value on its unsynced axes. Live transform
        /// reads only happen for those partial channels — when nothing scale-related is synced, s is Vector3.one
        /// (gate applying it on <see cref="HasSyncedScale"/>). Subclasses can use this to drive something other
        /// than the Target transform — e.g. a Rigidbody with prediction + correction.
        /// </summary>
        protected bool ComposeSyncedPose(out Vector3 p, out Quaternion r, out Vector3 s)
        {
            p = default;
            r = Quaternion.identity;
            s = Vector3.one;
            if (Target == null) return false;

            if (_needsLivePose)
            {
                if (RelativeTo != null)
                {
                    Target.GetPositionAndRotation(out p, out r);
                    p = RelativeTo.InverseTransformPoint(p);
                    r = Quaternion.Inverse(RelativeTo.rotation) * r;
                }
                else if (WorldSpace) Target.GetPositionAndRotation(out p, out r);
                else Target.GetLocalPositionAndRotation(out p, out r);
            }
            if (_needsLiveScale) s = Target.localScale;

            if (_posX.IsValid) p.x = GetFloat(_posX);
            if (_posY.IsValid) p.y = GetFloat(_posY);
            if (_posZ.IsValid) p.z = GetFloat(_posZ);

            if (_rotQuat.IsValid)
            {
                r = GetQuaternion(_rotQuat);
            }
            else if (_rotQw.IsValid)
            {
                Quaternion q = new Quaternion(GetFloat(_rotQx), GetFloat(_rotQy), GetFloat(_rotQz), GetFloat(_rotQw));
                float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (mag > 1e-6f)
                {
                    float inv = 1f / mag;
                    r = new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
                }
            }
            else if (_rotEuler)
            {
                Vector3 e = _heldEuler;
                if (_eulerX.IsValid) e.x = GetAngle(_eulerX);
                if (_eulerY.IsValid) e.y = GetAngle(_eulerY);
                if (_eulerZ.IsValid) e.z = GetAngle(_eulerZ);
                r = Quaternion.Euler(e);
            }

            if (_scaleUniform.IsValid)
            {
                float u = GetFloat(_scaleUniform);
                s = new Vector3(u, u, u);
            }
            else
            {
                if (_scaleX.IsValid) s.x = GetFloat(_scaleX);
                if (_scaleY.IsValid) s.y = GetFloat(_scaleY);
                if (_scaleZ.IsValid) s.z = GetFloat(_scaleZ);
            }
            return true;
        }

        protected override bool TryGetSyncGizmoSpatial(BasisSyncValues from, BasisSyncValues to, out Vector3 fromWorld, out Vector3 toWorld)
        {
            fromWorld = default;
            toWorld = default;
            if (Target == null || (!_posX.IsValid && !_posY.IsValid && !_posZ.IsValid)) return false;

            // Unsynced axes have no keyframe data — hold them at the Target's live value so the
            // from/to points sit on the real motion path.
            Vector3 baseLocal = RelativeTo != null
                ? RelativeTo.InverseTransformPoint(Target.position)
                : WorldSpace ? Target.position : Target.localPosition;
            Vector3 f = baseLocal;
            Vector3 t = baseLocal;
            if (_posX.IsValid) { int o = Schema.GetField(_posX.FieldIndex).Offset; f.x = from.Cont[o]; t.x = to.Cont[o]; }
            if (_posY.IsValid) { int o = Schema.GetField(_posY.FieldIndex).Offset; f.y = from.Cont[o]; t.y = to.Cont[o]; }
            if (_posZ.IsValid) { int o = Schema.GetField(_posZ.FieldIndex).Offset; f.z = from.Cont[o]; t.z = to.Cont[o]; }

            Transform parent = Target.parent;
            if (RelativeTo != null)
            {
                fromWorld = RelativeTo.TransformPoint(f);
                toWorld = RelativeTo.TransformPoint(t);
            }
            else if (WorldSpace || parent == null)
            {
                fromWorld = f;
                toWorld = t;
            }
            else
            {
                fromWorld = parent.TransformPoint(f);
                toWorld = parent.TransformPoint(t);
            }
            return true;
        }

        protected override bool TryGetSyncWorldPosition(out Vector3 position)
        {
            if (Target != null) { position = Target.position; return true; }
            position = default;
            return false;
        }
    }
}
