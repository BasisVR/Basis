using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Valve.VR;
namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    [DefaultExecutionOrder(15001)]
    public class BasisOpenVRInputController : BasisInputController
    {
        public OpenVRDevice Device;
        public SteamVR_Input_Sources inputSource;

        public SteamVR_Action_Pose DeviceposeAction = SteamVR_Input.GetAction<SteamVR_Action_Pose>("Pose");
        public bool HasOnUpdate = false;

        // Raw skeleton (local-to-skeleton) data from SteamVR
        public Vector3[] BonePositions;      // local positions relative to skeleton root (meters)
        public Quaternion[] BoneRotations;   // local rotations relative to skeleton root

        // Device pose (controller) from compositor
        public TrackedDevicePose_t devicePose = new TrackedDevicePose_t();
        public TrackedDevicePose_t deviceGamePose = new TrackedDevicePose_t();
        public SteamVR_Utils.RigidTransform DeviceLocalSpace;
        public EVRCompositorError result;
        public void Initialize(OpenVRDevice device, string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, SteamVR_Input_Sources SteamVR_Input_Sources)
        {
            HandBiasSplay = -0.8f;

            // existing hand rotation offsets
            leftHandToIKRotationOffset = new Vector3(170, 0, -120);
            rightHandToIKRotationOffset = new Vector3(170, 0, 120);

            // default position offsets (tweak as needed; mirrored on X by default).
            // set to zero if you prefer to tune in inspector.
            leftHandToIKPositionOffset = new Vector3(0, 0.05f, -0.02f);
            rightHandToIKPositionOffset = new Vector3(0, 0.05f, -0.02f);

            if (HasOnUpdate && DeviceposeAction != null)
            {
                HasOnUpdate = false;
            }

            inputSource = SteamVR_Input_Sources;
            Device = device;

            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);

            if (DeviceposeAction != null && HasOnUpdate == false)
            {
                HasOnUpdate = true;
            }

            BasisDebug.Log("set Controller to inputSource " + inputSource + " bone role " + basisBoneTrackedRole);
        }

        public new void OnDestroy()
        {
            if (DeviceposeAction != null)
            {
                HasOnUpdate = false;
            }
            base.OnDestroy();
        }

        public override void DoPollData()
        {
            if (!SteamVR.active)
                return;

            // Buttons / axes
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

            // Update hand (left/right)
            switch (inputSource)
            {
                case SteamVR_Input_Sources.LeftHand:
                    {
                        SteamVR_Action_Skeleton leftHand = SteamVR_Actions.default_SkeletonLeftHand;
                        UpdateHandPose(BasisLocalPlayer.Instance.LocalHandDriver.LeftHand, leftHand, isLeft: true);
                        break;
                    }
                case SteamVR_Input_Sources.RightHand:
                    {
                        SteamVR_Action_Skeleton rightHand = SteamVR_Actions.default_SkeletonRightHand;
                        UpdateHandPose(BasisLocalPlayer.Instance.LocalHandDriver.RightHand, rightHand, isLeft: false);
                        break;
                    }
            }

            UpdatePlayerControl();
        }

        private void UpdateHandPose(BasisFingerPose hand, SteamVR_Action_Skeleton skeletonAction, bool isLeft)
        {
            // Latest compositor-space device pose
            result = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(Device.deviceIndex, ref devicePose, ref deviceGamePose);
            if (result != EVRCompositorError.None)
            {
                return;
            }
            if (deviceGamePose.bPoseIsValid == false)
            {
                return;
            }
            DeviceLocalSpace = new SteamVR_Utils.RigidTransform(deviceGamePose.mDeviceToAbsoluteTracking);

            // ------- CURLS (0..1 from SteamVR) -> [-1..1] rig values
            float[] curls = skeletonAction.GetFingerCurls();

            // ------- SPLAY (pairwise 0..1) -> per-finger [-1..1] with your bias
            float[] pairSplays = skeletonAction.GetFingerSplays();

            if (pairSplays != null && pairSplays.Length == SteamVR_Skeleton_FingerSplayIndexes.enumArray.Length && curls != null && curls.Length == SteamVR_Skeleton_FingerIndexes.enumArray.Length)
            {
                float thumbIndex = pairSplays[SteamVR_Skeleton_FingerSplayIndexes.thumbIndex];
                float indexMiddle = pairSplays[SteamVR_Skeleton_FingerSplayIndexes.indexMiddle];
                float middleRing = pairSplays[SteamVR_Skeleton_FingerSplayIndexes.middleRing];
                float ringPinky = pairSplays[SteamVR_Skeleton_FingerSplayIndexes.ringPinky];

                float thumbSplay01 = thumbIndex;
                float indexSplay01 = 0.5f * (thumbIndex + indexMiddle);
                float middleSplay01 = 0.5f * (indexMiddle + middleRing);
                float ringSplay01 = 0.5f * (middleRing + ringPinky);
                float littleSplay01 = ringPinky;

                hand.ThumbPercentage[0] = Remap01ToMinus1To1(curls[0]);
                hand.IndexPercentage[0] = Remap01ToMinus1To1(curls[1]);
                hand.MiddlePercentage[0] = Remap01ToMinus1To1(curls[2]);
                hand.RingPercentage[0] = Remap01ToMinus1To1(curls[3]);
                hand.LittlePercentage[0] = Remap01ToMinus1To1(curls[4]);
                hand.ThumbPercentage[1] = SplayConversion(thumbSplay01);
                hand.IndexPercentage[1] = SplayConversion(indexSplay01);
                hand.MiddlePercentage[1] = SplayConversion(middleSplay01);
                hand.RingPercentage[1] = SplayConversion(ringSplay01);
                hand.LittlePercentage[1] = SplayConversion(littleSplay01);
            }

            // ------- raw bone arrays (local to skeleton root)
            BonePositions = skeletonAction.bonePositions;
            BoneRotations = skeletonAction.boneRotations;

            UnscaledDeviceCoord.position = DeviceLocalSpace.pos;
            UnscaledDeviceCoord.rotation = DeviceLocalSpace.rot;

            // ------- Compute world-space wrist & root (for IK)
            // scale from the avatar currently selected to the avatar's "default" rig size
            float avatarScale = BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;

            int idxWrist = SteamVR_Skeleton_JointIndexes.wrist;

            Vector3 wristLocalPos = BonePositions[idxWrist];
            Quaternion wristLocalRot = BoneRotations[idxWrist];

            // Rotation offset (per hand)
            Quaternion rotOffset = Quaternion.Euler(isLeft ? leftHandToIKRotationOffset : rightHandToIKRotationOffset);

            // Scale-sensitive bits
            Vector3 ScaledLocalPose = UnscaledDeviceCoord.position * avatarScale;
            Vector3 WristLocalPose = wristLocalPos * avatarScale;

            // Core composition (existing math)
            // Position: move from the device pose back to the wrist by the wrist's local offset
            Vector3 wristWorldPos = ScaledLocalPose - (UnscaledDeviceCoord.rotation * WristLocalPose);

            // Rotation: deviceRot * wristLocalRot (then apply extra offset)
            Quaternion baseWristWorldRot = UnscaledDeviceCoord.rotation * wristLocalRot;
            Quaternion wristWorldRot = baseWristWorldRot * rotOffset;

            Vector3 posOffsetLocal = isLeft ? leftHandToIKPositionOffset : rightHandToIKPositionOffset;
            if (UseIKPositionOffset)
            {
                Vector3 posOffsetWorld = UnscaledDeviceCoord.rotation * (posOffsetLocal * avatarScale);
                wristWorldPos += posOffsetWorld;
            }

            // Push results
            ScaledDeviceCoord.position = wristWorldPos;
            ScaledDeviceCoord.rotation = wristWorldRot;

            HandFinal.rotation = wristWorldRot;
            HandFinal.position = wristWorldPos;

            UpdateRaycastOffset();
            ControlOnlyAsHand();
            ComputeRaycastDirection();
        }

        public override void ShowTrackedVisual()
        {
            ShowTrackedVisualDefaultImplementation();
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            SteamVR_Actions.default_Haptic.Execute(0, duration, frequency, amplitude, inputSource);
        }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
    }
}
