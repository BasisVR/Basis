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
        public Vector3 LocalPositionOffset;
        public Quaternion LocalRotationOffset;

        public BasisPickupInteractable PickupRef;
        public Rigidbody RigidRef;
        public bool PreBindKinematic;
        public bool HasKinematicCaptured;
    }
}
