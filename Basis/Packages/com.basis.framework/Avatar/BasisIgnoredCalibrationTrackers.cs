using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Device_Management.Devices;
using System;
using System.Collections.Generic;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Devices kept out of full-body calibration, recognized by the identity strings their runtime
    /// reports. Hand-tracking bridges publish their solved hands as ordinary SteamVR trackers carrying
    /// no role, so the geometric classifier sees two extra free trackers sitting at hand height and
    /// binds them to LowerArm/Shoulder — dragging those roles off the real trackers. They are not
    /// body-segment trackers, so they are dropped from the constellation rather than scored against it.
    ///
    /// Gated behind IgnoreHandTrackingDevices (Tracker Settings, off by default): the only evidence
    /// here is a device name, so an unconditional list would silently stop a legitimately-named
    /// tracker from ever calibrating.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisIgnoredCalibrationTrackers
    {
        /// <summary>
        /// Identity strings of hand-tracking software that publishes SteamVR trackers. Matched
        /// case-insensitively (whole string) against the device serial, the common identifier
        /// (OpenVR's render model name) and the unique identifier's name half. Public so an
        /// integration package can add its own devices at RuntimeInitializeOnLoad time.
        /// </summary>
        public static readonly HashSet<string> IgnoredIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HANDL",
            "HANDR",
            "VRLINKQ_Hand_Left",
            "VRLINKQ_Hand_Right",
        };

        /// <summary>
        /// Whether calibration should skip this device. False unless the user turned the exclusion on,
        /// so the shipped behaviour is unchanged and every tracker still classifies as it always has.
        /// </summary>
        public static bool ShouldIgnore(BasisInput input)
        {
            return Basis.BasisUI.BasisSettingsDefaults.IgnoreHandTrackingDevices.RawValue
                && input != null
                && MatchesIgnoredName(input.DeviceSerial, input.CommonDeviceIdentifier, input.UniqueDeviceIdentifier);
        }

        /// <summary>The name test on its own, with no regard for the setting.</summary>
        public static bool MatchesIgnoredName(string deviceSerial, string commonDeviceIdentifier, string uniqueDeviceIdentifier)
        {
            return Matches(deviceSerial) || Matches(commonDeviceIdentifier) || Matches(StripDeviceIndex(uniqueDeviceIdentifier));
        }

        private static bool Matches(string identifier)
        {
            return !string.IsNullOrEmpty(identifier) && IgnoredIdentifiers.Contains(identifier.Trim());
        }

        /// <summary>
        /// OpenVR's unique identifier is "{deviceIndex}|{renderModelName}" — the index is session
        /// volatile, only the name half is worth matching. Other backends pass the name through as is.
        /// </summary>
        private static string StripDeviceIndex(string uniqueDeviceIdentifier)
        {
            if (string.IsNullOrEmpty(uniqueDeviceIdentifier)) return uniqueDeviceIdentifier;
            int split = uniqueDeviceIdentifier.LastIndexOf('|');
            return split >= 0 ? uniqueDeviceIdentifier.Substring(split + 1) : uniqueDeviceIdentifier;
        }
    }
}
