using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
    [Serializable]
    public class BasisLocalBoneControl
    {
        public const float AngleBeforeSpeedup = 25f;

        public const float trackersmooth = 25;

        public const float QuaternionLerp = 14;

        public const float QuaternionLerpFastMovement = 56;

        public const float PositionLerpAmount = 40;

        public static bool HasEvents { get; internal set; }

        [NonSerialized] internal BasisLocalBoneDriver Owner;

        [NonSerialized] internal int Index;

        [SerializeField] public BasisBoneTrackedRole Role;

        [NonSerialized] public int TargetIndex = -1;

        public bool HasTarget { get { return TargetIndex >= 0; } }

        public float3 Offset;

        [SerializeField] public Color Color = Color.blue;

        public Action<BasisHasTracked> OnHasTrackerDriverChanged;

        [SerializeField] private BasisHasTracked hasTrackerDriver = BasisHasTracked.HasNoTracker;

        public List<string> DevicesWithRoles = new List<string>();

        public Action<bool> OnHasRigChanged;

        [SerializeField] private BasisHasRigLayer hasRigLayer = BasisHasRigLayer.HasNoRigLayer;

        public float RigLayerWeight = 1f;

        [SerializeField] public BasisCalibratedCoords TposeLocal = new BasisCalibratedCoords();

        [SerializeField] public BasisCalibratedCoords TposeLocalScaled = new BasisCalibratedCoords();

        // ===== Native-backed pose: lives in Owner's store at Index, reached via raw pointer =====

        // Index is re-stamped by WireControlsAndInitStore on every (re)allocation, so it tracks the
        // store while the control is in Owner.Controls. A control dropped from that list keeps both
        // a live Owner and its old Index, and every accessor below indexes the store by raw pointer
        // — no bounds check in any build. The capacity test is one compare next to two null tests
        // already on this path.
        internal unsafe bool HasStore => Owner != null && Owner.simInputPtr != null && Owner.simStatePtr != null
            && (uint)Index < (uint)Owner.nativeCapacity;

        public unsafe BasisCalibratedCoords IncomingData
        {
            get
            {
                if (HasStore == false) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimInput i = ref Owner.simInputPtr[Index];
                return new BasisCalibratedCoords(i.IncomingPosition, i.IncomingRotation);
            }
        }

        public unsafe BasisCalibratedCoords OutGoingData
        {
            get
            {
                if (HasStore == false) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
                return new BasisCalibratedCoords(s.OutgoingPosition, s.OutgoingRotation);
            }
        }

        public unsafe BasisCalibratedCoords OutgoingWorldData
        {
            get
            {
                if (HasStore == false) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
                return new BasisCalibratedCoords(s.OutgoingWorldPosition, s.OutgoingWorldRotation);
            }
        }

        public unsafe BasisCalibratedCoords IKWorldData
        {
            get
            {
                if (HasStore == false) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
                return new BasisCalibratedCoords(s.IKWorldPosition, s.IKWorldRotation);
            }
        }

        public unsafe bool HasIKWorldData
        {
            get
            {
                if (HasStore == false) return false;
                ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
                quaternion r = s.IKWorldRotation;
                return r.value.x != 0f || r.value.y != 0f || r.value.z != 0f || r.value.w != 0f;
            }
        }

        public unsafe void SetIKWorldData(Vector3 position, Quaternion rotation)
        {
            if (HasStore == false) return;
            ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
            s.IKWorldPosition = position;
            s.IKWorldRotation = rotation;
        }

        public unsafe BasisCalibratedCoords LastRunData
        {
            get
            {
                if (HasStore == false) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
                return new BasisCalibratedCoords(s.LastRunPosition, s.LastRunRotation);
            }
        }

        public unsafe BasisCalibratedCoords InverseOffsetFromBone
        {
            get
            {
                if (HasStore == false) return BasisCalibratedCoords.Identity;
                ref BasisBoneSimInput i = ref Owner.simInputPtr[Index];
                return new BasisCalibratedCoords(i.InverseOffsetPosition, i.InverseOffsetRotation);
            }
        }

        public unsafe float3 ScaledOffset
        {
            get => HasStore == false ? float3.zero : Owner.simInputPtr[Index].ScaledOffset;
            set
            {
                if (HasStore == false) return;
                Owner.simInputPtr[Index].ScaledOffset = value;
            }
        }

        public unsafe bool HasVirtualOverride
        {
            get => HasStore && Owner.simInputPtr[Index].HasVirtualOverride != 0;
            set
            {
                if (HasStore == false) return;
                Owner.simInputPtr[Index].HasVirtualOverride = value ? (byte)1 : (byte)0;
            }
        }

        public unsafe bool UseInverseOffset
        {
            get => HasStore && Owner.simInputPtr[Index].UseInverseOffset != 0;
            set
            {
                if (HasStore == false) return;
                Owner.simInputPtr[Index].UseInverseOffset = value ? (byte)1 : (byte)0;
            }
        }

        public unsafe BasisHasTracked HasTracked
        {
            get => hasTrackerDriver;
            set
            {
                if (hasTrackerDriver != value)
                {
                    hasTrackerDriver = value;
                    if (HasStore)
                    {
                        Owner.simInputPtr[Index].HasTracker = (value == BasisHasTracked.HasTracker) ? (byte)1 : (byte)0;
                    }
                    OnHasTrackerDriverChanged?.Invoke(value);
                }
            }
        }

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
            if (HasStore == false) return;
            ref BasisBoneSimInput i = ref Owner.simInputPtr[Index];
            i.IncomingPosition = position;
            i.IncomingRotation = rotation;
        }

        public unsafe void SetOutgoing(Vector3 position, Quaternion rotation)
        {
            if (HasStore == false) return;
            ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
            s.OutgoingPosition = position;
            s.OutgoingRotation = rotation;
        }

        public unsafe void SetOutgoingWorld(Vector3 position, Quaternion rotation)
        {
            if (HasStore == false) return;
            ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
            s.OutgoingWorldPosition = position;
            s.OutgoingWorldRotation = rotation;
        }

        public unsafe void SetOutgoingWorldPosition(Vector3 position)
        {
            if (HasStore == false) return;
            Owner.simStatePtr[Index].OutgoingWorldPosition = position;
        }

        public unsafe void SetLastRun(Vector3 position, Quaternion rotation)
        {
            if (HasStore == false) return;
            ref BasisBoneSimState s = ref Owner.simStatePtr[Index];
            s.LastRunPosition = position;
            s.LastRunRotation = rotation;
        }

        public unsafe void SetInverseOffset(Vector3 position, Quaternion rotation)
        {
            if (HasStore == false) return;
            ref BasisBoneSimInput i = ref Owner.simInputPtr[Index];
            i.InverseOffsetPosition = position;
            i.InverseOffsetRotation = rotation;
        }

        public unsafe void SetInverseOffset(in BasisCalibratedCoords value)
        {
            if (HasStore == false) return;
            ref BasisBoneSimInput i = ref Owner.simInputPtr[Index];
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
