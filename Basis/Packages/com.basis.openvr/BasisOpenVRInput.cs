using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    ///only used for trackers!
    public class BasisOpenVRInput : BasisInput
    {
        [SerializeField]
        public OpenVRDevice Device;
        public TrackedDevicePose_t devicePose = new TrackedDevicePose_t();
        public TrackedDevicePose_t deviceGamePose = new TrackedDevicePose_t();
        public SteamVR_Utils.RigidTransform deviceTransform;
        public EVRCompositorError result;
        public bool HasInputSource = false;
        public SteamVR_Input_Sources inputSource;
        public void Initialize(OpenVRDevice device, string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole)
        {
            Device = device;
            // SteamVR's own trackers are lighthouse-tracked; InitializeTracking downgrades this for the
            // SlimeVR and Standable devices that also arrive through OpenVR.
            TrackingHardware = BasisTrackingHardware.Lighthouse;
            InitializeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
        }
        public override void LateDoPollData()
        {
            PollPose();
        }
        public override void RenderPollData()
        {
            if (!PollPose())
            {
                return;
            }
            if (HasInputSource)
            {
                CurrentInputState.Primary2DAxisClick = SteamVR_Actions._default.JoyStickClick.GetState(inputSource);
                CurrentInputState.Primary2DAxisRaw = SteamVR_Actions._default.Joystick.GetAxis(inputSource);
                CurrentInputState.PrimaryButtonGetState = SteamVR_Actions._default.A_Button.GetState(inputSource);
                CurrentInputState.SecondaryButtonGetState = SteamVR_Actions._default.B_Button.GetState(inputSource);
                CurrentInputState.Trigger = SteamVR_Actions._default.Trigger.GetAxis(inputSource);
            }
            UpdateInputEvents();
            ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
        }
        private bool PollPose()
        {
            if (!SteamVR.active)
            {
                return false;
            }
            result = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(Device.deviceIndex, ref devicePose, ref deviceGamePose);
            if (result != EVRCompositorError.None)
            {
                BasisDebug.LogError("Error getting device pose: " + result);
                return false;
            }
            if (!devicePose.bPoseIsValid)
            {
                return false;
            }
            deviceTransform = new SteamVR_Utils.RigidTransform(devicePose.mDeviceToAbsoluteTracking);
            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, deviceTransform.pos);
            UnscaledDeviceCoord.rotation = deviceTransform.rot;
            ConvertToScaledDeviceCoord();
            ControlOnlyAsDevice();
            return true;
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
