using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms
{
    public static class HVRCommsUtil
    {
        private static bool _applicationIsQuitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeQuitGuard()
        {
            _applicationIsQuitting = false;
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;
        }

        private static void OnApplicationQuitting()
        {
            _applicationIsQuitting = true;
        }

        public static T GetOrCreateSceneInstance<T>(ref T instance) where T : Component
        {
            if (instance != null) return instance;
            if (_applicationIsQuitting) return null;

            var go = new GameObject($"HVR.{typeof(T).Name}");
            Object.DontDestroyOnLoad(go);
            instance = go.AddComponent<T>();

            return instance;
        }

        public static BasisAvatar GetAvatar(Component anyComponentInsideAvatar)
        {
            return anyComponentInsideAvatar.GetComponentInParent<BasisAvatar>(true);
        }

        public static HVRAvatarComms GetComms(Component anyComponentInsideAvatar)
        {
            return GetComms(GetAvatar(anyComponentInsideAvatar));
        }

        public static HVRAvatarComms GetComms(BasisAvatar avatar)
        {
            if (avatar == null) return null;

            return avatar.GetComponentInChildren<HVRAvatarComms>(true);
        }

        /// Semantically used to sanitize a serializable field of objects provided by an End User.<br/>
        /// Given a nullable array of Unity Objects that may contain null-Destroy Objects,
        /// return a non-null array of Unity Objects that does not contain null-Destroy Objects.
        public static T[] SlowSanitizeEndUserProvidedObjectArray<T>(T[] objectsNullable) where T : Object
        {
            if (objectsNullable == null) return Array.Empty<T>();

            return objectsNullable.Where(t => t).ToArray();
        }

        /// Semantically used to sanitize a serializable field of structs provided by an End User.<br/>
        /// Returns itself, or an empty array if the parameter is null.
        public static T[] SlowSanitizeEndUserProvidedStructArray<T>(T[] structuresNullable) where T : struct
        {
            if (structuresNullable == null) return Array.Empty<T>();

            return structuresNullable;
        }

        public static (List<T> matches, List<T> rest) Partition<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            var matches = new List<T>();
            var rest = new List<T>();
            foreach (var item in source)
            {
                if (predicate(item)) matches.Add(item);
                else rest.Add(item);
            }
            return (matches, rest);
        }
    }

    [AddComponentMenu("HVR.Basis/Comms/Internal/Face Tracking Activity Relay")]
    public class FaceTrackingActivityRelay : MonoBehaviour, IHVRInitializable
    {
        public const string ActivityAddress = "HVR/Internal/FaceTrackingActive";
        public static readonly int ActivityAddressId = HVRAddress.AddressToId(ActivityAddress);
        public const float InactivityTimeoutSeconds = 0.5f;

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private AcquisitionService acquisition;

        private bool _isWearer;
        private bool _isTrackingActive;
        private float _lastActivityTime = float.NegativeInfinity;
        private HVRAvatarComms comms;
        private readonly Dictionary<int, bool> _isFaceTrackingAddress = new();
        private bool _isInitialized;
        private bool _isRegistered;

        // Fired when tracking activity state changes. Scoped per-avatar (this relay
        // is created per BasisAvatar), so local and remote subscribers never collide.
        // Replaces routing via the shared AcquisitionService singleton, which caused
        // remote players' activity state changes to fan-fire into local subscribers.
        public event Action<bool> OnTrackingActivityChanged;

        public bool IsTrackingActive => _isTrackingActive;

        public static FaceTrackingActivityRelay GetOrCreate(BasisAvatar avatar)
        {
            return GetOrCreate(avatar, out _);
        }

        public static FaceTrackingActivityRelay GetOrCreate(BasisAvatar avatar, out bool created)
        {
            created = false;
            if (avatar == null)
            {
                return null;
            }

            var relay = avatar.GetComponentInChildren<FaceTrackingActivityRelay>(true);
            if (relay != null)
            {
                return relay;
            }

            created = true;
            var relayRoot = new GameObject("Generated__FaceTrackingActivityRelay")
            {
                transform =
                {
                    parent = avatar.transform,
                }
            };
            return relayRoot.AddComponent<FaceTrackingActivityRelay>();
        }

        private void Awake()
        {
            if (avatar == null)
            {
                avatar = HVRCommsUtil.GetAvatar(this);
            }
            comms = HVRCommsUtil.GetComms(this);

            if (acquisition == null)
            {
                acquisition = AcquisitionService.SceneInstance;
            }
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _isInitialized = true;
            _isWearer = isWearer;
            RefreshDriverRegistration();

            comms.VariableStore.RegisterAddresses(new[] { ActivityAddressId }, OnAddressUpdated);
            if (isWearer)
            {
                comms.VariableStore.SubmitOrDefineDefaultValue(ActivityAddressId, 0f);
            }
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            _isWearer = isWearer;
            RefreshDriverRegistration();
            comms.RequireVariable(new HVRVariable
            {
                addressId = ActivityAddressId,
                initialValue = 0f,
                variableTypeCode = HVRVariableTypeCode.Float,
                needsInterpolation = false,
                min = 0f,
                max = 1f,
            });
        }

        private void OnEnable() => RefreshDriverRegistration();
        private void OnDisable() => RefreshDriverRegistration();

        private void RefreshDriverRegistration()
        {
            bool shouldRegister = _isWearer && isActiveAndEnabled;
            if (shouldRegister == _isRegistered) return;

            _isRegistered = shouldRegister;
            if (shouldRegister) HVRCommsUpdateDriver.Register(this);
            else HVRCommsUpdateDriver.Unregister(this);
        }

        internal void SimulateTick()
        {
            if (!_isInitialized || !_isWearer || !_isTrackingActive)
            {
                return;
            }

            if (Time.unscaledTime - _lastActivityTime > InactivityTimeoutSeconds)
            {
                comms.VariableStore.Submit(ActivityAddressId, 0f);
            }
        }

        public void NotifySourceSample(int addressId)
        {
            if (global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.Capture)
            {
                System.Threading.Interlocked.Increment(ref global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.HvrActivitySamples);
            }
            if (!_isWearer)
            {
                return;
            }

            if (_isFaceTrackingAddress.TryGetValue(addressId, out var isFaceTrackingAddress))
            {
                if (!isFaceTrackingAddress) return;
            }
            else
            {
                var stringAddress = HVRAddress.ResolveKnownAddressFromId(addressId);
                var startsWithFaceTracking = stringAddress.StartsWith("FT/");

                _isFaceTrackingAddress[addressId] = startsWithFaceTracking;
                if (!startsWithFaceTracking) return;
            }

            _lastActivityTime = Time.unscaledTime;
            if (!_isTrackingActive)
            {
                comms.VariableStore.Submit(ActivityAddressId, 1f);
            }
        }

        private void OnAddressUpdated(int addressId, float value)
        {
            if (addressId != ActivityAddressId) return;

            bool isTrackingActive = value > 0.5f;
            bool stateChanged = _isTrackingActive != isTrackingActive;
            _isTrackingActive = isTrackingActive;

            if (stateChanged)
            {
                OnTrackingActivityChanged?.Invoke(isTrackingActive);
            }
        }
    }
}
