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
        public static T GetOrCreateSceneInstance<T>(ref T instance) where T : Component
        {
            if (instance != null) return instance;

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
            var avatar = GetAvatar(anyComponentInsideAvatar);
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

        [NonSerialized] internal MutualizedFeatureInterpolator featureInterpolator;

        private bool _isWearer;
        private bool _isTrackingActive;
        private float _lastActivityTime = float.NegativeInfinity;

        // Fired when tracking activity state changes. Scoped per-avatar (this relay
        // is created per BasisAvatar), so local and remote subscribers never collide.
        // Replaces routing via the shared AcquisitionService singleton, which caused
        // remote players' activity state changes to fan-fire into local subscribers.
        public event Action<bool> OnTrackingActivityChanged;

        public bool IsTrackingActive => _isTrackingActive;

        public static FaceTrackingActivityRelay GetOrCreate(BasisAvatar avatar)
        {
            if (avatar == null)
            {
                return null;
            }

            var relay = avatar.GetComponentInChildren<FaceTrackingActivityRelay>(true);
            if (relay != null)
            {
                return relay;
            }

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

            if (acquisition == null)
            {
                acquisition = AcquisitionService.SceneInstance;
            }
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _isWearer = isWearer;
            ApplyTrackingState(false, submitToNetwork: false);
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            _isWearer = isWearer;
            featureInterpolator = CommsNetworking.UsingMutualizedInterpolator(avatar, new List<MutualizedInterpolationRange>
            {
                new MutualizedInterpolationRange
                {
                    addressId = ActivityAddressId,
                    lower = 0f,
                    upper = 1f,
                }
            }, OnInterpolatedDataChanged);

            if (_isWearer && featureInterpolator != null)
            {
                featureInterpolator.SubmitAbsolute(0, _isTrackingActive ? 1f : 0f);
            }
        }

        private void Update()
        {
            if (!_isWearer || !_isTrackingActive)
            {
                return;
            }

            if (Time.unscaledTime - _lastActivityTime > InactivityTimeoutSeconds)
            {
                ApplyTrackingState(false, submitToNetwork: true);
            }
        }

        public void NotifySourceSample()
        {
            if (!_isWearer)
            {
                return;
            }

            _lastActivityTime = Time.unscaledTime;
            if (!_isTrackingActive)
            {
                ApplyTrackingState(true, submitToNetwork: true);
            }
        }

        private void OnInterpolatedDataChanged(float[] current)
        {
            if (_isWearer || current == null || current.Length == 0)
            {
                return;
            }

            ApplyTrackingState(current[0] >= 0.5f, submitToNetwork: false);
        }

        private void ApplyTrackingState(bool isTrackingActive, bool submitToNetwork)
        {
            bool stateChanged = _isTrackingActive != isTrackingActive;
            _isTrackingActive = isTrackingActive;

            if (stateChanged)
            {
                OnTrackingActivityChanged?.Invoke(isTrackingActive);
            }

            if (submitToNetwork && _isWearer && featureInterpolator != null)
            {
                featureInterpolator.SubmitAbsolute(0, isTrackingActive ? 1f : 0f);
            }
        }
    }
}
