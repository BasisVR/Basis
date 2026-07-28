using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Per-bone inputs consumed by <see cref="BasisBoneSimChainJob"/>.
    /// Most fields change rarely (calibration); incoming pose/has-tracker change per frame.
    /// </summary>
    public struct BasisBoneSimInput
    {
        public float3 IncomingPosition;
        public quaternion IncomingRotation;

        public float3 InverseOffsetPosition;
        public quaternion InverseOffsetRotation;

        public float3 ScaledOffset;

        public int TargetIndex;

        public byte HasTracker;
        public byte UseInverseOffset;
        public byte HasVirtualOverride;
        public byte HasTarget;
    }

    /// <summary>
    /// Per-bone simulation state. Persists across frames inside the driver
    /// (used as the "previous" pose for lerp continuity).
    /// </summary>
    public struct BasisBoneSimState
    {
        public float3 OutgoingPosition;
        public quaternion OutgoingRotation;

        public float3 LastRunPosition;
        public quaternion LastRunRotation;

        public float3 OutgoingWorldPosition;
        public quaternion OutgoingWorldRotation;

        public float3 IKWorldPosition;
        public quaternion IKWorldRotation;
    }

    /// <summary>
    /// Burst-compiled job that processes one ordered chain of bones.
    /// Each bone's compute mirrors <c>BasisLocalBoneControl.ComputeMovementLocal</c>.
    /// The chain is processed serially within the job; multiple chain jobs may run
    /// concurrently as long as they write to disjoint indices and depend on any chain
    /// whose outputs they read (target lookups across chains).
    /// </summary>
    [BurstCompile]
    public struct BasisBoneSimChainJob : IJob
    {
        [ReadOnly] public NativeArray<int> ChainIndices;

        [ReadOnly] public NativeArray<BasisBoneSimInput> Inputs;

        [NativeDisableContainerSafetyRestriction]
        public NativeArray<BasisBoneSimState> States;

        public float4x4 ParentMatrix;
        public quaternion ParentRotation;
        public float DeltaTime;
        public byte InstantSnap;

        public void Execute()
        {
            int len = ChainIndices.Length;
            // Loop-invariant: the lerp constants are const and DeltaTime is per-job. trackersmooth (25)
            // saturates to 1 — math.lerp/slerp don't clamp like the original Vector3.Lerp/Slerp did.
            float ts = InstantSnap != 0 ? 1f : math.saturate(BasisLocalBoneControl.trackersmooth);
            // Gated by BasisBoneChainLagTests. clamp(rate*dt) made the time constant a function of the
            // framerate and clamped to 1 (no smoothing at all) at and below 40 fps.
            float posLerpFactor = InstantSnap != 0
                ? 1f
                : BasisSmoothingProfiles.FramerateIndependentAlpha(BasisLocalBoneControl.PositionLerpAmount, DeltaTime);
            for (int k = 0; k < len; k++)
            {
                int i = ChainIndices[k];
                BasisBoneSimInput input = Inputs[i];
                BasisBoneSimState state = States[i];

                if (input.HasTracker != 0)
                {
                    if (input.UseInverseOffset != 0)
                    {
                        float3 destPos = input.IncomingPosition
                            + math.mul(input.IncomingRotation, input.InverseOffsetPosition);
                        quaternion destRot = math.mul(input.IncomingRotation, input.InverseOffsetRotation);

                        state.OutgoingPosition = math.lerp(state.LastRunPosition, destPos, ts);
                        state.OutgoingRotation = math.slerp(state.LastRunRotation, destRot, ts);
                    }
                    else
                    {
                        state.OutgoingPosition = input.IncomingPosition;
                        state.OutgoingRotation = input.IncomingRotation;
                    }

                    ApplyWorldAndLast(ref state);
                    States[i] = state;
                }
                else if (input.HasVirtualOverride == 0 && input.HasTarget != 0)
                {
                    BasisBoneSimState targetState = States[input.TargetIndex];

                    state.OutgoingRotation = InstantSnap != 0
                        ? targetState.OutgoingRotation
                        : ApplyLerpToQuaternion(
                            state.LastRunRotation,
                            targetState.OutgoingRotation);

                    float3 customDirection = math.mul(targetState.OutgoingRotation, input.ScaledOffset);
                    float3 targetPosition = targetState.OutgoingPosition + customDirection;

                    state.OutgoingPosition = math.lerp(state.LastRunPosition, targetPosition, posLerpFactor);

                    ApplyWorldAndLast(ref state);
                    States[i] = state;
                }
                // else: no tracker, no target — leave state untouched (matches original behavior).
            }
        }

        private quaternion ApplyLerpToQuaternion(quaternion current, quaternion future)
        {
            float dot = math.dot(current.value, future.value);

            if (dot > 0.999999f)
                return future;

            float angle = math.acos(math.clamp(dot, -1f, 1f));
            if (angle < math.EPSILON)
                return future;

            float timing = math.min(angle / BasisLocalBoneControl.AngleBeforeSpeedup, 1f);
            float lerpAmount = BasisLocalBoneControl.QuaternionLerp + (BasisLocalBoneControl.QuaternionLerpFastMovement - BasisLocalBoneControl.QuaternionLerp) * timing;
            float lerpFactor = BasisSmoothingProfiles.FramerateIndependentAlpha(lerpAmount, DeltaTime);

            return math.slerp(current, future, lerpFactor);
        }

        private void ApplyWorldAndLast(ref BasisBoneSimState state)
        {
            state.LastRunPosition = state.OutgoingPosition;
            state.LastRunRotation = state.OutgoingRotation;

            float4 p = math.mul(ParentMatrix, new float4(state.OutgoingPosition, 1f));
            state.OutgoingWorldPosition = p.xyz;

            state.OutgoingWorldRotation = math.mul(ParentRotation, state.OutgoingRotation);
        }
    }

    /// <summary>
    /// Uniform final pass: recomputes every bone's world pose from its outgoing local pose and the
    /// parent matrix. Runs after the chain jobs so all bones — including any the chains leave
    /// untouched — have valid world data, replacing the former main-thread SimulateWorldDestinations.
    /// </summary>
    [BurstCompile]
    public struct BasisBoneWorldDestinationJob : IJobParallelFor
    {
        public NativeArray<BasisBoneSimState> States;
        public float4x4 ParentMatrix;
        public quaternion ParentRotation;

        public void Execute(int i)
        {
            BasisBoneSimState state = States[i];
            float4 p = math.mul(ParentMatrix, new float4(state.OutgoingPosition, 1f));
            state.OutgoingWorldPosition = p.xyz;
            state.OutgoingWorldRotation = math.mul(ParentRotation, state.OutgoingRotation);
            States[i] = state;
        }
    }
}
