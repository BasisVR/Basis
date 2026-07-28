using System.Collections;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.EyeTracking;
using Basis.Scripts.Networking.Receivers;
using HVR.Basis.Comms.HVRUtility;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using Unity.Mathematics;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [DefaultExecutionOrder(15010)] // Run after BasisEyeFollowBase
    [AddComponentMenu("HVR.Basis/Comms/Eye Tracking Bone Actuation")]
    public class EyeTrackingBoneActuation : MonoBehaviour, IHVRInitializable
    {
        private const string EyeLeftX = "FT/v2/EyeLeftX";
        private const string EyeRightX = "FT/v2/EyeRightX";
        private const string EyeY = "FT/v2/EyeY";
        private const string EyeTrackingActive = "HVR/Internal/EyeTrackingActive";

        private const string AddressOverrideEnabled = "HVR/EyeTracking/OverrideEnabled";
        private const string AddressMultiplyX = "HVR/EyeTracking/MultiplyX";
        private const string AddressMultiplyY = "HVR/EyeTracking/MultiplyY";
        private const string AddressMaxAngleX = "HVR/EyeTracking/MaxAngleX";
        private const string AddressMaxAngleY = "HVR/EyeTracking/MaxAngleY";
        public const float MaxAngleLimitDeg = 90f;

        private readonly int _eyeLeftXAddress;
        private readonly int _eyeRightXAddress;
        private readonly int _eyeYAddress;
        private readonly int _eyeTrackingActiveAddress;
        private readonly int[] _sourceEyeAddresses;

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [SerializeField] internal float multiplyX = 1f;
        [SerializeField] internal float multiplyY = 1f;

        private bool _runtimeOverrideEnabled;
        private float _runtimeMultiplyX = 1f;
        private float _runtimeMultiplyY = 1f;
        private float _runtimeMaxAngleXDeg = MaxAngleLimitDeg;
        private float _runtimeMaxAngleYDeg = MaxAngleLimitDeg;
        private bool _isWearer;

        public float DefaultMultiplyX => multiplyX;
        public float DefaultMultiplyY => multiplyY;
        public bool RuntimeOverrideEnabled
        {
            get => _runtimeOverrideEnabled;
            set
            {
                _runtimeOverrideEnabled = value;
                Persist(AddressOverrideEnabled, value ? 1f : 0f, 0f);
            }
        }
        public float RuntimeMultiplyX { get => _runtimeMultiplyX; set => SetOverride(ref _runtimeMultiplyX, value, multiplyX, AddressMultiplyX); }
        public float RuntimeMultiplyY { get => _runtimeMultiplyY; set => SetOverride(ref _runtimeMultiplyY, value, multiplyY, AddressMultiplyY); }
        public float RuntimeMaxAngleXDeg { get => _runtimeMaxAngleXDeg; set => SetOverride(ref _runtimeMaxAngleXDeg, Mathf.Clamp(value, 0f, MaxAngleLimitDeg), MaxAngleLimitDeg, AddressMaxAngleX); }
        public float RuntimeMaxAngleYDeg { get => _runtimeMaxAngleYDeg; set => SetOverride(ref _runtimeMaxAngleYDeg, Mathf.Clamp(value, 0f, MaxAngleLimitDeg), MaxAngleLimitDeg, AddressMaxAngleY); }
        internal float EffectiveMultiplyX => _runtimeOverrideEnabled ? _runtimeMultiplyX : multiplyX;
        internal float EffectiveMultiplyY => _runtimeOverrideEnabled ? _runtimeMultiplyY : multiplyY;
        internal float EffectiveMaxAngleXRad => (_runtimeOverrideEnabled ? _runtimeMaxAngleXDeg : MaxAngleLimitDeg) * Mathf.Deg2Rad;
        internal float EffectiveMaxAngleYRad => (_runtimeOverrideEnabled ? _runtimeMaxAngleYDeg : MaxAngleLimitDeg) * Mathf.Deg2Rad;

        public float _fEyeLeftX;
        public float _fEyeRightX;
        public float _fEyeY;

        private HVRAvatarComms comms;
        // NonSerialized is load-bearing: BasisNetworkReceiver is [Serializable], so without it
        // Unity auto-instantiates a junk receiver on prefab load (field initializers don't run
        // under deserialization) — the != null guards then pass and the per-frame eye write
        // NREs on its dead internals, killing every later AfterAvatarChanges subscriber.
        [System.NonSerialized] public BasisNetworkReceiver Receiver = null;

        private bool _trackingActive;
        public bool IsTrackingActive => _trackingActive;
        public bool IsEyeTrackingParametersActive => _eyeTracking?.IsEyeTrackingParametersActive ?? false;
        private FaceTrackingActivityRelay _activityRelay;

        private IEyeTracking _eyeTracking;

        public EyeTrackingBoneActuation()
        {
            _eyeLeftXAddress = HVRAddress.AddressToId(EyeLeftX);
            _eyeRightXAddress = HVRAddress.AddressToId(EyeRightX);
            _eyeYAddress = HVRAddress.AddressToId(EyeY);
            _eyeTrackingActiveAddress = HVRAddress.AddressToId(EyeTrackingActive);
            _sourceEyeAddresses = new[] { _eyeLeftXAddress, _eyeRightXAddress, _eyeYAddress, _eyeTrackingActiveAddress };
        }

        private void Awake()
        {
            if (avatar == null) avatar = HVRCommsUtil.GetAvatar(this);
        }


        public void OnHVRAvatarReady(bool isWearer)
        {
            _isWearer = isWearer;
            _runtimeMultiplyX = multiplyX;
            _runtimeMultiplyY = multiplyY;
            if (isWearer && isActiveAndEnabled) StartCoroutine(RestoreRuntimeOverridesNextFrame());
            comms = HVRCommsUtil.GetComms(avatar);
            _activityRelay = FaceTrackingActivityRelay.GetOrCreate(avatar, out var relayCreated);
            if (relayCreated)
            {
                _activityRelay.OnHVRAvatarReady(isWearer);
            }

            _eyeTracking = isWearer ? new EyeTracking_Wearer(this) : new EyeTracking_Remote(this);

            if (_activityRelay != null)
            {
                _eyeTracking.OnEyeTrackingActiveAddressUpdated(_activityRelay != null && _activityRelay.IsTrackingActive);
                _activityRelay.OnTrackingActivityChanged -= OnTrackingActivityUpdated;
                _activityRelay.OnTrackingActivityChanged += OnTrackingActivityUpdated;
            }

            comms.VariableStore.RegisterAddresses(_sourceEyeAddresses, OnAddressUpdated);
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
          //  HVRLogging.ProtocolDebug("OnReadyBothAvatarAndNetwork called on EyeTrackingBoneActuation.");

            var addresses = new[] { _eyeLeftXAddress, _eyeRightXAddress, _eyeYAddress };
            foreach (var address in addresses)
            {
                comms.RequireVariable(new HVRVariable
                {
                    addressId = address,
                    initialValue = 0f,
                    variableTypeCode = HVRVariableTypeCode.Float,
                    needsInterpolation = true,
                    min = -1f,
                    max = 1f,
                });
            }
            comms.RequireVariable(new HVRVariable
            {
                addressId = _eyeTrackingActiveAddress,
                initialValue = 0f,
                variableTypeCode = HVRVariableTypeCode.Float,
                needsInterpolation = false,
                min = 0f,
                max = 1f,
            });

            if (!isWearer)
            {
                if (BasisNetworkPlayers.AvatarToPlayer(avatar, out _, out var networkPlayer))
                {
                    Receiver = networkPlayer as BasisNetworkReceiver;
                }
                else
                {
                    BasisDebug.LogError($"Could not find BasisNetworkReceiver for avatar {avatar.name}");
                }
            }
        }

        private void OnEnable()
        {
            BasisNetworkTransmitter.AfterAvatarChanges += UpdateAfterAvatarChangesApplied;
            HVRCommsUpdateDriver.Register(this);
        }

        private void OnDisable()
        {
            BasisNetworkTransmitter.AfterAvatarChanges -= UpdateAfterAvatarChangesApplied;
            HVRCommsUpdateDriver.Unregister(this);
        }

        private void OnDestroy()
        {
            if (_activityRelay != null)
            {
                _activityRelay.OnTrackingActivityChanged -= OnTrackingActivityUpdated;
            }

            if (comms != null)
            {
                comms.VariableStore.UnregisterAddresses(_sourceEyeAddresses, OnAddressUpdated);
            }
            _eyeTracking?.OnDestroy();
            if (_isWearer) HVRVixxyPersistentStore.FlushNow();
        }

        internal void SimulateTick()
        {
            _eyeTracking?.Update();
            if (_isWearer) HVRVixxyPersistentStore.Tick();
        }
        private void UpdateAfterAvatarChangesApplied() => _eyeTracking?.UpdateAfterAvatarChangesApplied();

        private void SetOverride(ref float field, float value, float defaultValue, string address)
        {
            field = value;
            Persist(address, value, defaultValue);
        }

        private void Persist(string address, float value, float defaultValue)
        {
            if (!_isWearer) return;
            if (TryResolvePersistenceKey(address, out var key))
            {
                HVRVixxyPersistentStore.Set(key, value, defaultValue);
            }
        }

        private IEnumerator RestoreRuntimeOverridesNextFrame()
        {
            yield return null;
            _runtimeOverrideEnabled = RestoreOverride(AddressOverrideEnabled, 0f) > 0.5f;
            _runtimeMultiplyX = RestoreOverride(AddressMultiplyX, multiplyX);
            _runtimeMultiplyY = RestoreOverride(AddressMultiplyY, multiplyY);
            _runtimeMaxAngleXDeg = Mathf.Clamp(RestoreOverride(AddressMaxAngleX, MaxAngleLimitDeg), 0f, MaxAngleLimitDeg);
            _runtimeMaxAngleYDeg = Mathf.Clamp(RestoreOverride(AddressMaxAngleY, MaxAngleLimitDeg), 0f, MaxAngleLimitDeg);
        }

        private static float RestoreOverride(string address, float defaultValue)
        {
            return TryResolvePersistenceKey(address, out var key) && HVRVixxyPersistentStore.TryGet(key, out var saved)
                ? saved
                : defaultValue;
        }

        private static bool TryResolvePersistenceKey(string address, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(BasisLocalPlayer.CurrentAvatarUniqueID)) return false;
            key = $"avatar:{BasisLocalPlayer.CurrentAvatarUniqueID}|{address}";
            return true;
        }

        private class EyeTracking_Wearer : IEyeTracking
        {
            private readonly EyeTrackingBoneActuation our;
            private readonly HvrOscEyeProvider _provider = new HvrOscEyeProvider();
            private bool _trackingActive;
            private bool _eyeTrackingActive;
            private bool _suppressStoreEcho;

            public EyeTracking_Wearer(EyeTrackingBoneActuation our)
            {
                this.our = our;
                BasisEyeTrackingManager.Register(_provider);
            }

            public void Update()
            {
                BasisEyeTrackingData data = BasisEyeTrackingManager.Current;

                bool applyGaze = data.HasPerEyeAngles && BasisLocalEyeDriver.IsEnabled;
                if (applyGaze)
                {
                    BasisLocalEyeDriver.SetOverrideEyeOffsets(
                        BasisEyeMath.CanonicalAnglesToEyeOffset(ClampAngles(data.LeftAngles), BasisLocalEyeDriver.calLeft),
                        BasisEyeMath.CanonicalAnglesToEyeOffset(ClampAngles(data.RightAngles), BasisLocalEyeDriver.calRight));
                }
                BasisLocalEyeDriver.Override = applyGaze;

                var localPlayer = BasisLocalPlayer.Instance;
                if (localPlayer != null && localPlayer.FacialBlinkDriver != null)
                {
                    localPlayer.FacialBlinkDriver.SetOverride(data.HasOpenness);
                }

                // HMD-won gaze never touches the variable store — only the OSC face-tracking
                // source submits the FT/v2 eye addresses — so remotes see frozen eyes whenever
                // the wearer's gaze comes from the headset. Mirror the winning angles into the
                // store in the OSC-normalized form the remote actuation expects.
                if (applyGaze && BasisEyeTrackingManager.Arbitration.GazeFromHmd)
                {
                    SubmitHmdGazeToStore(data);
                }
            }

            private float2 ClampAngles(float2 angles)
            {
                return new float2(
                    Mathf.Clamp(angles.x, -our.EffectiveMaxAngleXRad, our.EffectiveMaxAngleXRad),
                    Mathf.Clamp(angles.y, -our.EffectiveMaxAngleYRad, our.EffectiveMaxAngleYRad));
            }

            private void SubmitHmdGazeToStore(BasisEyeTrackingData data)
            {
                // Inverse of HvrOscEyeProvider.SetAngles: canonical = asin(v) * multiply.
                float invX = our.EffectiveMultiplyX == 0f ? 1f : 1f / our.EffectiveMultiplyX;
                float invY = our.EffectiveMultiplyY == 0f ? 1f : 1f / our.EffectiveMultiplyY;
                const float HalfPi = Mathf.PI / 2f;
                float xLeft = Mathf.Sin(Mathf.Clamp(data.LeftAngles.x * invX, -HalfPi, HalfPi));
                float xRight = Mathf.Sin(Mathf.Clamp(data.RightAngles.x * invX, -HalfPi, HalfPi));
                float y = Mathf.Sin(Mathf.Clamp((data.LeftAngles.y + data.RightAngles.y) * 0.5f * invY, -HalfPi, HalfPi));

                // The submits synchronously re-enter our own OnAddressUpdated listeners; without
                // the echo guard they would push the mirrored values into the OSC provider and
                // activate it, flipping arbitration away from the HMD and freezing the bridge.
                _suppressStoreEcho = true;
                try
                {
                    // Eye addresses are FT/-prefixed, so this keeps the activity relay's timeout
                    // fed — the remote only applies eyes while the activity variable is 1.
                    our._activityRelay?.NotifySourceSample(our._eyeLeftXAddress);
                    var store = our.comms.VariableStore;
                    store.Submit(our._eyeTrackingActiveAddress, 1f);
                    store.Submit(our._eyeLeftXAddress, xLeft);
                    store.Submit(our._eyeRightXAddress, xRight);
                    store.Submit(our._eyeYAddress, y);
                }
                finally
                {
                    _suppressStoreEcho = false;
                }
            }

            public void OnTrackingActivityUpdated(bool isTrackingActive)
            {
                _trackingActive = isTrackingActive;

                if (!_trackingActive)
                {
                    TurnOffEyeTracking();
                }
            }

            public void OnEyeAngleAddressUpdated(float xLeft, float xRight, float y)
            {
                if (_suppressStoreEcho) return;
                if (!(xLeft == 0f && xRight == 0f && y == 0f))
                {
                    our.comms.VariableStore.Submit(our._eyeTrackingActiveAddress, 1f);
                }

                _provider.SetAngles(xLeft, xRight, y, our.EffectiveMultiplyX, our.EffectiveMultiplyY);
            }

            public void OnDestroy()
            {
                BasisEyeTrackingManager.Unregister(_provider);
                TurnOffEyeTracking();
            }

            public bool IsEyeTrackingParametersActive => _eyeTrackingActive;

            public void OnEyeTrackingActiveAddressUpdated(bool eyeTrackingActive)
            {
                if (_suppressStoreEcho) return;
                if (_eyeTrackingActive == eyeTrackingActive) return;

                _eyeTrackingActive = eyeTrackingActive;
                _provider.SetActive(eyeTrackingActive);
            }

            public void UpdateAfterAvatarChangesApplied()
            {
            }

            private void TurnOffEyeTracking()
            {
                _eyeTrackingActive = false;
                _provider.SetActive(false);
                our.comms.VariableStore.Submit(our._eyeTrackingActiveAddress, 0f);
                our.comms.VariableStore.Submit(our._eyeLeftXAddress, 0f);
                our.comms.VariableStore.Submit(our._eyeRightXAddress, 0f);
                our.comms.VariableStore.Submit(our._eyeYAddress, 0f);
            }
        }

        private class EyeTracking_Remote : IEyeTracking
        {
            private readonly EyeTrackingBoneActuation our;

            private bool _isTrackingActive;
            private bool _isEyeTrackingActive;
            private float _yClamped;
            private float _xLeftClamped;
            private float _xRightClamped;

            public bool IsEyeTrackingParametersActive => false;

            public EyeTracking_Remote(EyeTrackingBoneActuation our)
            {
                this.our = our;
            }

            public void Update()
            {
            }

            public void OnEyeTrackingActiveAddressUpdated(bool eyeTrackingActive)
            {
                if (_isEyeTrackingActive == eyeTrackingActive) return;

                _isEyeTrackingActive = eyeTrackingActive;
                ReevaluateActivity();
            }

            public void UpdateAfterAvatarChangesApplied()
            {
                if (our.Receiver != null)
                {
                    our.Receiver.EyesAndMouth[0] = _yClamped;
                    our.Receiver.EyesAndMouth[1] = _xLeftClamped;
                    our.Receiver.EyesAndMouth[2] = _yClamped;
                    our.Receiver.EyesAndMouth[3] = _xRightClamped;
                }
            }

            public void OnTrackingActivityUpdated(bool isTrackingActive)
            {
                _isTrackingActive = isTrackingActive;
                ReevaluateActivity();
            }

            private void ReevaluateActivity()
            {
                var shouldOverride = _isTrackingActive && _isEyeTrackingActive;
                if (our.Receiver != null)
                {
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideEye = shouldOverride;
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideBlinking = shouldOverride;
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideViseme = shouldOverride;
                }
            }

            public void OnEyeAngleAddressUpdated(float xLeft, float xRight, float y)
            {
                // We convert to an angle, multiply, clamp, and convert back.
                float maxXRad = our.EffectiveMaxAngleXRad;
                float maxYRad = our.EffectiveMaxAngleYRad;
                _yClamped = -Mathf.Sin(ClampToEyeAngleLimits(Mathf.Asin(-y) * our.EffectiveMultiplyY, maxYRad));
                _xLeftClamped = Mathf.Sin(ClampToEyeAngleLimits(Mathf.Asin(xLeft) * our.EffectiveMultiplyX, maxXRad));
                _xRightClamped = Mathf.Sin(ClampToEyeAngleLimits(Mathf.Asin(xRight) * our.EffectiveMultiplyX, maxXRad));
            }

            public void OnDestroy()
            {
                if (our.Receiver != null)
                {
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideEye = false;
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideBlinking = false;
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideViseme = false;
                }
            }
        }

        private interface IEyeTracking
        {
            void Update();
            void OnTrackingActivityUpdated(bool isTrackingActive);
            void OnEyeAngleAddressUpdated(float xLeft, float xRight, float y);
            void OnDestroy();
            bool IsEyeTrackingParametersActive { get; }
            void OnEyeTrackingActiveAddressUpdated(bool eyeTrackingActive);
            void UpdateAfterAvatarChangesApplied();
        }

        private static float ClampToEyeAngleLimits(float value, float maxRad)
        {
            return Mathf.Clamp(value, -maxRad, maxRad);
        }

        private void OnAddressUpdated(int address, float value)
        {
            if (_eyeTrackingActiveAddress == address)
            {
                _eyeTracking?.OnEyeTrackingActiveAddressUpdated(value > 0.5f);
            }
            else
            {
                float sanitizedValue = SanitizeAndClampEyeValue(value);
                switch (address)
                {
                    case var _ when address == _eyeLeftXAddress:
                        _fEyeLeftX = sanitizedValue;
                        break;
                    case var _ when address == _eyeRightXAddress:
                        _fEyeRightX = sanitizedValue;
                        break;
                    case var _ when address == _eyeYAddress:
                        _fEyeY = sanitizedValue;
                        break;
                    default:
                        return;
                }

                _eyeTracking?.OnEyeAngleAddressUpdated(_fEyeLeftX, _fEyeRightX, _fEyeY);
            }
        }

        private void OnTrackingActivityUpdated(bool isTrackingActive)
        {
            _eyeTracking?.OnTrackingActivityUpdated(isTrackingActive);
        }

        private static float SanitizeAndClampEyeValue(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0f;
            }

            return Mathf.Clamp(value, -1f, 1f);
        }
    }
}

