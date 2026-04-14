using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using HVR.Basis.Comms.HVRUtility;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [DefaultExecutionOrder(15010)] // Run after BasisEyeFollowBase
    [AddComponentMenu("HVR.Basis/Comms/Eye Tracking Bone Actuation")]
    public class EyeTrackingBoneActuation : BasisAvatarMonoBehaviour, IHVRInitializable
    {
        new public static bool VisibleInAvatarMenu = false;
        private const string EyeLeftX = "FT/v2/EyeLeftX";
        private const string EyeRightX = "FT/v2/EyeRightX";
        private const string EyeY = "FT/v2/EyeY";
        private const string EyeTrackingActive = "HVR/Internal/EyeTrackingActive";
        private const float EyeParameterInactivityTimeoutSeconds = 0.5f;

        private const int LeftEyeFeatureIndex = 0;
        private const int RightEyeFeatureIndex = 1;
        private const int EyeYFeatureIndex = 2;
        private const int EyeTrackingActiveFeatureIndex = 3;

        private readonly int _eyeLeftXAddress;
        private readonly int _eyeRightXAddress;
        private readonly int _eyeYAddress;
        private readonly int _eyeTrackingActiveAddress;
        private readonly int[] _sourceEyeAddresses;

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
        private bool _eyeTrackingParametersActive;
        public bool IsTrackingActive => _trackingActive;
        public bool IsEyeTrackingParametersActive => _eyeTrackingParametersActive;
        private float _lastEyeParameterSampleTime = float.NegativeInfinity;
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

        public EyeTrackingBoneActuation()
        {
            _eyeLeftXAddress = HVRAddress.AddressToId(EyeLeftX);
            _eyeRightXAddress = HVRAddress.AddressToId(EyeRightX);
            _eyeYAddress = HVRAddress.AddressToId(EyeY);
            _eyeTrackingActiveAddress = HVRAddress.AddressToId(EyeTrackingActive);
            _sourceEyeAddresses = new[] { _eyeLeftXAddress, _eyeRightXAddress, _eyeYAddress };
        }

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
            _eyeTrackingParametersActive = false;
            _lastEyeParameterSampleTime = float.NegativeInfinity;

            acquisition.RegisterAddresses(new[] { FaceTrackingActivityRelay.ActivityAddressId }, OnTrackingActivityUpdated);
            if (isWearer)
            {
                acquisition.RegisterAddresses(_sourceEyeAddresses, OnAddressUpdated);
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

            var mutualizedInterpolationRanges = new List<MutualizedInterpolationRange>
            {
                new MutualizedInterpolationRange { address = _eyeLeftXAddress, lower = -1f, upper = 1f },
                new MutualizedInterpolationRange { address = _eyeRightXAddress, lower = -1f, upper = 1f },
                new MutualizedInterpolationRange { address = _eyeYAddress, lower = -1f, upper = 1f },
                new MutualizedInterpolationRange { address = _eyeTrackingActiveAddress, lower = 0f, upper = 1f }
            };
            featureInterpolator = CommsNetworking.UsingMutualizedInterpolator(avatar, mutualizedInterpolationRanges, OnInterpolatedDataChanged);
            bool shouldApply = ShouldApplyEyeTracking();
            if (IsLocal)
            {
                SubmitEyeTrackingParameterStateToNetwork();
                SetBuiltInEyeFollowDriverOverriden(shouldApply);
                if (shouldApply)
                {
                    SubmitCurrentEyeStateToNetwork();
                }
                else
                {
                    SubmitNeutralEyesToNetwork();
                }
            }
            else if (!shouldApply)
            {
                ClearRemoteOverrides();
            }
        }

        private void OnEnable()
        {
            if (ShouldApplyEyeTracking() && _eyeFollowDriverApplicable)
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
                    acquisition.UnregisterAddresses(_sourceEyeAddresses, OnAddressUpdated);
                }
            }

            ClearRemoteOverrides();
            SetBuiltInEyeFollowDriverOverriden(false);
        }

        private void Update()
        {
            if (!_eyeFollowDriverApplicable || !_trackingActive || !_eyeTrackingParametersActive)
            {
                return;
            }

            if (Time.unscaledTime - _lastEyeParameterSampleTime > EyeParameterInactivityTimeoutSeconds)
            {
                SetLocalEyeParameterState(false);
                SetBuiltInEyeFollowDriverOverriden(false);
                SubmitNeutralEyesToNetwork();
            }
        }

        private void OnAddressUpdated(int address, float value)
        {
            if (!_trackingActive)
            {
                return;
            }

            float sanitizedValue = SanitizeAndClampEyeValue(value);
            switch (address)
            {
                case var _ when address == _eyeLeftXAddress:
                    _fEyeLeftX = sanitizedValue;
                    if (featureInterpolator != null && IsLocal) featureInterpolator.SubmitAbsolute(LeftEyeFeatureIndex, sanitizedValue);
                    break;
                case var _ when address == _eyeRightXAddress:
                    _fEyeRightX = sanitizedValue;
                    if (featureInterpolator != null && IsLocal) featureInterpolator.SubmitAbsolute(RightEyeFeatureIndex, sanitizedValue);
                    break;
                case var _ when address == _eyeYAddress:
                    _fEyeY = sanitizedValue;
                    if (featureInterpolator != null && IsLocal) featureInterpolator.SubmitAbsolute(EyeYFeatureIndex, sanitizedValue);
                    break;
                default:
                    return;
            }

            if (_eyeFollowDriverApplicable)
            {
                _lastEyeParameterSampleTime = Time.unscaledTime;
                if (!_eyeTrackingParametersActive)
                {
                    SetLocalEyeParameterState(true);
                    SetBuiltInEyeFollowDriverOverriden(true);
                }
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
            if (_eyeFollowDriverApplicable && !_trackingActive)
            {
                SetLocalEyeParameterState(false);
            }

            bool shouldApplyEyeTracking = ShouldApplyEyeTracking();
            if (_eyeFollowDriverApplicable)
            {
                SetBuiltInEyeFollowDriverOverriden(shouldApplyEyeTracking);
            }

            if (_trackingActive)
            {
                if (_eyeFollowDriverApplicable)
                {
                    if (shouldApplyEyeTracking)
                    {
                        SubmitCurrentEyeStateToNetwork();
                    }
                    else
                    {
                        SubmitNeutralEyesToNetwork();
                    }
                }
                else if (!shouldApplyEyeTracking)
                {
                    ClearRemoteOverrides();
                }
                return;
            }

            ResetEyeValuesToZero();
            _eyeTrackingParametersActive = false;

            if (_eyeFollowDriverApplicable)
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
            if (!ShouldApplyEyeTracking())
            {
                return;
            }

            SetEyeRotation(_fEyeLeftX, _fEyeY, EyeSide.Left);
            SetEyeRotation(_fEyeRightX, _fEyeY, EyeSide.Right);
        }

        private void SetEyeRotation(float x, float y, EyeSide side)
        {
            x = SanitizeAndClampEyeValue(x);
            y = SanitizeAndClampEyeValue(y);

            if (_eyeFollowDriverApplicable)
            {

                // Uses EyeCalibration from BasisLocalEyeDriver to handle arbitrary eye bone orientations for local player.
                // Retaining Hai's original FIXME: This could/should be replaced by a WIP normalized muscle system

                float xRad = Mathf.Asin(x) * multiplyX;
                float yRad = Mathf.Asin(-y) * multiplyY;
                quaternion yaw = quaternion.AxisAngle(new float3(0, 1, 0), xRad);
                quaternion pitch = quaternion.AxisAngle(new float3(1, 0, 0), yRad);
                quaternion canonical = math.mul(yaw, pitch);

                switch (side)
                {
                    case EyeSide.Left:
                    {
                        var cal = BasisLocalEyeDriver.calLeft;
                        quaternion rigOffset = math.mul(math.mul(cal.basis, canonical), cal.invBasis);
                        BasisLocalEyeDriver.leftEyeTransform.localRotation =
                            math.mul(cal.initialRotation, rigOffset);
                        break;
                    }
                    case EyeSide.Right:
                    {
                        var cal = BasisLocalEyeDriver.calRight;
                        quaternion rigOffset = math.mul(math.mul(cal.basis, canonical), cal.invBasis);
                        BasisLocalEyeDriver.rightEyeTransform.localRotation =
                            math.mul(cal.initialRotation, rigOffset);
                        break;
                    }
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
            if (!_eyeFollowDriverApplicable)
            {
                return;
            }

            BasisLocalEyeDriver.Override = value;

            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer != null && localPlayer.FacialBlinkDriver != null)
            {
                localPlayer.FacialBlinkDriver.SetOverride(value);
            }
        }

        private void SubmitCurrentEyeStateToNetwork()
        {
            if (!IsLocal || featureInterpolator == null)
            {
                return;
            }

            featureInterpolator.SubmitAbsolute(LeftEyeFeatureIndex, SanitizeAndClampEyeValue(_fEyeLeftX));
            featureInterpolator.SubmitAbsolute(RightEyeFeatureIndex, SanitizeAndClampEyeValue(_fEyeRightX));
            featureInterpolator.SubmitAbsolute(EyeYFeatureIndex, SanitizeAndClampEyeValue(_fEyeY));
        }

        private void SubmitNeutralEyesToNetwork()
        {
            if (!IsLocal || featureInterpolator == null)
            {
                return;
            }

            featureInterpolator.SubmitAbsolute(LeftEyeFeatureIndex, 0f);
            featureInterpolator.SubmitAbsolute(RightEyeFeatureIndex, 0f);
            featureInterpolator.SubmitAbsolute(EyeYFeatureIndex, 0f);
        }

        private void SetLocalEyeParameterState(bool isActive)
        {
            _eyeTrackingParametersActive = isActive;
            _lastEyeParameterSampleTime = isActive ? Time.unscaledTime : float.NegativeInfinity;
            SubmitEyeTrackingParameterStateToNetwork();
        }

        private void SubmitEyeTrackingParameterStateToNetwork()
        {
            if (!IsLocal || featureInterpolator == null)
            {
                return;
            }

            featureInterpolator.SubmitAbsolute(EyeTrackingActiveFeatureIndex, _eyeTrackingParametersActive ? 1f : 0f);
        }

        private bool ShouldApplyEyeTracking()
        {
            return _trackingActive && _eyeTrackingParametersActive;
        }

        private static float SanitizeAndClampEyeValue(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0f;
            }

            return Mathf.Clamp(value, -1f, 1f);
        }

        private void ResetEyeValuesToZero()
        {
            _fEyeLeftX = 0f;
            _fEyeRightX = 0f;
            _fEyeY = 0f;
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
            if (current == null)
            {
                return;
            }

            if (!IsLocal)
            {
                if (current.Length > EyeTrackingActiveFeatureIndex)
                {
                    _eyeTrackingParametersActive = current[EyeTrackingActiveFeatureIndex] >= 0.5f;
                }
                else
                {
                    // Legacy compatibility with senders that only stream 3 values.
                    _eyeTrackingParametersActive = true;
                }
            }

            bool shouldApply = ShouldApplyEyeTracking();
            if (!shouldApply || current.Length < 3)
            {
                if (!IsLocal && !shouldApply)
                {
                    ClearRemoteOverrides();
                }
                return;
            }

            _fEyeLeftX = SanitizeAndClampEyeValue(current[LeftEyeFeatureIndex]);
            _fEyeRightX = SanitizeAndClampEyeValue(current[RightEyeFeatureIndex]);
            _fEyeY = SanitizeAndClampEyeValue(current[EyeYFeatureIndex]);
        }
#endregion
    }
}

