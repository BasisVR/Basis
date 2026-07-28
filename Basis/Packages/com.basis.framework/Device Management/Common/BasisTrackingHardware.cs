using System;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// How a device produces its pose. This is a claim about the tracking technology, not about the
    /// model, because the technology is what decides how noisy the pose is: an IMU that drifts and
    /// buzzes and a lighthouse tracker that is quiet to a fraction of a millimetre want opposite
    /// filtering. Each backend stamps its own devices; <see cref="Unknown"/> is always safe and means
    /// "filter this like anything else".
    ///
    /// Order is load-bearing. Values ascend by how much filtering the technology needs, so a body
    /// group fed by mixed hardware can take the highest and be filtered for its noisiest contributor.
    /// Unknown sits at 0 so it loses that comparison to any device that actually identified itself.
    /// </summary>
    public enum BasisTrackingHardware : byte
    {
        Unknown = 0,
        Simulated = 1,
        Lighthouse = 2,
        InsideOut = 3,
        Optical = 4,
        Estimated = 5,
        Inertial = 6,
    }

    /// <summary>
    /// Second-guesses a backend's own claim about its devices, for the cases where one backend class
    /// serves several kinds of hardware and only the device strings can tell them apart.
    /// </summary>
    public static class BasisTrackingHardwareClassifier
    {
        /// <summary>
        /// SlimeVR stamps the body part into the serial ("human://WAIST"). Both of its delivery paths
        /// do it — the SolarXR source and the virtual SteamVR trackers its driver publishes — so this
        /// single test catches SlimeVR whether it announces itself as its own subsystem or arrives
        /// disguised as an ordinary OpenVR tracker. Without it, IMU trackers inherit OpenVR's
        /// lighthouse assumption and get filtered far too lightly, which is exactly the buzz they need
        /// smoothing for. Kept in sync with BasisSlimeVRTrackerRoles.SerialPrefix, which cannot be
        /// referenced from here (the OpenVR package must not depend on the SlimeVR integration).
        /// </summary>
        private const string InertialSerialPrefix = "human://";

        /// <summary>
        /// Standable publishes software-estimated full-body tracking through SteamVR under these
        /// render models. The pose is inferred from other trackers rather than measured, so it moves
        /// like an estimate and wants more filtering than the real trackers feeding it.
        /// </summary>
        private const string EstimatedModelPrefix = "{standable}";

        /// <summary>
        /// Refines a backend's assumed classification using the device identity strings, which must
        /// already be populated. Returns <paramref name="assumed"/> when nothing more specific applies.
        /// </summary>
        public static BasisTrackingHardware Refine(BasisTrackingHardware assumed, string commonDeviceIdentifier, string deviceSerial, bool isCameraTracked)
        {
            if (!string.IsNullOrEmpty(deviceSerial) && deviceSerial.StartsWith(InertialSerialPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BasisTrackingHardware.Inertial;
            }

            if (!string.IsNullOrEmpty(commonDeviceIdentifier) && commonDeviceIdentifier.StartsWith(EstimatedModelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BasisTrackingHardware.Estimated;
            }

            if (isCameraTracked)
            {
                return BasisTrackingHardware.Optical;
            }

            return assumed;
        }
    }
}
