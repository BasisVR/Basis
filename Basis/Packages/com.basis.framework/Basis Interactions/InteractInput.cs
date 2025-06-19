using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
public partial class BasisPlayerInteract
{
    [Tooltip("Both of the above are relative to object transforms, objects with larger colliders may have issues")]
    [System.Serializable]
    public struct InteractInput
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
        public HoverSphere hoverSphere { get; set; }
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public InteractableObject lastTarget { get; set; }

        public bool IsInput(BasisInput input)
        {
            return deviceUid == input.UniqueDeviceIdentifier;
        }
    }
}
