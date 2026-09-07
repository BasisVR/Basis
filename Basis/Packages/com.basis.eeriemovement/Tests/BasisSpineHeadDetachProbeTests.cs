using System.Collections.Generic;
using System.Linq;
using System.Text;
using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    // Probe for "the head can become disconnected and flip around". Runs the headset-only pipeline the way
    // production wires it: real virtual-spine job (BasisLocalVirtualSpineDriver.Simulate mirrored field for field)
    // -> rig-driver target fill -> BasisEeriePlanner.Frame with no chest tracker -> real SolveSpine +
    // ApplyCervicalLordosis with the shipped defaults (path-sum minHeadSpineHeight, lordosis on, anatomical ROM on,
    // chest IK toggle on but gated off by the missing tracker). Synthetic head motion, 90 Hz. Gates: the solved
    // head bone stays on the HMD target and no chain bone flips between frames.
    public sealed class BasisSpineHeadDetachProbeTests
    {
        const int vsHead = 0, vsNeck = 1, vsChest = 2, vsSpine = 3, vsHips = 4, vsCount = 5;
        const float Dt = 1f / 90f, FlipStepDeg = 45f;
        struct Rest { public Vector3 Hips, Spine, Chest, UpperChest, Neck, Head, Eye; }
        sealed class Rig : System.IDisposable
        {
            public GameObject Root;
            public Transform[] Bones;
            public BasisPoseSkeleton Skeleton;
            public NativeArray<BasisBoneHandle> Chain;
            public NativeArray<BasisSpineRestFrame> RestFrames;
            public NativeArray<BasisChestSpringState> ChestSpring;
            public BasisEerieMovement Job;
            public Rest Rest;
            public float ChainReach, LenTotal, PathRest;
            public void Dispose()
            {
                if (Chain.IsCreated) Chain.Dispose();
                if (RestFrames.IsCreated) RestFrames.Dispose();
                if (ChestSpring.IsCreated) ChestSpring.Dispose();
                Skeleton?.Dispose();
                if (Root != null) Object.DestroyImmediate(Root);
            }
        }
        delegate void EyeMotion(float t, out Vector3 eyePos, out Quaternion eyeRot, out bool moving);
        sealed class Scenario
        {
            public string Name;
            public float Seconds;
            public EyeMotion Motion;
            public System.Func<float, Vector3> TrackedHips;
        }
        struct Toggles
        {
            public string Name;
            public bool ChestSpringOff, LordosisOff, RomOff, PreBendOff, PostureModelOff, CounterbalanceOff, CrouchOffsetOff;
            public float TrackingLiftY;
        }
        struct Sample
        {
            public float T, HeadErr, HeadRotErr, MaxStep, ReachRatio, HipsBelowHead, HipsPush, NeckHeadAngle, SpringLag, EyeY;
            public int MaxStepBone;
            public bool TorsoFlipped;
        }
        // A ~1.7 m rig with a real sagittal curve (path length longer than the chord) so the path-vs-chord
        // class of pelvis placement bugs is visible; every other spine fixture in this folder is straight.
        static Rest CurvedRest()
        {
            Rest r;
            r.Hips = new Vector3(0f, 0.95f, 0f);
            r.Spine = new Vector3(0f, 1.06f, -0.012f);
            r.Chest = new Vector3(0f, 1.21f, -0.022f);
            r.UpperChest = new Vector3(0f, 1.33f, -0.008f);
            r.Neck = new Vector3(0f, 1.45f, 0.012f);
            r.Head = new Vector3(0f, 1.57f, 0.022f);
            r.Eye = r.Head + new Vector3(0f, 0.07f, 0.09f);
            return r;
        }
        static Rest StraightRest()
        {
            Rest r = CurvedRest();
            r.Spine.z = r.Chest.z = r.UpperChest.z = r.Neck.z = r.Head.z = 0f;
            r.Eye = r.Head + new Vector3(0f, 0.07f, 0.09f);
            return r;
        }
        static Rig BuildRig(in Rest rest, BasisIKLockMode lockMode)
        {
            var rig = new Rig { Root = new GameObject("HeadDetachProbeRig"), Rest = rest };
            Vector3[] positions = { rest.Hips, rest.Spine, rest.Chest, rest.UpperChest, rest.Neck, rest.Head };
            string[] names = { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" };
            rig.Bones = new Transform[6];
            Transform parent = rig.Root.transform;
            for (int i = 0; i < 6; i++)
            {
                var go = new GameObject(names[i]);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = i == 0 ? positions[0] : positions[i] - positions[i - 1];
                rig.Bones[i] = go.transform;
                parent = go.transform;
            }
            rig.Skeleton = new BasisPoseSkeleton();
            rig.Skeleton.Build(rig.Bones[0], rig.Bones);
            rig.Skeleton.GatherNow();
            rig.Chain = new NativeArray<BasisBoneHandle>(6, Allocator.Persistent);
            for (int i = 0; i < 6; i++) rig.Chain[i] = rig.Skeleton.Bind(rig.Bones[5 - i]);
            // BasisEerieMovementSetup.BuildSpineAnatomy, verbatim: frames for chain[1..n-2], hips-right from the shoulders.
            rig.RestFrames = new NativeArray<BasisSpineRestFrame>(6, Allocator.Persistent);
            Transform[] chainTipFirst = { rig.Bones[5], rig.Bones[4], rig.Bones[3], rig.Bones[2], rig.Bones[1], rig.Bones[0] };
            for (int i = 1; i <= 4; i++)
            {
                Transform bone = chainTipFirst[i], child = chainTipFirst[i - 1], par = chainTipFirst[i + 1];
                BasisSpineSegment segment = bone == rig.Bones[1] ? BasisSpineSegment.Lumbar : bone == rig.Bones[2] ? BasisSpineSegment.LowerThoracic : bone == rig.Bones[3] ? BasisSpineSegment.UpperThoracic : BasisSpineSegment.Cervical;
                BasisSpineRestFrame frame = BasisSpineAnatomy.BuildRestFrame(bone.position, child.position, bone.rotation, par.rotation, Vector3.right);
                frame.Segment = segment;
                rig.RestFrames[i] = frame;
            }
            rig.ChestSpring = new NativeArray<BasisChestSpringState>(1, Allocator.Persistent);
            rig.ChainReach = 0f;
            for (int i = 0; i < 5; i++) rig.ChainReach += Vector3.Distance(positions[i], positions[i + 1]);
            // BasisLocalRigDriver.SetHandCollisionScale: the CONTROL path hips->spine->chest->neck->head (no upper chest).
            rig.PathRest = Vector3.Distance(rest.Hips, rest.Spine) + Vector3.Distance(rest.Spine, rest.Chest) + Vector3.Distance(rest.Chest, rest.Neck) + Vector3.Distance(rest.Neck, rest.Head);
            rig.LenTotal = math.max(1e-4f, Vector3.Distance(rest.Neck, rest.Chest) + Vector3.Distance(rest.Chest, rest.Spine) + Vector3.Distance(rest.Spine, rest.Hips));

            rig.Job = new BasisEerieMovement
            {
                chainHeadToSpine = rig.Chain,
                chainSpineRestFrames = rig.RestFrames,
                chestSpring = rig.ChestSpring,
                chainChestIdx = 3,
                handleHips = rig.Skeleton.Bind(rig.Bones[0]),
                handleSpine = rig.Skeleton.Bind(rig.Bones[1]),
                handleChest = rig.Skeleton.Bind(rig.Bones[2]),
                handleUpperChest = rig.Skeleton.Bind(rig.Bones[3]),
                handleNeck = rig.Skeleton.Bind(rig.Bones[4]),
                handleHead = rig.Skeleton.Bind(rig.Bones[5]),
                offsetRotationHead = Quaternion.identity, offsetRotationHips = Quaternion.identity, offsetRotationChest = Quaternion.identity,
                playerUp = Vector3.up,
                ikLockMode = lockMode,
                minHeadSpineHeight = rig.PathRest,
                minFactor = 0.95f, maxFactor = 1.05f,
                spineMaxIterations = 20, spineTolerance = 0.001f,
                maxBendDeg = 90f, maxChestDeltaDeg = 90f,
                spineBendPitch = 0.45f, spineBendYaw = 0.10f, spineBendRoll = 0.35f,
                upperChestBendPitch = 0.25f, upperChestBendYaw = 0.30f, upperChestBendRoll = 0.20f,
                hipHingeStartDeg = 40f, hipHingeMaxAddDeg = 52f,
                chestSpringHz = 12f, chestSpringDamping = 1f,
                spineMaxForwardDeg = 60f, spineMaxBackwardDeg = 25f, spineMaxLateralDeg = 25f,
                spineSquishBoost = 0.5f, spineGazeFollow = 0.25f, neckGazeFollow = 0.3f, neckGazeFollowMaxDeg = 18f,
                neckExtensionDamp = 0.65f, neckFlexionDamp = 0.5f,
                moveBodyBackWhenCrouching = 1f, trunkCounterbalance = 0.38f, trunkCounterbalanceMaxSpineFrac = 0.45f,
                spineCCDRelax = 1f, spineTwistKeep = 0.25f, spineNeckTwistKeep = 0.9f, neckMaxConeDeg = 45f,
                thoracicBendStiffen = 0.3f, spineTautBandFrac = 0.015f, bendTwistCoupling = 0.15f,
                anatDifferentialStiffness = true, anatShoulderSlide = true, anatCervicalLordosis = true, anatPelvicTwistRouting = true,
                spineAnatomicalRom = true, chestIkTarget = true,
                chestIkWeight = 0.5f, chestIkIterations = 8, chestIkHeadRestoreSweeps = 2, chestPosPullMaxDeg = 20f, chestPullMaxDist = 0.5f, chestFollowChestShare = 0.6f, chestHeadBudget = BasisEerieMovementSetup.ChestHeadBudgetMeters,
                chestArmSwingFactor = 0.3f, chestArmSwingMaxDeg = 15f,
                lordosisPitchGainDeg = 8f, lordosisBaseDeg = 5f, lordosisNeckShare = 0.65f, lordosisMaxHeadPitchDeg = 80f,
                lordosisExtremeStartDeg = 50f, lordosisExtremeFullDeg = 80f, lordosisExtremeRollForwardMaxDeg = 10f, lordosisExtremeRollBackwardMaxDeg = 4f,
                lordosisExtremeHipsHorizontalMax = 0.025f, lordosisExtremeChestHorizontalMax = 0.04f, lordosisExtremeHipsHorizontalLookUp = 0.025f, lordosisExtremeChestHorizontalLookUp = 0.010f,
                lordosisExtremeHipsDownMax = 0.015f, lordosisExtremeChestDownMax = 0.025f, lordosisExtremeHipsDownLookUp = 0.0005f, lordosisExtremeChestDownLookUp = 0.001f,
                tposeHeadToNeckLocal = Quaternion.Inverse(rig.Bones[5].rotation) * (rest.Neck - rest.Head),
                tposeLengthNeckToHips = rest.Neck - rest.Hips,
                tposeBakeScale = 1f, tposeArmFitScale = 1f, tposeTorsoFitScale = 1f,
                standingHeadHeight = rest.Head.y,
            };
            BasisEeriePlanner.Bind(ref rig.Job);
            return rig;
        }
        // BasisLocalVirtualSpineDriver.Simulate + RecomputeSegmentLengths, mirrored (VR, nod-pivot estimator off).
        static BasisVirtualSpineCore.SpineSolveParams Params(in Rig rig, NativeArray<BasisBoneSimState> states, Vector3 eyePos, Quaternion eyeRot, bool moving)
        {
            Rest rest = rig.Rest;
            float neckChest = Vector3.Distance(rest.Neck, rest.Chest), chestSpine = Vector3.Distance(rest.Chest, rest.Spine);
            float3 eyeFromHead = rest.Eye - rest.Head;
            var (along, perp) = RestGeometry(rest);
            return new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = Dt, Scale = 1f, TrackingLiftY = 0f, ParentMatrix = float4x4.identity, ParentRotation = quaternion.identity, EyeRot = eyeRot,
                HeadTargetPos = eyePos, HeadTargetRot = eyeRot,
                NeckTargetPos = states[vsHead].OutgoingPosition, NeckTargetRot = states[vsHead].OutgoingRotation,
                ChestTargetPos = states[vsNeck].OutgoingPosition, ChestTargetRot = states[vsNeck].OutgoingRotation,
                SpineTargetPos = states[vsChest].OutgoingPosition, SpineTargetRot = states[vsChest].OutgoingRotation,
                HeadScaledOffset = rest.Head - rest.Eye, NeckScaledOffset = rest.Neck - rest.Head, ChestScaledOffset = rest.Chest - rest.Neck, SpineScaledOffset = rest.Spine - rest.Chest,
                ChestTposeY = rest.Chest.y, SpineTposeY = rest.Spine.y, TposeHips = rest.Hips,
                ChestPitchFrac = 0.30f, ChestRollFrac = 0.30f, SpinePitchFrac = 0.10f, SpineRollFrac = 0.10f,
                NeckRotationSpeed = 40f, ChestRotationSpeed = 25f, SpineRotationSpeed = 30f, HipsRotationSpeed = 20f,
                HipsForwardBias = 0f, NeckExtensionDamp = 0.65f, NeckFlexionDamp = 0.5f,
                TorsoYawDeadzoneDeg = 0f, TorsoYawBlendSpeed = 8f,
                HipsFreeze = 0, IsLocomoting = (byte)(moving ? 1 : 0),
                LenTotal = rig.LenTotal, TChest = math.saturate(neckChest / rig.LenTotal), TSpine = math.saturate((neckChest + chestSpine) / rig.LenTotal),
                StandingHipsLocalY = rest.Neck.y - rig.LenTotal, StandingHeadLocalY = math.max(rest.Head.y, 1e-3f),
                EyePos = eyePos, GazeSwingLever = eyeFromHead, TposeNeckMinusEyeY = rest.Neck.y - rest.Eye.y, GazeSwingRemoval = 1f,
                HipsAnchorOffsetLocal = new float3(rest.Hips.x - rest.Eye.x, 0f, rest.Hips.z - rest.Eye.z),
                HeadRestFromEyeLocal = new float3(rest.Head.x - rest.Eye.x, 0f, rest.Head.z - rest.Eye.z),
                YawPivotFromEyeLocal = new float3(rest.Neck.x - rest.Eye.x, 0f, rest.Neck.z - rest.Eye.z),
                PostureModel = 1, HipsCompressionStrength = 0.85f, HipsMaxDropMeters = 0.3f, HipsRestDropY = 0f,
                RestChordDir = math.normalizesafe((float3)(rest.Neck - rest.Hips)), ChestRestAlong = along.chest, ChestRestPerp = perp.chest, SpineRestAlong = along.spine, SpineRestPerp = perp.spine,
            };
        }
        static ((float chest, float spine) along, (float3 chest, float3 spine) perp) RestGeometry(in Rest rest)
        {
            float3 chord = rest.Neck - rest.Hips;
            float lenSq = math.lengthsq(chord);
            BasisLocalVirtualSpineDriver.RestOffsetFromChord(rest.Chest, rest.Hips, chord, lenSq, out float chestAlong, out float3 chestPerp);
            BasisLocalVirtualSpineDriver.RestOffsetFromChord(rest.Spine, rest.Hips, chord, lenSq, out float spineAlong, out float3 spinePerp);
            return ((chestAlong, spineAlong), (chestPerp, spinePerp));
        }
        static BasisVirtualSpineCore.SpineSolveParams ParamsToggled(in Rig rig, NativeArray<BasisBoneSimState> states, Vector3 eyePos, Quaternion eyeRot, bool moving, Toggles tg)
        {
            BasisVirtualSpineCore.SpineSolveParams p = Params(rig, states, eyePos, eyeRot, moving);
            if (tg.PostureModelOff) p.PostureModel = 0;
            p.TrackingLiftY = tg.TrackingLiftY;
            return p;
        }
        static Quaternion Gaze(float pitchDeg, float yawDeg) => Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.AngleAxis(pitchDeg, Vector3.right);
        static List<Sample> Run(Rig rig, Scenario s, System.Random rng, float noise) => Run(rig, s, rng, noise, default);
        static List<Sample> Run(Rig rig, Scenario s, System.Random rng, float noise, Toggles tg)
        {
            if (tg.ChestSpringOff) rig.Job.chestSpringHz = 0f;
            if (tg.LordosisOff) rig.Job.anatCervicalLordosis = false;
            if (tg.RomOff) rig.Job.spineAnatomicalRom = false;
            if (tg.PreBendOff) { rig.Job.spineBendPitch = rig.Job.spineBendYaw = rig.Job.spineBendRoll = rig.Job.upperChestBendPitch = rig.Job.upperChestBendYaw = rig.Job.upperChestBendRoll = 0f; }
            if (tg.CounterbalanceOff) rig.Job.trunkCounterbalance = 0f;
            if (tg.CrouchOffsetOff) rig.Job.moveBodyBackWhenCrouching = 0f;
            var samples = new List<Sample>();
            var states = new NativeArray<BasisBoneSimState>(vsCount, Allocator.Temp);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Temp);
            Vector3[] restPos = { rig.Rest.Head, rig.Rest.Neck, rig.Rest.Chest, rig.Rest.Spine, rig.Rest.Hips };
            try
            {
                for (int i = 0; i < vsCount; i++) states[i] = new BasisBoneSimState { OutgoingPosition = restPos[i], LastRunPosition = restPos[i], OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                solve[0] = default;
                Quaternion[] prevRot = new Quaternion[6];
                bool have = false;
                int frames = Mathf.RoundToInt(s.Seconds / Dt);
                for (int f = 0; f < frames; f++)
                {
                    float t = f * Dt;
                    s.Motion(t, out Vector3 eyePos, out Quaternion eyeRot, out bool moving);
                    if (noise > 0f) eyePos += new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)) * noise;
                    bool hipsTracked = s.TrackedHips != null;
                    new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states, State = solve, P = ParamsToggled(rig, states, eyePos, eyeRot, moving, tg),
                        IdxHead = vsHead, IdxNeck = vsNeck, IdxChest = vsChest, IdxSpine = vsSpine, IdxHips = vsHips,
                        SkipHips = (byte)(hipsTracked ? 1 : 0),
                    }.Execute();
                    var facts = new BasisEerieFrameFacts { deltaTime = Dt, moving = moving, upright = true, hipsTracked = hipsTracked };
                    BasisEeriePlanner.Frame(ref rig.Job, in facts);
                    Vector3 headPos = states[vsHead].OutgoingPosition;
                    Quaternion headRot = states[vsHead].OutgoingRotation;
                    Vector3 hipsIn = hipsTracked ? s.TrackedHips(t) : (Vector3)states[vsHips].OutgoingPosition;
                    rig.Job.targetPositionHips = hipsIn;
                    rig.Job.targetRotationHips = hipsTracked ? Quaternion.identity : (Quaternion)states[vsHips].OutgoingRotation;
                    rig.Job.targetPositionHead = headPos;
                    rig.Job.targetRotationHead = headRot;
                    rig.Job.targetPositionChest = rig.Job.targetPositionChestRaw = states[vsChest].OutgoingPosition;
                    rig.Job.targetRotationChest = states[vsChest].OutgoingRotation;
                    rig.Job.crouchDepth = Mathf.Max(0f, rig.Rest.Head.y - headPos.y);
                    rig.Skeleton.GatherNow();
                    rig.Job.poseStream = rig.Skeleton.Stream;
                    rig.Job.poseStream.deltaTime = Dt;
                    rig.Job.SolveSpine();
                    if (rig.Job.plan.lordosis) rig.Job.ApplyCervicalLordosis();

                    BasisPoseStream st = rig.Skeleton.Stream;
                    Vector3 headBone = st.GetPosition(rig.Job.handleHead), neckBone = st.GetPosition(rig.Job.handleNeck), chestBone = st.GetPosition(rig.Job.handleChest), hipsBone = st.GetPosition(rig.Job.handleHips);
                    Quaternion headBoneRot = st.GetRotation(rig.Job.handleHead);
                    Sample smp;
                    smp.T = t;
                    smp.HeadErr = (headBone - headPos).magnitude;
                    smp.HeadRotErr = Quaternion.Angle(headBoneRot, headRot);
                    smp.ReachRatio = (hipsBone - headPos).magnitude / rig.ChainReach;
                    smp.HipsBelowHead = headPos.y - hipsBone.y;
                    smp.HipsPush = (rig.Job.targetPositionHips - hipsIn).magnitude;
                    smp.NeckHeadAngle = Vector3.Angle(neckBone - chestBone, headBone - neckBone);
                    Vector3 chestFwd = st.GetRotation(rig.Job.handleChest) * Vector3.forward, headFwd = headRot * Vector3.forward;
                    chestFwd.y = 0f; headFwd.y = 0f;
                    smp.TorsoFlipped = chestFwd.sqrMagnitude > 1e-6f && headFwd.sqrMagnitude > 1e-6f && Vector3.Dot(chestFwd.normalized, headFwd.normalized) < 0f;
                    smp.MaxStep = 0f;
                    smp.MaxStepBone = -1;
                    smp.EyeY = eyePos.y;
                    smp.SpringLag = rig.Job.chestSpringHz > 0f ? (rig.ChestSpring[0].Pos - headPos).magnitude : 0f;
                    for (int b = 0; b < 6; b++)
                    {
                        Quaternion r = st.GetRotation(rig.Chain[b]);
                        if (have)
                        {
                            float step = Quaternion.Angle(prevRot[b], r);
                            if (step > smp.MaxStep) { smp.MaxStep = step; smp.MaxStepBone = b; }
                        }
                        prevRot[b] = r;
                    }
                    have = true;
                    samples.Add(smp);
                }
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
            }
            return samples;
        }
        static Scenario[] Scenarios(Rest rest)
        {
            Vector3 eye = rest.Eye, pivot = rest.Eye + new Vector3(0f, -0.10f, -0.06f);
            Vector3 Orbit(float pitch, float yaw) => pivot + Gaze(pitch, yaw) * (eye - pivot);
            return new[]
            {
                new Scenario { Name = "stand still", Seconds = 4f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye; r = Quaternion.identity; m = false; } },
                new Scenario { Name = "nod +-60 deg 0.25Hz", Seconds = 8f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float pitch = 60f * Mathf.Sin(2f * Mathf.PI * 0.25f * t); p = Orbit(pitch, 0f); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "look up 70 hold", Seconds = 4f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float pitch = -70f * Mathf.Clamp01(t); p = Orbit(pitch, 0f); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "look down 85 hold", Seconds = 4f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float pitch = 85f * Mathf.Clamp01(t); p = Orbit(pitch, 0f); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "lean fwd 35cm", Seconds = 6f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, 0f, 0.35f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 6f))); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "lean back 25cm", Seconds = 6f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, 0f, -0.25f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 6f))); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "crouch 45cm", Seconds = 6f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, -0.45f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 6f)), 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "squat 75cm", Seconds = 6f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, -0.75f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 6f)), 0.10f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 6f))); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "yaw +-170", Seconds = 8f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float yaw = 170f * Mathf.Sin(2f * Mathf.PI * 0.125f * t); p = Orbit(0f, yaw); r = Gaze(0f, yaw); m = false; } },
                new Scenario { Name = "walk bob", Seconds = 6f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, 0.03f * Mathf.Sin(2f * Mathf.PI * 2f * t), 1.2f * t); r = Quaternion.identity; m = true; } },
                new Scenario { Name = "jump +35cm", Seconds = 3f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float u = Mathf.Clamp01((t - 1f) / 0.5f); p = eye + new Vector3(0f, 0.35f * Mathf.Sin(Mathf.PI * u), 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "jump +35cm slow (1s)", Seconds = 4f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float u = Mathf.Clamp01((t - 1f) / 2f); p = eye + new Vector3(0f, 0.35f * Mathf.Sin(Mathf.PI * u), 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "fast stand-up 45cm/0.3s", Seconds = 3f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float u = t < 1f ? 0f : Mathf.Clamp01((t - 1f) / 0.3f); p = eye + new Vector3(0f, -0.45f * (1f - u), 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "fast crouch 45cm/0.3s", Seconds = 3f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float u = t < 1f ? 0f : Mathf.Clamp01((t - 1f) / 0.3f); p = eye + new Vector3(0f, -0.45f * u, 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "head +12cm above rest", Seconds = 4f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, 0.12f, 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "head -12cm below rest", Seconds = 4f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye + new Vector3(0f, -0.12f, 0f); r = Quaternion.identity; m = false; } },
                new Scenario { Name = "look at feet (lean 25 + down 60)", Seconds = 6f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float u = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 6f)); float pitch = 60f * u; p = Orbit(pitch, 0f) + new Vector3(0f, -0.05f * u, 0.25f * u); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "nod while crouched 30", Seconds = 8f, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float pitch = 45f * Mathf.Sin(2f * Mathf.PI * 0.25f * t); p = Orbit(pitch, 0f) + new Vector3(0f, -0.30f, 0f); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "FBT hips: touch toes", Seconds = 8f, TrackedHips = t => rest.Hips, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float u = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 8f)); float pitch = 90f * u; p = eye + new Vector3(0f, -0.70f * u, 0.45f * u); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "FBT hips: nod +-60", Seconds = 8f, TrackedHips = t => rest.Hips, Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { float pitch = 60f * Mathf.Sin(2f * Mathf.PI * 0.25f * t); p = Orbit(pitch, 0f); r = Gaze(pitch, 0f); m = false; } },
                new Scenario { Name = "FBT hips 8cm too low", Seconds = 4f, TrackedHips = t => rest.Hips + new Vector3(0f, -0.08f, 0f), Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye; r = Quaternion.identity; m = false; } },
                new Scenario { Name = "FBT hips 8cm too high", Seconds = 4f, TrackedHips = t => rest.Hips + new Vector3(0f, 0.08f, 0f), Motion = (float t, out Vector3 p, out Quaternion r, out bool m) => { p = eye; r = Quaternion.identity; m = false; } },
            };
        }
        static string Row(string scenario, List<Sample> s)
        {
            float maxErr = s.Max(x => x.HeadErr), meanErr = s.Average(x => x.HeadErr);
            Sample worst = s.OrderByDescending(x => x.HeadErr).First();
            int flips = s.Count(x => x.MaxStep > FlipStepDeg), above = s.Count(x => x.HipsBelowHead < 0f), torso = s.Count(x => x.TorsoFlipped);
            float maxStep = s.Max(x => x.MaxStep), maxReach = s.Max(x => x.ReachRatio), minReach = s.Min(x => x.ReachRatio), maxRot = s.Max(x => x.HeadRotErr), maxPush = s.Max(x => x.HipsPush), maxNeck = s.Max(x => x.NeckHeadAngle);
            return $"{scenario,-34} | head err cm max {maxErr * 100f,6:F2} mean {meanErr * 100f,5:F2} (t={worst.T,4:F2}s) | rot err max {maxRot,6:F2} | bone step max {maxStep,6:F2} deg, flips {flips,3} | reach {minReach,5:F3}..{maxReach,5:F3} | hips above head {above,3} | torso flipped {torso,3} | hips push max cm {maxPush * 100f,5:F1} | neck angle max {maxNeck,5:F1}";
        }
        const float HeadsetOnlyHeadErrCeiling = 0.08f;
        [Test]
        public void HeadsetOnlyScenarios_TheHeadStaysOnTheHmd_AndNoBoneFlips()
        {
            var report = new StringBuilder();
            foreach ((string rigName, Rest rest) in new[] { ("curved rest", CurvedRest()), ("straight rest", StraightRest()) })
            {
                foreach (BasisIKLockMode mode in new[] { BasisIKLockMode.LockHead, BasisIKLockMode.LockBoth })
                {
                    using Rig probe = BuildRig(rest, mode);
                    report.AppendLine($"=== {rigName} | {mode} | chain reach {probe.ChainReach * 100f:F2} cm, control path (minHeadSpineHeight) {probe.PathRest * 100f:F2} cm, chord {Vector3.Distance(rest.Hips, rest.Head) * 100f:F2} cm, vspine lenTotal {probe.LenTotal * 100f:F2} cm");
                    foreach (Scenario s in Scenarios(rest))
                    {
                        using Rig rig = BuildRig(rest, mode);
                        List<Sample> samples = Run(rig, s, new System.Random(1234), 0.0003f);
                        foreach (Sample smp in samples)
                        {
                            Assert.IsFalse(float.IsNaN(smp.HeadErr) || float.IsInfinity(smp.HeadErr), $"{rigName}/{mode}/{s.Name}: non-finite head error");
                        }
                        report.AppendLine(Row(s.Name, samples));
                        if (!s.Name.StartsWith("FBT"))
                        {
                            Assert.AreEqual(0, samples.Count(x => x.MaxStep > FlipStepDeg), $"{rigName}/{mode}/{s.Name}: a chain bone stepped more than {FlipStepDeg} deg in one frame.");
                            Assert.Less(samples.Max(x => x.HeadErr), HeadsetOnlyHeadErrCeiling, $"{rigName}/{mode}/{s.Name}: the head bone sits {samples.Max(x => x.HeadErr) * 100f:F1} cm off the HMD.");
                        }
                    }
                    report.AppendLine();
                }
            }
            TestContext.WriteLine(report.ToString());
        }
        [Test]
        public void HeadAboveRest_WithoutAChestTracker_TheHeadStaysOnTheHmd_AndNeverFlips()
        {
            // A tracked head sitting a fixed distance above the avatar's rest head is exactly what a scale/eye-height
            // mismatch produces (avatar too small for the player, seated mode while standing, arm-span mode with a
            // bent-arm span). The headset-only chest IK target used to fold the spine here: its target was the virtual
            // spine's T-pose-height chest control, which sat below the lumbar joint past one vertebra of rise (27 cm head
            // error, 90 deg/frame flips, standing still). The chest target now needs a chest tracker, so with the toggle
            // on and no tracker the plan must leave it off and the sweep must be quiet.
            const float HeadErrCeiling = 0.05f;
            var report = new StringBuilder();
            foreach ((string rigName, Rest rest) in new[] { ("curved rest", CurvedRest()), ("straight rest", StraightRest()) })
            {
                report.AppendLine($"=== steady head height sweep {rigName} LockHead (chest T-pose y {rest.Chest.y * 100f:F1} cm, spine T-pose y {rest.Spine.y * 100f:F1} cm)");
                report.AppendLine($"{"head vs rest",-14} | {"production: err cm/flips/step",-36} | {"with playspace lift",-36}");
                for (int cm = -30; cm <= 60; cm += 5)
                {
                    float off = cm * 0.01f;
                    Vector3 eye = rest.Eye + new Vector3(0f, off, 0f);
                    Scenario s = new Scenario { Name = "steady", Seconds = 3f, Motion = (float t, out Vector3 pp, out Quaternion rr, out bool mm) => { pp = eye; rr = Quaternion.identity; mm = false; } };
                    var cells = new List<string>();
                    List<Sample> production = null;
                    foreach (Toggles tg in new[] { new Toggles { Name = "production" }, new Toggles { Name = "lift", TrackingLiftY = off } })
                    {
                        using Rig rig = BuildRig(rest, BasisIKLockMode.LockHead);
                        List<Sample> samples = Run(rig, s, new System.Random(1234), 0.0003f, tg);
                        Assert.IsFalse(rig.Job.plan.chestTarget, $"{rigName}: no chest tracker, yet the plan enabled the chest IK target.");
                        if (production == null) production = samples;
                        cells.Add($"{samples.Max(x => x.HeadErr) * 100f,7:F2} / {samples.Count(x => x.MaxStep > FlipStepDeg),3} / {samples.Max(x => x.MaxStep),6:F1}");
                    }
                    report.AppendLine($"{cm,+12} cm | {cells[0],-36} | {cells[1],-36}");
                    Assert.AreEqual(0, production.Count(x => x.MaxStep > FlipStepDeg), $"{rigName}: head {cm:+0;-0} cm from rest, standing still: a chain bone stepped more than {FlipStepDeg} deg in one frame.");
                    Assert.Less(production.Max(x => x.HeadErr), HeadErrCeiling, $"{rigName}: head {cm:+0;-0} cm from rest, standing still: the head bone sits {production.Max(x => x.HeadErr) * 100f:F1} cm off the HMD.");
                }
                report.AppendLine();
            }
            TestContext.WriteLine(report.ToString());
        }
        [Test]
        public void FeatureIsolation_NoSingleFeatureToggle_FlipsTheChain()
        {
            Toggles[] toggles =
            {
                new Toggles { Name = "production" },
                new Toggles { Name = "chest spring off", ChestSpringOff = true },
                new Toggles { Name = "lordosis off", LordosisOff = true },
                new Toggles { Name = "anatomical ROM off", RomOff = true },
                new Toggles { Name = "pre-bend off", PreBendOff = true },
                new Toggles { Name = "vspine posture model off", PostureModelOff = true },
                new Toggles { Name = "counterbalance off", CounterbalanceOff = true },
                new Toggles { Name = "crouch setback off", CrouchOffsetOff = true },
            };
            string[] wanted = { "jump +35cm", "jump +35cm slow (1s)", "fast stand-up 45cm/0.3s", "fast crouch 45cm/0.3s", "look down 85 hold", "crouch 45cm", "nod +-60 deg 0.25Hz" };
            var report = new StringBuilder();
            foreach ((string rigName, Rest rest) in new[] { ("straight rest", StraightRest()), ("curved rest", CurvedRest()) })
            {
                report.AppendLine($"=== isolation {rigName} LockHead: rows = feature toggled, cells = max head err cm / flip frames (>{FlipStepDeg} deg) / max bone step deg");
                report.AppendLine($"{"toggle",-30} | " + string.Join(" | ", wanted.Select(w => $"{w,-24}")));
                foreach (Toggles tg in toggles)
                {
                    var cells = new List<string>();
                    foreach (string w in wanted)
                    {
                        using Rig rig = BuildRig(rest, BasisIKLockMode.LockHead);
                        Scenario s = Scenarios(rest).First(x => x.Name == w);
                        List<Sample> samples = Run(rig, s, new System.Random(1234), 0.0003f, tg);
                        int flips = samples.Count(x => x.MaxStep > FlipStepDeg);
                        cells.Add($"{samples.Max(x => x.HeadErr) * 100f,6:F2} /{flips,4} /{samples.Max(x => x.MaxStep),6:F1}");
                        if (!tg.RomOff)
                        {
                            Assert.AreEqual(0, flips, $"{rigName} / {tg.Name} / {w}: a chain bone stepped more than {FlipStepDeg} deg in one frame.");
                        }
                    }
                    report.AppendLine($"{tg.Name,-30} | " + string.Join(" | ", cells));
                }
                report.AppendLine();
            }
            TestContext.WriteLine(report.ToString());
        }
    }
}
