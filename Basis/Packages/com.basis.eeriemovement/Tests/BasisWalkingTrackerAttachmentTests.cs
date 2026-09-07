using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisWalkingTrackerAttachmentTests
    {
        const float Dt = 1f / 90f;
        const float WalkSpeed = 1.5f;          // m/s, brisk walk on the stick
        const float TurnDegPerSec = 45f;       // smooth turn while walking
        const int WarmupFrames = 30, MeasureFrames = 90;
        // The "Light" preset (PresetForHardware Lighthouse/InsideOut): minCutoff 8, beta 2, dCutoff 3.
        static float4 LightPosTuning => new float4(8f, 2f, 3f, BasisFilterMath.Alpha(30f, Dt));
        static float4 LightRotTuning => new float4(8f, 2f, 3f, BasisFilterMath.Alpha(35f, Dt));
        static readonly float3 LocalHead = new float3(0f, 1.6f, 0.1f);
        static float4x4 PlayspaceAt(int frame)
        {
            float t = frame * Dt;
            quaternion yaw = quaternion.AxisAngle(math.up(), math.radians(TurnDegPerSec * t));
            return float4x4.TRS(new float3(0f, 0f, WalkSpeed * t), yaw, new float3(1f, 1f, 1f));
        }
        static float RunPositionFilterWalk(byte mode, bool worldSpaceInputs, out float maxLag)
        {
            var modes = new NativeArray<byte>(1, Allocator.TempJob);
            var tuning = new NativeArray<float4>(1, Allocator.TempJob);
            var inputs = new NativeArray<float3>(1, Allocator.TempJob);
            var outputs = new NativeArray<float3>(1, Allocator.TempJob);
            var euroStates = new NativeArray<BasisEuroVec3State>(1, Allocator.TempJob);
            var fallbackStates = new NativeArray<float3>(1, Allocator.TempJob);

            modes[0] = mode;
            tuning[0] = LightPosTuning;
            maxLag = 0f;
            float steadyLag = 0f;

            try
            {
                for (int frame = 0; frame < WarmupFrames + MeasureFrames; frame++)
                {
                    float4x4 playspace = PlayspaceAt(frame);
                    float3 trackerWorld = math.transform(playspace, LocalHead);

                    // Post-fix pipeline: local in, playspace on the job. Pre-fix negative: world in,
                    // identity playspace — the filter state chases the walk itself.
                    inputs[0] = worldSpaceInputs ? trackerWorld : LocalHead;

                    new BasisBatchPositionFilterJob
                    {
                        mode = modes,
                        rawInputs = inputs,
                        tuning = tuning,
                        euroStates = euroStates,
                        fallbackStates = fallbackStates,
                        outputs = outputs,
                        dt = Dt,
                        playspaceToWorld = worldSpaceInputs ? float4x4.identity : playspace,
                    }.Run(1);

                    if (frame < WarmupFrames) continue;

                    float lag = math.length(outputs[0] - trackerWorld);
                    if (lag > maxLag) maxLag = lag;
                    steadyLag = lag;
                }
            }
            finally
            {
                modes.Dispose();
                tuning.Dispose();
                inputs.Dispose();
                outputs.Dispose();
                euroStates.Dispose();
                fallbackStates.Dispose();
            }

            return steadyLag;
        }
        [Test]
        public void AWalk_PassesThroughTheFilters_WithZeroLag()
        {
            foreach (byte mode in new[] { (byte)BasisFilterMode.Passthrough, (byte)BasisFilterMode.Fallback, (byte)BasisFilterMode.Euro })
            {
                RunPositionFilterWalk(mode, worldSpaceInputs: false, out float maxLag);
                Assert.Less(maxLag, 1e-4f, $"filter mode {(BasisFilterMode)mode} let the head target trail the tracker by " + $"{maxLag * 1000f:F2} mm during a plain stick walk — playspace motion is leaking " + "into the smoothing state again.");
            }
        }
        [Test]
        public void WorldSpaceFiltering_TrailsTheTrackerWhileWalking()
        {
            float euroLag = RunPositionFilterWalk((byte)BasisFilterMode.Euro, worldSpaceInputs: true, out _);
            Assert.Greater(euroLag, 0.008f, $"world-space euro filtering only trailed by {euroLag * 1000f:F1} mm at walk speed — the " + "defect this suite documents is no longer reproducible on this tuning.");

            float fallbackLag = RunPositionFilterWalk((byte)BasisFilterMode.Fallback, worldSpaceInputs: true, out _);
            Assert.Greater(fallbackLag, 0.004f, $"world-space fallback smoothing only trailed by {fallbackLag * 1000f:F1} mm at walk speed.");
        }
        [Test]
        public void TrackingNoise_IsStillSmoothed_WhileWalking()
        {
            float euroP2P = RunNoisyWalk((byte)BasisFilterMode.Euro);
            float rawP2P = RunNoisyWalk((byte)BasisFilterMode.Passthrough);

            Assert.Less(euroP2P, rawP2P * 0.6f, $"euro filtering no longer attenuates tracking-space noise during a walk: filtered p2p " + $"{euroP2P * 1000f:F2} mm vs raw {rawP2P * 1000f:F2} mm.");
        }
        static float RunNoisyWalk(byte mode)
        {
            var modes = new NativeArray<byte>(1, Allocator.TempJob);
            var tuning = new NativeArray<float4>(1, Allocator.TempJob);
            var inputs = new NativeArray<float3>(1, Allocator.TempJob);
            var outputs = new NativeArray<float3>(1, Allocator.TempJob);
            var euroStates = new NativeArray<BasisEuroVec3State>(1, Allocator.TempJob);
            var fallbackStates = new NativeArray<float3>(1, Allocator.TempJob);

            modes[0] = mode;
            tuning[0] = LightPosTuning;
            float min = float.MaxValue, max = float.MinValue;

            try
            {
                var rng = new System.Random(1234);
                for (int frame = 0; frame < WarmupFrames + MeasureFrames; frame++)
                {
                    float4x4 playspace = PlayspaceAt(frame);
                    float3 jitter = new float3((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)) * 0.002f;
                    inputs[0] = LocalHead + jitter;

                    new BasisBatchPositionFilterJob
                    {
                        mode = modes,
                        rawInputs = inputs,
                        tuning = tuning,
                        euroStates = euroStates,
                        fallbackStates = fallbackStates,
                        outputs = outputs,
                        dt = Dt,
                        playspaceToWorld = playspace,
                    }.Run(1);

                    if (frame < WarmupFrames) continue;

                    // Measure in tracking space so the walk itself does not count as motion.
                    float local = math.transform(math.inverse(playspace), outputs[0]).x;
                    if (local < min) min = local;
                    if (local > max) max = local;
                }
            }
            finally
            {
                modes.Dispose();
                tuning.Dispose();
                inputs.Dispose();
                outputs.Dispose();
                euroStates.Dispose();
                fallbackStates.Dispose();
            }

            return max - min;
        }
        // ────────────────────────────────────────────────────────────────────────────────────────
        //  Virtual spine ordering: sim-then-spine must track the CURRENT frame's eye.
        // ────────────────────────────────────────────────────────────────────────────────────────
        const int Eye = 0, Head = 1, Neck = 2, Chest = 3, Spine = 4, Hips = 5;
        const int ControlCount = 6;
        static BasisVirtualSpineCore.SpineSolveParams SpineParamsFromEye(float3 eyePos, quaternion eyeRot)
        {
            return new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = Dt,
                Scale = 1f,
                ParentMatrix = float4x4.identity,
                ParentRotation = quaternion.identity,
                EyeRot = eyeRot,
                EyePos = eyePos,

                // Head targets the eye with a zero offset, so the solved head must equal the eye pose
                // exactly — any difference is pipeline staleness, not solve behavior.
                HeadTargetPos = eyePos,
                HeadTargetRot = eyeRot,
                NeckTargetPos = eyePos,
                NeckTargetRot = eyeRot,
                ChestTargetPos = eyePos,
                ChestTargetRot = eyeRot,
                SpineTargetPos = eyePos,
                SpineTargetRot = eyeRot,

                HeadScaledOffset = float3.zero,
                NeckScaledOffset = new float3(0f, -0.12f, 0f),
                ChestScaledOffset = float3.zero,
                SpineScaledOffset = float3.zero,

                ChestTposeY = 1.30f,
                SpineTposeY = 1.10f,
                TposeHips = new float3(0f, 0.95f, 0f),

                ChestPitchFrac = 0.30f,
                ChestRollFrac = 0.30f,
                SpinePitchFrac = 0.10f,
                SpineRollFrac = 0.10f,
                NeckRotationSpeed = 40f,
                ChestRotationSpeed = 25f,
                SpineRotationSpeed = 30f,
                HipsRotationSpeed = 20f,
                HipsForwardBias = 0.02f,
                TorsoYawDeadzoneDeg = 45f,
                TorsoYawBlendSpeed = 8f,

                LenTotal = 0.65f,
                TChest = 0.35f,
                TSpine = 0.65f,

                StandingHipsLocalY = 0.95f,
                StandingHeadLocalY = 1.60f,
                PostureModel = 1,
                HipsCompressionStrength = 0.85f,
                HipsMaxDropMeters = 0.30f,
            };
        }
        static float RunEyePipeline(bool freshEyeParams)
        {
            var chain = new NativeArray<int>(ControlCount, Allocator.TempJob);
            var inputs = new NativeArray<BasisBoneSimInput>(ControlCount, Allocator.TempJob);
            var states = new NativeArray<BasisBoneSimState>(ControlCount, Allocator.TempJob);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.TempJob);

            float maxHeadError = 0f;

            try
            {
                for (int i = 0; i < ControlCount; i++)
                {
                    chain[i] = i;
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                    // Virtual bones: no tracker, virtual override — the sim job leaves them alone.
                    inputs[i] = new BasisBoneSimInput { HasVirtualOverride = 1, InverseOffsetRotation = quaternion.identity, IncomingRotation = quaternion.identity };
                }
                // The eye is the tracked device bone: the sim publishes its incoming pose verbatim.
                inputs[Eye] = new BasisBoneSimInput { HasTracker = 1, IncomingRotation = quaternion.identity };
                solve[0] = default;

                for (int frame = 0; frame < 60; frame++)
                {
                    // A physical lean/step: the eye moves through tracking space at walk speed.
                    float3 eyeNow = new float3(0f, 1.6f, 0f) + new float3(0f, 0f, WalkSpeed * frame * Dt);
                    BasisBoneSimInput eyeInput = inputs[Eye];
                    eyeInput.IncomingPosition = eyeNow;
                    inputs[Eye] = eyeInput;

                    var simJob = new BasisBoneSimChainJob
                    {
                        ChainIndices = chain,
                        Inputs = inputs,
                        States = states,
                        ParentMatrix = float4x4.identity,
                        ParentRotation = quaternion.identity,
                        DeltaTime = Dt,
                        InstantSnap = 0,
                    };

                    // Fresh = the driver's IncomingData read (this frame's poll). Stale = the eye
                    // control's outgoing before the sim has published, i.e. last frame's pose —
                    // exactly what the OnVirtualData-subscribed spine used to consume.
                    float3 eyeSeen = freshEyeParams ? eyeNow : states[Eye].OutgoingPosition;
                    quaternion eyeSeenRot = freshEyeParams ? quaternion.identity : states[Eye].OutgoingRotation;

                    new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states,
                        State = solve,
                        P = SpineParamsFromEye(eyeSeen, eyeSeenRot),
                        IdxHead = Head,
                        IdxNeck = Neck,
                        IdxChest = Chest,
                        IdxSpine = Spine,
                        IdxHips = Hips,
                    }.Execute();

                    simJob.Execute();

                    if (frame < 2) continue; // let the first pose land

                    float headError = math.length(states[Head].OutgoingPosition - eyeNow);
                    if (headError > maxHeadError) maxHeadError = headError;
                }
            }
            finally
            {
                chain.Dispose();
                inputs.Dispose();
                states.Dispose();
                solve.Dispose();
            }

            return maxHeadError;
        }
        [Test]
        public void TheVirtualSpine_ConsumesTheCurrentFramesEye()
        {
            float error = RunEyePipeline(freshEyeParams: true);
            Assert.Less(error, 1e-4f, $"the virtual spine's head missed the same-frame eye by {error * 1000f:F2} mm — the " + "pipeline is feeding the spine stale eye data again.");
        }
        [Test]
        public void SpineFedThePreSimOutgoing_LagsTheEyeByExactlyOneFrame()
        {
            float error = RunEyePipeline(freshEyeParams: false), oneFrame = WalkSpeed * Dt;

            Assert.Greater(error, oneFrame * 0.75f, $"the stale-eye order only lagged {error * 1000f:F2} mm; the one-frame defect this suite " + "documents is no longer reproducible.");
            Assert.Less(error, oneFrame * 1.25f, $"the stale-eye order lagged {error * 1000f:F2} mm — more than one frame; something besides " + "the ordering is stale.");
        }
        [Test]
        public void FollowerChain_SeesTheSameFramesVirtualHips()
        {
            float errSpineFirst = RunFollowerPipeline(spineBeforeSim: true, out float hipsTravelA);
            float errSimFirst = RunFollowerPipeline(spineBeforeSim: false, out float hipsTravelB);

            Assert.Greater(hipsTravelA, 1e-5f, "sanity: the virtual hips must actually move in this scenario");
            Assert.AreEqual(hipsTravelA, hipsTravelB, hipsTravelA * 0.05f,"sanity: both orders must see the same hips motion for the comparison to mean anything");

            float gap = errSimFirst - errSpineFirst;
            Assert.Greater(gap, hipsTravelA * 0.5f, $"sim-before-spine should trail the fresh hips by about one frame of hips travel " + $"({hipsTravelA * 1000f:F2} mm) more than spine-before-sim, but the gap was only " + $"{gap * 1000f:F2} mm — the follower-staleness defect is no longer reproducible.");
            Assert.Less(errSpineFirst, errSimFirst,
                "the production order (spine before sim) must be the one whose followers track the "
                + "same-frame hips.");
        }
        static float RunFollowerPipeline(bool spineBeforeSim, out float avgHipsTravel)
        {
            const int Follower = 6, Count = 7;
            var chain = new NativeArray<int>(Count, Allocator.TempJob);
            var inputs = new NativeArray<BasisBoneSimInput>(Count, Allocator.TempJob);
            var states = new NativeArray<BasisBoneSimState>(Count, Allocator.TempJob);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.TempJob);

            float3 followOffset = new float3(0f, -0.4f, 0f);
            float errSum = 0f, travelSum = 0f;
            int measured = 0;

            try
            {
                for (int i = 0; i < Count; i++)
                {
                    chain[i] = i;
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                    inputs[i] = new BasisBoneSimInput { HasVirtualOverride = 1, InverseOffsetRotation = quaternion.identity, IncomingRotation = quaternion.identity };
                }
                inputs[Eye] = new BasisBoneSimInput { HasTracker = 1, IncomingRotation = quaternion.identity };
                inputs[Follower] = new BasisBoneSimInput
                {
                    HasTarget = 1,
                    TargetIndex = Hips,
                    ScaledOffset = followOffset,
                    IncomingRotation = quaternion.identity,
                    InverseOffsetRotation = quaternion.identity,
                };
                solve[0] = default;

                float3 prevHips = default;
                bool havePrevHips = false;

                for (int frame = 0; frame < 90; frame++)
                {
                    float3 eyeNow = new float3(0f, 1.6f, 0f) + new float3(0f, 0f, WalkSpeed * frame * Dt);
                    BasisBoneSimInput eyeInput = inputs[Eye];
                    eyeInput.IncomingPosition = eyeNow;
                    inputs[Eye] = eyeInput;

                    var spineJob = new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states,
                        State = solve,
                        P = SpineParamsFromEye(eyeNow, quaternion.identity),
                        IdxHead = Head,
                        IdxNeck = Neck,
                        IdxChest = Chest,
                        IdxSpine = Spine,
                        IdxHips = Hips,
                    };
                    var simJob = new BasisBoneSimChainJob
                    {
                        ChainIndices = chain,
                        Inputs = inputs,
                        States = states,
                        ParentMatrix = float4x4.identity,
                        ParentRotation = quaternion.identity,
                        DeltaTime = Dt,
                        InstantSnap = 0,
                    };

                    if (spineBeforeSim) { spineJob.Execute(); simJob.Execute(); }
                    else { simJob.Execute(); spineJob.Execute(); }

                    float3 hipsNow = states[Hips].OutgoingPosition;
                    float3 followerIdeal = hipsNow + math.mul(states[Hips].OutgoingRotation, followOffset);

                    if (frame >= 60)
                    {
                        errSum += math.length(states[Follower].OutgoingPosition - followerIdeal);
                        if (havePrevHips) { travelSum += math.length(hipsNow - prevHips); }
                        measured++;
                    }
                    prevHips = hipsNow;
                    havePrevHips = true;
                }
            }
            finally
            {
                chain.Dispose();
                inputs.Dispose();
                states.Dispose();
                solve.Dispose();
            }

            avgHipsTravel = travelSum / math.max(1, measured - 1);
            return errSum / math.max(1, measured);
        }
        [Test]
        public void TrackedTorsoBones_KeepTheSimPose()
        {
            var states = new NativeArray<BasisBoneSimState>(ControlCount, Allocator.TempJob);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.TempJob);

            try
            {
                for (int i = 0; i < ControlCount; i++)
                {
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                }
                float3 trackedHips = new float3(0.123f, 0.9f, -0.05f);
                var hipsState = states[Hips];
                hipsState.OutgoingPosition = trackedHips;
                states[Hips] = hipsState;
                solve[0] = default;

                float3 eyePos = new float3(0f, 1.6f, 0f);

                var job = new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                {
                    States = states,
                    State = solve,
                    P = SpineParamsFromEye(eyePos, quaternion.identity),
                    IdxHead = Head,
                    IdxNeck = Neck,
                    IdxChest = Chest,
                    IdxSpine = Spine,
                    IdxHips = Hips,
                    SkipHips = 1,
                };
                job.Execute();

                Assert.AreEqual(0f, math.length(states[Hips].OutgoingPosition - trackedHips), 0f,
                    "a hips bone flagged tracker-owned was overwritten by the virtual spine — with the "
                    + "spine now running after the sim, that clobbers live FBT tracker data.");
                Assert.AreNotEqual(0f, math.length(states[Head].OutgoingPosition),"unskipped bones must still be written");

                // And with default flags the hips ARE the spine's to write (historical behavior for
                // the sweeps/tests that construct this job without flags).
                job.SkipHips = 0;
                job.Execute();
                Assert.AreNotEqual(0f, math.length(states[Hips].OutgoingPosition - trackedHips),"with default flags the virtual spine must own the hips again");
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
            }
        }
        // ────────────────────────────────────────────────────────────────────────────────────────
        //  Top-to-bottom: the spine solve holds the head pin while the playspace walks.
        // ────────────────────────────────────────────────────────────────────────────────────────
        static readonly float[] Heights = { 0.95f, 1.06f, 1.21f, 1.33f, 1.45f, 1.57f };
        [Test]
        public void AWalkingHead_StaysPinned_ThroughTheSpineSolve()
        {
            var root = new GameObject("WalkingPinRig");
            Transform[] bones = new Transform[Heights.Length];
            BasisPoseSkeleton skeleton = null;
            var chain = new NativeArray<BasisBoneHandle>(6, Allocator.Persistent);

            try
            {
                string[] names = { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" };
                Transform parent = root.transform;
                for (int i = 0; i < names.Length; i++)
                {
                    var go = new GameObject(names[i]);
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = new Vector3(0f, Heights[i] - (i == 0 ? 0f : Heights[i - 1]), 0f);
                    bones[i] = go.transform;
                    parent = go.transform;
                }

                skeleton = new BasisPoseSkeleton();
                skeleton.Build(bones[0], bones);
                skeleton.GatherNow();

                for (int i = 0; i < 6; i++)
                {
                    chain[i] = skeleton.Bind(bones[5 - i]);
                }

                var job = new BasisEerieMovement
                {
                    chainHeadToSpine = chain,
                    chainChestIdx = 3,
                    handleHips = skeleton.Bind(bones[0]),
                    spineMaxIterations = 20,
                    spineTolerance = 0.001f,
                    spineCCDRelax = 1.0f,
                    spineTwistKeep = 0.25f,
                    spineNeckTwistKeep = 0.9f,
                    neckMaxConeDeg = 45f,
                    maxChestDeltaDeg = 30f,
                    thoracicBendStiffen = 0.3f,
                    spineTautBandFrac = 0.015f,
                    bendTwistCoupling = 0.15f,
                    chestIkWeight = 0.5f,
                    chestIkIterations = 8,
                    chestIkHeadRestoreSweeps = 2,
                    chestPosPullMaxDeg = 20f,
                    chestPullMaxDist = 0.5f,
                    offsetRotationHead = Quaternion.identity,
                    playerUp = Vector3.up,
                    chestIkTarget = false,
                    spineAnatomicalRom = false,
                };
                BasisEeriePlanner.Bind(ref job);
                BasisEeriePlanner.Frame(ref job, default);

                Vector3 restHead = new Vector3(0f, Heights[5], 0f);

                // 5 cm of real compression with a 1 cm forward offset — the DeepCompression geometry:
                // a defined bow plane, clear of the full-extension taut band, where the solve is
                // well-conditioned. The standing residual at this pose is the solver's own
                // compression character; walking must reproduce it, not add to it.
                Vector3 localOffset = new Vector3(0f, -0.05f, 0.01f);

                root.transform.position = Vector3.zero;
                skeleton.GatherNow();
                job.poseStream = skeleton.Stream;
                job.SolveSequentialSpineIK(restHead + localOffset, Quaternion.identity);
                float standingErr = (skeleton.Stream.GetPosition(chain[0]) - (restHead + localOffset)).magnitude;
                float maxDivergence = 0f;
                for (int frame = 0; frame < 100; frame++)
                {
                    // The playspace (rig root) and the tracked head ride the same walk.
                    Vector3 walk = new Vector3(0f, 0f, WalkSpeed * frame * Dt);
                    root.transform.position = walk;
                    Vector3 target = restHead + walk + localOffset;

                    skeleton.GatherNow();
                    job.poseStream = skeleton.Stream;
                    job.SolveSequentialSpineIK(target, Quaternion.identity);

                    float err = (skeleton.Stream.GetPosition(chain[0]) - target).magnitude;
                    float divergence = Mathf.Abs(err - standingErr);
                    if (divergence > maxDivergence) maxDivergence = divergence;
                }

                Assert.Less(maxDivergence, 0.002f, $"walking changed the solved head's residual by {maxDivergence * 1000f:F2} mm vs the " + $"identical standing pose ({standingErr * 1000f:F2} mm) — the spine solve is not " + "translation-equivariant, i.e. the head detaches specifically while moving.");
            }
            finally
            {
                if (chain.IsCreated) chain.Dispose();
                skeleton?.Dispose();
                Object.DestroyImmediate(root);
            }
        }
    }
}
