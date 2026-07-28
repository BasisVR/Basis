using Basis.IK;
using Basis.Scripts.Avatar;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Runs the stock locomotion controller as data: a main-thread stepper reproduces the state
    /// machine (see BasisLocomotionGraph), and a Burst job blends baked clip samples straight into the
    /// pose skeleton stream while the rest of BasisLocalPlayer.Simulate runs. Active ONLY when the
    /// avatar wears the stock BasisLocomotion controller — reference identity is re-checked every
    /// frame, so custom animators always keep the engine Animator path untouched. Also owns the
    /// full-FBT animator freeze, which applies to the stock controller on either path.
    /// </summary>
    public sealed class BasisLocomotionPoseSystem
    {
        // ── FAST PATH SWITCH ──
        // Setting-backed (settings → Body Tracking → Job-Driven Locomotion, default ON) and read every
        // frame, so it can be A/B-toggled live: ON stops the engine Animator once the per-avatar bake is
        // ready and produces the base pose via BasisLocomotionPoseJob on a worker; OFF resumes the
        // Animator immediately (the bake is kept for instant re-enable).
        public static bool JobDrivenLocomotionPose => Basis.BasisUI.BasisSettingsDefaults.FBIKJobLocomotion.RawValue;

        // With full leg FBT and DisableAnimationsInFBT, every animator-visible bone is tracker-driven or
        // vetoed, so the base pose cannot matter — stop evaluating it entirely. Stock controller only:
        // a custom controller may drive non-humanoid transforms that a freeze would visibly kill.
        public static readonly bool FreezeAnimatorInFullFBT = true;

        // The freeze waits for the StopAll idle params to settle so it cannot latch a mid-crossfade pose.
        const float FreezeArmSeconds = 0.5f;

        static RuntimeAnimatorController sStockController;

        public static void NotifyStockControllerAssigned(RuntimeAnimatorController controller)
        {
            sStockController = controller;
        }

        public static bool IsStockController(Animator animator)
        {
            return sStockController != null && animator != null
                && ReferenceEquals(animator.runtimeAnimatorController, sStockController);
        }

        /// <summary>
        /// True while this system wants the Animator not evaluated this frame (fast path active or
        /// full-FBT freeze). The rig driver consults this before a legacy manual Evaluate.
        /// </summary>
        public bool EngineAnimatorSuppressed { get; private set; }

        BasisLocomotionPoseBaker _baker;
        BasisLocomotionPoseBake _bake;
        BasisLocoSimState _sim;
        bool _simDirty = true;
        bool _landingLatch;
        float _freezeArmTimer;
        bool _graphStopped;

        readonly BasisLocoContribution[] _contributionsManaged = new BasisLocoContribution[BasisLocomotionGraph.MaxContributions];
        int _contributionCount;
        NativeArray<BasisLocoContribution> _contributionsNative;
        NativeArray<float3> _restPositions;
        JobHandle _handle;
        bool _scheduled;

        public void NotifyLanding()
        {
            _landingLatch = true;
        }

        /// <summary>
        /// Called from BuildBuilder after the pose skeleton exists. Drops the previous avatar's bake;
        /// Schedule lazily starts a fresh one once the gates pass (which also makes turning the setting
        /// on mid-session work without an avatar swap).
        /// </summary>
        public void OnRigBuilt()
        {
            CompleteIfPending();
            DisposeBakeData();
            _graphStopped = false;
            EngineAnimatorSuppressed = false;
            _simDirty = true;
            _landingLatch = false;
            _freezeArmTimer = 0f;
        }

        /// <summary>
        /// Called at the top of BasisLocalPlayer.Simulate. Ticks the bake, manages the animator
        /// stop/resume latch, and — when the fast path is live — steps the state machine and schedules
        /// the pose job so it overlaps the rest of Simulate.
        /// </summary>
        public void Schedule(BasisLocalRigDriver rig, Animator animator, in BasisLocoParams frameParams, float deltaTime)
        {
            bool rigReady = rig != null && rig.IKDataReady && rig.IKJobCreated && rig.RigLayerActive && rig.PoseSkeleton.IsCreated;
            bool graphValid = rigReady && rig.PlayableGraph.IsValid();
            bool tposeLike = BasisLocalAvatarDriver.CurrentlyTposing || BasisLocalAvatarDriver.SavedruntimeAnimatorController != null;
            bool stock = IsStockController(animator);

            // Lazy bake kickoff: a Start failure leaves the failed baker in place, which blocks retries
            // until the next rig build.
            if (JobDrivenLocomotionPose && stock && rigReady && _bake == null && _baker == null)
            {
                _baker = new BasisLocomotionPoseBaker();
                _baker.Start(animator, sStockController, rig.PoseSkeleton.DebugNodes, rig.basisTransformMapping.Hips);
            }

            if (_baker != null && !_baker.Failed)
            {
                if (!_baker.Tick() && _baker.Done)
                {
                    _bake = _baker.TakeBake();
                    EnsureRuntimeArrays();
                    _baker.Dispose();
                    _baker = null;
                }
            }

            bool fbtConditions = FreezeAnimatorInFullFBT && stock && !tposeLike
                && BasisAvatarIKStageCalibration.HasLegFBIKTrackers
                && Basis.BasisUI.BasisSettingsDefaults.DisableAnimationsInFBT.RawValue;
            _freezeArmTimer = fbtConditions ? _freezeArmTimer + deltaTime : 0f;
            bool frozen = fbtConditions && _freezeArmTimer >= FreezeArmSeconds;

            bool fastActive = JobDrivenLocomotionPose && stock && !tposeLike && rigReady && graphValid
                && _bake != null && _bake.Ready;

            bool suppress = graphValid && (fastActive || (stock && frozen));
            EngineAnimatorSuppressed = suppress;
            if (graphValid && suppress != _graphStopped)
            {
                if (suppress)
                {
                    rig.PlayableGraph.Stop();
                }
                else
                {
                    rig.PlayableGraph.Play();
                }
                _graphStopped = suppress;
            }

            if (!fastActive)
            {
                _simDirty = true;
                return;
            }

            if (_simDirty)
            {
                _sim = BasisLocomotionGraph.DefaultSimState;
                _landingLatch = false;
                _simDirty = false;
                _contributionCount = 0;
            }

            if (!frozen || _contributionCount == 0)
            {
                BasisLocoParams stepParams = frameParams;
                stepParams.LandingTrigger = _landingLatch;
                _contributionCount = BasisLocomotionGraph.Step(
                    ref _sim, ref stepParams, deltaTime,
                    _bake.ClipLengthsManaged, _bake.ClipLoopingManaged, _contributionsManaged);
                _landingLatch = stepParams.LandingTrigger;
                for (int i = 0; i < _contributionCount; i++)
                {
                    _contributionsNative[i] = _contributionsManaged[i];
                }
            }

            if (_contributionCount == 0)
            {
                return;
            }

            rig.PoseSkeleton.CopyRestPositionsInto(_restPositions);
            var job = new BasisLocomotionPoseJob
            {
                Contributions = _contributionsNative,
                ContributionCount = _contributionCount,
                Rotations = _bake.Rotations,
                HipsPositions = _bake.HipsPositions,
                SnapshotScales = _bake.SnapshotScales,
                ClipRotationOffset = _bake.ClipRotationOffset,
                ClipHipsOffset = _bake.ClipHipsOffset,
                ClipSampleCount = _bake.ClipSampleCount,
                ClipLength = _bake.ClipLength,
                RestPositions = _restPositions,
                NodeCount = _bake.NodeCount,
                HipsNode = _bake.HipsNode,
                OutLocalPosition = rig.PoseSkeleton.Stream.LocalPosition,
                OutLocalRotation = rig.PoseSkeleton.Stream.LocalRotation,
                OutLocalScale = rig.PoseSkeleton.Stream.LocalScale,
            };
            _handle = job.Schedule();
            _scheduled = true;
            JobHandle.ScheduleBatchedJobs();
        }

        /// <summary>
        /// Called where the manual PlayableGraph.Evaluate used to run. True means the stream already
        /// holds this frame's base pose, so RunIKSolve must skip its transform gather.
        /// </summary>
        public bool TryComplete(BasisPoseSkeleton skeleton)
        {
            if (!_scheduled)
            {
                return false;
            }
            _handle.Complete();
            _scheduled = false;
            skeleton.SyncAnchor();
            skeleton.RefreshRootFromTransform();
            return true;
        }

        public void CompleteIfPending()
        {
            if (_scheduled)
            {
                _handle.Complete();
                _scheduled = false;
            }
        }

        void EnsureRuntimeArrays()
        {
            if (!_contributionsNative.IsCreated)
            {
                _contributionsNative = new NativeArray<BasisLocoContribution>(BasisLocomotionGraph.MaxContributions, Allocator.Persistent);
            }
            if (_restPositions.IsCreated && _restPositions.Length != _bake.NodeCount)
            {
                _restPositions.Dispose();
            }
            if (!_restPositions.IsCreated)
            {
                _restPositions = new NativeArray<float3>(_bake.NodeCount, Allocator.Persistent);
            }
        }

        void DisposeBakeData()
        {
            _baker?.Dispose();
            _baker = null;
            _bake?.Dispose();
            _bake = null;
            _contributionCount = 0;
        }

        public void Dispose()
        {
            CompleteIfPending();
            _baker?.Dispose();
            _baker = null;
            _bake?.Dispose();
            _bake = null;
            if (_contributionsNative.IsCreated)
            {
                _contributionsNative.Dispose();
            }
            if (_restPositions.IsCreated)
            {
                _restPositions.Dispose();
            }
            EngineAnimatorSuppressed = false;
            _graphStopped = false;
        }
    }
}
