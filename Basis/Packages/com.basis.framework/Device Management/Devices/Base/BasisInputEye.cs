using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices
{
    /// <summary>
    /// Abstract base class for eye-tracking input devices.
    /// Provides left/right eye positions and lifecycle hooks for initialization, simulation, and shutdown.
    /// </summary>
    public abstract class BasisInputEye : MonoBehaviour
    {
        /// <summary>
        /// Current world-space position of the left eye.
        /// </summary>
        public Vector3 LeftPosition;

        /// <summary>
        /// Current world-space position of the right eye.
        /// </summary>
        public Vector3 RightPosition;

        /// <summary>
        /// Initializes the eye input system (device setup, calibration, etc.).
        /// </summary>
        public abstract void Initalize();

        /// <summary>
        /// Updates eye-tracking values in a simulated mode (e.g., when no hardware is available).
        /// </summary>
        public abstract void Simulate();

        /// <summary>
        /// Shuts down and releases any resources associated with the eye input system.
        /// </summary>
        public abstract void Shutdown();
    }
}
