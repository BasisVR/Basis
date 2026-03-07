using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using HVR.Basis.Comms.HVRUtility;
using System;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [DefaultExecutionOrder(15010)] // Run after BasisEyeFollowBase
    [AddComponentMenu("HVR.Basis/Comms/Eye Tracking Bone Actuation")]
    public class EyeTrackingBoneActuation : BasisAvatarMonoBehaviour, IHVRInitializable
    {
        private const string EyeLeftX = "FT/v2/EyeLeftX";
        private const string EyeRightX = "FT/v2/EyeRightX";
        private const string EyeY = "FT/v2/EyeY";
        private readonly int EyeLeftXAddress;
        private readonly int EyeRightXAddress;
        private readonly int EyeYAddress;
        private int[] OurAddresses;

        public EyeTrackingBoneActuation()
        {
            EyeLeftXAddress = HVRAddress.AddressToId(EyeLeftX);
            EyeRightXAddress = HVRAddress.AddressToId(EyeRightX);
            EyeYAddress = HVRAddress.AddressToId(EyeY);
            OurAddresses = new[] { EyeLeftXAddress, EyeRightXAddress, EyeYAddress };
        }

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private AcquisitionService acquisition;
        [SerializeField] internal float multiplyX = 1f;
        [SerializeField] internal float multiplyY = 1f;

        public float _fEyeLeftX;
        public float _fEyeRightX;
        public float _fEyeY;
        public bool IsLocal;
        public BasisNetworkReceiver Receiver = null;

        private bool _eyeFollowDriverApplicable;
        private bool _trackingActive;
        private bool _registeredSourceAddresses;
        private FaceTrackingActivityRelay _activityRelay;

        #region NetworkingFields
        // Can be null due to:
        // - Application with no network, or
        // - Network late initialization.
        // Nullability is needed for local tests without initialization scene.
        // - Becomes non-null after HVRAvatarComms.OnAvatarNetworkReady is successfully invoked
        [NonSerialized] internal MutualizedFeatureInterpolator featureInterpolator;
        #endregion

        private void Awake()
        {
            if (avatar == null) avatar = HVRCommsUtil.GetAvatar(this);
            if (acquisition == null) acquisition = AcquisitionService.SceneInstance;
            _activityRelay = FaceTrackingActivityRelay.GetOrCreate(avatar);
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _registeredSourceAddresses = isWearer;
            _eyeFollowDriverApplicable = isWearer;
            _trackingActive = _activityRelay != null && _activityRelay.IsTrackingActive;

            acquisition.RegisterAddresses(new[] { FaceTrackingActivityRelay.ActivityAddressId }, OnTrackingActivityUpdated);
            if (isWearer)
            {
                acquisition.RegisterAddresses(OurAddresses, OnAddressUpdated);
            }
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            HVRLogging.ProtocolDebug("OnReadyBothAvatarAndNetwork called on BlendshapeActuation.");
            IsLocal = isWearer;

            if (!IsLocal)
            {
                Receiver = NetworkedPlayer as BasisNetworkReceiver;
            }

            var mutualizedInterpolationRanges = OurAddresses.Select(address => new MutualizedInterpolationRange
            {
                address = address,
                lower = -1f,
                upper = 1f,
            }).ToList();
            featureInterpolator = CommsNetworking.UsingMutualizedInterpolator(avatar, mutualizedInterpolationRanges, OnInterpolatedDataChanged);

            if (IsLocal)
            {
                SetBuiltInEyeFollowDriverOverriden(_trackingActive);
                if (_trackingActive)
                {
                    SubmitCurrentEyeStateToNetwork();
                }
                else
                {
                    SubmitNeutralEyesToNetwork();
                }
            }
            else if (!_trackingActive)
            {
                ClearRemoteOverrides();
            }
        }

        private void OnEnable()
        {
            if (_trackingActive && _eyeFollowDriverApplicable)
            {
                SetBuiltInEyeFollowDriverOverriden(true);
            }
            BasisNetworkTransmitter.AfterAvatarChanges += ForceUpdate;
        }

        private void OnDisable()
        {
            SetBuiltInEyeFollowDriverOverriden(false);
            BasisNetworkTransmitter.AfterAvatarChanges -= ForceUpdate;
            ClearRemoteOverrides();
        }

        private void OnDestroy()
        {
            if (acquisition != null)
            {
                acquisition.UnregisterAddresses(new[] { FaceTrackingActivityRelay.ActivityAddressId }, OnTrackingActivityUpdated);
                if (_registeredSourceAddresses)
                {
                    acquisition.UnregisterAddresses(OurAddresses, OnAddressUpdated);
                }
            }

            ClearRemoteOverrides();
            SetBuiltInEyeFollowDriverOverriden(false);
        }

        private void OnAddressUpdated(int address, float value)
        {
            if (!_trackingActive)
            {
                return;
            }

            if (address == EyeLeftXAddress)
            {
                _fEyeLeftX = value;
                if (featureInterpolator != null && IsLocal) featureInterpolator.SubmitAbsolute(0, value);
            }
            else if (address == EyeRightXAddress)
            {
                _fEyeRightX = value;
                if (featureInterpolator != null && IsLocal) featureInterpolator.SubmitAbsolute(1, value);
            }
            else if (address == EyeYAddress)
            {
                _fEyeY = value;
                if (featureInterpolator != null && IsLocal) featureInterpolator.SubmitAbsolute(2, value);
            }
        }

        private void OnTrackingActivityUpdated(int address, float value)
        {
            if (address != FaceTrackingActivityRelay.ActivityAddressId)
            {
                return;
            }

            bool isTrackingActive = value >= 0.5f;
            if (_trackingActive == isTrackingActive)
            {
                return;
            }

            _trackingActive = isTrackingActive;
            if (IsLocal)
            {
                SetBuiltInEyeFollowDriverOverriden(_trackingActive);
            }

            if (_trackingActive)
            {
                if (IsLocal)
                {
                    SubmitCurrentEyeStateToNetwork();
                }
                return;
            }

            _fEyeLeftX = 0f;
            _fEyeRightX = 0f;
            _fEyeY = 0f;

            if (IsLocal)
            {
                SubmitNeutralEyesToNetwork();
            }
            else
            {
                SetNeutralRemoteEyes();
                ClearRemoteOverrides();
            }
        }

        private void ForceUpdate()
        {
            if (!_trackingActive)
            {
                return;
            }

            SetEyeRotation(_fEyeLeftX, _fEyeY, EyeSide.Left);
            SetEyeRotation(_fEyeRightX, _fEyeY, EyeSide.Right);
        }

        private void SetEyeRotation(float x, float y, EyeSide side)
        {
            if (_eyeFollowDriverApplicable)
            {
                var xDeg = Mathf.Asin(x) * Mathf.Rad2Deg * multiplyX;
                var yDeg = Mathf.Asin(-y) * Mathf.Rad2Deg * multiplyY;
                Quaternion Euler = Quaternion.Euler(yDeg, xDeg, 0);
                switch (side)
                {
                    // FIXME: This wrongly assumes that eye bone transforms are oriented the same.
                    // This needs to be fixed later by using the work-in-progress normalized muscle system instead.
                    case EyeSide.Left:
                        BasisLocalEyeDriver.leftEyeTransform.localRotation = math.mul(BasisLocalEyeDriver.leftEyeInitialRotation, Euler);
                        break;
                    case EyeSide.Right:
                        BasisLocalEyeDriver.rightEyeTransform.localRotation = math.mul(BasisLocalEyeDriver.rightEyeInitialRotation, Euler);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(side), side, null);
                }
            }
            else if (!IsLocal && Receiver != null)
            {
                Receiver.RemotePlayer.RemoteFaceDriver.OverrideEye = true;
                Receiver.RemotePlayer.RemoteFaceDriver.OverrideBlinking = true;
                switch (side)
                {
                    case EyeSide.Left:
                        Receiver.EyesAndMouth[0] = (y + 1) / 2;
                        Receiver.EyesAndMouth[1] = (x + 1) / 2;
                        break;
                    case EyeSide.Right:
                        Receiver.EyesAndMouth[2] = (y + 1) / 2;
                        Receiver.EyesAndMouth[3] = (x + 1) / 2;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(side), side, null);
                }
            }
        }

        private void SetBuiltInEyeFollowDriverOverriden(bool value)
        {
            BasisLocalEyeDriver.Override = value;
        }

        private void SubmitCurrentEyeStateToNetwork()
        {
            if (!IsLocal || featureInterpolator == null)
            {
                return;
            }

            featureInterpolator.SubmitAbsolute(0, _fEyeLeftX);
            featureInterpolator.SubmitAbsolute(1, _fEyeRightX);
            featureInterpolator.SubmitAbsolute(2, _fEyeY);
        }

        private void SubmitNeutralEyesToNetwork()
        {
            if (!IsLocal || featureInterpolator == null)
            {
                return;
            }

            featureInterpolator.SubmitAbsolute(0, 0f);
            featureInterpolator.SubmitAbsolute(1, 0f);
            featureInterpolator.SubmitAbsolute(2, 0f);
        }

        private void SetNeutralRemoteEyes()
        {
            if (Receiver == null)
            {
                return;
            }

            Receiver.EyesAndMouth[0] = 0.5f;
            Receiver.EyesAndMouth[1] = 0.5f;
            Receiver.EyesAndMouth[2] = 0.5f;
            Receiver.EyesAndMouth[3] = 0.5f;
        }

        private void ClearRemoteOverrides()
        {
            if (IsLocal || Receiver == null)
            {
                return;
            }

            Receiver.RemotePlayer.RemoteFaceDriver.OverrideEye = false;
            Receiver.RemotePlayer.RemoteFaceDriver.OverrideBlinking = false;
        }

        private enum EyeSide
        {
            Left, Right
        }

#region NetworkingMethods
        private void OnInterpolatedDataChanged(float[] current)
        {
            if (!_trackingActive || current == null || current.Length < 3)
            {
                return;
            }

            _fEyeLeftX = current[0];
            _fEyeRightX = current[1];
            _fEyeY = current[2];
        }
#endregion
    }
}
