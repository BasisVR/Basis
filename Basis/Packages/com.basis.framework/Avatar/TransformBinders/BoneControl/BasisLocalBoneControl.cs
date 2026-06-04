using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
    /// <summary>
    /// Handle to a single local-avatar bone. The per-frame pose lives in the owning
    /// <see cref="BasisLocalBoneDriver"/>'s native store (indexed by <see cref="Index"/>);
    /// this type exposes it through get-properties / setters plus the managed-only
    /// metadata (name, target, calibration, events) the jobs don't touch. Store access goes
    /// through the owner's cached raw pointers to skip the NativeArray indexer's safety overhead.
    /// </summary>
    [Serializable]
    public class BasisLocalBoneControl
    {
        /// <summary>Angle (degrees) after which rotation interpolation speeds up.</summary>
        public const float AngleBeforeSpeedup = 25f;

        /// <summary>Smoothing factor for tracker-driven motion (position/rotation).</summary>
        public const float trackersmooth = 25;

        /// <summary>Base interpolation rate for quaternions (per second).</summary>
        public const float QuaternionLerp = 14;

        /// <summary>Fast interpolation rate used when angular delta exceeds threshold.</summary>
        public const float QuaternionLerpFastMovement = 56;

        /// <summary>Base interpolation rate for positions (per second).</summary>
        public const float PositionLerpAmount = 40;

        /// <summary>Indicates whether any global events have been wired (if applicable).</summary>
        public static bool HasEvents { get; internal set; }

        /// <summary>Owning driver whose native store backs this bone's pose.</summary>
        [NonSerialized] internal BasisLocalBoneDriver Owner;

        /// <summary>Index of this bone within the owner's store and Controls array.</summary>
        [NonSerialized] internal int Index;

        /// <summary>Debug/display name for this bone control.</summary>
        [SerializeField] public string name;

        /// <summary>Index (in the owner's Controls/store) of the optional target bone used when tracking is absent; -1 if none.</summary>
        [NonSerialized] public int TargetIndex = -1;

        /// <summary>True if a valid <see cref="TargetIndex"/> has been assigned.</summary>
        public bool HasTarget { get { return TargetIndex >= 0; } }

        /// <summary>Local-space offset applied relative to the target bone.</summary>
        public float3 Offset;

        /// <summary>Editor/debug color for visualization.</summary>
        [SerializeField] public Color Color = Color.blue;

        /// <summary>Raised when <see cref="HasTracked"/> changes.</summary>
        public Action<BasisHasTracked> OnHasTrackerDriverChanged;

        [SerializeField] private BasisHasTracked hasTrackerDriver = BasisHasTracked.HasNoTracker;

        public List<string> DevicesWithRoles = new List<string>();

        /// <summary>Raised when <see cref="HasRigLayer"/> changes.</summary>
        public Action<bool> OnHasRigChanged;

        [SerializeField] private BasisHasRigLayer hasRigLayer = BasisHasRigLayer.HasNoRigLayer;

        /// <summary>T-pose local-space reference (calibration data, not job-accessed).</summary>
        [SerializeField] public BasisCalibratedCoords TposeLocal = new BasisCalibratedCoords();

        /// <summary>Scaled T-pose local-space reference (calibration data, not job-accessed).</summary>
        [SerializeField] public BasisCalibratedCoords TposeLocalScaled = new BasisCalibratedCoords();

        // ===== Native-backed pose: lives in Owner's store at Index, reached via raw pointer =====

        /// <summary>Incoming (tracker or virtual) local-space pose.</summary>
        public unsafe BasisCalibratedCoords IncomingData
        {
            get
            {
                if (Owner == null) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimInput i = ref Owner._simInputsPtr[Index];
                return new BasisCalibratedCoords(i.IncomingPosition, i.IncomingRotation);
            }
        }

        /// <summary>Outgoing local-space pose after processing.</summary>
        public unsafe BasisCalibratedCoords OutGoingData
        {
            get
            {
                if (Owner == null) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner._simStatesPtr[Index];
                return new BasisCalibratedCoords(s.OutgoingPosition, s.OutgoingRotation);
            }
        }

        /// <summary>Outgoing world-space pose after applying parent transform.</summary>
        public unsafe BasisCalibratedCoords OutgoingWorldData
        {
            get
            {
                if (Owner == null) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner._simStatesPtr[Index];
                return new BasisCalibratedCoords(s.OutgoingWorldPosition, s.OutgoingWorldRotation);
            }
        }

        /// <summary>Pose from the previous compute step (local space).</summary>
        public unsafe BasisCalibratedCoords LastRunData
        {
            get
            {
                if (Owner == null) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner._simStatesPtr[Index];
                return new BasisCalibratedCoords(s.LastRunPosition, s.LastRunRotation);
            }
        }

        /// <summary>Inverse offset from the bone used when <see cref="UseInverseOffset"/> is true.</summary>
        public unsafe BasisCalibratedCoords InverseOffsetFromBone
        {
            get
            {
                if (Owner == null) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimInput i = ref Owner._simInputsPtr[Index];
                return new BasisCalibratedCoords(i.InverseOffsetPosition, i.InverseOffsetRotation);
            }
        }

        /// <summary>Scaled version of <see cref="Offset"/> (e.g., scaled by avatar height).</summary>
        public unsafe float3 ScaledOffset
        {
            get => Owner == null ? float3.zero : Owner._simInputsPtr[Index].ScaledOffset;
            set
            {
                if (Owner == null) return;
                Owner._simInputsPtr[Index].ScaledOffset = value;
            }
        }

        /// <summary>True if a virtual override is driving this bone instead of tracking.</summary>
        public unsafe bool HasVirtualOverride
        {
            get => Owner != null && Owner._simInputsPtr[Index].HasVirtualOverride != 0;
            set
            {
                if (Owner == null) return;
                Owner._simInputsPtr[Index].HasVirtualOverride = value ? (byte)1 : (byte)0;
            }
        }

        /// <summary>When true, applies the inverse offset from the bone on incoming data.</summary>
        public unsafe bool UseInverseOffset
        {
            get => Owner != null && Owner._simInputsPtr[Index].UseInverseOffset != 0;
            set
            {
                if (Owner == null) return;
                Owner._simInputsPtr[Index].UseInverseOffset = value ? (byte)1 : (byte)0;
            }
        }

        /// <summary>
        /// Indicates whether this bone currently has tracker input.
        /// Invokes <see cref="OnHasTrackerDriverChanged"/> and syncs the store flag when changed.
        /// </summary>
        public unsafe BasisHasTracked HasTracked
        {
            get => hasTrackerDriver;
            set
            {
                if (hasTrackerDriver != value)
                {
                    hasTrackerDriver = value;
                    if (Owner != null)
                    {
                        Owner._simInputsPtr[Index].HasTracker = (value == BasisHasTracked.HasTracker) ? (byte)1 : (byte)0;
                    }
                    OnHasTrackerDriverChanged?.Invoke(value);
                }
            }
        }

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
                    OnHasRigChanged?.Invoke(value == BasisHasRigLayer.HasRigLayer);
                }
            }
        }

        // ===== Setters: single write path, in-place into the store via ref =====

        public unsafe void SetIncoming(Vector3 position, Quaternion rotation)
        {
            ref BasisBoneSimInput i = ref Owner._simInputsPtr[Index];
            i.IncomingPosition = position;
            i.IncomingRotation = rotation;
        }

        public unsafe void SetOutgoing(Vector3 position, Quaternion rotation)
        {
            ref BasisBoneSimState s = ref Owner._simStatesPtr[Index];
            s.OutgoingPosition = position;
            s.OutgoingRotation = rotation;
        }

        public unsafe void SetOutgoingWorld(Vector3 position, Quaternion rotation)
        {
            ref BasisBoneSimState s = ref Owner._simStatesPtr[Index];
            s.OutgoingWorldPosition = position;
            s.OutgoingWorldRotation = rotation;
        }

        public unsafe void SetOutgoingWorldPosition(Vector3 position)
        {
            Owner._simStatesPtr[Index].OutgoingWorldPosition = position;
        }

        public unsafe void SetLastRun(Vector3 position, Quaternion rotation)
        {
            ref BasisBoneSimState s = ref Owner._simStatesPtr[Index];
            s.LastRunPosition = position;
            s.LastRunRotation = rotation;
        }

        public unsafe void SetInverseOffset(Vector3 position, Quaternion rotation)
        {
            ref BasisBoneSimInput i = ref Owner._simInputsPtr[Index];
            i.InverseOffsetPosition = position;
            i.InverseOffsetRotation = rotation;
        }

        public unsafe void SetInverseOffset(in BasisCalibratedCoords value)
        {
            ref BasisBoneSimInput i = ref Owner._simInputsPtr[Index];
            i.InverseOffsetPosition = value.position;
            i.InverseOffsetRotation = value.rotation;
        }

        public void SetTposeLocal(Vector3 position, Quaternion rotation)
        {
            TposeLocal.position = position;
            TposeLocal.rotation = rotation;
        }

        public void SetTposeScaled(Vector3 position, Quaternion rotation)
        {
            TposeLocalScaled.position = position;
            TposeLocalScaled.rotation = rotation;
        }
    }
}
