using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Body-proportion regression tests for the two-bone limb solvers.
    ///
    /// Every other IK test in this suite runs one "adult-ish" skeleton. These run HUMAN INPUT POSES
    /// (the targets a person actually produces in VR: hands at sides, touching the chin, reaching
    /// across to the other shoulder, squatting, sitting, stepping onto a box...) across a catalog of
    /// body proportions from a 0.6 m chibi to a 2.4 m giant, including skewed segment ratios
    /// (long-forearm, long-thigh) and width extremes (lanky vs stocky). Poses are built from BODY
    /// LANDMARKS (chin, opposite shoulder, knee, a step of absolute height), so the same human intent
    /// lands at a different reach ratio and direction on every body -- exactly what avatar variety
    /// does to the solver in production.
    ///
    /// Three failure classes are unique to proportion changes, and each gets a dedicated guard here:
    ///
    ///   1. ABSOLUTE-LENGTH ASSUMPTIONS -- a constant tuned in metres (a fade window, an epsilon, a
    ///      clearance) that is correct at 1.75 m and wrong at 0.6 m / 2.4 m. Guarded by
    ///      Solve_IsScaleInvariant_FromChibiToGiant: a geometrically-similar body must produce
    ///      identical angles at any scale.
    ///   2. MIRROR ASYMMETRY -- left/right limbs solved through mirrored inputs must give mirrored
    ///      outputs on EVERY body, or one arm looks natural and the other doesn't.
    ///   3. RATIO-TRIGGERED POLE FLIPS -- a pole/axis choice that is stable at 50:50 upper:lower but
    ///      flips somewhere along the ratio range rigs actually ship. Guarded by
    ///      Solve_StaysContinuous_AsLimbProportionsMorph.
    ///
    /// Thresholds were calibrated offline against a float64 port of the cores running this exact
    /// matrix (ik_bodyprop_calibrate.py); each assert notes the measured value so a failure means a
    /// real regression, not float noise.
    /// </summary>
    public class BasisBodyProportionTests
    {
        // Human inputs are capped short of the genuine full-extension singularity: a person holding a
        // controller almost never pins the arm dead straight, and the >0.95 regime has its own suite
        // (BasisElbowDirectionTests full-extension arcs). Beyond-reach inputs are tested UNCAPPED below.
        const float ArmReachCap = 0.95f;
        const float LegReachCap = 0.97f;

        static readonly Vector3 RestElbowDir = new Vector3(0.15f, -0.95f, 0.27f).normalized;
        static readonly Vector3 RestForearmDir = new Vector3(0f, -0.30f, 0.95f).normalized;
        static readonly Vector3 RestKneeDir = new Vector3(0f, -0.97f, 0.20f).normalized;
        static readonly Vector3 RestShinDir = new Vector3(0f, -0.98f, -0.10f).normalized;
        // KneeBendPref: the rig feeds the same hips-right bend normal to BOTH legs (BasisLocalRigDriver).
        static readonly Vector3 HipsRight = Vector3.right;

        NativeArray<Vector3> _table;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_table.IsCreated) _table.Dispose();
        }

        // ----------------------------------------------------------------- the body catalog

        // A body the tests solve against: right-side skeleton derived from height fractions.
        // Body faces +Z, +X is the body's right. Hip height is DERIVED from the leg segments
        // (thigh + shin + ankle clearance) so "standing" is exactly reachable on every profile,
        // the same consistency a real rigged avatar has.
        struct BodyProfile
        {
            public string Name;
            public float Height;
            public float Upper, Fore, ArmLen;
            public float Thigh, Shin, LegLen;
            public float ShoulderY, ShoulderX;
            public float HipY, HipX;
            public float KneeY;
            public Vector3 Shoulder, Hip;
            public Vector3 RestElbow, RestHand;
            public Vector3 RestKnee, RestAnkle;

            public static BodyProfile Make(string name, float h, float shoulderYF, float shoulderXF,
                float hipXF, float upperF, float foreF, float thighF, float shinF, float ankleYF)
            {
                BodyProfile b;
                b.Name = name;
                b.Height = h;
                b.Upper = upperF * h; b.Fore = foreF * h; b.ArmLen = b.Upper + b.Fore;
                b.Thigh = thighF * h; b.Shin = shinF * h; b.LegLen = b.Thigh + b.Shin;
                b.ShoulderY = shoulderYF * h; b.ShoulderX = shoulderXF * h;
                b.HipY = (thighF + shinF + ankleYF) * h; b.HipX = hipXF * h;
                b.KneeY = (shinF + ankleYF) * h;
                b.Shoulder = new Vector3(b.ShoulderX, b.ShoulderY, 0f);
                b.Hip = new Vector3(b.HipX, b.HipY, 0f);
                b.RestElbow = b.Shoulder + RestElbowDir * b.Upper;
                b.RestHand = b.RestElbow + RestForearmDir * b.Fore;
                b.RestKnee = b.Hip + RestKneeDir * b.Thigh;
                b.RestAnkle = b.RestKnee + RestShinDir * b.Shin;
                return b;
            }
        }

        // Ten bodies spanning what actually connects to a Basis instance. AverageAdult reproduces the
        // canonical suite skeleton (arm 0.28+0.26, leg 0.42+0.42); the rest bend every ratio the solver
        // sees: absolute scale (Chibi 0.6 m -> Giant 2.4 m), arm:height (TallLanky +10%, Stocky -8%),
        // shoulder width : arm length (Stocky wide vs TallLanky narrow -- decides whether cross-body
        // landmarks are even reachable), upper:lower segment skew both ways, and leg:torso split.
        static BodyProfile[] Profiles() => new[]
        {
            BodyProfile.Make("AverageAdult",      1.75f, 0.818f, 0.105f, 0.050f, 0.160f, 0.1486f, 0.240f, 0.240f, 0.039f),
            BodyProfile.Make("ShortAdult",        1.50f, 0.818f, 0.105f, 0.050f, 0.160f, 0.1486f, 0.240f, 0.240f, 0.039f),
            BodyProfile.Make("TallLanky",         2.05f, 0.823f, 0.085f, 0.048f, 0.176f, 0.1630f, 0.240f, 0.240f, 0.039f),
            BodyProfile.Make("Stocky",            1.65f, 0.810f, 0.135f, 0.060f, 0.147f, 0.1370f, 0.225f, 0.225f, 0.042f),
            BodyProfile.Make("ChildLike",         1.20f, 0.770f, 0.100f, 0.052f, 0.150f, 0.1400f, 0.225f, 0.225f, 0.045f),
            BodyProfile.Make("Chibi",             0.60f, 0.750f, 0.120f, 0.060f, 0.130f, 0.1200f, 0.200f, 0.200f, 0.050f),
            BodyProfile.Make("Giant",             2.40f, 0.818f, 0.105f, 0.050f, 0.160f, 0.1486f, 0.240f, 0.240f, 0.039f),
            BodyProfile.Make("UpperDominantArms", 1.75f, 0.818f, 0.105f, 0.050f, 0.183f, 0.1260f, 0.240f, 0.240f, 0.039f),
            BodyProfile.Make("ForearmDominant",   1.75f, 0.818f, 0.105f, 0.050f, 0.126f, 0.1830f, 0.270f, 0.210f, 0.039f),
            BodyProfile.Make("LongLegs",          1.85f, 0.830f, 0.100f, 0.050f, 0.160f, 0.1486f, 0.270f, 0.265f, 0.039f),
        };

        // ----------------------------------------------------------------- the human pose catalogs

        struct ArmPose
        {
            public string Name;
            public Vector3 Touch;   // the world point the hand is trying to touch
            public bool Lowish;     // a pose where a natural elbow clearly hangs down (swivel is pinned)
        }

        // What a person does with their hands all session. Landmark poses (chin, opposite shoulder,
        // overhead, lower back, knee) move with the BODY, so their reach ratio and direction differ per
        // profile -- e.g. the opposite shoulder is a folded 0.52 reach on TallLanky but beyond-cap on
        // Stocky and Chibi, whose arms are short relative to their shoulder width.
        static ArmPose[] ArmPoses(in BodyProfile b)
        {
            Vector3 s = b.Shoulder;
            float h = b.Height;
            float chinY = b.ShoulderY + 0.18f * (h - b.ShoulderY);
            return new[]
            {
                new ArmPose { Name = "HandsAtSides",     Touch = s + new Vector3(0.02f, -1f, 0.06f).normalized * b.ArmLen, Lowish = true },
                new ArmPose { Name = "ControllersChest", Touch = new Vector3(b.ShoulderX * 0.5f, b.ShoulderY * 0.90f, 0.18f * h), Lowish = true },
                new ArmPose { Name = "TouchChin",        Touch = new Vector3(0.02f * h, chinY, 0.06f * h), Lowish = false },
                new ArmPose { Name = "OppositeShoulder", Touch = new Vector3(-b.ShoulderX, b.ShoulderY, 0.05f * h), Lowish = false },
                new ArmPose { Name = "ReachOverhead",    Touch = new Vector3(0.03f * h, h + 0.10f * h, 0.05f * h), Lowish = false },
                new ArmPose { Name = "ReachForward",     Touch = s + new Vector3(0.05f, -0.08f, 1f).normalized * b.ArmLen, Lowish = true },
                new ArmPose { Name = "TPoseOut",         Touch = s + new Vector3(1f, 0f, 0.03f).normalized * b.ArmLen, Lowish = false },
                new ArmPose { Name = "BehindBack",       Touch = new Vector3(b.HipX * 0.3f, b.HipY * 0.95f, -0.14f * h), Lowish = false },
                new ArmPose { Name = "LowReachKnee",     Touch = new Vector3(b.HipX + 0.04f * h, b.KneeY, 0.16f * h), Lowish = true },
            };
        }

        struct LegPose
        {
            public string Name;
            public Vector3 Ankle;   // the ankle/foot IK target
        }

        // Everyday leg use. Fraction poses exercise the thigh:shin ratio; landmark poses (a box of
        // ABSOLUTE knee-ish height, the other foot's position) couple leg:height and hip-width:leg-length,
        // so short-legged bodies climb the "same" step with a much more folded knee (Chibi solves
        // StepUpBox at 0.86 reach vs LongLegs at 0.79).
        static LegPose[] LegPoses(in BodyProfile b)
        {
            Vector3 p = b.Hip;
            float l = b.LegLen;
            float h = b.Height;
            return new[]
            {
                new LegPose { Name = "StandingSoft",    Ankle = p + new Vector3(0f, -1f, 0.02f) * l },
                new LegPose { Name = "HalfSquat",       Ankle = p + new Vector3(0f, -0.80f, 0.15f) * l },
                new LegPose { Name = "DeepSquat",       Ankle = p + new Vector3(0f, -0.58f, 0.22f) * l },
                new LegPose { Name = "Seated90",        Ankle = p + Vector3.forward * b.Thigh + Vector3.down * b.Shin },
                new LegPose { Name = "StepUpBox",       Ankle = new Vector3(b.HipX, 0.28f * h, 0.30f * h) },
                new LegPose { Name = "TrailLeg",        Ankle = p + new Vector3(0.02f, -0.85f, -0.30f) * l },
                new LegPose { Name = "CrossedStep",     Ankle = p + new Vector3(-0.30f, -0.88f, 0.10f) * l },
                new LegPose { Name = "FootToOtherFoot", Ankle = new Vector3(-b.HipX, 1.1f * (b.HipY - l), 0.06f * h) },
                new LegPose { Name = "HighKick",        Ankle = p + new Vector3(0.02f, -0.35f, 0.80f) * l },
                new LegPose { Name = "InwardFold",      Ankle = p + new Vector3(-0.40f, -0.60f, 0.30f) * l },
            };
        }

        // ----------------------------------------------------------------- arm: reach + anatomy

        [Test]
        public void Arm_HumanPoses_ReachTargetAndStayAnatomical_AcrossBodyProportions()
        {
            // Every profile must land its hand on every (capped-reachable) human target and keep the
            // elbow inside the anatomical flexion range. Measured: worst hand error ~0.0000 arm-lengths,
            // elbow range [31.9, 143.6] deg over the whole matrix -- both bounds have large headroom.
            int tested = 0;
            foreach (var b in Profiles())
            {
                float tol = 0.005f * b.ArmLen;
                foreach (var pose in ArmPoses(b))
                {
                    var r = SolveArmPose(b, pose.Touch);
                    tested++;

                    AssertFinite(r.HandSolved, $"{b.Name}/{pose.Name}: solved hand");
                    AssertFinite(r.ElbowSolved, $"{b.Name}/{pose.Name}: solved elbow");
                    Assert.That(r.HandError, Is.LessThanOrEqualTo(tol),
                        $"{b.Name}/{pose.Name}: hand missed a reachable human target by {r.HandError:0.0000} m (> {tol:0.0000}).");
                    Assert.That(r.ElbowAngleDeg, Is.InRange(BasisArmSolveCore.MinElbowAngleDeg - 1f, BasisArmSolveCore.MaxElbowAngleDeg + 1f),
                        $"{b.Name}/{pose.Name}: elbow angle {r.ElbowAngleDeg:0.0} deg left the anatomical range.");
                }
            }
            Assert.That(tested, Is.EqualTo(Profiles().Length * ArmPoses(Profiles()[0]).Length), "pose matrix changed; update the calibration script too.");
        }

        [Test]
        public void Arm_ElbowHangsNatural_OnLowHumanPoses_AcrossBodyProportions()
        {
            // On the poses a person holds for hours (hands at sides, controllers at chest, forward
            // reach, reaching down), a natural elbow hangs near straight-down on EVERY body -- a chibi
            // or a long-forearm rig must not wing its elbows where an average adult doesn't. Swivel is
            // the signed elbow angle around the shoulder->hand axis from straight-down. Measured worst
            // over the matrix is 18.2 deg (HandsAtSides), so 60 is a 3x regression guard.
            foreach (var b in Profiles())
            {
                foreach (var pose in ArmPoses(b))
                {
                    if (!pose.Lowish) continue;
                    var r = SolveArmPose(b, pose.Touch);
                    float swivel = Swivel(b.Shoulder, r.HandSolved, r.ElbowSolved);
                    if (float.IsNaN(swivel)) continue;
                    Assert.That(Mathf.Abs(swivel), Is.LessThan(60f),
                        $"{b.Name}/{pose.Name}: elbow swivel {swivel:0.0} deg is far from the natural hang -- this body proportion wings the elbow.");
                }
            }
        }

        [Test]
        public void Arm_BeyondReachInputs_FallShortCleanly_AcrossBodyProportions()
        {
            // THE everyday proportion mismatch: the player's real hand goes where the avatar's shorter
            // arm cannot follow (short-armed avatars get these inputs constantly). The solver must
            // straighten the arm toward the target and fall short by exactly (distance - armLen) --
            // never NaN, never hyperextend, never orbit off-direction. Measured: shortfall deviation
            // 0.000000 arm-lengths, elbow exactly 180 deg.
            int tested = 0;
            foreach (var b in Profiles())
            {
                foreach (var pose in ArmPoses(b))
                {
                    float dist = (pose.Touch - b.Shoulder).magnitude;
                    if (dist <= b.ArmLen * 1.0001f) continue; // only genuinely-unreachable raw landmarks
                    var r = SolveArmUncapped(b, pose.Touch);
                    tested++;

                    AssertFinite(r.HandSolved, $"{b.Name}/{pose.Name}: beyond-reach solved hand");
                    float expected = dist - b.ArmLen;
                    Assert.That(Mathf.Abs(r.HandError - expected), Is.LessThanOrEqualTo(0.005f * b.ArmLen),
                        $"{b.Name}/{pose.Name}: beyond-reach input should fall short by {expected:0.000} m, hand error was {r.HandError:0.000} m -- the arm is not extending straight toward it.");
                    Assert.That(r.ElbowAngleDeg, Is.LessThanOrEqualTo(BasisArmSolveCore.MaxElbowAngleDeg + 0.5f),
                        $"{b.Name}/{pose.Name}: beyond-reach elbow angle {r.ElbowAngleDeg:0.0} deg -- hyperextension.");
                }

                foreach (var pose in LegPoses(b))
                {
                    float dist = (pose.Ankle - b.Hip).magnitude;
                    if (dist <= b.LegLen * 1.0001f) continue;
                    Vector3 hint = b.Hip + KneeHintDir * (0.55f * b.LegLen);
                    var r = SolveLegCore(b.Hip, b.RestKnee, b.RestAnkle, pose.Ankle, hint, 1f);
                    tested++;

                    AssertFinite(r.FootSolved, $"{b.Name}/{pose.Name}: beyond-reach solved foot");
                    float expected = dist - b.LegLen;
                    Assert.That(Mathf.Abs(r.FootError - expected), Is.LessThanOrEqualTo(0.005f * b.LegLen),
                        $"{b.Name}/{pose.Name}: beyond-reach input should fall short by {expected:0.000} m, foot error was {r.FootError:0.000} m -- the leg is not extending straight toward it.");
                }
            }
            Assert.That(tested, Is.GreaterThanOrEqualTo(20), $"only {tested} beyond-reach inputs exercised; the landmark poses no longer overshoot short limbs.");
        }

        // ----------------------------------------------------------------- leg: reach + anatomy

        [Test]
        public void Leg_HumanPoses_ReachTargetAndStayAnatomical_AcrossBodyProportions()
        {
            // Squat, sit, step and cross on every body, with and without a knee tracker hint. The foot
            // must land on the target (the weight-scaled leg hint is not perfectly reach-preserving;
            // measured worst 0.0013 leg-lengths, so 0.005 is a 4x guard) and the knee must respect the
            // anatomical flexion clamp (measured min 75.5 deg over the matrix -- deep squats, nowhere
            // near the 20 deg fold limit).
            int tested = 0;
            foreach (var b in Profiles())
            {
                float tol = 0.005f * b.LegLen;
                foreach (var pose in LegPoses(b))
                {
                    foreach (bool hintOn in new[] { true, false })
                    {
                        var r = SolveLegPose(b, pose.Ankle, hintOn);
                        tested++;

                        AssertFinite(r.FootSolved, $"{b.Name}/{pose.Name}/hint={hintOn}: solved foot");
                        AssertFinite(r.KneeSolved, $"{b.Name}/{pose.Name}/hint={hintOn}: solved knee");
                        Assert.That(r.FootError, Is.LessThanOrEqualTo(tol),
                            $"{b.Name}/{pose.Name}/hint={hintOn}: foot missed a reachable human target by {r.FootError:0.0000} m (> {tol:0.0000}).");
                        Assert.That(r.KneeAngleDeg, Is.GreaterThanOrEqualTo(BasisLegSolveCore.MinKneeInteriorDeg - 1f),
                            $"{b.Name}/{pose.Name}/hint={hintOn}: knee folded to {r.KneeAngleDeg:0.0} deg, past the anatomical limit.");
                    }
                }
            }
            Assert.That(tested, Is.EqualTo(Profiles().Length * LegPoses(Profiles()[0]).Length * 2), "pose matrix changed; update the calibration script too.");
        }

        [Test]
        public void Leg_KneeNeverInverts_OnHumanPoses_AcrossBodyProportions()
        {
            // The knee's off-axis direction must keep a FORWARD component on every human pose and every
            // body -- a knee drifting behind the hip->ankle line is the backward-bending-leg artifact
            // (see BasisLegInversionSweep). This includes the crossing/inward poses, where a badly
            // conditioned pole could tip the knee backward on wide-hipped or short-legged bodies.
            // Measured min forward fraction over the matrix is +0.31 (HighKick, where the knee
            // legitimately points up-forward), comfortably above 0.
            foreach (var b in Profiles())
            {
                foreach (var pose in LegPoses(b))
                {
                    foreach (bool hintOn in new[] { true, false })
                    {
                        var r = SolveLegPose(b, pose.Ankle, hintOn);
                        float fwd = KneeForwardFraction(b.Hip, r.FootSolved, r.KneeSolved);
                        if (float.IsNaN(fwd)) continue;
                        Assert.That(fwd, Is.GreaterThan(0f),
                            $"{b.Name}/{pose.Name}/hint={hintOn}: knee forward fraction {fwd:+0.00;-0.00} -- the knee is bending sideways-to-backward on this body.");
                    }
                }
            }
        }

        // ----------------------------------------------------------------- proportion invariants

        [Test]
        public void Solve_IsScaleInvariant_FromChibiToGiant()
        {
            // A geometrically-similar body must solve to IDENTICAL angles at any absolute scale: every
            // internal constant of the cores is either dimensionless or scaled by limb length, and this
            // pins that property. A future absolute-metre constant (fade window, epsilon, clearance)
            // passes every fixed-size test and breaks ONLY here. Measured deviation is 0.000000 deg in
            // float64; 0.25 deg absorbs float32 noise.
            var baseline = Profiles()[0]; // AverageAdult, 1.75 m
            foreach (float scale in new[] { 0.343f, 1.371f }) // 0.60 m and 2.40 m
            {
                var scaled = BodyProfile.Make("Scaled", baseline.Height * scale,
                    0.818f, 0.105f, 0.050f, 0.160f, 0.1486f, 0.240f, 0.240f, 0.039f);

                var basePoses = ArmPoses(baseline);
                var scaledPoses = ArmPoses(scaled);
                for (int i = 0; i < basePoses.Length; i++)
                {
                    var r0 = SolveArmPose(baseline, basePoses[i].Touch);
                    var r1 = SolveArmPose(scaled, scaledPoses[i].Touch);
                    float s0 = Swivel(baseline.Shoulder, r0.HandSolved, r0.ElbowSolved);
                    float s1 = Swivel(scaled.Shoulder, r1.HandSolved, r1.ElbowSolved);
                    Assert.That(Mathf.Abs(r0.ElbowAngleDeg - r1.ElbowAngleDeg), Is.LessThan(0.25f),
                        $"{basePoses[i].Name} @ x{scale}: elbow angle changed {r0.ElbowAngleDeg:0.00} -> {r1.ElbowAngleDeg:0.00} with pure scale -- an absolute-length constant is in the solve path.");
                    if (!float.IsNaN(s0) && !float.IsNaN(s1))
                        Assert.That(Mathf.Abs(Mathf.DeltaAngle(s0, s1)), Is.LessThan(0.25f),
                            $"{basePoses[i].Name} @ x{scale}: elbow swivel changed {s0:0.00} -> {s1:0.00} with pure scale.");
                }

                var baseLegs = LegPoses(baseline);
                var scaledLegs = LegPoses(scaled);
                for (int i = 0; i < baseLegs.Length; i++)
                {
                    foreach (bool hintOn in new[] { true, false })
                    {
                        var r0 = SolveLegPose(baseline, baseLegs[i].Ankle, hintOn);
                        var r1 = SolveLegPose(scaled, scaledLegs[i].Ankle, hintOn);
                        Assert.That(Mathf.Abs(r0.KneeAngleDeg - r1.KneeAngleDeg), Is.LessThan(0.25f),
                            $"{baseLegs[i].Name}/hint={hintOn} @ x{scale}: knee angle changed {r0.KneeAngleDeg:0.00} -> {r1.KneeAngleDeg:0.00} with pure scale.");
                    }
                }
            }
        }

        [Test]
        public void Solve_IsMirrorSymmetric_AcrossBodyProportions()
        {
            // Solving the LEFT limb through mirrored inputs (mirrored body, target and hint -- exactly
            // what the rig feeds it) must give the mirrored joint on every body, or one side of an
            // avatar poses differently from the other. The cores build everything from cross/dot
            // products of the inputs, so this holds to float exactness; measured deviation 0.0000000
            // limb-lengths, and 1e-3 is pure headroom.
            foreach (var b in Profiles())
            {
                foreach (var pose in ArmPoses(b))
                {
                    var right = SolveArmPose(b, pose.Touch);
                    var left = SolveArmPoseMirrored(b, pose.Touch);
                    float dev = (right.ElbowSolved - MirrorX(left.ElbowSolved)).magnitude / b.ArmLen;
                    Assert.That(dev, Is.LessThan(1e-3f),
                        $"{b.Name}/{pose.Name}: left elbow is {dev:0.00000} arm-lengths off the mirrored right elbow -- the solve is chirality-dependent.");
                }
                foreach (var pose in LegPoses(b))
                {
                    foreach (bool hintOn in new[] { true, false })
                    {
                        var right = SolveLegPose(b, pose.Ankle, hintOn);
                        var left = SolveLegPoseMirrored(b, pose.Ankle, hintOn);
                        float dev = (right.KneeSolved - MirrorX(left.KneeSolved)).magnitude / b.LegLen;
                        Assert.That(dev, Is.LessThan(1e-3f),
                            $"{b.Name}/{pose.Name}/hint={hintOn}: left knee is {dev:0.00000} leg-lengths off the mirrored right knee.");
                    }
                }
            }
        }

        [Test]
        public void Solve_StaysContinuous_AsLimbProportionsMorph()
        {
            // Sweep the upper:lower split from 35:65 to 65:35 (beyond anything a sane rig ships) while
            // holding each human pose fixed. The solved pole must move SMOOTHLY with the ratio: a jump
            // between adjacent steps is a proportion-triggered pole flip -- an avatar that looks fine
            // until someone uploads the one rig with a long forearm. Measured worst adjacent-step jump:
            // arm 0.00 deg, leg 0.88 deg (the near-extension bend-normal blend), so 8 deg separates
            // continuous drift from a flip.
            const int steps = 60;
            var baseBody = Profiles()[0];
            int armPoseCount = ArmPoses(baseBody).Length;
            int legPoseCount = LegPoses(baseBody).Length;

            for (int p = 0; p < armPoseCount; p++)
            {
                float prev = float.NaN;
                for (int s = 0; s <= steps; s++)
                {
                    float f = Mathf.Lerp(0.35f, 0.65f, s / (float)steps);
                    float armF = 0.160f + 0.1486f;
                    var b = BodyProfile.Make("Morph", 1.75f, 0.818f, 0.105f, 0.050f, armF * f, armF * (1f - f), 0.240f, 0.240f, 0.039f);
                    var r = SolveArmPose(b, ArmPoses(b)[p].Touch);
                    float sw = Swivel(b.Shoulder, r.HandSolved, r.ElbowSolved);
                    if (!float.IsNaN(sw) && !float.IsNaN(prev))
                        Assert.That(Mathf.Abs(Mathf.DeltaAngle(prev, sw)), Is.LessThan(8f),
                            $"arm pose #{p}: elbow swivel jumped {Mathf.Abs(Mathf.DeltaAngle(prev, sw)):0.0} deg between adjacent upper:lower ratios near {f:0.00} -- a segment-ratio pole flip.");
                    prev = sw;
                }
            }

            for (int p = 0; p < legPoseCount; p++)
            {
                foreach (bool hintOn in new[] { true, false })
                {
                    float prev = float.NaN;
                    for (int s = 0; s <= steps; s++)
                    {
                        float f = Mathf.Lerp(0.35f, 0.65f, s / (float)steps);
                        float legF = 0.240f + 0.240f;
                        var b = BodyProfile.Make("Morph", 1.75f, 0.818f, 0.105f, 0.050f, 0.160f, 0.1486f, legF * f, legF * (1f - f), 0.039f);
                        var r = SolveLegPose(b, LegPoses(b)[p].Ankle, hintOn);
                        float sw = Swivel(b.Hip, r.FootSolved, r.KneeSolved);
                        if (!float.IsNaN(sw) && !float.IsNaN(prev))
                            Assert.That(Mathf.Abs(Mathf.DeltaAngle(prev, sw)), Is.LessThan(8f),
                                $"leg pose #{p}/hint={hintOn}: knee swivel jumped {Mathf.Abs(Mathf.DeltaAngle(prev, sw)):0.0} deg between adjacent thigh:shin ratios near {f:0.00} -- a segment-ratio pole flip.");
                        prev = sw;
                    }
                }
            }
        }

        // ----------------------------------------------------------------- helpers (test scaffolding)

        // Clamp a "trying to touch Q" input to the human reach cap along the same direction, the way a
        // person's hand simply arrives from their own body -- unreachable landmarks become a full
        // (capped) reach TOWARD the landmark, which is exactly the input shape production sees.
        static Vector3 TowardPoint(Vector3 origin, Vector3 q, float limbLen, float cap)
        {
            Vector3 d = q - origin;
            float dist = d.magnitude;
            if (dist < 1e-9f) return origin;
            return origin + d / dist * Mathf.Min(dist, cap * limbLen);
        }

        // Production right-arm solve: lookup-derived elbow hint, stateless, no rate limit -- the same
        // drive as BasisElbowDirectionTests.SolveWithLookupHint, but for an arbitrary body.
        BasisArmSolveResult SolveArmPose(in BodyProfile b, Vector3 touch)
        {
            Vector3 target = TowardPoint(b.Shoulder, touch, b.ArmLen, ArmReachCap);
            return SolveArmAt(b, target);
        }

        BasisArmSolveResult SolveArmUncapped(in BodyProfile b, Vector3 touch) => SolveArmAt(b, touch);

        BasisArmSolveResult SolveArmAt(in BodyProfile b, Vector3 target)
        {
            Vector3 bend = BasisArmBendLookup.SampleTrilinear(_table, (target - b.Shoulder) / b.ArmLen).normalized;
            Vector3 hint = b.Shoulder + 0.5f * b.ArmLen * bend;
            return SolveArmCore(b.Shoulder, b.RestElbow, b.RestHand, target, hint);
        }

        // LEFT arm: mirror the body, the pose and the lookup sample across X (the rig samples the
        // shared right-handed table in mirrored space and mirrors the bend back), then solve. Callers
        // compare against MirrorX of the right-arm result.
        BasisArmSolveResult SolveArmPoseMirrored(in BodyProfile b, Vector3 touch)
        {
            Vector3 shoulder = MirrorX(b.Shoulder);
            Vector3 restElbow = MirrorX(b.RestElbow);
            Vector3 restHand = MirrorX(b.RestHand);
            Vector3 target = TowardPoint(shoulder, MirrorX(touch), b.ArmLen, ArmReachCap);
            Vector3 bend = MirrorX(BasisArmBendLookup.SampleTrilinear(_table, MirrorX(target - shoulder) / b.ArmLen).normalized);
            Vector3 hint = shoulder + 0.5f * b.ArmLen * bend;
            return SolveArmCore(shoulder, restElbow, restHand, target, hint);
        }

        static BasisArmSolveResult SolveArmCore(Vector3 shoulder, Vector3 elbow, Vector3 hand, Vector3 target, Vector3 hint)
        {
            BasisArmSolveInput input = default;
            input.Shoulder = shoulder;
            input.Elbow = elbow;
            input.Hand = hand;
            input.RootRotation = Quaternion.identity;
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = target;
            input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint;
            input.HintWeight = true;
            input.HintIsTracker = false;
            input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up;
            input.HintMaxStepDeg = float.MaxValue;
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            return r;
        }

        // Production leg solve: forward/slightly-out knee hint at 0.55 leg-lengths (hint mode) or the
        // hips-right KneeBendPref alone (no-tracker mode) -- the two ways BasisLocalRigDriver drives it.
        static readonly Vector3 KneeHintDir = new Vector3(0.15f, -0.25f, 1f).normalized;

        BasisLegSolveResult SolveLegPose(in BodyProfile b, Vector3 ankle, bool hintOn)
        {
            Vector3 target = TowardPoint(b.Hip, ankle, b.LegLen, LegReachCap);
            Vector3 hint = b.Hip + KneeHintDir * (0.55f * b.LegLen);
            return SolveLegCore(b.Hip, b.RestKnee, b.RestAnkle, target, hint, hintOn ? 1f : 0f);
        }

        BasisLegSolveResult SolveLegPoseMirrored(in BodyProfile b, Vector3 ankle, bool hintOn)
        {
            Vector3 hip = MirrorX(b.Hip);
            Vector3 target = TowardPoint(hip, MirrorX(ankle), b.LegLen, LegReachCap);
            Vector3 hint = hip + MirrorX(KneeHintDir) * (0.55f * b.LegLen);
            return SolveLegCore(hip, MirrorX(b.RestKnee), MirrorX(b.RestAnkle), target, hint, hintOn ? 1f : 0f);
        }

        static BasisLegSolveResult SolveLegCore(Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 target, Vector3 hint, float hintWeight)
        {
            BasisLegSolveInput input = default;
            input.Root = hip;
            input.Mid = knee;
            input.Tip = ankle;
            input.RootRotation = Quaternion.identity;
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = target;
            input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint;
            input.HintWeight = hintWeight;
            input.TargetOffset = Quaternion.identity;
            input.BendNormal = HipsRight; // same un-mirrored KneeBendPref for both legs, as the rig feeds it
            BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
            return r;
        }

        // Signed pole angle around the root->tip axis, measured from straight-down (arm convention;
        // reused for the knee where only CONTINUITY of the value matters, not its zero).
        static float Swivel(Vector3 root, Vector3 tip, Vector3 mid)
        {
            Vector3 axis = tip - root;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 refVec = Vector3.ProjectOnPlane(Vector3.down, axis);
            Vector3 poleVec = Vector3.ProjectOnPlane(mid - root, axis);
            if (refVec.sqrMagnitude < 1e-8f || poleVec.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.SignedAngle(refVec, poleVec, axis);
        }

        // Forward (+Z) component of the knee's unit offset from the hip->ankle line -- the
        // BasisLegInversionSweep classifier: negative-and-large means a backward-bending knee.
        static float KneeForwardFraction(Vector3 hip, Vector3 ankle, Vector3 knee)
        {
            Vector3 axis = ankle - hip;
            if (axis.sqrMagnitude < 1e-10f) return float.NaN;
            axis.Normalize();
            Vector3 off = Vector3.ProjectOnPlane(knee - hip, axis);
            if (off.sqrMagnitude < 1e-6f) return float.NaN;
            return off.normalized.z;
        }

        static Vector3 MirrorX(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        static void AssertFinite(Vector3 v, string what)
        {
            Assert.That(!float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                     && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z),
                $"{what} is not finite: {v}.");
        }
    }
}
