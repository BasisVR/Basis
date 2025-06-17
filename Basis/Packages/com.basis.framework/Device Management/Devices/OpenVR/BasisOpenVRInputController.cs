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
            float[] Curls = skeletonAction.GetFingerCurls();
            float[] Splays = skeletonAction.GetFingerSplays();

            hand.ThumbPercentage[0] = Remap01ToMinus1To1(Curls[0]);
            hand.IndexPercentage[0] = Remap01ToMinus1To1(Curls[1]);
            hand.MiddlePercentage[0] = Remap01ToMinus1To1(Curls[2]);
            hand.RingPercentage[0] = Remap01ToMinus1To1(Curls[3]);
            hand.LittlePercentage[0] = Remap01ToMinus1To1(Curls[4]);

            //someone else can solve this
            //its Distance between each finger.
            hand.ThumbPercentage[1] = 0;
            hand.IndexPercentage[1] = 0;// Remap01ToMinus1To1(Splays[0]);
            hand.MiddlePercentage[1] = 0;// Remap01ToMinus1To1(Splays[1]);
            hand.RingPercentage[1] = 0;// Remap01ToMinus1To1(Splays[2]);
            hand.LittlePercentage[1] = 0;// Remap01ToMinus1To1(Splays[3]);
        }
        float Remap01ToMinus1To1(float value)
        {
            return (0.75f - value) * 2f - 0.75f;
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
    }
}
