using System;
using System.Linq;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Eye_Follow;
using Basis.Scripts.Networking.Receivers;
using HVR.Basis.Comms.HVRUtility;
using LiteNetLib;
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
        private static readonly string[] OurAddresses = { EyeLeftX, EyeRightX, EyeY };

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private AcquisitionService acquisition;
        [SerializeField] internal float multiplyX = 1f;
        [SerializeField] internal float multiplyY = 1f;

        public float _fEyeLeftX;
        public float _fEyeRightX;
        public float _fEyeY;
        public bool _anyAddressUpdated;
        public bool IsLocal;
        #region NetworkingFields
        // Can be null due to:
        // - Application with no network, or
        // - Network late initialization.
        // Nullability is needed for local tests without initialization scene.
        // - Becomes non-null after HVRAvatarComms.OnAvatarNetworkReady is successfully invoked
        [NonSerialized] internal MutualizedFeatureInterpolator featureInterpolator;
        [NonSerialized] public BasisLocalEyeDriver _eyeFollowDriverLateInit;
        #endregion
        public BasisNetworkReceiver Receiver = null;
        private bool _eyeFollowDriverApplicable;

        private void Awake()
        {
            if (avatar == null) avatar = HVRCommsUtil.GetAvatar(this);
            if (acquisition == null) acquisition = AcquisitionService.SceneInstance;
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            if (isWearer)
            {
                acquisition.RegisterAddresses(OurAddresses, OnAddressUpdated);
                _eyeFollowDriverApplicable = true;
                _eyeFollowDriverLateInit = BasisLocalPlayer.Instance.LocalEyeDriver;
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

            var mutualizedInterpolationRanges = OurAddresses.Select(key => new MutualizedInterpolationRange
            {
                key = key,
                lower = -1f,
                upper = 1f,
            }).ToList();
            featureInterpolator = CommsNetworking.UsingMutualizedInterpolator(avatar, mutualizedInterpolationRanges, OnInterpolatedDataChanged);
        }

        private void OnEnable()
        {
            SetBuiltInEyeFollowDriverOverriden(true);
        }

        private void OnDisable()
        {
            SetBuiltInEyeFollowDriverOverriden(false);
        }

        private void OnDestroy()
        {
            if (IsLocal)
            {
                acquisition.UnregisterAddresses(OurAddresses, OnAddressUpdated);
            }
        }

        private void OnAddressUpdated(string address, float value)
        {
            // FIXME: Temp fix, we'll need to hook to NetworkReady instead.
            // This is a quick fix so that we don't need to reupload the avatar.
            _anyAddressUpdated = _anyAddressUpdated || value != 0f;
            if (_anyAddressUpdated && _eyeFollowDriverLateInit != null)
            {
                _eyeFollowDriverLateInit.IsEnabled = false;
            }

            switch (address)
            {
                case EyeLeftX:
                {
                    _fEyeLeftX = value;
                    if (featureInterpolator != null) featureInterpolator.SubmitAbsolute(0, value);
                    break;
                }
                case EyeRightX:
                {
                    _fEyeRightX = value;
                    if (featureInterpolator != null) featureInterpolator.SubmitAbsolute(1, value);
                    break;
                }
                case EyeY:
                {
                    _fEyeY = value;
                    if (featureInterpolator != null) featureInterpolator.SubmitAbsolute(2, value);
                    break;
                }
            }
        }
/* this should not be required? (lD)
        private void Update()
        {
            ForceUpdate();
        }
*/
        private void LateUpdate()
        {
            ForceUpdate();
        }

        private void ForceUpdate()
        {
            if (IsLocal && !_anyAddressUpdated)
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
                        _eyeFollowDriverLateInit.leftEyeTransform.localRotation = math.mul(_eyeFollowDriverLateInit.leftEyeInitialRotation, Euler);
                        break;
                    case EyeSide.Right:
                        _eyeFollowDriverLateInit.rightEyeTransform.localRotation = math.mul(_eyeFollowDriverLateInit.rightEyeInitialRotation, Euler);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(side), side, null);
                }
            }
            else
            {
                if (IsLocal && Receiver != null)
                {
                    switch (side)
                    {
                        case EyeSide.Left:
                            float result0 = (y + 1) / 2;
                            float result1 = (x + 1) / 2;
                            Receiver.EyesAndMouth[0] = result0;
                            Receiver.EyesAndMouth[1] = result1;
                            break;
                        case EyeSide.Right:
                            result0 = (y + 1) / 2;
                            result1 = (x + 1) / 2;
                            Receiver.EyesAndMouth[2] = result0;
                            Receiver.EyesAndMouth[3] = result1;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(side), side, null);
                    }
                }
            }
        }

        private void SetBuiltInEyeFollowDriverOverriden(bool value)
        {
            if (_eyeFollowDriverLateInit == null)
            {
                return;
            }
            BasisLocalEyeDriver.Override = value;
        }

        private enum EyeSide
        {
            Left, Right
        }

#region NetworkingMethods
        private void OnInterpolatedDataChanged(float[] current)
        {
            _fEyeLeftX = current[0];
            _fEyeRightX = current[1];
            _fEyeY = current[2];
        }
#endregion
    }
}
