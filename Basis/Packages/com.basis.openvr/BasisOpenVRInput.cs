using Basis.Scripts.BasisSdk.Players;
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
            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
        }
        public override void DoPollData()
        {
            if (SteamVR.active)
            {
                result = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(Device.deviceIndex, ref devicePose, ref deviceGamePose);
                if (result == EVRCompositorError.None)
                {
                    if (deviceGamePose.bPoseIsValid)
                    {
                        deviceTransform = new SteamVR_Utils.RigidTransform(deviceGamePose.mDeviceToAbsoluteTracking);

                        UnscaledDeviceCoord.position = deviceTransform.pos;
                        UnscaledDeviceCoord.rotation = deviceTransform.rot;

                        ConvertToScaledDeviceCoord();
                        ControlOnlyAsDevice();
                        if (HasInputSource)
                        {
                            CurrentInputState.Primary2DAxis = SteamVR_Actions._default.Joystick.GetAxis(inputSource);
                            CurrentInputState.PrimaryButtonGetState = SteamVR_Actions._default.A_Button.GetState(inputSource);
                            CurrentInputState.SecondaryButtonGetState = SteamVR_Actions._default.B_Button.GetState(inputSource);
                            CurrentInputState.Trigger = SteamVR_Actions._default.Trigger.GetAxis(inputSource);
                        }
                        UpdatePlayerControl();
                    }
                }
                else
                {
                    BasisDebug.LogError("Error getting device pose: " + result);
                }
            }
        }
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null && LoadedDeviceRequest == null)
            {
                DeviceSupportInformation Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    LoadModelWithKey(Match.DeviceID);
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        LoadModelWithKey(FallbackDeviceID);
                    }
                }
            }
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
