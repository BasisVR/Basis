namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Defines string constants used throughout the Basis SDK for device and runtime management.
    /// </summary>
    public static class BasisConstants
    {
        /// <summary>
        /// Represents an invalid or unrecognized state or request.
        /// </summary>
        public const string InvalidConst = "Invalid";

        /// <summary>
        /// Identifier for desktop-based device management.
        /// </summary>
        public const string Desktop = "Desktop";

        /// <summary>
        /// Identifier for headless mode, where no graphics device is present or rendering is disabled.
        /// </summary>
        public const string Headless = "Headless";

        /// <summary>
        /// Identifier used when simulating XR (Extended Reality) input or devices.
        /// </summary>
        public const string SimulateXR = "SimulateXR";

        /// <summary>
        /// Identifier for the OpenVR loader, used for managing OpenVR-based XR runtimes.
        /// </summary>
        public const string OpenVRLoader = "OpenVRLoader";

        /// <summary>
        /// Identifier for the OpenXR loader, used for managing OpenXR-based XR runtimes.
        /// </summary>
        public const string OpenXRLoader = "OpenXRLoader";

        /// <summary>
        /// Identifier representing the system or SDK exiting state.
        /// </summary>
        public const string Exiting = "Exiting";

        /// <summary>
        /// Identifier representing the absence of any device or state.
        /// </summary>
        public const string None = "None";

        /// <summary>
        /// Identifier for the network management system or module.
        /// </summary>
        public const string NetworkManagement = "NetworkManagement";
    }
}
