using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [Tooltip("Both of the above are relative to object transforms, objects with larger colliders may have issues")]
    [System.Serializable]
    public struct BasisInteractInput
    {
        [HideInInspector]
        [field: System.NonSerialized]
        public string deviceUid { get; set; }
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public BasisInput input { get; set; }
        [HideInInspector]
        [field: System.NonSerialized]
        public Transform interactOrigin { get; set; }
        // TODO: use this ref
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public LineRenderer lineRenderer { get; set; }
        [SerializeField]
        public BasisHoverSphere hoverSphere { get; set; }
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public BasisInteractableObject lastTarget { get; set; }
        public bool IsInput(BasisInput input)
        {
            return deviceUid == input.UniqueDeviceIdentifier;
        }
    }
}
