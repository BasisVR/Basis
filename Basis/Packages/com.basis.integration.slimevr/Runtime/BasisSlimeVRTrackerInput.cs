#if BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using solarxr_protocol.datatypes;
using UnityEngine;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// A Basis input device whose pose is sourced from the SlimeVR server over SolarXR rather than from
    /// the OpenVR/OpenXR runtime. Created and driven by <see cref="BasisSlimeVRTrackerSource"/>. It carries
    /// the same "human://..." serial an OpenVR-sourced SlimeVR tracker would, so role assignment
    /// (BasisAnnouncedTrackerRoles) and calibration treat it identically — this class only supplies poses.
    /// </summary>
    public class BasisSlimeVRTrackerInput : BasisInput
    {
        /// <summary>SlimeVR body part this device represents; the source resolves its pose per frame.</summary>
        public BodyPart BodyPart;

        public void Initialize(string uniqueID, string unUniqueID, string subSystem, string deviceSerial, BodyPart bodyPart)
        {
            BodyPart = bodyPart;
            DeviceSerial = deviceSerial;
            TrackingHardware = BasisTrackingHardware.Inertial;
            // No forced role: the announced-role scanner binds this by its serial, exactly as for an
            // OpenVR-sourced SlimeVR tracker.
            InitializeTracking(uniqueID, unUniqueID, subSystem, false, BasisBoneTrackedRole.CenterEye);
        }

        public override void RenderPollData()
        {
            if (!PollPose())
            {
                return;
            }
            UpdateInputEvents();
            ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
        }

        public override void LateDoPollData()
        {
            PollPose();
        }

        private bool PollPose()
        {
            if (!BasisSlimeVRTrackerSource.TryGetTrackerPose(BodyPart, out Vector3 position, out Quaternion rotation))
            {
                return false;
            }

            // The pose already comes back in Basis unscaled playspace (the source anchors it to the HMD's
            // UnscaledDeviceCoord, which the runtime has already offset for seated/play-space shift), so it
            // is written straight in — passing it through ComputeUnscaledDeviceCoord would double that offset.
            UnscaledDeviceCoord.position = position;
            UnscaledDeviceCoord.rotation = rotation;
            ConvertToScaledDeviceCoord();
            ControlOnlyAsDevice();
            return true;
        }

        public override void ShowTrackedVisual()
        {
            // Cosmetic puck models are left to the physical-tracker backends; the server-sourced device
            // drives the rig without a visual. (Role gizmos/spheres still render from the bone system.)
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
        }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
    }
}
#endif
