using Basis.Scripts.Device_Management.Devices.OpenVR;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Valve.VR;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;

namespace Basis.Scripts.Device_Management.Devices.Unity_Spatial_Tracking
{
    [DefaultExecutionOrder(15001)]
    public class BasisOpenVRInputSpatial : BasisInput
    {
        // SteamVR/OpenVR tracked device backing this "spatial" device
        public OpenVRDevice Device;

        // Compositor poses (same pattern as your controller)
        public TrackedDevicePose_t devicePose = new TrackedDevicePose_t();
        public TrackedDevicePose_t deviceGamePose = new TrackedDevicePose_t();
        public EVRCompositorError result;

        // Optional: keep if you still want a notion of what this *represents*
        // (CenterEye vs GenericTracker etc). Not used for pose anymore.
        public BasisBoneTrackedRole InitialRole;

        public BasisOpenVRInputEye BasisOpenVRInputEye;
        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();

        /// <summary>
        /// SteamVR/OpenVR init. Mirrors your controller-style init.
        /// </summary>
        public void Initialize(
            OpenVRDevice device,
            string UniqueID,
            string UnUniqueID,
            string subSystems,
            bool AssignTrackedRole,
            BasisBoneTrackedRole basisBoneTrackedRole)
        {
            Device = device;
            InitialRole = basisBoneTrackedRole;

            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);

            if (basisBoneTrackedRole == BasisBoneTrackedRole.CenterEye)
            {
                BasisOpenVRInputEye = gameObject.AddComponent<BasisOpenVRInputEye>();
                BasisOpenVRInputEye.Initalize();
                BasisVirtualSpine.Initialize();
            }
        }

        public new void OnDestroy()
        {
            BasisVirtualSpine.DeInitialize();

            if (BasisOpenVRInputEye != null)
                BasisOpenVRInputEye.Shutdown();

            base.OnDestroy();
        }

        public override void LateDoPollData()
        {
            // Intentionally empty (matches your existing pattern)
        }

        public override void RenderPollData()
        {
            if (!SteamVR.active || SteamVR.instance == null || SteamVR.instance.compositor == null)
            {
                BasisDebug.LogError("Cant Poll SteamVR was not active");
                return;
            }
            // Pull latest pose directly from compositor (SteamVR way)
            result = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(
                Device.deviceIndex,
                ref devicePose,
                ref deviceGamePose
            );

            if (result != EVRCompositorError.None)
            {
                return;
            }

            if (!devicePose.bPoseIsValid)
            {
                return;
            }

            // Unscaled device coord in *real* tracking space
            ComputeUnscaledDeviceCoord(
                ref UnscaledDeviceCoord,
                devicePose.mDeviceToAbsoluteTracking.GetPosition()
            );
            UnscaledDeviceCoord.rotation = devicePose.mDeviceToAbsoluteTracking.GetRotation();

            // Your existing scaling pipeline
            ConvertToScaledDeviceCoord();

            // CenterEye extra simulation path
            if (TryGetRole(out var currentRole) && currentRole == BasisBoneTrackedRole.CenterEye)
            {
                if (BasisOpenVRInputEye != null)
                {
                    BasisOpenVRInputEye.Simulate();
                }
            }

            // Push into your device pipeline
            ControlOnlyAsDevice();

            // Ray: same as before
            ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);

            UpdateInputEvents();
        }

        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null)
            {
                DeviceSupportInformation match =
                    BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);

                if (match.CanDisplayPhysicalTracker)
                {
                    LoadModelWithKey(match.DeviceID);
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
            BasisDebug.LogError("Spatial does not support Haptics Playback");
        }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
    }
}
