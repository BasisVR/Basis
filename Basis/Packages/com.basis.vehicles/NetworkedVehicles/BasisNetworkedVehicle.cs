using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Sync;
using Basis.Scripts.Vehicles.Main;
using Basis.Scripts.Vehicles.Parts;
using UnityEngine;
namespace Basis.Network.Vehicles
{
    /// <summary>
    /// Networked vehicle built on <see cref="BasisSyncedTransform"/>: the body transform plus per-wheel
    /// spin, per-steer angle, engine revs and steer ratio are synced as typed fields and interpolated by
    /// the central sync engine. The local pilot (seat occupant) takes ownership and drives the physics;
    /// every other client interpolates the body + visuals.
    /// </summary>
    public class BasisNetworkedVehicle : BasisSyncedTransform, IBasisStaticLockable
    {
        /// <summary>
        /// Server-authoritative "locked" state from the library Static toggle. When true the vehicle
        /// is frozen (kinematic, wheels disabled) and ignores input on every client, so it can't move.
        /// </summary>
        public bool IsLocked { get; private set; }
        public BasisVehicleBody BasisVehicleBody;
        public BasisVehiclePilotSeat Seat;
        public BasisSeatSync SeatSync;
        public Rigidbody Rigidbody;
        public WheelCollider[] Colliders;
        public BasisVehicleWheel[] Wheels;
        public BasisVehicleEngineAudio EngineAudio;
        public BasisVehicleSteeringWheel SteeringWheel;

        [Header("Wheel Visual Transforms (REQUIRED for remote visuals)")]
        public Transform[] SpinVisuals;
        public Transform[] SteerVisuals;

        public Vector3 SpinAxisLocal = Vector3.right;
        public Vector3 SteerAxisLocal = Vector3.up;
        public float SteerRangeDeg = 60f;

        [Header("Wheel / Extras Precision")]
        public bool HalfPrecisionWheels = true;

        public IBasisPlayer Player;
        private float[] _ownerSpinAbsDeg;
        private int _wheelCount;
        private int _steerCount;
        private bool _hooksAdded;
        const float idle = 0.12f;
        const float throttleInfluence = 1.0f;

        private BasisSyncHandle[] _spinHandles;
        private BasisSyncHandle[] _steerHandles;
        private BasisSyncHandle _engineHandle;
        private BasisSyncHandle _steerRatioHandle;

        protected override void Awake()
        {
            // The body is a world-space rigidbody; sync full position/rotation/scale.
            Target = transform;
            WorldSpace = true;
            SyncPosition = true;
            SyncRotation = true;
            SyncScale = true;
            PositionX = PositionY = PositionZ = true;
            RotationX = RotationY = RotationZ = true;
            ScaleX = ScaleY = ScaleZ = true;
            base.Awake();

            _wheelCount = Wheels != null ? Wheels.Length : 0;
            _steerCount = SteerVisuals != null ? SteerVisuals.Length : 0;
            _ownerSpinAbsDeg = new float[_wheelCount];

            _spinHandles = new BasisSyncHandle[_wheelCount];
            for (int i = 0; i < _wheelCount; i++) _spinHandles[i] = RegisterAngle(true, HalfPrecisionWheels);
            _steerHandles = new BasisSyncHandle[_steerCount];
            for (int i = 0; i < _steerCount; i++) _steerHandles[i] = RegisterAngle(true, HalfPrecisionWheels);
            _engineHandle = RegisterFloat(true, HalfPrecisionWheels);
            _steerRatioHandle = RegisterFloat(true, HalfPrecisionWheels);

            ToggleItems(false);
            ApplyRemoteExtrasToParts(0f, 0f);
        }

        private void OnEnable()
        {
            if (_hooksAdded) return;
            _hooksAdded = true;

            BasisLocalPlayer.JustBeforeNetworkApply.AddAction(9, OwnerTick);

            if (SeatSync != null)
            {
                SeatSync.OnNetworkPlayerEnterSeat += OnPlayerEnterSeat;
                SeatSync.OnNetworkPlayerExitSeat += OnPlayerExitSeat;
            }
        }

        private void OnDisable()
        {
            if (!_hooksAdded) return;
            _hooksAdded = false;

            BasisLocalPlayer.JustBeforeNetworkApply.RemoveAction(9, OwnerTick);

            if (SeatSync != null)
            {
                SeatSync.OnNetworkPlayerEnterSeat -= OnPlayerEnterSeat;
                SeatSync.OnNetworkPlayerExitSeat -= OnPlayerExitSeat;
            }
        }

        private void OnPlayerExitSeat(IBasisPlayer player)
        {
            Player = player;
            ToggleItems(false);
            BasisDebug.Log($"Player Exited Seat {player.DisplayName}");
        }

        private void OnPlayerEnterSeat(IBasisPlayer player)
        {
            Player = player;
            bool isLocal = player != null && player.IsLocal;
            // A locked vehicle stays frozen even when someone sits in it.
            ToggleItems(isLocal && !IsLocked);
            BasisDebug.Log($"Player Entered Seat {player.DisplayName}");
            if (isLocal)
            {
                for (int Index = 0; Index < _ownerSpinAbsDeg.Length; Index++) _ownerSpinAbsDeg[Index] = 0f;
                if (EngineAudio != null) EngineAudio.UseNetworkRevs = false;
                if (SteeringWheel != null) SteeringWheel.UseNetworkSteerRatio = false;
                // Become the network owner so the sync engine streams our state.
                TakeOwnership();
            }
            else
            {
                ApplyRemoteExtrasToParts(0f, 0f);
            }
        }

        public override void OnOwnershipTransfer(BasisNetworkPlayer NetIdNewOwner)
        {
            base.OnOwnershipTransfer(NetIdNewOwner);
            if (SeatSync != null && SeatSync.IsLocallyEntered() && !IsOwnedLocallyOnServer)
            {
                TakeOwnership();
            }
        }

        public override void OnServerOwnershipDestroyed()
        {
            base.OnServerOwnershipDestroyed();
            // Ownership returned to no one (e.g. driver disconnected): leave the vehicle idle, don't destroy it.
            if (SeatSync != null && SeatSync.IsLocallyEntered())
            {
                BasisLocalPlayer.Instance?.LocalSeatDriver?.Stand();
            }
            Player = null;
            ToggleItems(false);
            ApplyRemoteExtrasToParts(0f, 0f);
        }

        // Per-frame on the driving client: accumulate absolute wheel spin from the live colliders.
        private void OwnerTick()
        {
            if (IsLocked) return;
            if (Player != null && Player.IsLocal) UpdateOwnerAbsoluteWheelSpin();
        }

        protected override void OnBeforeTransmit()
        {
            if (IsLocked) return;
            base.OnBeforeTransmit();

            for (int i = 0; i < _wheelCount; i++)
            {
                float spin = i < _ownerSpinAbsDeg.Length ? _ownerSpinAbsDeg[i] : 0f;
                LocalSet(_spinHandles[i], spin);
            }
            for (int i = 0; i < _steerCount; i++)
            {
                float steer = 0f;
                if (Colliders != null && i < Colliders.Length && Colliders[i] != null)
                    steer = Mathf.Clamp(Colliders[i].steerAngle, -SteerRangeDeg, SteerRangeDeg);
                LocalSet(_steerHandles[i], steer);
            }
            LocalSet(_engineHandle, ComputeEngineRevs01ForNetwork());
            LocalSet(_steerRatioHandle, ComputeSteerRatioForNetwork());
        }

        protected override void ApplyInterpolated()
        {
            if (IsLocked) return;
            base.ApplyInterpolated();

            if (SpinVisuals != null)
            {
                int n = Mathf.Min(SpinVisuals.Length, _wheelCount);
                for (int i = 0; i < n; i++)
                {
                    Transform t = SpinVisuals[i];
                    if (t == null) continue;
                    SetAxisLocalRotation(t, SpinAxisLocal, Wrap360(GetAngle(_spinHandles[i])));
                }
            }
            if (SteerVisuals != null)
            {
                int n = Mathf.Min(SteerVisuals.Length, _steerCount);
                for (int i = 0; i < n; i++)
                {
                    Transform t = SteerVisuals[i];
                    if (t == null) continue;
                    SetAxisLocalRotation(t, SteerAxisLocal, GetAngle(_steerHandles[i]));
                }
            }
            ApplyRemoteExtrasToParts(GetFloat(_engineHandle), GetFloat(_steerRatioHandle));
        }

        private static float Wrap360(float deg)
        {
            deg %= 360f;
            if (deg < 0f) deg += 360f;
            return deg;
        }

        private void UpdateOwnerAbsoluteWheelSpin()
        {
            if (Colliders == null || _ownerSpinAbsDeg == null) return;

            int n = Mathf.Min(Colliders.Length, _ownerSpinAbsDeg.Length);
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            for (int Index = 0; Index < n; Index++)
            {
                var wc = Colliders[Index];
                if (wc == null) continue;
                float degPerSec = wc.rpm * 6f;
                _ownerSpinAbsDeg[Index] = Wrap360(_ownerSpinAbsDeg[Index] + degPerSec * dt);
            }
        }

        private float ComputeEngineRevs01ForNetwork()
        {
            float throttle = 0f;
            float speed = 0f;
            float maxSpeed = 30f;
            if (Rigidbody != null) speed = Rigidbody.linearVelocity.magnitude;
            if (BasisVehicleBody != null)
            {
                throttle = Mathf.Clamp(BasisVehicleBody.LinearActivation.z, -1f, 1f);
                throttle = Mathf.Abs(throttle);
            }
            if (BasisVehicleBody != null && BasisVehicleBody.MaxSpeed >= 0.001f) maxSpeed = BasisVehicleBody.MaxSpeed;
            float speedRatio = (maxSpeed > 0.001f) ? Mathf.Clamp01(speed / maxSpeed) : 0f;
            return Mathf.Max(idle, Mathf.Clamp01(speedRatio + throttle * throttleInfluence * (1f - speedRatio)));
        }

        private float ComputeSteerRatioForNetwork()
        {
            if (Colliders == null || Colliders.Length == 0) return 0f;
            float denom = Mathf.Max(1f, SteerRangeDeg);
            float sum = 0f;
            int count = 0;
            for (int Index = 0; Index < Colliders.Length; Index++)
            {
                var wc = Colliders[Index];
                if (wc == null) continue;
                sum += Mathf.Clamp(wc.steerAngle / denom, -1f, 1f);
                count++;
            }
            return (count == 0) ? 0f : (sum / count);
        }

        private void ApplyRemoteExtrasToParts(float engineRevs01, float steerRatio)
        {
            if (EngineAudio != null)
            {
                EngineAudio.UseNetworkRevs = true;
                EngineAudio.NetworkRevs01 = Mathf.Clamp01(engineRevs01);
            }
            if (SteeringWheel != null)
            {
                SteeringWheel.UseNetworkSteerRatio = true;
                SteeringWheel.NetworkSteerRatio = Mathf.Clamp(steerRatio, -1f, 1f);
            }
        }

        private static void SetAxisLocalRotation(Transform t, Vector3 axisLocal, float degrees)
        {
            axisLocal = axisLocal.sqrMagnitude > 1e-8f ? axisLocal.normalized : Vector3.right;
            t.localRotation = Quaternion.AngleAxis(degrees, axisLocal);
        }

        public void ToggleItems(bool state)
        {
            BasisDebug.Log($"Toggle Vehicle To {state}");
            if (Rigidbody != null)
            {
                Rigidbody.isKinematic = !state;
                Rigidbody.interpolation = state ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            }
            if (Colliders != null)
            {
                for (int Index = 0; Index < Colliders.Length; Index++)
                {
                    WheelCollider item = Colliders[Index];
                    if (item != null) item.enabled = state;
                }
            }
            if (Wheels != null)
            {
                for (int Index = 0; Index < Wheels.Length; Index++)
                {
                    BasisVehicleWheel wheel = Wheels[Index];
                    if (wheel != null) wheel.enabled = state;
                }
            }
        }

        /// <summary>
        /// Apply or release the server-authoritative static / locked state (<see cref="IBasisStaticLockable"/>).
        /// Locking freezes the rigidbody and disables wheels on every client; unlocking restores the normal
        /// owner/remote state. Sync transmit + apply are short-circuited while locked.
        /// </summary>
        public void SetStatic(bool isStatic)
        {
            if (IsLocked == isStatic) return;
            IsLocked = isStatic;
            if (BasisVehicleBody != null) BasisVehicleBody.IsLocked = isStatic;
            if (isStatic)
            {
                if (Rigidbody != null)
                {
                    Rigidbody.linearVelocity = Vector3.zero;
                    Rigidbody.angularVelocity = Vector3.zero;
                }
                ToggleItems(false);
            }
            else
            {
                bool isLocalPilot = Player != null && Player.IsLocal;
                ToggleItems(isLocalPilot);
            }
        }
    }
}
