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
        BasisLocoSimState graphSim;
        bool simDirty = true;
        bool landingLatch;
        float freezeArmTimer;
        readonly BasisLocoContribution[] contributionsManaged = new BasisLocoContribution[BasisLocomotionGraph.MaxContributions];
        int contributionCount;
        NativeArray<BasisLocoContribution> contributionsNative;
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

            Step4StepLocomotionGraph(in frameParams, deltaTime, frozen);
            if (contributionCount == 0)
            {
                return;
            }

            Step5DispatchPoseJob(rig);
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
        void Step4StepLocomotionGraph(in BasisLocoParams frameParams, float deltaTime, bool frozen)
        {
            if (simDirty)
            {
                graphSim = BasisLocomotionGraph.DefaultSimState;
                landingLatch = false;
                simDirty = false;
                contributionCount = 0;
            }

            if (frozen && contributionCount != 0)
            {
                return;
            }

            using var _ = BasisLocoPoseMarkers.GraphStep.Auto();
            BasisLocoParams stepParams = frameParams;
            stepParams.LandingTrigger = landingLatch;
            contributionCount = BasisLocomotionGraph.Step( ref graphSim, ref stepParams, deltaTime, poseBake.ClipLengthsManaged, poseBake.ClipLoopingManaged, contributionsManaged);
            landingLatch = stepParams.LandingTrigger;
            for (int i = 0; i < contributionCount; i++)
            {
                contributionsNative[i] = contributionsManaged[i];
            }
        }
        void Step5DispatchPoseJob(BasisLocalRigDriver rig)
        {
            using var dispatch = BasisLocoPoseMarkers.Dispatch.Auto();
            var job = new BasisLocomotionPoseJob
            {
                Contributions = contributionsNative,
                ContributionCount = contributionCount,
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
            poseJobHandle = job.Schedule();
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
            skeleton.RefreshRootFromTransform();
            return true;
        }
        public void CompleteIfPending()
        {
            if (jobScheduled)
            {
                poseJobHandle.Complete();
                jobScheduled = false;
            }
        }
        void EnsureRuntimeArrays()
        {
            if (!contributionsNative.IsCreated)
            {
                contributionsNative = new NativeArray<BasisLocoContribution>(BasisLocomotionGraph.MaxContributions, Allocator.Persistent);
            }
        }
        void DisposeBakeData()
        {
            baker?.Dispose();
            baker = null;
            poseBake?.Dispose();
            poseBake = null;
            contributionCount = 0;
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
        }
    }
}
