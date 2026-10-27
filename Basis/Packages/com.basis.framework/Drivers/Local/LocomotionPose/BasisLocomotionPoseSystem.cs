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
        public static readonly bool FreezeAnimatorInFullFBT = true;
        const float FreezeArmSeconds = 0.5f;
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
                Rotations = poseBake.Rotations,
                HipsPositions = poseBake.HipsPositions,
                SnapshotScales = poseBake.SnapshotScales,
                ClipRotationOffset = poseBake.ClipRotationOffset,
                ClipHipsOffset = poseBake.ClipHipsOffset,
                ClipSampleCount = poseBake.ClipSampleCount,
                ClipLength = poseBake.ClipLength,
                RestPositions = rig.PoseSkeleton.Stream.RestLocalPosition,
                NodeCount = poseBake.NodeCount,
                HipsNode = poseBake.HipsNode,
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
            baker?.Dispose();
            baker = null;
            poseBake?.Dispose();
            poseBake = null;
            if (contributionCountNative.IsCreated) contributionCountNative[0] = 0;
        }
        public void Dispose()
        {
            CompleteIfPending();
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
