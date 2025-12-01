using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Simulation
{
    /// <summary>
    /// Device management provider that simulates XR tracked devices without real hardware.
    /// Useful for debugging, editor play mode, or automated testing.
    /// </summary>
    public class BasisSimulateXR : BasisBaseTypeManagement
    {
        /// <summary>
        /// All simulated XR inputs currently created by this provider.
        /// </summary>
        public List<BasisInputXRSimulate> Inputs = new List<BasisInputXRSimulate>();

        /// <summary>
        /// Creates a new simulated XR tracked device and registers it with <see cref="BasisDeviceManagement"/>.
        /// </summary>
        /// <param name="UniqueID">Unique identifier for the simulated device.</param>
        /// <param name="UnUniqueID">Secondary identifier, typically non-unique (e.g., model name).</param>
        /// <param name="Role">The bone-tracked role (e.g., left hand, right hand, head).</param>
        /// <param name="hasrole">Whether the simulated device is assigned a tracked role.</param>
        /// <param name="subSystems">Name of the subsystem reporting this device (defaults to "BasisSimulateXR").</param>
        /// <returns>The created <see cref="BasisInputXRSimulate"/> component.</returns>
        public BasisInputXRSimulate CreatePhysicalTrackedDevice(
            string UniqueID,
            string UnUniqueID,
            BasisBoneTrackedRole Role = BasisBoneTrackedRole.LeftHand,
            bool hasrole = false,
            string subSystems = "BasisSimulateXR")
        {
            // Root GameObject representing the device
            GameObject gameObject = new GameObject(UniqueID);
            gameObject.transform.parent = BasisLocalPlayer.Instance.transform;

            // Movable transform used for simulated tracking updates
            GameObject Moveable = new GameObject(UniqueID + " move transform");
            Moveable.transform.parent = BasisLocalPlayer.Instance.transform;

            // Attach simulated input component
            BasisInputXRSimulate BasisInput = gameObject.AddComponent<BasisInputXRSimulate>();
            BasisInput.FollowMovement = Moveable.transform;
            BasisInput.InitalizeTracking(UniqueID, UnUniqueID, subSystems, hasrole, Role);

            // Track in local list and global device management
            if (!Inputs.Contains(BasisInput))
            {
                Inputs.Add(BasisInput);
            }
            BasisDeviceManagement.Instance.TryAdd(BasisInput);

            return BasisInput;
        }

        /// <summary>
        /// Determines whether this device management provider can boot under a given mode.
        /// </summary>
        /// <param name="BootRequest">Requested boot string (e.g., "OpenXR", "SimulateXR").</param>
        /// <returns>True if <paramref name="BootRequest"/> matches "SimulateXR".</returns>
        public override bool IsDeviceBootable(string BootRequest)
        {
            return BootRequest == "SimulateXR";
        }
    }
}
