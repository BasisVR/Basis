using Unity.Scripting.LifecycleManagement;
#if BASIS_FRAMEWORK_EXISTS
using System;
using System.Collections.Generic;
using Basis.Scripts.Avatar;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// Registers SlimeVR's body-part announcement convention with the framework's announced-role
    /// scanner (<see cref="BasisAnnouncedTrackerRoles"/>): SlimeVR's virtual SteamVR trackers
    /// carry their body part in their serial ("human://WAIST", "human://LEFT_FOOT", ...). Gated
    /// by AutoBindSlimeVRTrackers (default on). The serial claims the device even when auto-bind
    /// is off — SlimeVR stamps the SteamVR role too, so falling through would re-bind it via the
    /// scanner's TrustSteamVRRoles path.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisSlimeVRTrackerRoles
    {
        private const string SerialPrefix = "human://";

        /// <summary>
        /// SlimeVR serial body-part token to Basis role. SlimeVR's KNEE/ELBOW trackers ride the
        /// upper-leg/upper-arm segment ends, which are Basis's LowerLeg/LowerArm tracker roles.
        /// HEAD and the HANDs are deliberately absent: the HMD and controllers own those bones.
        /// </summary>
        private static readonly Dictionary<string, BasisBoneTrackedRole> RolesBySerialPart =
            new Dictionary<string, BasisBoneTrackedRole>(StringComparer.OrdinalIgnoreCase)
        {
            { "WAIST", BasisBoneTrackedRole.Hips },
            { "CHEST", BasisBoneTrackedRole.Chest },
            { "LEFT_FOOT", BasisBoneTrackedRole.LeftFoot },
            { "RIGHT_FOOT", BasisBoneTrackedRole.RightFoot },
            { "LEFT_KNEE", BasisBoneTrackedRole.LeftLowerLeg },
            { "RIGHT_KNEE", BasisBoneTrackedRole.RightLowerLeg },
            { "LEFT_ELBOW", BasisBoneTrackedRole.LeftLowerArm },
            { "RIGHT_ELBOW", BasisBoneTrackedRole.RightLowerArm },
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Register()
        {
            // The scanner only runs on desktop platforms; registering elsewhere is harmless.
            BasisAnnouncedTrackerRoles.RegisterSource(MapSlimeVRSerial);
        }

        private static BasisAnnouncedTrackerRoles.RoleClaim MapSlimeVRSerial(BasisInput input, out BasisBoneTrackedRole role)
        {
            role = BasisBoneTrackedRole.CenterEye;
            string serial = input.DeviceSerial;
            if (string.IsNullOrEmpty(serial) || !serial.StartsWith(SerialPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BasisAnnouncedTrackerRoles.RoleClaim.NotMine;
            }
            if (!BasisSlimeVRSettings.AutoBindSlimeVRTrackers.RawValue)
            {
                return BasisAnnouncedTrackerRoles.RoleClaim.Suppress;
            }
            return RolesBySerialPart.TryGetValue(serial.Substring(SerialPrefix.Length).Trim(), out role)
                ? BasisAnnouncedTrackerRoles.RoleClaim.Mapped
                : BasisAnnouncedTrackerRoles.RoleClaim.Suppress;
        }
    }
}
#endif
