using Unity.Scripting.LifecycleManagement;
using Basis.IK;
using Basis.Scripts.Avatar;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    [AutoStaticsCleanup]
    public sealed partial class BasisLocomotionPoseSystem
    {
        public static bool JobDrivenLocomotionPose => Basis.BasisUI.BasisSettingsDefaults.FBIKJobLocomotion.RawValue;
        public static bool MotionMatchingEnabled => Basis.BasisUI.BasisSettingsDefaults.FBIKMotionMatching.RawValue;
        public static readonly bool FreezeAnimatorInFullFBT = true;
        const float FreezeArmSeconds = 0.5f, MotionFrameTeleportSq = 1f;
        static RuntimeAnimatorController sStockController;
        public static void NotifyStockControllerAssigned(RuntimeAnimatorController controller)
        {
            sStockController = controller;
        }
        public static bool IsStockController(Animator animator)
        {
            return sStockController != null && animator != null && ReferenceEquals(animator.runtimeAnimatorController, sStockController);
        }
        BasisLocomotionPoseBaker baker;
        BasisLocomotionPoseBake poseBake;
        bool simDirty = true;
        bool landingLatch;
        float freezeArmTimer;
        NativeArray<BasisLocoContribution> contributionsNative;
        NativeArray<BasisLocoSimState> graphSimNative;
        NativeArray<int> contributionCountNative;
        NativeArray<byte> landingLatchNative;
        NativeArray<float2> childPositionsNative;
        NativeArray<float> childTimeScalesNative;
        JobHandle poseJobHandle;
        bool jobScheduled;
        BasisMotionMatchingDatabase motionDatabase;
        bool motionDatabaseFailed;
        bool motionActive;
        bool motionLegsValid;
        Transform motionFrame;
        BasisMotionMatchingLegNodes motionLeftLeg, motionRightLeg;
        bool motionFootTargetsValid, motionHasLastFrame;
        Vector3 motionLastFramePosition;
        NativeArray<BasisMotionMatchingState> motionStateNative;
        NativeArray<float3> motionScratchPosition, motionPreviousAngularVelocity, motionOffsetAngularVelocity;
        NativeArray<quaternion> motionScratchRotation, motionPreviousRotation, motionOffsetRotation, motionFinalRotation;
        public void NotifyLanding()
        {
            landingLatch = true;
        }
        public void OnRigBuilt()
        {
            CompleteIfPending();
            DisposeBakeData();
            simDirty = true;
            landingLatch = false;
            freezeArmTimer = 0f;
        }
        public void Schedule(BasisLocalRigDriver rig, Animator animator, in BasisLocoParams frameParams, float deltaTime)
        {
            bool frozen;
            bool fastActive;
            using (BasisLocoPoseMarkers.Gate.Auto())
            {
                bool rigReady = rig != null && rig.IKDataReady && rig.IKJobCreated && rig.RigLayerActive && rig.PoseSkeleton.IsCreated;
                bool graphValid = rigReady && rig.PlayableGraph.IsValid();
                bool stock = IsStockController(animator);

                Step1TickBake(rig, animator, stock, rigReady);
                Step2ResolveGates(stock, rigReady, graphValid, deltaTime, out frozen, out fastActive);
            }

            if (!fastActive)
            {
                simDirty = true;
                return;
            }

            bool useMotionMatching = MotionMatchingEnabled && motionDatabase != null && motionDatabase.Ready;
            if (useMotionMatching != motionActive)
            {
                motionActive = useMotionMatching;
                simDirty = true;
            }
            if (useMotionMatching)
            {
                DispatchMotionMatching(rig, in frameParams, frozen ? 0f : deltaTime);
                return;
            }

            JobHandle graphHandle = Step4StepLocomotionGraph(in frameParams, deltaTime, frozen);
            Step5DispatchPoseJob(rig, graphHandle);
        }
        void Step1TickBake(BasisLocalRigDriver rig, Animator animator, bool stock, bool rigReady)
        {
            if (JobDrivenLocomotionPose && stock && rigReady && poseBake == null && baker == null)
            {
                baker = new BasisLocomotionPoseBaker();
                baker.Start(animator, sStockController, rig.PoseSkeleton.Nodes, rig.basisTransformMapping.Hips);
            }

            if (baker != null && !baker.Failed)
            {
                if (!baker.Tick() && baker.Done)
                {
                    poseBake = baker.TakeBake();
                    EnsureRuntimeArrays();
                    baker.Dispose();
                    baker = null;
                }
            }

            if (MotionMatchingEnabled && motionDatabase == null && !motionDatabaseFailed && poseBake != null && poseBake.Ready)
            {
                StartMotionDatabase(rig);
            }
            motionDatabase?.Tick();
        }
        void StartMotionDatabase(BasisLocalRigDriver rig)
        {
            BasisPoseSkeleton skeleton = rig.PoseSkeleton;
            int leftFoot = System.Array.IndexOf(skeleton.Nodes, rig.basisTransformMapping.leftFoot);
            int rightFoot = System.Array.IndexOf(skeleton.Nodes, rig.basisTransformMapping.rightFoot);
            if (!DescendsFromHips(skeleton, leftFoot) || !DescendsFromHips(skeleton, rightFoot))
            {
                // Validated here so the feature job can trust its foot indices; the graph path keeps running.
                motionDatabaseFailed = true;
                BasisDebug.LogWarning("Motion matching unavailable: this avatar's feet are not under its hips in the pose skeleton.", BasisDebug.LogTag.Avatar);
                return;
            }
            int hipsParent = skeleton.Stream.Parent[poseBake.HipsNode];
            motionFrame = hipsParent >= 0 ? skeleton.Nodes[hipsParent] : skeleton.Anchor;
            float3 up = motionFrame != null ? (float3)motionFrame.InverseTransformDirection(Vector3.up) : new float3(0f, 1f, 0f);
            motionDatabase = BasisMotionMatchingDatabase.ScheduleBuild(poseBake, skeleton.Stream.Parent, skeleton.Stream.RestLocalPosition, leftFoot, rightFoot, up, motionFrame != null ? math.length(((float4x4)motionFrame.worldToLocalMatrix).c0.xyz) : 1f, childPositionsNative, childTimeScalesNative);

            var mapping = rig.basisTransformMapping;
            motionLeftLeg = new BasisMotionMatchingLegNodes { Upper = System.Array.IndexOf(skeleton.Nodes, mapping.LeftUpperLeg), Lower = System.Array.IndexOf(skeleton.Nodes, mapping.LeftLowerLeg), Foot = leftFoot };
            motionRightLeg = new BasisMotionMatchingLegNodes { Upper = System.Array.IndexOf(skeleton.Nodes, mapping.RightUpperLeg), Lower = System.Array.IndexOf(skeleton.Nodes, mapping.RightLowerLeg), Foot = rightFoot };
            motionLegsValid = motionFrame != null && LegChainValid(skeleton, in motionLeftLeg) && LegChainValid(skeleton, in motionRightLeg);
            motionHasLastFrame = false;
        }
        bool LegChainValid(BasisPoseSkeleton skeleton, in BasisMotionMatchingLegNodes leg)
        {
            return DescendsFrom(skeleton, leg.Upper, poseBake.HipsNode) && DescendsFrom(skeleton, leg.Lower, leg.Upper) && DescendsFrom(skeleton, leg.Foot, leg.Lower);
        }
        static bool DescendsFrom(BasisPoseSkeleton skeleton, int node, int ancestor)
        {
            if (node < 0 || ancestor < 0)
            {
                return false;
            }
            NativeArray<int> parents = skeleton.Stream.Parent;
            while (node >= 0)
            {
                if (node == ancestor)
                {
                    return true;
                }
                node = parents[node];
            }
            return false;
        }
        bool DescendsFromHips(BasisPoseSkeleton skeleton, int node) => DescendsFrom(skeleton, node, poseBake.HipsNode);
        void DispatchMotionMatching(BasisLocalRigDriver rig, in BasisLocoParams frameParams, float deltaTime)
        {
            using var dispatch = BasisLocoPoseMarkers.Dispatch.Auto();
            EnsureMotionArrays();
            if (simDirty)
            {
                motionStateNative[0] = BasisMotionMatchingState.Default;
                landingLatchNative[0] = 0;
                landingLatch = false;
                simDirty = false;
            }
            landingLatchNative[0] = (byte)(landingLatch ? 1 : 0);
            landingLatch = false;
            // Trackers already place the feet, and a lock would only fight them.
            bool hasFrame = motionFrame != null;
            bool footLock = motionLegsValid && hasFrame && !BasisAvatarIKStageCalibration.HasLegFBIKTrackers;
            float4x4 skeletonToWorld = float4x4.identity, worldToSkeleton = float4x4.identity;
            float unitsPerMeter = 1f;
            quaternion facingToSkeleton = quaternion.identity;
            if (hasFrame)
            {
                skeletonToWorld = motionFrame.localToWorldMatrix;
                worldToSkeleton = motionFrame.worldToLocalMatrix;
                unitsPerMeter = math.length(worldToSkeleton.c0.xyz);
                // This runs before the body moves this frame. Lead the frame by the last move so a world-space foot lands where the body will be.
                motionFrame.GetPositionAndRotation(out Vector3 framePosition, out Quaternion frameRotation);
                Vector3 lead = motionHasLastFrame ? framePosition - motionLastFramePosition : Vector3.zero;
                motionLastFramePosition = framePosition;
                motionHasLastFrame = true;
                if (deltaTime > 0f && lead.sqrMagnitude < MotionFrameTeleportSq)
                {
                    float3 leadVector = lead;
                    skeletonToWorld = math.mul(float4x4.Translate(leadVector), skeletonToWorld);
                    worldToSkeleton = math.inverse(skeletonToWorld);
                }
                facingToSkeleton = math.mul(math.inverse((quaternion)frameRotation),(quaternion)Basis.Scripts.BasisCharacterController.BasisLocalCharacterDriver.GetMovementFacing());
            }
            motionFootTargetsValid = footLock;
            poseJobHandle = new BasisMotionMatchingJob
            {
                Params = frameParams,
                DeltaTime = deltaTime,
                SkeletonUnitsPerMeter = unitsPerMeter,
                FootLockEnabled = footLock,
                InertializationEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKInertialization.RawValue,
                FacingToSkeleton = facingToSkeleton,
                Tuning = BasisMotionMatchingTuning.Default,
                LeftLeg = motionLeftLeg,
                RightLeg = motionRightLeg,
                SkeletonToWorld = skeletonToWorld,
                WorldToSkeleton = worldToSkeleton,
                Parents = rig.PoseSkeleton.Stream.Parent,
                ContactFlags = motionDatabase.ContactFlags,
                GridStanceSpeed = motionDatabase.GridStanceSpeed,
                ClipPhaseOffset = motionDatabase.ClipPhaseOffset,
                Sampler = poseBake.CreateSampler(rig.PoseSkeleton.Stream.RestLocalPosition),
                ClipLooping = poseBake.ClipLooping,
                SnapshotScales = poseBake.SnapshotScales,
                ClipGroundSpeed = motionDatabase.ClipGroundSpeed,
                ChildTimeScales = childTimeScalesNative,
                FeatureScale = motionDatabase.FeatureScale,
                Features = motionDatabase.Features,
                State = motionStateNative,
                LandingLatch = landingLatchNative,
                Contributions = contributionsNative,
                ScratchPosition = motionScratchPosition,
                ScratchRotation = motionScratchRotation,
                PreviousRotation = motionPreviousRotation,
                OffsetRotation = motionOffsetRotation,
                FinalRotation = motionFinalRotation,
                PreviousAngularVelocity = motionPreviousAngularVelocity,
                OffsetAngularVelocity = motionOffsetAngularVelocity,
                OutLocalPosition = rig.PoseSkeleton.Stream.LocalPosition,
                OutLocalRotation = rig.PoseSkeleton.Stream.LocalRotation,
                OutLocalScale = rig.PoseSkeleton.Stream.LocalScale,
            }.Schedule();
            jobScheduled = true;
            JobHandle.ScheduleBatchedJobs();
        }
        public bool TryGetFootTarget(bool left, out Vector3 world, out float weight)
        {
            world = Vector3.zero;
            weight = 0f;
            if (!motionFootTargetsValid || !motionActive || jobScheduled || !motionStateNative.IsCreated)
            {
                return false;
            }
            BasisMotionMatchingState state = motionStateNative[0];
            BasisMotionMatchingFootLock foot = left ? state.LeftFoot : state.RightFoot;
            world = foot.TargetWorld;
            weight = foot.TargetWeight;
            return weight > 0f;
        }
        void EnsureMotionArrays()
        {
            if (motionStateNative.IsCreated)
            {
                return;
            }
            int nodes = poseBake.NodeCount;
            motionStateNative = new NativeArray<BasisMotionMatchingState>(1, Allocator.Persistent);
            motionScratchPosition = new NativeArray<float3>(nodes, Allocator.Persistent);
            motionPreviousAngularVelocity = new NativeArray<float3>(nodes, Allocator.Persistent);
            motionOffsetAngularVelocity = new NativeArray<float3>(nodes, Allocator.Persistent);
            motionScratchRotation = new NativeArray<quaternion>(nodes, Allocator.Persistent);
            motionPreviousRotation = new NativeArray<quaternion>(nodes, Allocator.Persistent);
            motionOffsetRotation = new NativeArray<quaternion>(nodes, Allocator.Persistent);
            motionFinalRotation = new NativeArray<quaternion>(nodes, Allocator.Persistent);
            simDirty = true;
        }
        void DisposeMotionData()
        {
            motionDatabase?.Dispose();
            motionDatabase = null;
            motionDatabaseFailed = false;
            motionActive = false;
            motionLegsValid = false;
            motionFootTargetsValid = false;
            motionHasLastFrame = false;
            motionFrame = null;
            if (motionStateNative.IsCreated) motionStateNative.Dispose();
            if (motionScratchPosition.IsCreated) motionScratchPosition.Dispose();
            if (motionPreviousAngularVelocity.IsCreated) motionPreviousAngularVelocity.Dispose();
            if (motionOffsetAngularVelocity.IsCreated) motionOffsetAngularVelocity.Dispose();
            if (motionScratchRotation.IsCreated) motionScratchRotation.Dispose();
            if (motionPreviousRotation.IsCreated) motionPreviousRotation.Dispose();
            if (motionOffsetRotation.IsCreated) motionOffsetRotation.Dispose();
            if (motionFinalRotation.IsCreated) motionFinalRotation.Dispose();
        }
        void Step2ResolveGates(bool stock, bool rigReady, bool graphValid, float deltaTime, out bool frozen, out bool fastActive)
        {
            bool tposeLike = BasisLocalAvatarDriver.CurrentlyTposing || BasisLocalAvatarDriver.SavedruntimeAnimatorController != null;
            bool fbtConditions = FreezeAnimatorInFullFBT && stock && !tposeLike && BasisAvatarIKStageCalibration.HasLegFBIKTrackers && Basis.BasisUI.BasisSettingsDefaults.DisableAnimationsInFBT.RawValue;
            freezeArmTimer = fbtConditions ? freezeArmTimer + deltaTime : 0f;
            frozen = fbtConditions && freezeArmTimer >= FreezeArmSeconds;

            fastActive = JobDrivenLocomotionPose && stock && !tposeLike && rigReady && graphValid && poseBake != null && poseBake.Ready;
        }
        JobHandle Step4StepLocomotionGraph(in BasisLocoParams frameParams, float deltaTime, bool frozen)
        {
            if (simDirty)
            {
                graphSimNative[0] = BasisLocomotionGraph.DefaultSimState;
                contributionCountNative[0] = 0;
                landingLatchNative[0] = 0;
                landingLatch = false;
                simDirty = false;
            }

            if (frozen && contributionCountNative[0] != 0)
            {
                return default;
            }

            using var _ = BasisLocoPoseMarkers.GraphStep.Auto();
            landingLatchNative[0] = (byte)(landingLatch ? 1 : 0);
            landingLatch = false;
            return new BasisLocomotionGraphJob
            {
                Params = frameParams,
                DeltaTime = deltaTime,
                ClipLengths = poseBake.ClipLength,
                ClipLooping = poseBake.ClipLooping,
                ChildPositions = childPositionsNative,
                ChildTimeScales = childTimeScalesNative,
                Sim = graphSimNative,
                Contributions = contributionsNative,
                ContributionCount = contributionCountNative,
                LandingLatch = landingLatchNative,
            }.Schedule();
        }
        void Step5DispatchPoseJob(BasisLocalRigDriver rig, JobHandle dependency)
        {
            using var dispatch = BasisLocoPoseMarkers.Dispatch.Auto();
            var job = new BasisLocomotionPoseJob
            {
                Contributions = contributionsNative,
                ContributionCount = contributionCountNative,
                SnapshotScales = poseBake.SnapshotScales,
                Sampler = poseBake.CreateSampler(rig.PoseSkeleton.Stream.RestLocalPosition),
                OutLocalPosition = rig.PoseSkeleton.Stream.LocalPosition,
                OutLocalRotation = rig.PoseSkeleton.Stream.LocalRotation,
                OutLocalScale = rig.PoseSkeleton.Stream.LocalScale,
            };
            poseJobHandle = job.Schedule(dependency);
            jobScheduled = true;
            JobHandle.ScheduleBatchedJobs();
        }
        public bool TryComplete(BasisPoseSkeleton skeleton)
        {
            if (!jobScheduled)
            {
                return false;
            }
            poseJobHandle.Complete();
            jobScheduled = false;
            landingLatch |= landingLatchNative[0] != 0;
            skeleton.RefreshRootFromTransform();
            return true;
        }
        public void CompleteIfPending()
        {
            if (jobScheduled)
            {
                poseJobHandle.Complete();
                jobScheduled = false;
                if (landingLatchNative.IsCreated) landingLatch |= landingLatchNative[0] != 0;
            }
        }
        void EnsureRuntimeArrays()
        {
            if (!contributionsNative.IsCreated)
            {
                contributionsNative = new NativeArray<BasisLocoContribution>(BasisLocomotionGraph.MaxContributions, Allocator.Persistent);
                graphSimNative = new NativeArray<BasisLocoSimState>(1, Allocator.Persistent);
                contributionCountNative = new NativeArray<int>(1, Allocator.Persistent);
                landingLatchNative = new NativeArray<byte>(1, Allocator.Persistent);
                childPositionsNative = new NativeArray<float2>(BasisLocomotionGraph.ProneClipStart + BasisLocomotionGraph.ProneChildCount, Allocator.Persistent);
                childTimeScalesNative = new NativeArray<float>(childPositionsNative.Length, Allocator.Persistent);
                CopyTree(BasisLocomotionGraph.WalkingClipStart, BasisLocomotionGraph.WalkingChildPositions, BasisLocomotionGraph.WalkingChildTimeScales);
                CopyTree(BasisLocomotionGraph.CrouchingClipStart, BasisLocomotionGraph.CrouchingChildPositions, BasisLocomotionGraph.CrouchingChildTimeScales);
                CopyTree(BasisLocomotionGraph.ProneClipStart, BasisLocomotionGraph.ProneChildPositions, BasisLocomotionGraph.ProneChildTimeScales);
            }
        }
        void CopyTree(int start, float2[] positions, float[] timeScales)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                childPositionsNative[start + i] = positions[i];
                childTimeScalesNative[start + i] = timeScales[i];
            }
        }
        void DisposeBakeData()
        {
            DisposeMotionData();
            baker?.Dispose();
            baker = null;
            poseBake?.Dispose();
            poseBake = null;
            if (contributionCountNative.IsCreated) contributionCountNative[0] = 0;
        }
        public void Dispose()
        {
            CompleteIfPending();
            DisposeMotionData();
            baker?.Dispose();
            baker = null;
            poseBake?.Dispose();
            poseBake = null;
            if (contributionsNative.IsCreated)
            {
                contributionsNative.Dispose();
            }
            if (graphSimNative.IsCreated) graphSimNative.Dispose();
            if (contributionCountNative.IsCreated) contributionCountNative.Dispose();
            if (landingLatchNative.IsCreated) landingLatchNative.Dispose();
            if (childPositionsNative.IsCreated) childPositionsNative.Dispose();
            if (childTimeScalesNative.IsCreated) childTimeScalesNative.Dispose();
        }
    }
}
