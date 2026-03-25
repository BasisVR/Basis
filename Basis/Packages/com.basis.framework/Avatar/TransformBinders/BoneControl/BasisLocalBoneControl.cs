using Basis.Scripts.Common;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
    /// <summary>
    /// Computes local and world-space bone transforms for the local avatar.
    /// Supports tracking-driven motion (with optional inverse offset), or
    /// virtual motion that lerps toward a target bone when tracking is absent.
    /// </summary>
    [Serializable]
    [BurstCompile]
    public class BasisLocalBoneControl
    {
        /// <summary>Angle (degrees) after which rotation interpolation speeds up.</summary>
        public static readonly float AngleBeforeSpeedup = 25f;

        /// <summary>Smoothing factor for tracker-driven motion (position/rotation).</summary>
        public static readonly float trackersmooth = 25;

        /// <summary>Base interpolation rate for quaternions (per second).</summary>
        public static readonly float QuaternionLerp = 14;

        /// <summary>Fast interpolation rate used when angular delta exceeds threshold.</summary>
        public static readonly float QuaternionLerpFastMovement = 56;

        /// <summary>Base interpolation rate for positions (per second).</summary>
        public static float PositionLerpAmount = 40;

        /// <summary>Indicates whether any global events have been wired (if applicable).</summary>
        public static bool HasEvents { get; internal set; }

        /// <summary>Debug/display name for this bone control.</summary>
        [SerializeField] public string name;

        /// <summary>Optional target bone used when tracking is absent.</summary>
        [NonSerialized] public BasisLocalBoneControl Target;

        /// <summary>Whether to draw a line gizmo for this control.</summary>
        public bool HasLineDraw;

        /// <summary>Index used by a line-drawing system, if any.</summary>
        public int LineDrawIndex;

        /// <summary>True if a valid <see cref="Target"/> has been assigned.</summary>
        public bool HasTarget { get { return Target != null; } }

        /// <summary>Local-space offset applied relative to the target bone.</summary>
        public float3 Offset;

        /// <summary>Scaled version of <see cref="Offset"/> (e.g., scaled by avatar height).</summary>
        public float3 ScaledOffset;

        /// <summary>True if a virtual override is driving this bone instead of tracking.</summary>
        public bool HasVirtualOverride;

        /// <summary>When true, applies the inverse offset from the bone on incoming data.</summary>
        public bool UseInverseOffset;

        /// <summary>Editor/debug color for visualization.</summary>
        [SerializeField] public Color Color = Color.blue;

        /// <summary>Raised when <see cref="HasTracked"/> changes.</summary>
        public Action<BasisHasTracked> OnHasTrackerDriverChanged;

        [SerializeField] private BasisHasTracked hasTrackerDriver = BasisHasTracked.HasNoTracker;

        public List<string> DevicesWithRoles = new List<string>();
        /// <summary>
        /// Indicates whether this bone currently has tracker input.
        /// Invokes <see cref="OnHasTrackerDriverChanged"/> when changed.
        /// </summary>
        public BasisHasTracked HasTracked
        {
            get => hasTrackerDriver;
            set
            {
                if (hasTrackerDriver != value)
                {
                    hasTrackerDriver = value;
                    OnHasTrackerDriverChanged?.Invoke(value);
                }
            }
        }

        /// <summary>Raised when <see cref="HasRigLayer"/> changes.</summary>
        public Action<bool> OnHasRigChanged;

        [SerializeField] private BasisHasRigLayer hasRigLayer = BasisHasRigLayer.HasNoRigLayer;

        /// <summary>
        /// Indicates whether this bone participates in a rig layer.
        /// Invokes <see cref="OnHasRigChanged"/> when changed.
        /// </summary>
        public BasisHasRigLayer HasRigLayer
        {
            get => hasRigLayer;
            set
            {
                if (hasRigLayer != value)
                {
                    hasRigLayer = value;
                    OnHasRigChanged?.Invoke(false);//means the ik does not detach
                }
            }
        }

        /// <summary>Incoming (tracker or virtual) local-space pose.</summary>
        [SerializeField] public BasisCalibratedCoords IncomingData = new BasisCalibratedCoords();

        /// <summary>Outgoing local-space pose after processing.</summary>
        [SerializeField] public BasisCalibratedCoords OutGoingData = new BasisCalibratedCoords();

        /// <summary>Outgoing world-space pose after applying parent transform.</summary>
        [SerializeField] public BasisCalibratedCoords OutgoingWorldData = new BasisCalibratedCoords();

        /// <summary>Pose from the previous compute step (local space).</summary>
        [SerializeField] public BasisCalibratedCoords LastRunData = new BasisCalibratedCoords();

        /// <summary>Inverse offset from the bone used when <see cref="UseInverseOffset"/> is true.</summary>
        [SerializeField] public BasisCalibratedCoords InverseOffsetFromBone = new BasisCalibratedCoords();

        /// <summary>T-pose local-space reference.</summary>
        [SerializeField] public BasisCalibratedCoords TposeLocal = new BasisCalibratedCoords();

        /// <summary>Scaled T-pose local-space reference (e.g., by avatar height change).</summary>
        [SerializeField] public BasisCalibratedCoords TposeLocalScaled = new BasisCalibratedCoords();

        /// <summary>
        /// Computes the outgoing local and world pose for this bone.
        /// If tracking is present, copies (or offset-corrects) incoming data; otherwise,
        /// lerps toward the <see cref="Target"/> plus <see cref="ScaledOffset"/>.
        /// </summary>
        /// <param name="parentMatrix">Parent transform matrix for world conversion.</param>
        /// <param name="DeltaTime">Frame delta time (unscaled).</param>
        public void ComputeMovementLocal(Matrix4x4 parentMatrix, float DeltaTime)
        {
            if (hasTrackerDriver == BasisHasTracked.HasTracker)
            {
                if (UseInverseOffset)
                {
                    Vector3 DestinationPosition = IncomingData.position + IncomingData.rotation * InverseOffsetFromBone.position;
                    Quaternion DestinationRotation = IncomingData.rotation * InverseOffsetFromBone.rotation;

                    // Smooth toward destination
                    OutGoingData.position = Vector3.Lerp(LastRunData.position, DestinationPosition, trackersmooth);
                    OutGoingData.rotation = Quaternion.Slerp(LastRunData.rotation, DestinationRotation, trackersmooth);
                }
                else
                {
                    // Directly use the incoming tracker pose
                    OutGoingData.rotation = IncomingData.rotation;
                    OutGoingData.position = IncomingData.position;
                }

                ApplyWorldAndLast(parentMatrix);
            }
            else
            {
                if (!HasVirtualOverride && HasTarget)
                {
                    OutGoingData.rotation = ApplyLerpToQuaternion(DeltaTime, LastRunData.rotation, Target.OutGoingData.rotation);

                    // Offset relative to target’s rotation
                    Vector3 customDirection = Target.OutGoingData.rotation * ScaledOffset;
                    Vector3 targetPosition = Target.OutGoingData.position + customDirection;

                    float lerpFactor = ClampInterpolationFactor(PositionLerpAmount, DeltaTime);
                    OutGoingData.position = Vector3.Lerp(LastRunData.position, targetPosition, lerpFactor);

                    ApplyWorldAndLast(parentMatrix);
                }
            }
        }

        /// <summary>
        /// Interpolates between two rotations using a speed that increases with angular difference.
        /// </summary>
        /// <param name="DeltaTime">Frame delta time.</param>
        /// <param name="CurrentRotation">Current rotation.</param>
        /// <param name="FutureRotation">Target rotation.</param>
        /// <returns>Interpolated rotation.</returns>
        public Quaternion ApplyLerpToQuaternion(float DeltaTime, Quaternion CurrentRotation, Quaternion FutureRotation)
        {
            // Dot product ≈ cosine of half-angle; detects similarity
            float dotProduct = math.dot(CurrentRotation, FutureRotation);

            // Early-outs for near-identical rotations
            if (dotProduct > 0.999999f)
            {
                return FutureRotation;
            }

            float angleDifference = math.acos(math.clamp(dotProduct, -1f, 1f));
            if (angleDifference < math.EPSILON)
            {
                return FutureRotation;
            }

            // Blend rate between normal and fast movement using normalized angle fraction
            float lerpAmountNormal = QuaternionLerp;
            float timing = math.min(angleDifference / AngleBeforeSpeedup, 1f);
            float lerpAmount = lerpAmountNormal + (QuaternionLerpFastMovement - lerpAmountNormal) * timing;

            // Frame-rate-independent factor
            float lerpFactor = ClampInterpolationFactor(lerpAmount, DeltaTime);

            return math.slerp(CurrentRotation, FutureRotation, lerpFactor);
        }

        /// <summary>
        /// Converts a per-second interpolation rate into a clamped [0,1] fraction.
        /// </summary>
        /// <param name="lerpAmount">Interpolation rate (per second).</param>
        /// <param name="DeltaTime">Frame delta time.</param>
        /// <returns>Clamped interpolation factor in [0,1].</returns>
        private float ClampInterpolationFactor(float lerpAmount, float DeltaTime)
        {
            return math.clamp(lerpAmount * DeltaTime, 0f, 1f);
        }

        /// <summary>
        /// Writes <see cref="OutGoingData"/> to <see cref="LastRunData"/>,
        /// computes world-space pose into <see cref="OutgoingWorldData"/> using <paramref name="parentMatrix"/>.
        /// </summary>
        /// <param name="parentMatrix">Parent transform matrix.</param>
        public void ApplyWorldAndLast(Matrix4x4 parentMatrix)
        {
            LastRunData.position = OutGoingData.position;
            LastRunData.rotation = OutGoingData.rotation;

            OutgoingWorldData.position = parentMatrix.MultiplyPoint3x4(OutGoingData.position);
            OutgoingWorldData.rotation = parentMatrix.rotation * OutGoingData.rotation;
        }
    }
}
