using Basis.Scripts.Common;
using System;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.TransformBinders.BoneControl
{
    [System.Serializable]
    public class BasisRemoteBoneControl
    {
        [SerializeField]
        public string name;
        [NonSerialized]
        public BasisRemoteBoneControl Target;
        public bool HasLineDraw;
        public int LineDrawIndex;
        public bool HasTarget = false;
        public float3 Offset;
        public float3 ScaledOffset;
        [SerializeField]
        public BasisCalibratedCoords OutGoingData = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords TposeLocal = new BasisCalibratedCoords();
        //the scaled tpose is tpose * the avatar height change
        [SerializeField]
        public BasisCalibratedCoords TposeLocalScaled = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords IncomingData = new BasisCalibratedCoords();
        public int GizmoReference = -1;
        public bool HasGizmo = false;

        public int TposeGizmoReference = -1;
        public bool TposeHasGizmo = false;
        public void ComputeMovementRemote()
        {
            if (hasTrackerDriver == BasisHasTracked.HasTracker)
            {
                OutGoingData.rotation = IncomingData.rotation;
                OutGoingData.position = IncomingData.position;
            }
            else
            {
                if (HasTarget)
                {
                    var targetRotation = Target.OutGoingData.rotation;
                    var targetPosition = Target.OutGoingData.position;

                    Vector3 offset = targetRotation * ScaledOffset;
                    OutGoingData.position = targetPosition + offset;

                    OutGoingData.rotation = targetRotation;
                }
                else
                {
                    OutGoingData.rotation = IncomingData.rotation;
                    OutGoingData.position = IncomingData.position;
                }
            }
        }
        [SerializeField]
        [HideInInspector]
        private Color gizmoColor = Color.blue;
        // Events for property changes
        public System.Action<BasisHasTracked> OnHasTrackerDriverChanged;
        // Backing fields for the properties
        [SerializeField]
        private BasisHasTracked hasTrackerDriver = BasisHasTracked.HasNoTracker;
        // Properties with get/set accessors
        public BasisHasTracked HasTracked
        {
            get => hasTrackerDriver;
            set
            {
                if (hasTrackerDriver != value)
                {
                    // BasisDebug.Log("Setting Tracker To has Tracker Position Driver " + value);
                    hasTrackerDriver = value;
                    OnHasTrackerDriverChanged?.Invoke(value);
                }
            }
        }
        public Color Color { get => gizmoColor; set => gizmoColor = value; }
        public bool HasBone { get; internal set; }
        public void Initialize()
        {
            OutGoingData.position = Vector3.zero;
            OutGoingData.rotation = Quaternion.identity;
            HasBone = true;
        }
    }
}
