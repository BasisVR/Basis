using Basis.Scripts.Common;
using System;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.TransformBinders.BoneControl
{
    [System.Serializable]
    [BurstCompile]
    public class BasisLocalBoneControl
    {
        [SerializeField]
        public string name;
        [NonSerialized]
        public BasisLocalBoneControl Target;

        public float LerpAmountNormal;
        public float LerpAmountFastMovement;
        public float AngleBeforeSpeedup;

        public bool HasLineDraw;
        public int LineDrawIndex;
        public bool HasTarget = false;
        public Vector3 Offset;
        public Vector3 ScaledOffset;
        public float LerpAmount;
        public int GizmoReference = -1;
        public bool HasGizmo = false;

        public int TposeGizmoReference = -1;
        public bool TposeHasGizmo = false;
        public bool HasVirtualOverride;
        //previous
        [SerializeField]
        public BasisCalibratedCoords LastRunData = new BasisCalibratedCoords();
        //next
        [SerializeField]
        public BasisCalibratedCoords IncomingData = new BasisCalibratedCoords();
        //out going
        [SerializeField]
        public BasisCalibratedCoords OutgoingWorldData = new BasisCalibratedCoords();

        [SerializeField]
        public BasisCalibratedCoords OutGoingData = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords TposeLocal = new BasisCalibratedCoords();
        //the scaled tpose is tpose * the avatar height change
        [SerializeField]
        public BasisCalibratedCoords TposeLocalScaled = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords InverseOffsetFromBone = new BasisCalibratedCoords();
        [SerializeField]
        [HideInInspector]
        private Color gizmoColor = Color.blue;
        [HideInInspector]
        [SerializeField]
        private float positionWeight = 1;
        [HideInInspector]
        [SerializeField]
        private float rotationWeight = 1;
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
        // Events for property changes
        public Action OnHasRigChanged;

        public Action<float, float> WeightsChanged;
        // Backing fields for the properties
        [SerializeField]
        private BasisHasRigLayer hasRigLayer = BasisHasRigLayer.HasNoRigLayer;
        // Properties with get/set accessors
        public BasisHasRigLayer HasRigLayer
        {
            get => hasRigLayer;
            set
            {
                if (hasRigLayer != value)
                {
                    hasRigLayer = value;
                    OnHasRigChanged?.Invoke();
                }
            }
        }
        public float PositionWeight
        {
            get => positionWeight;
            set
            {
                if (positionWeight != value)
                {
                    positionWeight = value;
                    WeightsChanged.Invoke(positionWeight, rotationWeight);
                }
            }
        }
        public float RotationWeight
        {
            get => rotationWeight;
            set
            {
                if (rotationWeight != value)
                {
                    rotationWeight = value;
                    WeightsChanged.Invoke(positionWeight, rotationWeight);
                }
            }
        }
        public Color Color { get => gizmoColor; set => gizmoColor = value; }
    }
}
