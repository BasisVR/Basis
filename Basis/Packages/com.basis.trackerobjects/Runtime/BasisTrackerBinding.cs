using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.TrackerObjects
{
    public class BasisTrackerBinding
    {
        public int Id;
        public BasisInput Tracker;
        public Transform Target;
        public string UniqueDeviceIdentifier;
        public string LoadedNetID;
        // Read every frame by the render drive, so external code can adjust a grip
        // offset on a live binding.
        public Vector3 LocalPositionOffset;
        public Quaternion LocalRotationOffset;

        // Capture/restore state for a clean unbind; internal so outside code can't
        // desync it from the bind/unbind lifecycle.
        internal BasisPickupInteractable PickupRef;
        internal Rigidbody RigidRef;
        internal bool PreBindKinematic;
        internal bool HasKinematicCaptured;

        internal BasisPickupSyncNetworking SyncRef;
        internal bool PreBindDistanceReduction;
    }
}
