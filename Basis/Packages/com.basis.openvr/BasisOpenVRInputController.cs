using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Valve.VR;
using BasisInputKey = Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject.BasisInputKey;
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
        public const int WristAnchorPollFrames = 120;
        public const string OculusTouchControllerType = "oculus_touch";
        private static readonly VRBoneTransform_t[] referenceBones = new VRBoneTransform_t[SteamVR_Action_Skeleton.numBones];
        private Vector3 wristAnchorPosition;
        private Quaternion wristAnchorRotation;
        private bool wristAnchored, wristSampled;
        private int nextWristAnchorPoll;

        // Device pose (controller) from compositor
        [System.NonSerialized] public TrackedDevicePose_t devicePose = new TrackedDevicePose_t();
        [System.NonSerialized] public TrackedDevicePose_t deviceGamePose = new TrackedDevicePose_t();
        public EVRCompositorError result;
        public Vector3 LeftRaycastOffset = new Vector3(0, 0, 0.06f);
        public Vector3 RightRaycastOffset = new Vector3(0, 0, 0.06f);
        public override bool AppliesDeviceOffsetAtSource => true;
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
            wristAnchored = false;
            nextWristAnchorPoll = 0;
            TrackingHardware = BasisTrackingHardware.Lighthouse;

            InitializeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole,true);

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
            if (RenderModelAnchor != null)
            {
                GameObject.Destroy(RenderModelAnchor.gameObject);
                RenderModelAnchor = null;
            }
            base.OnDestroy();
        }
        public override void LateDoPollData()
        {
            if (!SteamVR.active)
            {
                return;
            }

            // Poll input state here so InputUpdate() sees fresh data after LastUpdatePlayerControl()
            PollButtons();
            PollPose();
        }
        public override void RenderPollData()
        {
            if (!SteamVR.active)
            {
                BasisDebug.LogError("Can't Poll SteamVR was not active");
                return;
            }

            // Buttons / axes
            PollButtons();

            PollPose();

            UpdateInputEvents();
        }
        private void PollButtons()
        {
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
            CurrentInputState.PrimaryButtonTouch = SteamVR_Actions._default.A_Touch.GetState(inputSource);
            CurrentInputState.SecondaryButtonTouch = SteamVR_Actions._default.B_Touch.GetState(inputSource);
            CurrentInputState.Primary2DAxisTouch = SteamVR_Actions._default.JoystickTouch.GetState(inputSource);
            CurrentInputState.Secondary2DAxisTouch = SteamVR_Actions._default.TrackPadTouched.GetState(inputSource);
            CurrentInputState.TriggerTouch = SteamVR_Actions._default.TriggerTouch.GetState(inputSource);
            CurrentInputState.ThumbrestTouch = SteamVR_Actions._default.ThumbrestTouch.GetState(inputSource);
        }
        private void PollPose()
        {
            if (!SteamVR.active)
            {
                return;
            }
            SteamVR_Action_Skeleton skeleton;
            switch (inputSource)
            {
                case SteamVR_Input_Sources.LeftHand:
                    skeleton = SteamVR_Actions.default_SkeletonLeftHand;
                    break;
                case SteamVR_Input_Sources.RightHand:
                    skeleton = SteamVR_Actions.default_SkeletonRightHand;
                    break;
                default:
                    return;
            }
            if (skeleton.rangeOfMotion != EVRSkeletalMotionRange.WithoutController) skeleton.rangeOfMotion = EVRSkeletalMotionRange.WithoutController;
            bool isLeft = TryGetRole(out BasisBoneTrackedRole role) ? role == BasisBoneTrackedRole.LeftHand : inputSource == SteamVR_Input_Sources.LeftHand;
            var handDriver = BasisLocalPlayer.Instance.LocalHandDriver;
            UpdateHandPose(isLeft ? handDriver.LeftHand : handDriver.RightHand, skeleton, isLeft);
        }
        static readonly int[] boneParents = { -1, 0, 1, 2, 3, 4, 1, 6, 7, 8, 9, 1, 11, 12, 13, 14, 1, 16, 17, 18, 19, 1, 21, 22, 23, 24, 0, 0, 0, 0, 0 };
        static readonly int[] fingerBaseBones = { 2, 7, 12, 17, 22 }, fingerTipBones = { 5, 10, 15, 20, 25 };
        static readonly Vector3[] chainPositions = new Vector3[31];
        static readonly Quaternion[] chainRotations = new Quaternion[31];
        static readonly Vector3[] chainBases = new Vector3[5], chainTips = new Vector3[5];
        private readonly Vector2[] solvedFingers = BasisFingerTipRetarget.NewState();
        public static bool ApplySkeletalBones(BasisFingerPose hand, Vector3[] bonePositions, Quaternion[] boneRotations, BasisFingerTipRetarget retarget, bool isLeft, Vector2[] previous)
        {
            if (retarget == null || !retarget.IsCreated || bonePositions == null || boneRotations == null || bonePositions.Length < 26 || boneRotations.Length < 26) return false;
            chainPositions[0] = Vector3.zero;
            chainRotations[0] = Quaternion.identity;
            for (int i = 1; i < 26; i++)
            {
                int p = boneParents[i];
                chainPositions[i] = chainPositions[p] + chainRotations[p] * bonePositions[i];
                chainRotations[i] = chainRotations[p] * boneRotations[i];
            }
            for (int f = 0; f < 5; f++)
            {
                chainBases[f] = chainPositions[fingerBaseBones[f]];
                chainTips[f] = chainPositions[fingerTipBones[f]];
            }
            if (!retarget.SolveHand(isLeft ? 0 : 5, chainPositions[1], chainPositions[7], chainPositions[12], chainPositions[22], chainBases, chainTips, previous)) return false;
            BasisFingerTipRetarget.Write(hand, previous);
            return true;
        }
        public static void ApplySkeletalSummary(BasisFingerPose hand, float[] curls, float thumbIndex, float indexMiddle, float middleRing, float ringPinky, float splayBias)
        {
            hand.ThumbPercentage[0] = 0.75f - curls[0] * 1.75f;
            hand.IndexPercentage[0] = 0.75f - curls[1] * 1.75f;
            hand.MiddlePercentage[0] = 0.75f - curls[2] * 1.75f;
            hand.RingPercentage[0] = 0.75f - curls[3] * 1.75f;
            hand.LittlePercentage[0] = 0.75f - curls[4] * 1.75f;
            hand.ThumbPercentage[1] = thumbIndex * 2f - 1f;
            hand.IndexPercentage[1] = -(indexMiddle * 2f - 1f + splayBias);
            hand.MiddlePercentage[1] = 0.5f * (indexMiddle + middleRing) * 2f - 1f + splayBias;
            hand.RingPercentage[1] = 0.5f * (middleRing + ringPinky) * 2f - 1f + splayBias;
            hand.LittlePercentage[1] = ringPinky * 2f - 1f + splayBias;
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

            // ------- raw bone arrays (local to skeleton root)
            BonePositions = skeletonAction.bonePositions;
            BoneRotations = skeletonAction.boneRotations;

            if (hasRoleAssigned && !IgnoresFingers && !(skeletonAction.activeBinding && ApplySkeletalBones(hand, BonePositions, BoneRotations, BasisLocalPlayer.Instance.LocalHandDriver.TipRetarget, isLeft, solvedFingers)) && pairSplays != null && pairSplays.Length == SteamVR_Skeleton_FingerSplayIndexes.enumArray.Length &&
                curls != null && curls.Length == SteamVR_Skeleton_FingerIndexes.enumArray.Length)
            {
                ApplySkeletalSummary(hand, curls, pairSplays[SteamVR_Skeleton_FingerSplayIndexes.thumbIndex], pairSplays[SteamVR_Skeleton_FingerSplayIndexes.indexMiddle], pairSplays[SteamVR_Skeleton_FingerSplayIndexes.middleRing], pairSplays[SteamVR_Skeleton_FingerSplayIndexes.ringPinky], HandBiasSplay);
            }

            // Raw device pose in *unscaled* world space
            ComputeUnscaledDeviceCoord(ref PhysicalDeviceCoord, devicePose.mDeviceToAbsoluteTracking.GetPosition());
            PhysicalDeviceCoord.rotation = devicePose.mDeviceToAbsoluteTracking.GetRotation();
            ResolveUnscaledFromPhysical(true);

            if (RenderModelAnchor != null)
            {
                ConvertToScaledDeviceCoord(ref UnscaledDeviceCoord, ref rawScaledDeviceCoord);
                Vector3 anchorPos = rawScaledDeviceCoord.position;
                Quaternion anchorRot = rawScaledDeviceCoord.rotation;
                BasisLocalPlayspaceMover.ApplyFlipToLocalPose(ref anchorPos, ref anchorRot);
                RenderModelAnchor.SetLocalPositionAndRotation(anchorPos, anchorRot);
            }

            float Scale = BasisHeightDriver.DeviceScale;

            // Wrist data from skeleton
            int idxWrist = SteamVR_Skeleton_JointIndexes.wrist;
            bool skeletonActive = skeletonAction.GetActive();
            wristSampled |= skeletonActive;
            if (skeletonActive && Time.frameCount >= nextWristAnchorPoll)
            {
                nextWristAnchorPoll = Time.frameCount + WristAnchorPollFrames;
                RefreshWristAnchor(skeletonAction);
            }
            bool useWristAnchor = wristAnchored && BasisSettingsDefaults.QuestControllerFix.RawValue;
            Vector3 wristLocalPos = useWristAnchor ? wristAnchorPosition : wristSampled ? BonePositions[idxWrist] : Vector3.zero;
            Quaternion wristLocalRot = useWristAnchor ? wristAnchorRotation : wristSampled ? BoneRotations[idxWrist] : Quaternion.identity;

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
                wristWorldPos + (UnscaledDeviceCoord.rotation * ((isLeft ? LeftRaycastOffset : RightRaycastOffset) * Scale)),
                HandFinal.rotation,
                ActiveRaycastOffset
            );
        }

        private void RefreshWristAnchor(SteamVR_Action_Skeleton skeletonAction)
        {
            if (DeviceControllerType != OculusTouchControllerType || skeletonAction.GetSkeletalTrackingLevel() != EVRSkeletalTrackingLevel.VRSkeletalTracking_Estimated)
            {
                wristAnchored = false;
                return;
            }
            if (wristAnchored || Valve.VR.OpenVR.Input.GetSkeletalReferenceTransforms(skeletonAction.handle, EVRSkeletalTransformSpace.Parent, EVRSkeletalReferencePose.OpenHand, referenceBones) != EVRInputError.None)
            {
                return;
            }
            VRBoneTransform_t wrist = referenceBones[SteamVR_Skeleton_JointIndexes.wrist];
            wristAnchorPosition = new Vector3(-wrist.position.v0, wrist.position.v1, wrist.position.v2);
            wristAnchorRotation = new Quaternion(wrist.orientation.x, -wrist.orientation.y, -wrist.orientation.z, wrist.orientation.w);
            wristAnchored = true;
        }

        private BasisOpenVRRenderModel _runtimeModel;
        public override void ShowTrackedVisual()
        {
            ShowTrackedVisualDefaultImplementation();
        }

        public override bool TryShowRuntimeDeviceModel()
        {
            return BasisOpenVRRenderModel.TryLoad(this, ref _runtimeModel);
        }

        /// <summary>
        /// A node kept at the raw controller device pose. This device's own <c>transform</c> is
        /// remapped to the avatar wrist (with an IK rotation offset), which would misplace a device
        /// model authored at the raw pose — so render models attach here instead. Driven every render
        /// frame in <see cref="UpdateHandPose"/>.
        /// </summary>
        public Transform RenderModelAnchor;
        private BasisCalibratedCoords rawScaledDeviceCoord = new BasisCalibratedCoords();

        public override Transform GetVisualAnchor()
        {
            if (RenderModelAnchor == null)
            {
                RenderModelAnchor = new GameObject("ControllerRenderModelAnchor").transform;
                RenderModelAnchor.SetParent(BasisLocalPlayer.Instance.transform, false);
            }
            return RenderModelAnchor;
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            SteamVR_Actions.default_Haptic.Execute(0, duration, frequency, amplitude, inputSource);
        }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }

        public override bool TryGetInputDisplayName(BasisInputKey key, out string name)
        {
            ISteamVR_Action_In action = ActionFor(key);
            name = action != null ? action.GetLocalizedOriginPart(inputSource, EVRInputStringBits.VRInputString_InputSource) : null;
            return !string.IsNullOrWhiteSpace(name);
        }

        private static ISteamVR_Action_In ActionFor(BasisInputKey key)
        {
            var actions = SteamVR_Actions._default;
            switch (key)
            {
                case BasisInputKey.Trigger: return actions.Trigger;
                case BasisInputKey.SecondaryTrigger: return actions.HandTrigger;
                case BasisInputKey.GripButton: return actions.Grip;
                case BasisInputKey.SystemOrMenuButton: return actions.System;
                case BasisInputKey.PrimaryButtonGetState: return actions.A_Button;
                case BasisInputKey.SecondaryButtonGetState: return actions.B_Button;
                case BasisInputKey.Primary2DAxis: return actions.Joystick;
                case BasisInputKey.Primary2DAxisClick: return actions.JoyStickClick;
                case BasisInputKey.Secondary2DAxis: return actions.TrackPad;
                case BasisInputKey.Secondary2DAxisClick:
                case BasisInputKey.Secondary2DAxisTouch: return actions.TrackPadTouched;
                case BasisInputKey.PrimaryButtonTouch: return actions.A_Touch;
                case BasisInputKey.SecondaryButtonTouch: return actions.B_Touch;
                case BasisInputKey.Primary2DAxisTouch: return actions.JoystickTouch;
                case BasisInputKey.TriggerTouch: return actions.TriggerTouch;
                case BasisInputKey.ThumbrestTouch: return actions.ThumbrestTouch;
                default: return null;
            }
        }
    }
}
