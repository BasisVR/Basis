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
            TrackingHardware = BasisTrackingHardware.Lighthouse;

            InitializeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);

            if (basisBoneTrackedRole == BasisBoneTrackedRole.CenterEye)
            {
                BasisOpenVRInputEye = gameObject.AddComponent<BasisOpenVRInputEye>();
                BasisOpenVRInputEye.Initialize();
            }
        }

        public new void OnDestroy()
        {
            if (BasisOpenVRInputEye != null)
                BasisOpenVRInputEye.Shutdown();

            base.OnDestroy();
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

            // CenterEye extra simulation path
            if (TryGetRole(out var currentRole) && currentRole == BasisBoneTrackedRole.CenterEye)
            {
                if (BasisOpenVRInputEye != null)
                {
                    BasisOpenVRInputEye.Simulate();
                }
            }

            // Ray: same as before
            ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);

            UpdateInputEvents();
        }

        private bool PollPose()
        {
            if (!SteamVR.active || SteamVR.instance == null || SteamVR.instance.compositor == null)
            {
                BasisDebug.LogError("Can't Poll SteamVR was not active");
                return false;
            }
            // Pull latest pose directly from compositor (SteamVR way)
            result = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(
                Device.deviceIndex,
                ref devicePose,
                ref deviceGamePose
            );

            if (result != EVRCompositorError.None)
            {
                return false;
            }

            if (!devicePose.bPoseIsValid)
            {
                return false;
            }

            // Unscaled device coord in *real* tracking space
            Vector3 devicePosition = devicePose.mDeviceToAbsoluteTracking.GetPosition();
            Quaternion deviceRotation = devicePose.mDeviceToAbsoluteTracking.GetRotation();

            // The HMD's tracked origin isn't the eyes. Cache the full head-local origin->center-eye offset
            // (averaged eye-to-head; head-local so head pitch can't leak the forward term into the height).
            if (Device.deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd
                && SteamVR.instance != null && SteamVR.instance.eyes != null && SteamVR.instance.eyes.Length >= 2)
            {
                CenterEyeOffset = (SteamVR.instance.eyes[0].pos + SteamVR.instance.eyes[1].pos) * 0.5f;
                CenterEyeVerticalOffset = CenterEyeOffset.y;
            }

            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, devicePosition);
            UnscaledDeviceCoord.rotation = deviceRotation;

            // Your existing scaling pipeline
            ConvertToScaledDeviceCoord();

            // Place the avatar eye bone at the true center-eye (Control only -- the camera keeps the device-
            // origin pose so SteamVR's per-eye render offset isn't doubled), scaled with the avatar.
            ScaledControlPositionOffset = ScaledDeviceCoord.rotation * (CenterEyeOffset * BasisHeightDriver.DeviceScale);

            // Push into your device pipeline
            ControlOnlyAsDevice();
            return true;
        }

        public override void ShowTrackedVisual()
        {
            ShowTrackedVisualDefaultImplementation();
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
