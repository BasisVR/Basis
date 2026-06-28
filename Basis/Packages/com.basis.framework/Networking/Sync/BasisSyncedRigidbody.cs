using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// Rigidbody sibling of <see cref="BasisSyncedTransform"/>. The owner streams the body's world position and
    /// orientation (with the same per-axis compression and rotation modes) plus linear / angular velocity; every
    /// other client drives its copy. Remote bodies are made kinematic by default so physics never fights the
    /// synced pose — turn that off to feed the synced velocity into a live simulation instead. Owner-authoritative;
    /// call TakeOwnership() (e.g. on grab) to become the authority.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class BasisSyncedRigidbody : BasisSyncedObject
    {
        public Rigidbody Body;
        public Transform RelativeTo;

        public bool SyncPosition = true;
        public bool SyncRotation = true;
        public BasisRotationSyncMode RotationMode = BasisRotationSyncMode.SmallestThree;
        [Range(6, 16)] public int SmallestThreeBits = 9;
        public bool InterpolatePosition = true;
        public bool InterpolateRotation = true;

        public bool SyncScale = false;
        public bool ScaleUniform = false;
        public bool ScaleX = true;
        public bool ScaleY = true;
        public bool ScaleZ = true;
        public bool InterpolateScale = true;

        public bool SyncLinearVelocity = true;
        public bool SyncAngularVelocity = true;
        public bool DriveRemoteKinematic = true;

        public BasisTransformAxisCompression PosCompX = BasisTransformAxisCompression.PositionDefault;
        public BasisTransformAxisCompression PosCompY = BasisTransformAxisCompression.PositionDefault;
        public BasisTransformAxisCompression PosCompZ = BasisTransformAxisCompression.PositionDefault;
        public BasisTransformAxisCompression LinearVelocityComp = new BasisTransformAxisCompression { Mode = BasisTransformAxisMode.Half };
        public BasisTransformAxisCompression AngularVelocityComp = new BasisTransformAxisCompression { Mode = BasisTransformAxisMode.Half };
        public BasisTransformAxisCompression ScaleCompX = BasisTransformAxisCompression.ScaleDefault;
        public BasisTransformAxisCompression ScaleCompY = BasisTransformAxisCompression.ScaleDefault;
        public BasisTransformAxisCompression ScaleCompZ = BasisTransformAxisCompression.ScaleDefault;

        private BasisSyncHandle _posX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _posY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _posZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQuat = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQx = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQy = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQz = BasisSyncHandle.Invalid;
        private BasisSyncHandle _rotQw = BasisSyncHandle.Invalid;
        private BasisSyncHandle _eulerX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _eulerY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _eulerZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _velX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _velY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _velZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _angX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _angY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _angZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleX = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleY = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleZ = BasisSyncHandle.Invalid;
        private BasisSyncHandle _scaleUniform = BasisSyncHandle.Invalid;
        private bool _rotEuler;
        private bool _originalKinematic;

        private void Reset()
        {
            Body = GetComponent<Rigidbody>();
        }

        protected virtual void Awake()
        {
            if (Body == null) Body = GetComponent<Rigidbody>();
            WantsMainThreadApply = true;
            _originalKinematic = Body != null && Body.isKinematic;

            int posAxes = 0;
            if (SyncPosition)
            {
                _posX = RegisterFloat(PosCompX.ToSpec(), InterpolatePosition); posAxes++;
                _posY = RegisterFloat(PosCompY.ToSpec(), InterpolatePosition); posAxes++;
                _posZ = RegisterFloat(PosCompZ.ToSpec(), InterpolatePosition); posAxes++;
            }

            if (SyncRotation)
            {
                switch (RotationMode)
                {
                    case BasisRotationSyncMode.Euler:
                        _rotEuler = true;
                        _eulerX = RegisterAngle(BasisQuantSpec.Raw, InterpolateRotation);
                        _eulerY = RegisterAngle(BasisQuantSpec.Raw, InterpolateRotation);
                        _eulerZ = RegisterAngle(BasisQuantSpec.Raw, InterpolateRotation);
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

            if (SyncLinearVelocity)
            {
                _velX = RegisterFloat(LinearVelocityComp.ToSpec());
                _velY = RegisterFloat(LinearVelocityComp.ToSpec());
                _velZ = RegisterFloat(LinearVelocityComp.ToSpec());
            }

            if (SyncAngularVelocity)
            {
                _angX = RegisterFloat(AngularVelocityComp.ToSpec());
                _angY = RegisterFloat(AngularVelocityComp.ToSpec());
                _angZ = RegisterFloat(AngularVelocityComp.ToSpec());
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

            TeleportWatchStart = 0;
            TeleportWatchCount = posAxes;
        }

        protected override void OnBeforeTransmit()
        {
            if (Body == null) return;
            Vector3 p = Body.position;
            Quaternion r = Body.rotation;
            if (RelativeTo != null)
            {
                p = RelativeTo.InverseTransformPoint(p);
                r = Quaternion.Inverse(RelativeTo.rotation) * r;
            }

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
                LocalSet(_eulerX, e.x);
                LocalSet(_eulerY, e.y);
                LocalSet(_eulerZ, e.z);
            }

            if (_velX.IsValid)
            {
                Vector3 v = Body.linearVelocity;
                LocalSet(_velX, v.x);
                LocalSet(_velY, v.y);
                LocalSet(_velZ, v.z);
            }

            if (_angX.IsValid)
            {
                Vector3 w = Body.angularVelocity;
                LocalSet(_angX, w.x);
                LocalSet(_angY, w.y);
                LocalSet(_angZ, w.z);
            }

            if (_scaleUniform.IsValid)
            {
                LocalSet(_scaleUniform, Body.transform.localScale.x);
            }
            else if (_scaleX.IsValid || _scaleY.IsValid || _scaleZ.IsValid)
            {
                Vector3 s = Body.transform.localScale;
                if (_scaleX.IsValid) LocalSet(_scaleX, s.x);
                if (_scaleY.IsValid) LocalSet(_scaleY, s.y);
                if (_scaleZ.IsValid) LocalSet(_scaleZ, s.z);
            }
        }

        protected override void ApplyInterpolated()
        {
            if (Body == null) return;

            Vector3 p = Body.position;
            Quaternion r = Body.rotation;
            bool hasSyncedPosition = _posX.IsValid;
            bool hasSyncedRotation = _rotQuat.IsValid || _rotQw.IsValid || _rotEuler;

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
                r = Quaternion.Euler(GetAngle(_eulerX), GetAngle(_eulerY), GetAngle(_eulerZ));
            }

            if (RelativeTo != null)
            {
                if (hasSyncedPosition) p = RelativeTo.TransformPoint(p);
                if (hasSyncedRotation) r = RelativeTo.rotation * r;
            }

            if (DriveRemoteKinematic)
            {
                if (!Body.isKinematic) Body.isKinematic = true;
                Body.position = p;
                Body.rotation = r;
            }
            else
            {
                // Velocity-assisted extrapolation: let the synced velocity carry the dynamic body between updates
                // and softly correct toward the synced pose, instead of hard-snapping it every frame.
                if (_velX.IsValid) Body.linearVelocity = new Vector3(GetFloat(_velX), GetFloat(_velY), GetFloat(_velZ));
                if (_angX.IsValid) Body.angularVelocity = new Vector3(GetFloat(_angX), GetFloat(_angY), GetFloat(_angZ));
                const float correction = 0.2f;
                Body.position = Vector3.Lerp(Body.position, p, correction);
                Body.rotation = Quaternion.Slerp(Body.rotation, r, correction);
            }

            if (_scaleUniform.IsValid)
            {
                float u = GetFloat(_scaleUniform);
                Body.transform.localScale = new Vector3(u, u, u);
            }
            else if (_scaleX.IsValid || _scaleY.IsValid || _scaleZ.IsValid)
            {
                Vector3 s = Body.transform.localScale;
                if (_scaleX.IsValid) s.x = GetFloat(_scaleX);
                if (_scaleY.IsValid) s.y = GetFloat(_scaleY);
                if (_scaleZ.IsValid) s.z = GetFloat(_scaleZ);
                Body.transform.localScale = s;
            }
        }

        public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
        {
            base.OnOwnershipTransfer(newOwner);
            if (Body != null && IsOwnedLocallyOnClient) Body.isKinematic = _originalKinematic;
        }

        protected override bool TryGetSyncWorldPosition(out Vector3 position)
        {
            if (Body != null) { position = Body.position; return true; }
            position = default;
            return false;
        }
    }
}
