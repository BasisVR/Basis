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
        public EVRCompositorError result;
        public Vector3 LeftRaycastOffset = new Vector3(0, 0, 0.06f);
        public Vector3 RightRaycastOffset = new Vector3(0, 0, 0.06f);
        public void Initialize(OpenVRDevice device, string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, SteamVR_Input_Sources SteamVR_Input_Sources)
        {
            HandBiasSplay = -0.8f;

            // existing hand rotation offsets
            leftHandToIKRotationOffset = new Vector3(80, 69, 150);
            rightHandToIKRotationOffset = new Vector3(112, 80,198);//114 -> 80

            leftHandToIKPositionOffset = new Vector3(0.02f, 0.08f, 0.02f);
            rightHandToIKPositionOffset = new Vector3(-0.02f, 0.08f, 0.02f);


            if (HasOnUpdate && DeviceposeAction != null)
            {
                HasOnUpdate = false;
            }

            inputSource = SteamVR_Input_Sources;
            Device = device;

            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole,true);

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
        public override void LateDoPollData()
        {
            
        }
        public override void RenderPollData()
        {
            if (!SteamVR.active)
            {
                BasisDebug.LogError("Cant Poll SteamVR was not active");
                return;
            }

            // Buttons / axes
            CurrentInputState.GripButton = SteamVR_Actions._default.Grip.GetState(inputSource);
            CurrentInputState.SystemOrMenuButton = SteamVR_Actions._default.System.GetState(inputSource);
            CurrentInputState.PrimaryButtonGetState = SteamVR_Actions._default.A_Button.GetState(inputSource);
            CurrentInputState.SecondaryButtonGetState = SteamVR_Actions._default.B_Button.GetState(inputSource);
            CurrentInputState.Primary2DAxisClick = SteamVR_Actions._default.JoyStickClick.GetState(inputSource);
            CurrentInputState.Primary2DAxisRaw = SteamVR_Actions._default.Joystick.GetAxis(inputSource);
            CurrentInputState.Trigger = SteamVR_Actions._default.Trigger.GetAxis(inputSource);
            CurrentInputState.SecondaryTrigger = SteamVR_Actions._default.HandTrigger.GetAxis(inputSource);
            CurrentInputState.Secondary2DAxisRaw = SteamVR_Actions._default.TrackPad.GetAxis(inputSource);
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

            UpdateInputEvents();
        }
        private void UpdateHandPose(BasisFingerPose hand, SteamVR_Action_Skeleton skeletonAction, bool isLeft)
        {
            // Latest compositor-space device pose
            result = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(Device.deviceIndex, ref devicePose, ref deviceGamePose);
            if (result != EVRCompositorError.None)
            {
                return;
            }
            if (devicePose.bPoseIsValid == false)
            {
                return;
            }

            // ------- CURLS (0..1 from SteamVR) -> [-1..1] rig values
            float[] curls = skeletonAction.GetFingerCurls();

            // ------- SPLAY (pairwise 0..1) -> per-finger [-1..1] with your bias
            float[] pairSplays = skeletonAction.GetFingerSplays();

            if (pairSplays != null && pairSplays.Length == SteamVR_Skeleton_FingerSplayIndexes.enumArray.Length &&
                curls != null && curls.Length == SteamVR_Skeleton_FingerIndexes.enumArray.Length)
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

            // Raw device pose in *unscaled* world space
            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, devicePose.mDeviceToAbsoluteTracking.GetPosition());
            UnscaledDeviceCoord.rotation = devicePose.mDeviceToAbsoluteTracking.GetRotation();

            float Scale = BasisHeightDriver.DeviceScale;

            // Wrist data from skeleton
            int idxWrist = SteamVR_Skeleton_JointIndexes.wrist;
            Vector3 wristLocalPos = BonePositions[idxWrist];       // meters, local to skeleton root
            Quaternion wristLocalRot = BoneRotations[idxWrist];

            // Rotation offset (per hand)
            Quaternion rotOffset = Quaternion.Euler(isLeft ? leftHandToIKRotationOffset : rightHandToIKRotationOffset);

            // --------- UN-SCALED WRIST WORLD POSE (with hand offset baked in) ---------

            // Move from device (controller) to wrist, using unscaled wrist local offset
            Vector3 wristWorldPosUnscaled = UnscaledDeviceCoord.position - (UnscaledDeviceCoord.rotation * wristLocalPos);

            // Apply IK hand offset in local device space, still unscaled
            Vector3 posOffsetLocal = isLeft ? leftHandToIKPositionOffset : rightHandToIKPositionOffset;
            if (UseIKPositionOffset)
            {
                wristWorldPosUnscaled += UnscaledDeviceCoord.rotation * posOffsetLocal;
            }

            // If OffsetCoords.position is defined in scaled avatar space,
            // keep adding it after scaling (same as before), so we do NOT fold it into UnscaledDeviceCoord.

            // Bake the unscaled wrist position (with hand offset) into UnscaledDeviceCoord
            UnscaledDeviceCoord.position = wristWorldPosUnscaled;

            // Rotation: deviceRot * wristLocalRot (then apply extra offset) — rotation is scale-independent
            Quaternion baseWristWorldRot = UnscaledDeviceCoord.rotation * wristLocalRot;
            Quaternion wristWorldRot = baseWristWorldRot * rotOffset;

            // ---------- SCALE UP TO AVATAR SPACE ----------
            Vector3 wristScaledPos = wristWorldPosUnscaled * Scale;

            // Apply OffsetCoords as a proper transform
            Vector3 wristWorldPos = OffsetCoords.position + (OffsetCoords.rotation * wristScaledPos);

            Quaternion wristFinalRot = OffsetCoords.rotation * wristWorldRot;

            ScaledDeviceCoord.position = wristWorldPos;
            ScaledDeviceCoord.rotation = wristFinalRot;

            HandFinal.position = wristWorldPos;
            HandFinal.rotation = wristFinalRot;

            ControlOnlyAsHand(HandFinal.position, HandFinal.rotation);

            UpdateRaycastOffset();
            ComputeRaycastDirection(
                wristWorldPos + (UnscaledDeviceCoord.rotation * (isLeft ? LeftRaycastOffset : RightRaycastOffset * Scale)),
                HandFinal.rotation,
                ActiveRaycastOffset
            );
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
