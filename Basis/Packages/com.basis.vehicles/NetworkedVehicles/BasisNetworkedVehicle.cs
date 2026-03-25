using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Vehicles.Main;
using Basis.Scripts.Vehicles.Parts;
using System;
using System.Collections.Generic;
using UnityEngine;
namespace Basis.Network.Vehicles
{
    public class BasisNetworkedVehicle : BasisNetworkBehaviour
    {
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

        [Header("Owner Send")]
        public float SendInterval = 0.05f;

        [Header("Remote Playback (Jitter Buffer)")]
        public int MaxBufferSize = 8;

        [Header("Wheel Quantization")]
        [Range(8, 12)] public int SpinBits = 12;
        [Range(7, 11)] public int SteerBits = 10;
        public float SteerRangeDeg = 60f;

        [Header("Engine / SteeringWheel Sync")]
        [Range(6, 10)] public int EngineBits = 8;
        [Range(7, 11)] public int SteerRatioBits = 9;

        private float _sendTimer;
        private Transform VehicleTransform;
        public BasisPlayer Player;
        private List<BasisVehicleSnapshot> SnapShots = new List<BasisVehicleSnapshot>(64);
        private float[] _ownerSpinAbsDeg;
        private int _wheelCount;
        private int _steerCount;
        private float _remoteEngineRevs01;
        private float _remoteSteerRatio;
        private bool _hooksAdded;
        const float idle = 0.12f;
        const float throttleInfluence = 1.0f;
        public BasisVehicleSnapshot Current;
        public BasisVehicleSnapshot Next;
        private float _snapLerpT = 0f;
        public float[] SteeringArray;
        public struct BasisVehicleSnapshot
        {
            public Vector3 pos;
            public Quaternion rot;
            public Vector3 scale;
            // Wheel angles:
            public float[] spinDeg;   // UNWRAPPED (can exceed 360)
            public float[] steerDeg;  // -range..+range
            public float engineRevs01;
            public float steerRatio;
        }
        public override void Start()
        {
            Player = null;
            base.Start();
            VehicleTransform = transform;
            _wheelCount = Wheels != null ? Wheels.Length : 0;
            _steerCount = SteerVisuals != null ? SteerVisuals.Length : 0;
            SteeringArray = new float[_steerCount];
            _ownerSpinAbsDeg = new float[_wheelCount];
            ToggleItems(false);
            ApplyRemoteExtrasToParts(0f, 0f);
        }
        private void OnEnable()
        {
            if (_hooksAdded)
            {
                return;
            }

            _hooksAdded = true;

            BasisLocalPlayer.JustBeforeNetworkApply.AddAction(9, Simulate);

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

            BasisLocalPlayer.JustBeforeNetworkApply.RemoveAction(9, Simulate);

            if (SeatSync != null)
            {
                SeatSync.OnNetworkPlayerEnterSeat -= OnPlayerEnterSeat;
                SeatSync.OnNetworkPlayerExitSeat -= OnPlayerExitSeat;
            }
        }
        private void OnPlayerExitSeat(BasisPlayer player)
        {
            Player = player;
            ToggleItems(false);
            BasisDebug.Log($"Player Exited Seat {player.DisplayName}");
        }

        private void OnPlayerEnterSeat(BasisPlayer player)
        {
            Player = player;
            bool isLocal = player != null && player.IsLocal;
            ToggleItems(isLocal);
            BasisDebug.Log($"Player Entered Seat {player.DisplayName}");
            if (isLocal)
            {
                SnapShots.Clear();
                _sendTimer = 0f;
                for (int Index = 0; Index < _ownerSpinAbsDeg.Length; Index++)
                {
                    _ownerSpinAbsDeg[Index] = 0f;
                }
                if (EngineAudio != null)
                {
                    EngineAudio.UseNetworkRevs = false;
                }
                if (SteeringWheel != null)
                {
                    SteeringWheel.UseNetworkSteerRatio = false;
                }
            }
            else
            {
                ApplyRemoteExtrasToParts(_remoteEngineRevs01, _remoteSteerRatio);
            }
        }
        public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            int expectedMin = BasisVehicleNetCodec.MinPacketSize + BasisVehicleWheelNetCodec.ExtraBytes(_wheelCount, _steerCount, SpinBits, SteerBits, EngineBits, SteerRatioBits);

            if (buffer.Length < expectedMin)
            {
                BasisDebug.LogError("Missing Buffer Length In Vehicle!");
                return;
            }
            BasisVehicleWheelNetCodec.ReadPacketWithWheels(buffer, _wheelCount, _steerCount, SpinBits, SteerBits, EngineBits, SteerRatioBits, -SteerRangeDeg, SteerRangeDeg, out Vector3 pos, out Quaternion rot, out Vector3 scale, out float[] spinDegMod, out float[] steerDeg, out float engineRevs01, out float steerRatio);
            float[] spinUnwrapped = UnwrapSpinAgainstLast(spinDegMod);
            _remoteEngineRevs01 = engineRevs01;
            _remoteSteerRatio = steerRatio;
            var Snapshot = new BasisVehicleSnapshot
            {
                pos = pos,
                rot = rot,
                scale = scale,
                spinDeg = spinUnwrapped,
                steerDeg = steerDeg,
                engineRevs01 = engineRevs01,
                steerRatio = steerRatio,
            };
            SnapShots.Add(Snapshot);
        }
        public void Simulate()
        {
            SimulateLocal();
            SimulateRemote();
        }
        public void SimulateLocal()
        {
            if (Player != null && Player.IsLocal)
            {
                UpdateOwnerAbsoluteWheelSpin();
                _sendTimer += Time.deltaTime;
                if (_sendTimer >= SendInterval)
                {
                    _sendTimer -= SendInterval;
                    VehicleTransform.GetPositionAndRotation(out var pos, out var rot);
                    var scale = VehicleTransform.localScale;
                    float[] spinDeg = GetOwnerWheelSpinAbs();
                    float[] steerDeg = GetOwnerSteerAbs();
                    float engineRevs01 = ComputeEngineRevs01ForNetwork();
                    float steerRatio = ComputeSteerRatioForNetwork();
                    byte[] data = BasisVehicleWheelNetCodec.WritePacketWithWheels(pos, rot, scale, spinDeg, steerDeg, engineRevs01, steerRatio, SpinBits, SteerBits, EngineBits, SteerRatioBits, -SteerRangeDeg, SteerRangeDeg);
                    SendCustomNetworkEvent(data, Basis.Network.Core.DeliveryMethod.Sequenced);
                }

                return;
            }
        }
        public void SimulateRemote()
        {
            // Need at least two snapshots to interpolate
            if (SnapShots.Count < 2)
            {
                return;
            }

            // Advance our "time within this snapshot pair"
            _snapLerpT += Time.deltaTime;

            // If we fell behind (big hitch), consume as many whole snapshots as needed
            while (_snapLerpT >= SendInterval && SnapShots.Count >= 2)
            {
                _snapLerpT -= SendInterval;

                // We finished blending SnapShots[0] -> SnapShots[1], so drop the old one
                SnapShots.RemoveAt(0);

                // If we no longer have a pair, stop here
                if (SnapShots.Count < 2)
                {
                    break;
                }
            }

            if (SnapShots.Count < 2)
            {
                return;
            }

            // The current interpolation pair
            BasisVehicleSnapshot s0 = SnapShots[0];
            BasisVehicleSnapshot s1 = SnapShots[1];

            // Update public references if you want them visible elsewhere
            Current = s0;
            Next = s1;

            float Percentage = Mathf.Clamp01(_snapLerpT / SendInterval);

            // Interpolate transform
            Vector3 pos = Vector3.LerpUnclamped(s0.pos, s1.pos, Percentage);
            Quaternion rot = Quaternion.SlerpUnclamped(s0.rot, s1.rot, Percentage);
            Vector3 scale = Vector3.LerpUnclamped(s0.scale, s1.scale, Percentage);

            VehicleTransform.SetPositionAndRotation(pos, rot);
            VehicleTransform.localScale = scale;

            // Wheels + extras
            ApplyRemoteWheelsInterpolated(s0, s1, Percentage);

            float engine = Mathf.LerpUnclamped(s0.engineRevs01, s1.engineRevs01, Percentage);
            float steerRatio = Mathf.LerpUnclamped(s0.steerRatio, s1.steerRatio, Percentage);
            ApplyRemoteExtrasToParts(engine, steerRatio);

            // Keep buffer bounded (drop oldest)
            while (SnapShots.Count > MaxBufferSize)
            {
                SnapShots.RemoveAt(0);
            }
        }
        private float[] UnwrapSpinAgainstLast(float[] incomingMod360)
        {
            int n = incomingMod360 != null ? incomingMod360.Length : 0;
            float[] outUnwrapped = new float[n];

            if (n == 0)
            {
                return outUnwrapped;
            }
            // if we have a last snapshot, unwrap against it; else just use incoming
            if (SnapShots.Count > 0 && SnapShots[SnapShots.Count - 1].spinDeg != null)
            {
                var prev = SnapShots[SnapShots.Count - 1].spinDeg;
                int m = Mathf.Min(prev.Length, n);
                for (int i = 0; i < m; i++)
                {
                    outUnwrapped[i] = UnwrapClosest(prev[i], incomingMod360[i]);
                }
                for (int i = m; i < n; i++)
                {
                    outUnwrapped[i] = incomingMod360[i];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    outUnwrapped[i] = incomingMod360[i];
                }
            }

            return outUnwrapped;
        }
        private static float UnwrapClosest(float prevUnwrapped, float incomingMod)
        {
            float baseVal = Mathf.Repeat(incomingMod, 360f);
            float k = Mathf.Round((prevUnwrapped - baseVal) / 360f);
            return baseVal + 360f * k;
        }
        private static float Wrap360(float deg)
        {
            deg %= 360f;
            if (deg < 0f)
            {
                deg += 360f;
            }
            return deg;
        }
        private void UpdateOwnerAbsoluteWheelSpin()
        {
            if (Colliders == null || _ownerSpinAbsDeg == null)
            {
                return;
            }

            int n = Mathf.Min(Colliders.Length, _ownerSpinAbsDeg.Length);
            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            for (int Index = 0; Index < n; Index++)
            {
                var wc = Colliders[Index];
                if (wc == null)
                {
                    continue;
                }

                float degPerSec = wc.rpm * 6f;
                _ownerSpinAbsDeg[Index] = Wrap360(_ownerSpinAbsDeg[Index] + degPerSec * dt);
            }
        }
        private float[] GetOwnerWheelSpinAbs()
        {
            float[] a = new float[_wheelCount];
            for (int i = 0; i < _wheelCount; i++)
            {
                a[i] = (i < _ownerSpinAbsDeg.Length) ? _ownerSpinAbsDeg[i] : 0f;
            }

            return a;
        }
        private float[] GetOwnerSteerAbs()
        {
            if (_steerCount == 0)
            {
                return SteeringArray;
            }

            for (int Index = 0; Index < _steerCount; Index++)
            {
                float steer = 0f;
                if (Colliders != null && Index < Colliders.Length && Colliders[Index] != null)
                {
                    steer = Colliders[Index].steerAngle;
                }

                SteeringArray[Index] = Mathf.Clamp(steer, -SteerRangeDeg, SteerRangeDeg);
            }

            return SteeringArray;
        }
        private float ComputeEngineRevs01ForNetwork()
        {
            float throttle = 0f;
            float speed = 0f;
            float maxSpeed = 30f;
            if (Rigidbody != null)
            {
                speed = Rigidbody.linearVelocity.magnitude;
            }
            if (BasisVehicleBody != null)
            {
                throttle = Mathf.Clamp(BasisVehicleBody.LinearActivation.z, -1f, 1f);
                throttle = Mathf.Abs(throttle);
            }
            if (BasisVehicleBody != null && BasisVehicleBody.MaxSpeed >= 0.001f)
            {
                maxSpeed = BasisVehicleBody.MaxSpeed;
            }
            float speedRatio = (maxSpeed > 0.001f) ? Mathf.Clamp01(speed / maxSpeed) : 0f;
            return Mathf.Max(idle, Mathf.Clamp01(speedRatio + throttle * throttleInfluence * (1f - speedRatio)));
        }
        private float ComputeSteerRatioForNetwork()
        {
            if (Colliders == null || Colliders.Length == 0)
            {
                return 0f;
            }
            float denom = Mathf.Max(1f, SteerRangeDeg);
            float sum = 0f;
            int count = 0;

            for (int Index = 0; Index < Colliders.Length; Index++)
            {
                var wc = Colliders[Index];
                if (wc == null)
                {
                    continue;
                }

                float r = Mathf.Clamp(wc.steerAngle / denom, -1f, 1f);
                sum += r;
                count++;
            }

            return (count == 0) ? 0f : (sum / count);
        }
        private void ApplyRemoteWheelsInterpolated(in BasisVehicleSnapshot a, in BasisVehicleSnapshot b, float alpha)
        {
            if (SpinVisuals != null && a.spinDeg != null && b.spinDeg != null)
            {
                int n = Mathf.Min(SpinVisuals.Length, Mathf.Min(a.spinDeg.Length, b.spinDeg.Length));
                for (int i = 0; i < n; i++)
                {
                    var t = SpinVisuals[i];
                    if (t == null)
                    {
                        continue;
                    }
                    float degUnwrapped = Mathf.Lerp(a.spinDeg[i], b.spinDeg[i], alpha);
                    float deg = Wrap360(degUnwrapped);
                    SetAxisLocalRotation(t, SpinAxisLocal, deg);
                }
            }
            if (SteerVisuals != null && a.steerDeg != null && b.steerDeg != null)
            {
                int n = Mathf.Min(SteerVisuals.Length, Mathf.Min(a.steerDeg.Length, b.steerDeg.Length));
                for (int i = 0; i < n; i++)
                {
                    var t = SteerVisuals[i];
                    if (t == null)
                    {
                        continue;
                    }
                    float deg = Mathf.LerpAngle(a.steerDeg[i], b.steerDeg[i], alpha);
                    SetAxisLocalRotation(t, SteerAxisLocal, deg);
                }
            }
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
            }
            if (Colliders != null)
            {
                for (int Index = 0; Index < Colliders.Length; Index++)
                {
                    WheelCollider item = Colliders[Index];
                    if (item != null)
                    {
                        item.enabled = state;
                    }
                }
            }
            if (Wheels != null)
            {
                for (int Index = 0; Index < Wheels.Length; Index++)
                {
                    BasisVehicleWheel wheel = Wheels[Index];
                    if (wheel != null)
                    {
                        wheel.enabled = state;
                    }
                }
            }
        }
    }
}
