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
        private const string AddressConvergence = "HVR/EyeTracking/Convergence", AddressOffsetX = "HVR/EyeTracking/OffsetX", AddressOffsetY = "HVR/EyeTracking/OffsetY";
        public const float MaxAngleLimitDeg = 90f, MaxConvergence = 2f, MaxOffsetDeg = 20f, MaxNetMultiply = 4f;
        private static readonly int NetActiveId = HVRAddress.AddressToId("HVR/EyeTracking/Net/Active"), NetMultiplyXId = HVRAddress.AddressToId("HVR/EyeTracking/Net/MultiplyX"), NetMultiplyYId = HVRAddress.AddressToId("HVR/EyeTracking/Net/MultiplyY"), NetMaxAngleXId = HVRAddress.AddressToId("HVR/EyeTracking/Net/MaxAngleX"), NetMaxAngleYId = HVRAddress.AddressToId("HVR/EyeTracking/Net/MaxAngleY"), NetConvergenceId = HVRAddress.AddressToId("HVR/EyeTracking/Net/Convergence"), NetOffsetXId = HVRAddress.AddressToId("HVR/EyeTracking/Net/OffsetX"), NetOffsetYId = HVRAddress.AddressToId("HVR/EyeTracking/Net/OffsetY");
        private static readonly int[] NetSettingIds = { NetActiveId, NetMultiplyXId, NetMultiplyYId, NetMaxAngleXId, NetMaxAngleYId, NetConvergenceId, NetOffsetXId, NetOffsetYId };
        private static readonly int EyeLidLeftId = HVRAddress.AddressToId("FT/v2/EyeLidLeft"), EyeLidRightId = HVRAddress.AddressToId("FT/v2/EyeLidRight");

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
        private float _runtimeConvergence = 1f, _runtimeOffsetXDeg, _runtimeOffsetYDeg;
        private bool _isWearer;
        private bool _registeredForTick;

        public float DefaultMultiplyX => multiplyX;
        public float DefaultMultiplyY => multiplyY;
        public bool RuntimeOverrideEnabled
        {
            get => _runtimeOverrideEnabled;
            set
            {
                _runtimeOverrideEnabled = value;
                Persist(AddressOverrideEnabled, value ? 1f : 0f, 0f);
                PublishNetworkSettings();
            }
        }
        public float RuntimeConvergence { get => _runtimeConvergence; set => SetOverride(ref _runtimeConvergence, Mathf.Clamp(value, 0f, MaxConvergence), 1f, AddressConvergence); }
        public float RuntimeOffsetXDeg { get => _runtimeOffsetXDeg; set => SetOverride(ref _runtimeOffsetXDeg, Mathf.Clamp(value, -MaxOffsetDeg, MaxOffsetDeg), 0f, AddressOffsetX); }
        public float RuntimeOffsetYDeg { get => _runtimeOffsetYDeg; set => SetOverride(ref _runtimeOffsetYDeg, Mathf.Clamp(value, -MaxOffsetDeg, MaxOffsetDeg), 0f, AddressOffsetY); }
        internal float EffectiveConvergence => _runtimeOverrideEnabled ? _runtimeConvergence : 1f;
        internal float EffectiveOffsetXRad => (_runtimeOverrideEnabled ? _runtimeOffsetXDeg : 0f) * Mathf.Deg2Rad;
        internal float EffectiveOffsetYRad => (_runtimeOverrideEnabled ? _runtimeOffsetYDeg : 0f) * Mathf.Deg2Rad;
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
            RefreshDriverRegistration();
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
                _activityRelay.OnExpressionActivityChanged -= OnExpressionActivityUpdated;
                _activityRelay.OnExpressionActivityChanged += OnExpressionActivityUpdated;
            }

            comms.VariableStore.RegisterAddresses(_sourceEyeAddresses, OnAddressUpdated);
            if (!isWearer) comms.VariableStore.RegisterAddresses(NetSettingIds, OnNetSettingUpdated);
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
            RequireNetSetting(NetActiveId, 0f, 1f);
            RequireNetSetting(NetMultiplyXId, -(MaxNetMultiply - 1f), MaxNetMultiply - 1f);
            RequireNetSetting(NetMultiplyYId, -(MaxNetMultiply - 1f), MaxNetMultiply - 1f);
            RequireNetSetting(NetMaxAngleXId, 0f, MaxAngleLimitDeg);
            RequireNetSetting(NetMaxAngleYId, 0f, MaxAngleLimitDeg);
            RequireNetSetting(NetConvergenceId, -(MaxConvergence - 1f), MaxConvergence - 1f);
            RequireNetSetting(NetOffsetXId, -MaxOffsetDeg, MaxOffsetDeg);
            RequireNetSetting(NetOffsetYId, -MaxOffsetDeg, MaxOffsetDeg);
            if (isWearer) PublishNetworkSettings();
            else ApplyNetworkSettings();

            if (!isWearer)
            {
                if (BasisNetworkPlayers.AvatarToPlayer(avatar, out _, out var networkPlayer))
                {
                    Receiver = networkPlayer as BasisNetworkReceiver;
                    _eyeTracking?.UpdateAfterAvatarChangesApplied();
                }
                else
                {
                    BasisDebug.LogError($"Could not find BasisNetworkReceiver for avatar {avatar.name}");
                }
            }
        }

        private void OnEnable()
        {
            RefreshDriverRegistration();
        }

        private void OnDisable()
        {
            RefreshDriverRegistration();
        }

        private void RefreshDriverRegistration()
        {
            bool shouldRegister = _isWearer && isActiveAndEnabled;
            if (shouldRegister == _registeredForTick) return;

            _registeredForTick = shouldRegister;
            if (shouldRegister) HVRCommsUpdateDriver.Register(this);
            else HVRCommsUpdateDriver.Unregister(this);
        }

        private void OnDestroy()
        {
            if (_activityRelay != null)
            {
                _activityRelay.OnTrackingActivityChanged -= OnTrackingActivityUpdated;
                _activityRelay.OnExpressionActivityChanged -= OnExpressionActivityUpdated;
            }

            if (comms != null)
            {
                comms.VariableStore.UnregisterAddresses(_sourceEyeAddresses, OnAddressUpdated);
                if (!_isWearer) comms.VariableStore.UnregisterAddresses(NetSettingIds, OnNetSettingUpdated);
            }
            _eyeTracking?.OnDestroy();
            if (_isWearer) HVRVixxyPersistentStore.FlushNow();
        }

        internal void SimulateTick()
        {
            _eyeTracking?.Update();
        }

        private void SetOverride(ref float field, float value, float defaultValue, string address)
        {
            field = value;
            Persist(address, value, defaultValue);
            PublishNetworkSettings();
        }

        private void RequireNetSetting(int addressId, float min, float max)
        {
            comms.RequireVariable(new HVRVariable
            {
                addressId = addressId,
                initialValue = 0f,
                variableTypeCode = HVRVariableTypeCode.Float,
                needsInterpolation = false,
                min = min,
                max = max,
            });
        }

        private void PublishNetworkSettings()
        {
            if (!_isWearer || comms == null) return;
            var store = comms.VariableStore;
            store.Submit(NetActiveId, _runtimeOverrideEnabled ? 1f : 0f);
            store.Submit(NetMultiplyXId, Mathf.Clamp(_runtimeMultiplyX, 0f, MaxNetMultiply) - 1f);
            store.Submit(NetMultiplyYId, Mathf.Clamp(_runtimeMultiplyY, 0f, MaxNetMultiply) - 1f);
            store.Submit(NetMaxAngleXId, _runtimeMaxAngleXDeg);
            store.Submit(NetMaxAngleYId, _runtimeMaxAngleYDeg);
            store.Submit(NetConvergenceId, _runtimeConvergence - 1f);
            store.Submit(NetOffsetXId, _runtimeOffsetXDeg);
            store.Submit(NetOffsetYId, _runtimeOffsetYDeg);
        }

        private void OnNetSettingUpdated(int address, float value) => ApplyNetworkSettings();

        private void ApplyNetworkSettings()
        {
            if (_isWearer || comms == null) return;
            var store = comms.VariableStore;
            _runtimeOverrideEnabled = store.GetValue(NetActiveId) > 0.5f;
            _runtimeMultiplyX = Mathf.Clamp(store.GetValue(NetMultiplyXId) + 1f, 0f, MaxNetMultiply);
            _runtimeMultiplyY = Mathf.Clamp(store.GetValue(NetMultiplyYId) + 1f, 0f, MaxNetMultiply);
            _runtimeMaxAngleXDeg = Mathf.Clamp(store.GetValue(NetMaxAngleXId), 0f, MaxAngleLimitDeg);
            _runtimeMaxAngleYDeg = Mathf.Clamp(store.GetValue(NetMaxAngleYId), 0f, MaxAngleLimitDeg);
            _runtimeConvergence = Mathf.Clamp(store.GetValue(NetConvergenceId) + 1f, 0f, MaxConvergence);
            _runtimeOffsetXDeg = Mathf.Clamp(store.GetValue(NetOffsetXId), -MaxOffsetDeg, MaxOffsetDeg);
            _runtimeOffsetYDeg = Mathf.Clamp(store.GetValue(NetOffsetYId), -MaxOffsetDeg, MaxOffsetDeg);
            _eyeTracking?.OnEyeAngleAddressUpdated(_fEyeLeftX, _fEyeRightX, _fEyeY);
        }

        internal void ShapeAngles(ref float2 left, ref float2 right)
        {
            float mean = (left.x + right.x) * 0.5f, half = (right.x - left.x) * 0.5f * EffectiveConvergence, offsetX = EffectiveOffsetXRad, offsetY = EffectiveOffsetYRad;
            left = new float2(mean - half - offsetX, left.y + offsetY);
            right = new float2(mean + half + offsetX, right.y + offsetY);
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
            _runtimeConvergence = Mathf.Clamp(RestoreOverride(AddressConvergence, 1f), 0f, MaxConvergence);
            _runtimeOffsetXDeg = Mathf.Clamp(RestoreOverride(AddressOffsetX, 0f), -MaxOffsetDeg, MaxOffsetDeg);
            _runtimeOffsetYDeg = Mathf.Clamp(RestoreOverride(AddressOffsetY, 0f), -MaxOffsetDeg, MaxOffsetDeg);
            PublishNetworkSettings();
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
            private float _lastMirroredLid = -1f;

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
                    float2 left = data.LeftAngles, right = data.RightAngles;
                    our.ShapeAngles(ref left, ref right);
                    BasisLocalEyeDriver.SetOverrideEyeOffsets(
                        BasisEyeMath.CanonicalAnglesToEyeOffset(ClampAngles(left), BasisLocalEyeDriver.calLeft),
                        BasisEyeMath.CanonicalAnglesToEyeOffset(ClampAngles(right), BasisLocalEyeDriver.calRight));
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

                if (applyGaze && !data.HasOpenness)
                {
                    MirrorBlinkToStore();
                }
                else
                {
                    _lastMirroredLid = -1f;
                }
            }

            private void MirrorBlinkToStore()
            {
                var localPlayer = BasisLocalPlayer.Instance;
                var blink = localPlayer != null ? localPlayer.FacialBlinkDriver : null;
                float lid = 0.75f * (1f - Mathf.Clamp01(blink != null && blink.IsEnabled ? blink.CurrentWeight / 100f : 0f));
                if (lid == _lastMirroredLid) return;
                _lastMirroredLid = lid;
                _suppressStoreEcho = true;
                try
                {
                    our.comms.VariableStore.Submit(EyeLidLeftId, lid);
                    our.comms.VariableStore.Submit(EyeLidRightId, lid);
                }
                finally
                {
                    _suppressStoreEcho = false;
                }
            }

            private float2 ClampAngles(float2 angles)
            {
                float maxX = our.EffectiveMaxAngleXRad, maxY = our.EffectiveMaxAngleYRad;
                if (BasisLocalEyeDriverData.MaxLookAngleEnabled)
                {
                    float avatarMaxRad = BasisLocalEyeDriverData.MaxLookAngleDeg * Mathf.Deg2Rad;
                    maxX = Mathf.Min(maxX, avatarMaxRad);
                    maxY = Mathf.Min(maxY, avatarMaxRad);
                }
                return new float2(Mathf.Clamp(angles.x, -maxX, maxX), Mathf.Clamp(angles.y, -maxY, maxY));
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
                var relay = our._activityRelay;
                var expressionDriven = relay == null || !relay.ExpressionActivityReported || relay.IsExpressionTrackingActive;
                if (our.Receiver != null)
                {
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideEye = shouldOverride;
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideBlinking = shouldOverride;
                    our.Receiver.RemotePlayer.RemoteFaceDriver.OverrideViseme = shouldOverride && expressionDriven;
                    if (shouldOverride) UpdateAfterAvatarChangesApplied();
                }
            }

            public void OnEyeAngleAddressUpdated(float xLeft, float xRight, float y)
            {
                // We convert to an angle, multiply, clamp, and convert back.
                float maxXRad = our.EffectiveMaxAngleXRad;
                float maxYRad = our.EffectiveMaxAngleYRad;
                float pitch = Mathf.Asin(y) * our.EffectiveMultiplyY;
                float2 left = new float2(Mathf.Asin(xLeft) * our.EffectiveMultiplyX, pitch), right = new float2(Mathf.Asin(xRight) * our.EffectiveMultiplyX, pitch);
                our.ShapeAngles(ref left, ref right);
                _yClamped = Mathf.Sin(ClampToEyeAngleLimits(left.y, maxYRad));
                _xLeftClamped = Mathf.Sin(ClampToEyeAngleLimits(left.x, maxXRad));
                _xRightClamped = Mathf.Sin(ClampToEyeAngleLimits(right.x, maxXRad));
                UpdateAfterAvatarChangesApplied();
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

        private void OnExpressionActivityUpdated(bool isExpressionActive)
        {
            if (_activityRelay != null) _eyeTracking?.OnTrackingActivityUpdated(_activityRelay.IsTrackingActive);
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

