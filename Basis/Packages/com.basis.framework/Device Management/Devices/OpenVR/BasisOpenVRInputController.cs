using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    [DefaultExecutionOrder(15001)]
    public class BasisOpenVRInputController : BasisInput
    {
        public OpenVRDevice Device;
        public SteamVR_Input_Sources inputSource;
        public SteamVR_Action_Pose DeviceposeAction = SteamVR_Input.GetAction<SteamVR_Action_Pose>("Pose");
        public bool HasOnUpdate = false;
        public void Initialize(OpenVRDevice device, string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, SteamVR_Input_Sources SteamVR_Input_Sources)
        {
            if (HasOnUpdate && DeviceposeAction != null)
            {
                DeviceposeAction[inputSource].onUpdate -= SteamVR_Behavior_Pose_OnUpdate;
                HasOnUpdate = false;
            }
            inputSource = SteamVR_Input_Sources;
            Device = device;
            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
            if (DeviceposeAction != null)
            {
                if (HasOnUpdate == false)
                {
                    DeviceposeAction[inputSource].onUpdate += SteamVR_Behavior_Pose_OnUpdate;
                    HasOnUpdate = true;
                }
            }
            BasisDebug.Log("set Controller to inputSource " + inputSource + " bone role " + basisBoneTrackedRole);
        }
        public new void OnDestroy()
        {
            if (DeviceposeAction != null)
            {
                DeviceposeAction[inputSource].onUpdate -= SteamVR_Behavior_Pose_OnUpdate;
                HasOnUpdate = false;
            }
            historyBuffer.Clear();
            base.OnDestroy();
        }
        public override void DoPollData()
        {
            if (SteamVR.active)
            {
                CurrentInputState.GripButton = SteamVR_Actions._default.Grip.GetState(inputSource);
                CurrentInputState.SystemOrMenuButton = SteamVR_Actions._default.System.GetState(inputSource);
                CurrentInputState.PrimaryButtonGetState = SteamVR_Actions._default.A_Button.GetState(inputSource);
                CurrentInputState.SecondaryButtonGetState = SteamVR_Actions._default.B_Button.GetState(inputSource);
                CurrentInputState.Primary2DAxisClick = SteamVR_Actions._default.JoyStickClick.GetState(inputSource);
                CurrentInputState.Primary2DAxis = SteamVR_Actions._default.Joystick.GetAxis(inputSource);
                CurrentInputState.Trigger = SteamVR_Actions._default.Trigger.GetAxis(inputSource);
                CurrentInputState.SecondaryTrigger = SteamVR_Actions._default.HandTrigger.GetAxis(inputSource);
                CurrentInputState.Secondary2DAxis = SteamVR_Actions._default.TrackPad.GetAxis(inputSource);
                CurrentInputState.Secondary2DAxisClick = SteamVR_Actions._default.TrackPadTouched.GetState(inputSource);
                switch (inputSource)
                {
                    case SteamVR_Input_Sources.LeftHand:
                        {
                            SteamVR_Action_Skeleton LeftHand = SteamVR_Actions.default_SkeletonLeftHand;
                            UpdateHandPose(BasisLocalPlayer.Instance.LocalHandDriver.LeftHand, LeftHand, inputSource);
                            break;
                        }

                    case SteamVR_Input_Sources.RightHand:
                        {
                            SteamVR_Action_Skeleton RightHand = SteamVR_Actions.default_SkeletonRightHand;
                            UpdateHandPose(BasisLocalPlayer.Instance.LocalHandDriver.RightHand, RightHand, inputSource);
                            break;
                        }
                }
                UpdatePlayerControl();
            }
        }
        private void UpdateHandPose(BasisFingerPose hand, SteamVR_Action_Skeleton skeletonAction, SteamVR_Input_Sources SteamVR_Input_Sources)
        {
            BonePositions = skeletonAction.bonePositions;
            BoneRotations = skeletonAction.boneRotations;

            // Finger curls (openness)
            var (thumb, index, middle, ring, pinky) = GetFingerOpenness(BoneRotations, SteamVR_Input_Sources);
            hand.ThumbPercentage[0] = thumb;
            hand.IndexPercentage[0] = index;
            hand.MiddlePercentage[0] = middle;
            hand.RingPercentage[0] = ring;
            hand.LittlePercentage[0] = pinky;

            // Finger splays
            var (indexSplay, middleSplay, ringSplay, pinkySplay) = GetFingerSplay(skeletonAction);
            hand.IndexPercentage[1] = indexSplay;
            hand.MiddlePercentage[1] = middleSplay;
            hand.RingPercentage[1] = ringSplay;
            hand.LittlePercentage[1] = pinkySplay;
        }
        public (float thumb, float index, float middle, float ring, float pinky) GetFingerOpenness(Quaternion[] currentPose, SteamVR_Input_Sources Source)
        {
            float thumb = Remap01ToMinus1To1(CalculateFingerOpenness(currentPose, ThumbJoints, Source));
            float index = Remap01ToMinus1To1(CalculateFingerOpenness(currentPose, IndexJoints, Source));
            float middle = Remap01ToMinus1To1(CalculateFingerOpenness(currentPose, MiddleJoints, Source));
            float ring = Remap01ToMinus1To1(CalculateFingerOpenness(currentPose, RingJoints, Source));
            float pinky = Remap01ToMinus1To1(CalculateFingerOpenness(currentPose, PinkyJoints, Source));

            return (thumb, index, middle, ring, pinky);
        }

        private float CalculateFingerOpenness(Quaternion[] Pose, int[] Joints, SteamVR_Input_Sources Source)
        {
            switch (Source)
            {
                case SteamVR_Input_Sources.LeftHand:
                    return CalculateFingerOpenness(Pose, LeftOpenHand, LeftClosedHand, Joints);
                case SteamVR_Input_Sources.RightHand:
                    return CalculateFingerOpenness(Pose, RightOpenHand, RightClosedHand, Joints);
            }
            return 0.5f;
        }
        float Remap01ToMinus1To1(float value)
        {
            return (0.75f - value) * 2f - 0.75f;
        }
        private (float index, float middle, float ring, float pinky) GetFingerSplay(SteamVR_Action_Skeleton skeletonAction)
        {
            // Get wrist position
            Vector3 wrist = skeletonAction.bonePositions[(int)SteamVR_Skeleton_JointIndexEnum.wrist];

            // Get metacarpal positions
            Vector3 indexMeta = skeletonAction.bonePositions[(int)SteamVR_Skeleton_JointIndexEnum.indexMetacarpal];
            Vector3 middleMeta = skeletonAction.bonePositions[(int)SteamVR_Skeleton_JointIndexEnum.middleMetacarpal];
            Vector3 ringMeta = skeletonAction.bonePositions[(int)SteamVR_Skeleton_JointIndexEnum.ringMetacarpal];
            Vector3 pinkyMeta = skeletonAction.bonePositions[(int)SteamVR_Skeleton_JointIndexEnum.pinkyMetacarpal];

            // Compute direction vectors
            Vector3 indexDir = (indexMeta - wrist).normalized;
            Vector3 middleDir = (middleMeta - wrist).normalized;
            Vector3 ringDir = (ringMeta - wrist).normalized;
            Vector3 pinkyDir = (pinkyMeta - wrist).normalized;

            // Calculate splay angles
            float indexSplay = Vector3.Angle(indexDir, middleDir);
            float middleSplay = Vector3.Angle(middleDir, ringDir);
            float ringSplay = Vector3.Angle(ringDir, pinkyDir);

            // Normalize to 0-1 range based on max expected splay (30-45 degrees is typical max)
            float maxSplay = 40f;

            return (
                Mathf.Clamp01(indexSplay / maxSplay),
                Mathf.Clamp01(middleSplay / maxSplay),
                Mathf.Clamp01(ringSplay / maxSplay),
                0f // pinky has no neighbor after it
            );
        }
        public static float GetQuaternionInterpolationValue(Quaternion start, Quaternion end, Quaternion q)
        {
            // Ensure quaternions are normalized
            start = Quaternion.Normalize(start);
            end = Quaternion.Normalize(end);
            q = Quaternion.Normalize(q);

            // Compute the angle between start and end
            float totalAngle = Quaternion.Angle(start, end);

            if (totalAngle == 0f)
                return 0f; // start and end are the same

            // Compute the angle from start to q
            float currentAngle = Quaternion.Angle(start, q);

            // Clamp to [0,1] just in case of numerical overshoot
            return Mathf.Clamp01(currentAngle / totalAngle);
        }

        private static readonly int[] ThumbJoints = {
    (int)SteamVR_Skeleton_JointIndexEnum.thumbMetacarpal,
    (int)SteamVR_Skeleton_JointIndexEnum.thumbProximal,
    (int)SteamVR_Skeleton_JointIndexEnum.thumbMiddle,
    // thumbDistal removed
    (int)SteamVR_Skeleton_JointIndexEnum.thumbTip
};

        private static readonly int[] IndexJoints = {
    (int)SteamVR_Skeleton_JointIndexEnum.indexMetacarpal,
    (int)SteamVR_Skeleton_JointIndexEnum.indexProximal,
    (int)SteamVR_Skeleton_JointIndexEnum.indexMiddle,
    // indexDistal removed
    (int)SteamVR_Skeleton_JointIndexEnum.indexTip
};

        private static readonly int[] MiddleJoints = {
    (int)SteamVR_Skeleton_JointIndexEnum.middleMetacarpal,
    (int)SteamVR_Skeleton_JointIndexEnum.middleProximal,
    (int)SteamVR_Skeleton_JointIndexEnum.middleMiddle,
    // middleDistal removed
    (int)SteamVR_Skeleton_JointIndexEnum.middleTip
};

        private static readonly int[] RingJoints = {
    (int)SteamVR_Skeleton_JointIndexEnum.ringMetacarpal,
    (int)SteamVR_Skeleton_JointIndexEnum.ringProximal,
    (int)SteamVR_Skeleton_JointIndexEnum.ringMiddle,
    // ringDistal removed
    (int)SteamVR_Skeleton_JointIndexEnum.ringTip
};

        private static readonly int[] PinkyJoints = {
    (int)SteamVR_Skeleton_JointIndexEnum.pinkyMetacarpal,
    (int)SteamVR_Skeleton_JointIndexEnum.pinkyProximal,
    (int)SteamVR_Skeleton_JointIndexEnum.pinkyMiddle,
    // pinkyDistal removed
    (int)SteamVR_Skeleton_JointIndexEnum.pinkyTip
};
        public float CalculateFingerOpenness(Quaternion[] currentHand, Quaternion[] closedHand, Quaternion[] openHand, int[] fingerJoints)
        {
            float totalInterpolation = 0f;
            int count = 0;

            foreach (var jointIndex in fingerJoints)
            {
                // Skip distal joints indices explicitly:
                // (These indices correspond to the distal joints in your original arrays)
                if (jointIndex == (int)SteamVR_Skeleton_JointIndexEnum.thumbDistal ||
                    jointIndex == (int)SteamVR_Skeleton_JointIndexEnum.indexDistal ||
                    jointIndex == (int)SteamVR_Skeleton_JointIndexEnum.middleDistal ||
                    jointIndex == (int)SteamVR_Skeleton_JointIndexEnum.ringDistal ||
                    jointIndex == (int)SteamVR_Skeleton_JointIndexEnum.pinkyDistal)
                {
                    continue; // skip distal joints
                }

                float interp = GetQuaternionInterpolationValue(closedHand[jointIndex], openHand[jointIndex], currentHand[jointIndex]);
                totalInterpolation += interp;
                count++;
            }

            return count > 0 ? totalInterpolation / count : 0f;
        }
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;
        public float3 ControllerPosition;
        public quaternion ControllerRotation;

        public float3 HandPalmPosition;
        public quaternion HandPalmRotation;

        public float3 leftHandToIKRotationOffset = new float3(0, 90, -180);
        public float3 rightHandToIKRotationOffset = new float3(0, -90, -180);
        public float3 AddedPosition = new float3(0, -0.05f, 0);

        private void SteamVR_Behavior_Pose_OnUpdate(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            float scale = BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
            UpdateHistoryBuffer();

            // Get controller pose
            ControllerFinalRotation = DeviceposeAction[inputSource].localRotation;
            ControllerFinalPosition = DeviceposeAction[inputSource].localPosition * scale;

            HandPalmPosition = BonePositions[1];
            HandPalmRotation = BoneRotations[1];

            float3 FinalPalmPosition = (AddedPosition + HandPalmPosition) * scale;
            quaternion ConvertedRotation = new quaternion();
            switch (fromSource)
            {
                case SteamVR_Input_Sources.LeftHand:
                    ConvertedRotation = math.mul(HandPalmRotation, Quaternion.Euler(leftHandToIKRotationOffset));
                    break;
                case SteamVR_Input_Sources.RightHand:
                    ConvertedRotation = math.mul(HandPalmRotation, Quaternion.Euler(rightHandToIKRotationOffset));
                    break;
                default:
                    ConvertedRotation = HandPalmRotation;
                    break;
            }
            HandFinalRotation = math.mul(ControllerFinalRotation, ConvertedRotation);

            // Transform hand position: Apply controller rotation to hand position, then add controller position
            HandFinalPosition = ControllerFinalPosition - math.mul(ControllerFinalRotation, FinalPalmPosition);

            if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
            {
                Control.IncomingData.position = HandFinalPosition;
                Control.IncomingData.rotation = HandFinalRotation;
            }
        }
        #region Mostly Unused Steam
        protected SteamVR_HistoryBuffer historyBuffer = new SteamVR_HistoryBuffer(30);
        protected int lastFrameUpdated;
        protected void UpdateHistoryBuffer()
        {
            int currentFrame = Time.frameCount;
            if (lastFrameUpdated != currentFrame)
            {
                historyBuffer.Update(DeviceposeAction[inputSource].localPosition, DeviceposeAction[inputSource].localRotation, DeviceposeAction[inputSource].velocity, DeviceposeAction[inputSource].angularVelocity);
                lastFrameUpdated = currentFrame;
            }
        }
        public Vector3 GetVelocity()
        {
            return DeviceposeAction[inputSource].velocity;
        }
        public Vector3 GetAngularVelocity()
        {
            return DeviceposeAction[inputSource].angularVelocity;
        }
        public bool GetVelocitiesAtTimeOffset(float secondsFromNow, out Vector3 velocity, out Vector3 angularVelocity)
        {
            return DeviceposeAction[inputSource].GetVelocitiesAtTimeOffset(secondsFromNow, out velocity, out angularVelocity);
        }
        public void GetEstimatedPeakVelocities(out Vector3 velocity, out Vector3 angularVelocity)
        {
            int top = historyBuffer.GetTopVelocity(10, 1);

            historyBuffer.GetAverageVelocities(out velocity, out angularVelocity, 2, top);
        }
        public bool isValid { get { return DeviceposeAction[inputSource].poseIsValid; } }
        public bool isActive { get { return DeviceposeAction[inputSource].active; } }
        #endregion
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null && LoadedDeviceRequest == null)
            {
                BasisDeviceMatchSettings Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    var op = Addressables.LoadAssetAsync<GameObject>(Match.DeviceID);
                    GameObject go = op.WaitForCompletion();
                    GameObject gameObject = Object.Instantiate(go);
                    gameObject.name = CommonDeviceIdentifier;
                    gameObject.transform.parent = this.transform;
                    if (gameObject.TryGetComponent(out BasisVisualTracker))
                    {
                        BasisVisualTracker.Initialization(this);
                    }
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(FallbackDeviceID);
                        GameObject go = op.WaitForCompletion();
                        GameObject gameObject = Object.Instantiate(go);
                        gameObject.name = CommonDeviceIdentifier;
                        gameObject.transform.parent = this.transform;
                        if (gameObject.TryGetComponent(out BasisVisualTracker))
                        {
                            BasisVisualTracker.Initialization(this);
                        }
                    }
                }
            }
        }
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            SteamVR_Actions.default_Haptic.Execute(0, duration, frequency, amplitude, inputSource);
        }
        public Quaternion[] LeftClosedHand = new Quaternion[]
{
    new Quaternion(0f, 1f, 0f, 0f), // 0 root
    new Quaternion(-0.09099f, 0.90964f, -0.39799f, -0.07675f), // 1 wrist
    new Quaternion(0.57849f, -0.04753f, -0.64034f, 0.50304f), // 2 thumbMetacarpal
    new Quaternion(0.0852f, -0.03512f, -0.17213f, 0.98075f), // 3 thumbProximal
    new Quaternion(-0.00365f, -0.02381f, -0.45784f, 0.88871f), // 4 thumbMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 5 thumbDistal
    new Quaternion(0.43168f, 0.45441f, -0.40143f, 0.66784f), // 6 thumbTip
    new Quaternion(-0.02309f, -0.1564f, 0.22298f, -0.96192f), // 7 indexMetacarpal
    new Quaternion(-0.03962f, -0.02307f, 0.5034f, -0.86284f), // 8 indexProximal
    new Quaternion(0.0441f, -0.08757f, 0.30698f, -0.94665f), // 9 indexMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 10 indexDistal
    new Quaternion(0.53635f, 0.44387f, -0.46617f, 0.5459f), // 11 indexTip
    new Quaternion(-0.10697f, 0.12072f, -0.54278f, 0.82424f), // 12 middleMetacarpal
    new Quaternion(0.0048f, -0.02907f, -0.55205f, 0.83329f), // 13 middleProximal
    new Quaternion(0.0465f, -0.02542f, -0.29304f, 0.95463f), // 14 middleMiddle
    new Quaternion(0f, 0f, -0.04013f, 0.99919f), // 15 middleDistal
    new Quaternion(0.52829f, 0.49705f, -0.45354f, 0.51784f), // 16 middleTip
    new Quaternion(-0.09794f, 0.14809f, -0.56066f, 0.80878f), // 17 ringMetacarpal
    new Quaternion(-0.00189f, -0.00117f, -0.55341f, 0.8329f), // 18 ringProximal
    new Quaternion(0.03204f, -0.01388f, -0.14614f, 0.98865f), // 19 ringMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 20 ringDistal
    new Quaternion(0.44361f, 0.64498f, -0.39271f, 0.48269f), // 21 ringTip
    new Quaternion(-0.03248f, 0.15642f, -0.57431f, 0.8029f), // 22 pinkyMetacarpal
    new Quaternion(0.00169f, 0.00083f, -0.47768f, 0.87853f), // 23 pinkyProximal
    new Quaternion(0.02039f, 0.0102f, -0.09125f, 0.99557f), // 24 pinkyMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 25 pinkyDistal
    new Quaternion(-0.50191f, -0.41855f, 0.33534f, 0.67856f), // 26 pinkyTip
    new Quaternion(0.93566f, -0.14735f, -0.19088f, -0.25766f), // 27 thumbAux
    new Quaternion(0.79901f, -0.54935f, -0.13497f, -0.20391f), // 28 indexAux
    new Quaternion(0.86692f, -0.46505f, -0.12784f, -0.12583f), // 29 middleAux
    new Quaternion(0.87168f, -0.46864f, -0.11146f, -0.09014f) // 30 ringAux
                                                              // Note: pinkyAux (31) is missing here
};
        public Quaternion[] LeftOpenHand = new Quaternion[]
        {
    new Quaternion(0f, 1f, 0f, 0f), // 0 root
    new Quaternion(-0.08407f, 0.91727f, -0.38192f, -0.0754f), // 1 wrist
    new Quaternion(0.54188f, -0.04745f, -0.7089f, 0.44897f), // 2 thumbMetacarpal
    new Quaternion(0.07906f, 0f, 0f, 0.99687f), // 3 thumbProximal
    new Quaternion(0.01202f, -0.00646f, 0.02225f, 0.99966f), // 4 thumbMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 5 thumbDistal
    new Quaternion(0.44338f, 0.45501f, -0.38481f, 0.66956f), // 6 thumbTip
    new Quaternion(0.00726f, 0.08114f, -0.25763f, 0.9628f), // 7 indexMetacarpal
    new Quaternion(0.04586f, 0f, 0f, 0.99895f), // 8 indexProximal
    new Quaternion(-0.00691f, 0.04985f, -0.09971f, 0.99374f), // 9 indexMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 10 indexDistal
    new Quaternion(0.55042f, 0.45202f, -0.44978f, 0.5389f), // 11 indexTip
    new Quaternion(-0.18168f, 0.03706f, -0.27629f, 0.94302f), // 12 middleMetacarpal
    new Quaternion(0.01768f, 0f, 0f, 0.99984f), // 13 middleProximal
    new Quaternion(-0.01183f, 0.00722f, 0.01348f, 0.99981f), // 14 middleMiddle
    new Quaternion(0f, 0f, -0.04013f, 0.99919f), // 15 middleDistal
    new Quaternion(0.55014f, 0.49555f, -0.42989f, 0.51669f), // 16 middleTip
    new Quaternion(-0.08322f, 0.05942f, -0.2903f, 0.95146f), // 17 ringMetacarpal
    new Quaternion(-0.00224f, 0f, 0f, 1f), // 18 ringProximal
    new Quaternion(-0.00385f, 0.00404f, -0.01938f, 0.9998f), // 19 ringMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 20 ringDistal
    new Quaternion(0.45592f, 0.63854f, -0.38317f, 0.48742f), // 21 ringTip
    new Quaternion(-0.0477f, 0.05274f, -0.16676f, 0.98343f), // 22 pinkyMetacarpal
    new Quaternion(0.0019f, 0f, 0f, 1f), // 23 pinkyProximal
    new Quaternion(-0.02761f, 0.03322f, 0.02543f, 0.99874f), // 24 pinkyMiddle
    new Quaternion(0f, 0f, 0f, 1f), // 25 pinkyDistal
    new Quaternion(-0.13211f, -0.62048f, -0.11054f, 0.76507f), // 26 pinkyTip
    new Quaternion(0.80476f, 0.47108f, 0.15021f, -0.32846f), // 27 thumbAux
    new Quaternion(0.80583f, 0.50105f, 0.21275f, -0.23307f), // 28 indexAux
    new Quaternion(0.83514f, 0.4847f, 0.11487f, -0.23328f), // 29 middleAux
    new Quaternion(0.83721f, 0.51909f, 0.13873f, -0.10187f) // 30 ringAux
                                                            // Note: pinkyAux (31) is missing here
        };
        public Quaternion[] RightClosedHand = new Quaternion[]
        {
    new Quaternion(-0.00000f, 1.00000f, 0.00000f, -0.00000f),
    new Quaternion(-0.09099f, -0.90964f, 0.39799f, -0.07675f),
    new Quaternion(-0.53638f, -0.61694f, 0.08410f, 0.56974f),
    new Quaternion(0.08035f, -0.04970f, -0.24748f, 0.96428f),
    new Quaternion(-0.02752f, -0.00656f, -0.48621f, 0.87338f),
    new Quaternion(0.00000f, -0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.66784f, -0.40143f, -0.45441f, 0.43168f),
    new Quaternion(-0.02309f, -0.15640f, 0.22298f, -0.96192f),
    new Quaternion(-0.03962f, -0.02307f, 0.50340f, -0.86284f),
    new Quaternion(0.04410f, -0.08756f, 0.30698f, -0.94665f),
    new Quaternion(0.00000f, 0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.54095f, -0.46261f, -0.45001f, 0.53931f),
    new Quaternion(-0.15227f, 0.08449f, -0.46481f, 0.86812f),
    new Quaternion(0.01887f, -0.01192f, -0.16752f, 0.98562f),
    new Quaternion(0.01389f, -0.02202f, -0.36733f, 0.92973f),
    new Quaternion(0.00000f, 0.00000f, -0.04013f, 0.99919f),
    new Quaternion(-0.50843f, -0.45632f, -0.50807f, 0.52453f),
    new Quaternion(-0.11872f, 0.14544f, -0.59334f, 0.78275f),
    new Quaternion(-0.00203f, -0.00091f, -0.43705f, 0.89944f),
    new Quaternion(0.01903f, -0.01544f, -0.41309f, 0.91036f),
    new Quaternion(0.00000f, 0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.46090f, -0.38475f, -0.66128f, 0.44972f),
    new Quaternion(-0.07548f, 0.12432f, -0.61569f, 0.77445f),
    new Quaternion(0.00180f, 0.00059f, -0.35094f, 0.93639f),
    new Quaternion(-0.01227f, 0.04025f, -0.35504f, 0.93391f),
    new Quaternion(0.00000f, 0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.53173f, 0.39418f, -0.36532f, 0.65454f),
    new Quaternion(0.93566f, 0.14735f, 0.19088f, -0.25766f),
    new Quaternion(0.94968f, 0.19437f, -0.00401f, -0.24558f),
    new Quaternion(0.75902f, 0.62912f, 0.14335f, -0.08686f),
    new Quaternion(0.76252f, 0.63175f, 0.12639f, -0.05903f)
        };
        public Quaternion[] RightOpenHand = new Quaternion[]
{
    new Quaternion(-0.00000f, 1.00000f, 0.00000f, -0.00000f),
    new Quaternion(-0.08407f, -0.91727f, 0.38192f, -0.07540f),
    new Quaternion(-0.44897f, -0.70890f, 0.04745f, 0.54188f),
    new Quaternion(0.07906f, 0.00001f, 0.00001f, 0.99687f),
    new Quaternion(0.01202f, -0.00646f, 0.02225f, 0.99966f),
    new Quaternion(0.00000f, -0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.66956f, -0.38481f, -0.45501f, 0.44338f),
    new Quaternion(0.00726f, 0.08114f, -0.25763f, 0.96280f),
    new Quaternion(0.04586f, -0.00001f, -0.00000f, 0.99895f),
    new Quaternion(-0.00691f, 0.04985f, -0.09971f, 0.99374f),
    new Quaternion(0.00000f, 0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.53890f, -0.44978f, -0.45202f, 0.55042f),
    new Quaternion(-0.18168f, 0.03706f, -0.27629f, 0.94302f),
    new Quaternion(0.01768f, 0.00007f, -0.00003f, 0.99984f),
    new Quaternion(-0.01183f, 0.00722f, 0.01348f, 0.99981f),
    new Quaternion(0.00000f, 0.00000f, -0.04013f, 0.99919f),
    new Quaternion(-0.51667f, -0.42991f, -0.49557f, 0.55012f),
    new Quaternion(-0.08329f, 0.05949f, -0.29065f, 0.95134f),
    new Quaternion(-0.00224f, 0.00000f, -0.00080f, 1.00000f),
    new Quaternion(-0.00386f, 0.00405f, -0.01950f, 0.99979f),
    new Quaternion(0.00000f, 0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.48742f, -0.38317f, -0.63854f, 0.45592f),
    new Quaternion(-0.04770f, 0.05274f, -0.16676f, 0.98343f),
    new Quaternion(0.00190f, -0.00000f, -0.00007f, 1.00000f),
    new Quaternion(-0.02761f, 0.03322f, 0.02543f, 0.99874f),
    new Quaternion(0.00000f, 0.00000f, 0.00000f, 1.00000f),
    new Quaternion(-0.13211f, 0.62048f, 0.11054f, 0.76507f),
    new Quaternion(0.80476f, -0.47108f, -0.15021f, -0.32846f),
    new Quaternion(0.80583f, -0.50105f, -0.21275f, -0.23307f),
    new Quaternion(0.83580f, -0.48361f, -0.11459f, -0.23329f),
    new Quaternion(0.83721f, -0.51909f, -0.13873f, -0.10187f)
};
    }
}
