using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// The cross-space conformance gate for the IK solve cores.
    ///
    /// One property covers the whole class of coordinate-space bugs:
    ///
    ///     EQUIVARIANCE:  Solve(T . inputs)  ==  T . Solve(inputs)      for any rigid T = (rotation, translation)
    ///
    /// Rotate and translate every world-space quantity a core is given -- joints, targets, hints, bind axes,
    /// PlayerUp, bend normals -- and the solved pose must come back rotated and translated by exactly the same
    /// T. Scalars the core reports (reach ratio, joint angle, penetration depth) must not move at all: they are
    /// properties of the body, not of where the body happens to be standing.
    ///
    /// A core that reaches for a WORLD constant -- Vector3.up, Vector3.forward, Vector3.down -- instead of the
    /// body/player frame it was handed cannot satisfy this, because the constant does not rotate with T. That is
    /// exactly how both shipped defects looked:
    ///   - SmoothKneeSwivel measured the knee against world forward, so a turn leaked into the knee DOF;
    ///   - the T-pose capture stored a world-axes offset that the bone sim then rotated a second time.
    /// Neither was reachable by the existing per-core gates, which all test one orientation. This one is
    /// orientation-blind by construction, so a leak has nowhere to hide.
    ///
    /// Being equivariant is also what "the avatar behaves the same wherever you stand and whichever way you
    /// face" means, formally: it is the property the whole rig is silently assumed to have.
    ///
    /// NOT transformed, on purpose: TargetOffset and the T-pose binds are LOCAL quantities (a bind rotation
    /// relative to the avatar root), so a global re-orientation of the world must leave them alone. Transforming
    /// them would test a different -- and wrong -- contract.
    /// </summary>
    public class BasisIKEquivarianceTests
    {
        const float PosTolM = 1e-3f;     // 1 mm
        const float RotTolDeg = 0.05f;
        const float ScalarTol = 1e-3f;

        /// <summary>A rigid transform T. Points rotate and translate; directions only rotate.</summary>
        readonly struct Rigid
        {
            public readonly Quaternion R;
            public readonly Vector3 T;
            public readonly string Name;

            public Rigid(Quaternion r, Vector3 t, string name) { R = r; T = t; Name = name; }

            public Vector3 Point(Vector3 p) => R * p + T;
            public Vector3 Dir(Vector3 d) => R * d;
            public Quaternion Rot(Quaternion q) => R * q;
        }

        // Identity is included deliberately: it catches a broken harness (if identity fails, the test is wrong,
        // not the core). The rest span yaw only, tilt only, and the general case.
        //
        // Deliberately NO far-from-origin case here. Distance from the world origin costs float32 PRECISION,
        // which is a different property from frame-correctness and must not be conflated with it: at 140 m the
        // float spacing is ~8 um, and differencing that to recover a 0.3 m bone leaves ~3e-5 relative error, so
        // an angle drifts ~0.002 deg for reasons that have nothing to do with the reference frame. Mixing the
        // two would force a tolerance loose enough to hide a real leak. Precision gets its own gate below,
        // which MEASURES the degradation instead of tolerating it.
        static readonly Rigid[] Transforms =
        {
            new Rigid(Quaternion.identity, Vector3.zero, "identity"),
            new Rigid(Quaternion.Euler(0f, 90f, 0f), Vector3.zero, "yaw 90"),
            new Rigid(Quaternion.Euler(0f, -137f, 0f), new Vector3(4f, 0f, -9f), "yaw -137 + move"),
            new Rigid(Quaternion.Euler(35f, 0f, 0f), Vector3.zero, "pitch 35"),
            new Rigid(Quaternion.Euler(0f, 0f, -50f), Vector3.zero, "roll -50"),
            new Rigid(Quaternion.Euler(20f, 160f, -40f), new Vector3(-6f, 3f, 11f), "general"),
        };

        static void SamePoint(Vector3 expected, Vector3 actual, Rigid t, string what)
        {
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(PosTolM),
                $"[{t.Name}] {what} is not equivariant: expected {expected}, got {actual} " +
                $"(off by {Vector3.Distance(expected, actual) * 100f:F2} cm)");
        }

        static void SameRot(Quaternion expected, Quaternion actual, Rigid t, string what)
        {
            Assert.That(Quaternion.Angle(expected, actual), Is.LessThan(RotTolDeg),
                $"[{t.Name}] {what} is not equivariant: off by {Quaternion.Angle(expected, actual):F3} deg");
        }

        static void SameScalar(float expected, float actual, Rigid t, string what)
        {
            if (float.IsNaN(expected) && float.IsNaN(actual)) return;
            Assert.That(actual, Is.EqualTo(expected).Within(ScalarTol),
                $"[{t.Name}] {what} moved with the body: it is a property of the pose, not of world placement");
        }

        // --------------------------------------------------------------------------------- arm

        static BasisArmSolveInput ArmInput()
        {
            BasisArmSolveInput i = default;
            i.Shoulder = new Vector3(0.18f, 1.40f, 0f);
            i.Elbow = new Vector3(0.45f, 1.22f, 0.04f);
            i.Hand = new Vector3(0.62f, 1.05f, 0.20f);
            i.RootRotation = Quaternion.Euler(0f, 10f, -70f);
            i.MidRotation = Quaternion.Euler(0f, 25f, -60f);
            i.TargetPosition = new Vector3(0.55f, 1.10f, 0.28f);
            i.TargetRotation = Quaternion.Euler(5f, 30f, -15f);
            i.HintPosition = new Vector3(0.40f, 1.05f, -0.10f);
            i.HintWeight = true;
            i.TargetOffset = Quaternion.Euler(0f, 90f, 0f); // LOCAL bind offset: must not be transformed
            i.PlayerUp = Vector3.up;
            i.HintMaxStepDeg = float.MaxValue;
            i.HintIsTracker = true;
            return i;
        }

        [Test]
        public void ArmSolve_IsEquivariant()
        {
            BasisArmSolveInput baseIn = ArmInput();
            BasisArmSolveCore.Solve(baseIn, out BasisArmSolveResult base_);

            foreach (Rigid t in Transforms)
            {
                BasisArmSolveInput i = baseIn;
                i.Shoulder = t.Point(baseIn.Shoulder);
                i.Elbow = t.Point(baseIn.Elbow);
                i.Hand = t.Point(baseIn.Hand);
                i.TargetPosition = t.Point(baseIn.TargetPosition);
                i.HintPosition = t.Point(baseIn.HintPosition);
                i.RootRotation = t.Rot(baseIn.RootRotation);
                i.MidRotation = t.Rot(baseIn.MidRotation);
                i.TargetRotation = t.Rot(baseIn.TargetRotation);
                i.PlayerUp = t.Dir(baseIn.PlayerUp);

                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                SamePoint(t.Point(base_.ElbowSolved), r.ElbowSolved, t, "arm ElbowSolved");
                SamePoint(t.Point(base_.HandSolved), r.HandSolved, t, "arm HandSolved");
                SameRot(t.Rot(base_.RootRotationSolved), r.RootRotationSolved, t, "arm RootRotationSolved");
                SameRot(t.Rot(base_.MidRotationSolved), r.MidRotationSolved, t, "arm MidRotationSolved");
                SameScalar(base_.ReachRatio, r.ReachRatio, t, "arm ReachRatio");
                SameScalar(base_.ElbowAngleDeg, r.ElbowAngleDeg, t, "arm ElbowAngleDeg");
                SameScalar(base_.TargetDistance, r.TargetDistance, t, "arm TargetDistance");
                SameScalar(base_.HandError, r.HandError, t, "arm HandError");
                Assert.That(r.AxisSource, Is.EqualTo(base_.AxisSource),
                    $"[{t.Name}] arm picked a different bend axis source ({base_.AxisSource} -> {r.AxisSource}); " +
                    "the pole strategy must not depend on which way the player faces");
            }
        }

        // --------------------------------------------------------------------------------- leg

        // Reach ratio ~0.87: comfortably bent. A leg at or past full extension is a KINEMATIC SINGULARITY --
        // the knee angle is pinned at the reach clamp and its conditioning collapses, so float noise alone
        // swings it. Equivariance still holds there in exact arithmetic, but the pose says nothing about the
        // reference frame, which is what this gate is for. (The first draft of this input was over-extended at
        // ratio 1.03 and failed for exactly that reason -- keep it bent.)
        static BasisLegSolveInput LegInput()
        {
            BasisLegSolveInput i = default;
            i.Root = new Vector3(0.09f, 0.92f, 0f);
            i.Mid = new Vector3(0.10f, 0.50f, 0.05f);
            i.Tip = new Vector3(0.11f, 0.08f, 0f);
            i.RootRotation = Quaternion.Euler(6f, 0f, 2f);
            i.MidRotation = Quaternion.Euler(-8f, 0f, 1f);
            i.TargetPosition = new Vector3(0.13f, 0.20f, 0.15f);
            i.TargetRotation = Quaternion.Euler(4f, 12f, 0f);
            i.HintPosition = new Vector3(0.12f, 0.52f, 0.30f);
            i.HintWeight = 1f;
            i.TargetOffset = Quaternion.identity;
            i.BendNormal = Vector3.right;
            return i;
        }

        [Test]
        public void LegSolve_IsEquivariant()
        {
            BasisLegSolveInput baseIn = LegInput();
            BasisLegSolveCore.Solve(baseIn, out BasisLegSolveResult base_);

            foreach (Rigid t in Transforms)
            {
                BasisLegSolveInput i = baseIn;
                i.Root = t.Point(baseIn.Root);
                i.Mid = t.Point(baseIn.Mid);
                i.Tip = t.Point(baseIn.Tip);
                i.TargetPosition = t.Point(baseIn.TargetPosition);
                i.HintPosition = t.Point(baseIn.HintPosition);
                i.RootRotation = t.Rot(baseIn.RootRotation);
                i.MidRotation = t.Rot(baseIn.MidRotation);
                i.TargetRotation = t.Rot(baseIn.TargetRotation);
                i.BendNormal = t.Dir(baseIn.BendNormal);

                BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);

                SamePoint(t.Point(base_.KneeSolved), r.KneeSolved, t, "leg KneeSolved");
                SamePoint(t.Point(base_.FootSolved), r.FootSolved, t, "leg FootSolved");
                SameRot(t.Rot(base_.RootRotationSolved), r.RootRotationSolved, t, "leg RootRotationSolved");
                SameRot(t.Rot(base_.MidRotationSolved), r.MidRotationSolved, t, "leg MidRotationSolved");
                SameScalar(base_.ReachRatio, r.ReachRatio, t, "leg ReachRatio");
                SameScalar(base_.KneeAngleDeg, r.KneeAngleDeg, t, "leg KneeAngleDeg");
                SameScalar(base_.FootError, r.FootError, t, "leg FootError");
                Assert.That(r.AxisSource, Is.EqualTo(base_.AxisSource),
                    $"[{t.Name}] leg picked a different bend axis source ({base_.AxisSource} -> {r.AxisSource})");
            }
        }

        // --------------------------------------------------------------------------------- crouch offset

        [Test]
        public void CrouchOffset_IsEquivariant()
        {
            BasisCrouchOffsetInput baseIn = default;
            baseIn.HeadTargetPos = new Vector3(0f, 1.35f, 0.05f);
            baseIn.HipsPos = new Vector3(0f, 0.73f, 0.05f);
            baseIn.HipsRot = Quaternion.Euler(0f, 15f, 0f);
            baseIn.PlayerUp = Vector3.up;
            baseIn.Factor = 1f;
            baseIn.RestDist = 0.62f;
            baseIn.CrouchDepth = 0.35f;       // scalars: invariant under a rigid world move
            baseIn.StandingHeadHeight = 1.70f;
            baseIn.Fade = 1f;

            BasisCrouchOffsetCore.Solve(baseIn, out BasisCrouchOffsetResult base_);
            Assert.That(base_.Applied, Is.True, "base crouch did not fire -- test would be vacuous.");

            foreach (Rigid t in Transforms)
            {
                BasisCrouchOffsetInput i = baseIn;
                i.HeadTargetPos = t.Point(baseIn.HeadTargetPos);
                i.HipsPos = t.Point(baseIn.HipsPos);
                i.HipsRot = t.Rot(baseIn.HipsRot);
                i.PlayerUp = t.Dir(baseIn.PlayerUp);

                BasisCrouchOffsetCore.Solve(i, out BasisCrouchOffsetResult r);

                SamePoint(t.Point(base_.HipsPos), r.HipsPos, t, "crouch HipsPos");
                SameScalar(base_.SetbackMeters, r.SetbackMeters, t, "crouch setback");
                SameScalar(base_.LeanDeg, r.LeanDeg, t, "crouch lean");
                Assert.That(r.Applied, Is.EqualTo(base_.Applied),
                    $"[{t.Name}] crouch engaged differently depending on world placement");
            }
        }

        // --------------------------------------------------------------------------------- hip hinge

        [Test]
        public void HipHinge_IsEquivariant()
        {
            BasisHipHingeInput baseIn = default;
            baseIn.HeadPos = new Vector3(0f, 1.30f, 0.35f); // leaning forward
            baseIn.HipsPos = new Vector3(0f, 0.95f, 0f);
            baseIn.HipsRot = Quaternion.Euler(0f, 20f, 0f);
            baseIn.PlayerUp = Vector3.up;
            baseIn.StartDeg = 30f;
            baseIn.MaxAddDeg = 15f;

            BasisHipHingeCore.Solve(baseIn, out BasisHipHingeResult base_);

            foreach (Rigid t in Transforms)
            {
                BasisHipHingeInput i = baseIn;
                i.HeadPos = t.Point(baseIn.HeadPos);
                i.HipsPos = t.Point(baseIn.HipsPos);
                i.HipsRot = t.Rot(baseIn.HipsRot);
                i.PlayerUp = t.Dir(baseIn.PlayerUp);

                BasisHipHingeCore.Solve(i, out BasisHipHingeResult r);

                SameRot(t.Rot(base_.HipsRot), r.HipsRot, t, "hip hinge HipsRot");
                SameScalar(base_.LeanDeg, r.LeanDeg, t, "hip hinge LeanDeg");
                SameScalar(base_.AddDeg, r.AddDeg, t, "hip hinge AddDeg");
                Assert.That(r.Applied, Is.EqualTo(base_.Applied),
                    $"[{t.Name}] hip hinge engaged differently depending on world placement");
            }
        }

        // --------------------------------------------------------------------------------- cervical

        /// <summary>
        /// Head pitches MUST span both sides of MaxHeadPitchDeg (80). The head-pitch clamp is a branch that only
        /// runs past that threshold, and it used to measure pitch against WORLD up (world XZ as the horizon,
        /// Cross(Vector3.up, ...) as the yaw axis) while the rest of the core used ReferenceUp -- so the core
        /// disagreed with itself the moment the torso tilted. The first draft of this gate fed a 55 deg
        /// look-down, never reached the branch, and passed while the bug was live. A gate that green-lights code
        /// it never executes is worse than no gate: exercise the clamp.
        /// </summary>
        static readonly Quaternion[] CervicalGazes =
        {
            Quaternion.Euler(0f, 0f, 0f),      // level
            Quaternion.Euler(55f, 25f, 0f),    // strong look-down, under the clamp
            Quaternion.Euler(88f, 25f, 0f),    // past the 80 deg clamp: look-down branch
            Quaternion.Euler(-88f, -40f, 0f),  // past the clamp the other way: look-up branch
        };

        [Test]
        public void Cervical_IsEquivariant()
        {
            foreach (Quaternion gaze in CervicalGazes)
            {
                CervicalEquivarianceAtGaze(gaze);
            }
        }

        static void CervicalEquivarianceAtGaze(Quaternion gaze)
        {
            BasisCervicalInput baseIn = default;
            baseIn.BaseDeg = 5f;
            baseIn.NeckShare = 0.65f;
            baseIn.MaxHeadPitchDeg = 80f;
            baseIn.ExtremeStartDeg = 50f;
            baseIn.ExtremeFullDeg = 80f;
            baseIn.ExtremeRollForwardMaxDeg = 10f;
            baseIn.ExtremeRollBackwardMaxDeg = 4f;
            baseIn.ExtremeHipsHorizontalMax = 0.025f;
            baseIn.ExtremeChestHorizontalMax = 0.04f;
            baseIn.ExtremeHipsDownMax = 0.015f;
            baseIn.ExtremeChestDownMax = 0.025f;
            baseIn.ExtremeHipsDownLookUp = 0.0005f;
            baseIn.ExtremeChestDownLookUp = 0.001f;
            baseIn.PitchGainDeg = 8f;
            baseIn.ReferenceUp = Vector3.up;
            baseIn.HeadTargetRot = gaze;
            baseIn.HasUpperChest = true;

            BasisCervicalSolveCore.Solve(baseIn, out BasisCervicalResult base_);

            foreach (Rigid t in Transforms)
            {
                BasisCervicalInput i = baseIn;
                i.ReferenceUp = t.Dir(baseIn.ReferenceUp);
                i.HeadTargetRot = t.Rot(baseIn.HeadTargetRot);

                BasisCervicalSolveCore.Solve(i, out BasisCervicalResult r);

                string at = $"gaze {gaze.eulerAngles}";
                SameRot(t.Rot(base_.HeadRotClamped), r.HeadRotClamped, t, $"cervical HeadRotClamped ({at})");
                SameScalar(base_.BhDeg, r.BhDeg, t, $"cervical BhDeg ({at})");
                SameScalar(base_.NeckDeg, r.NeckDeg, t, $"cervical NeckDeg ({at})");
                SameScalar(base_.HipsForwardAmount, r.HipsForwardAmount, t, $"cervical HipsForwardAmount ({at})");
                SameScalar(base_.ChestDownAmount, r.ChestDownAmount, t, $"cervical ChestDownAmount ({at})");
                SameScalar(base_.HeadPitchClampedDeg, r.HeadPitchClampedDeg, t, $"cervical HeadPitchClampedDeg ({at})");
                Assert.That(r.HasExtreme, Is.EqualTo(base_.HasExtreme),
                    $"[{t.Name}] cervical extreme-look engaged differently depending on world placement ({at})");
            }
        }

        /// <summary>
        /// Reproduces the old world-framed pitch clamp and asserts it violates equivariance under a torso tilt,
        /// so the defect is measured rather than remembered. Yaw alone does NOT expose it (world up lies on the
        /// yaw axis), which is why it survived: you have to bend over.
        /// </summary>
        [Test]
        public void Legacy_CervicalPitchClamp_BreaksUnderTorsoTilt_WorldUpReference()
        {
            Quaternion gaze = Quaternion.Euler(88f, 25f, 0f); // past the 80 deg clamp

            float basePitch = LegacyWorldFramedPitchDeg(gaze);
            float worst = 0f;
            foreach (Rigid t in Transforms)
            {
                float measured = LegacyWorldFramedPitchDeg(t.Rot(gaze));
                worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(basePitch, measured)));
            }

            Assert.That(worst, Is.GreaterThan(20f),
                $"the legacy world-framed head-pitch clamp is expected to break under a torso tilt (worst {worst:F1} deg); " +
                "if it no longer does, the defect model is wrong");
        }

        /// <summary>The pitch measurement exactly as BasisCervicalSolveCore computed it before the fix.</summary>
        static float LegacyWorldFramedPitchDeg(Quaternion headRot)
        {
            Vector3 hf = headRot * Vector3.forward;
            float horizMag = Mathf.Sqrt(hf.x * hf.x + hf.z * hf.z);
            return (horizMag > 1e-6f) ? Mathf.Atan2(-hf.y, horizMag) * Mathf.Rad2Deg : (hf.y < 0f ? 90f : -90f);
        }

        // --------------------------------------------------------------------------------- twist

        [Test]
        public void TwistSolve_IsEquivariant()
        {
            BasisTwistSolveInput baseIn = default;
            baseIn.ParentRotation = Quaternion.Euler(10f, 40f, -75f);
            baseIn.ChildRotation = Quaternion.Euler(10f, 40f, -20f); // rolled about the bone axis
            baseIn.ParentToChild = new Vector3(0.26f, -0.05f, 0f);
            baseIn.Fraction = 0.5f;

            BasisTwistSolveCore.Solve(baseIn, out BasisTwistSolveResult base_);

            foreach (Rigid t in Transforms)
            {
                BasisTwistSolveInput i = baseIn;
                i.ParentRotation = t.Rot(baseIn.ParentRotation);
                i.ChildRotation = t.Rot(baseIn.ChildRotation);
                i.ParentToChild = t.Dir(baseIn.ParentToChild);

                BasisTwistSolveCore.Solve(i, out BasisTwistSolveResult r);

                Assert.That(r.Apply, Is.EqualTo(base_.Apply), $"[{t.Name}] twist Apply flipped");
                if (!base_.Apply) continue;
                SameRot(t.Rot(base_.TwistWorldRotation), r.TwistWorldRotation, t, "twist TwistWorldRotation");
                SameScalar(base_.TwistAngleDeg, r.TwistAngleDeg, t, "twist TwistAngleDeg");
            }
        }

        // --------------------------------------------------------------------------------- elbow protect

        [Test]
        public void ElbowProtect_IsEquivariant()
        {
            BasisElbowProtectInput baseIn = default;
            // Hand pulled across the chest: the classic case that drives the elbow into the torso.
            baseIn.Shoulder = new Vector3(0.18f, 1.40f, 0f);
            baseIn.Elbow = new Vector3(0.05f, 1.25f, -0.02f);
            baseIn.Hand = new Vector3(-0.15f, 1.30f, 0.12f);
            baseIn.HipsPos = new Vector3(0f, 0.95f, 0f);
            baseIn.SpinePos = new Vector3(0f, 1.10f, 0f);
            baseIn.ChestPos = new Vector3(0f, 1.28f, 0f);
            baseIn.NeckPos = new Vector3(0f, 1.50f, 0f);
            baseIn.HasHips = true;
            baseIn.HasSpine = true;
            baseIn.ChestRadiusBase = 0.07f;
            baseIn.CollisionSkin = 0.05f;
            baseIn.HandRadius = 0.01f;
            baseIn.HandSkin = 0.03f;
            baseIn.PlayerUp = Vector3.up;

            BasisElbowProtectCore.Solve(baseIn, out BasisElbowProtectResult base_);

            foreach (Rigid t in Transforms)
            {
                BasisElbowProtectInput i = baseIn;
                i.Shoulder = t.Point(baseIn.Shoulder);
                i.Elbow = t.Point(baseIn.Elbow);
                i.Hand = t.Point(baseIn.Hand);
                i.HipsPos = t.Point(baseIn.HipsPos);
                i.SpinePos = t.Point(baseIn.SpinePos);
                i.ChestPos = t.Point(baseIn.ChestPos);
                i.NeckPos = t.Point(baseIn.NeckPos);
                i.PlayerUp = t.Dir(baseIn.PlayerUp);

                BasisElbowProtectCore.Solve(i, out BasisElbowProtectResult r);

                Assert.That(r.Engaged, Is.EqualTo(base_.Engaged),
                    $"[{t.Name}] elbow protect engaged differently depending on world placement -- " +
                    "torso collision must not care which way the player faces");
                SamePoint(t.Point(base_.DesiredElbow), r.DesiredElbow, t, "elbow protect DesiredElbow");
                SamePoint(t.Point(base_.ElbowCenter), r.ElbowCenter, t, "elbow protect ElbowCenter");
                SameScalar(base_.WorstPenetration, r.WorstPenetration, t, "elbow protect WorstPenetration");
                SameScalar(base_.SwingAngleDeg, r.SwingAngleDeg, t, "elbow protect SwingAngleDeg");
                SameScalar(base_.ResidualClearance, r.ResidualClearance, t, "elbow protect ResidualClearance");
            }
        }

        // --------------------------------------------------------------------------------- swivel smoother

        [Test]
        public void SwivelSmoother_IsEquivariant()
        {
            BasisSwivelSmootherInput baseIn = default;
            baseIn.Root = new Vector3(0.09f, 0.92f, 0f);
            baseIn.Mid = new Vector3(0.15f, 0.47f, 0.07f);
            baseIn.Tip = new Vector3(0.09f, 0.02f, 0f);
            baseIn.BodyRotation = Quaternion.Euler(0f, 25f, 0f);
            baseIn.ReferenceLocal = Vector3.forward;
            baseIn.FallbackLocal = Vector3.right;
            baseIn.Dt = 1f / 90f;
            baseIn.MinCutoffHz = 1.5f;
            baseIn.Beta = 0.20f;
            baseIn.DerivCutoffHz = 1f;
            baseIn.State = new BasisSwivelFilterState { Raw = 12f, Vel = 3f, Smooth = 10f };
            baseIn.Seeded = true;

            BasisSwivelSmootherCore.Solve(baseIn, out BasisSwivelSmootherResult base_);
            Assert.That(base_.Valid, Is.True, "harness bug: the baseline swivel solve must be valid");

            foreach (Rigid t in Transforms)
            {
                BasisSwivelSmootherInput i = baseIn;
                i.Root = t.Point(baseIn.Root);
                i.Mid = t.Point(baseIn.Mid);
                i.Tip = t.Point(baseIn.Tip);
                i.BodyRotation = t.Rot(baseIn.BodyRotation);
                // ReferenceLocal/FallbackLocal are BODY-local by construction: they must NOT be transformed.

                BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);

                Assert.That(r.Valid, Is.EqualTo(base_.Valid), $"[{t.Name}] swivel smoother validity flipped");
                SamePoint(t.Point(base_.DesiredMid), r.DesiredMid, t, "swivel DesiredMid");
                SameScalar(base_.RawSwivelDeg, r.RawSwivelDeg, t, "swivel RawSwivelDeg");
                SameScalar(base_.SmoothSwivelDeg, r.SmoothSwivelDeg, t, "swivel SmoothSwivelDeg");
            }
        }

        // --------------------------------------------------------------------------------- precision vs distance

        /// <summary>
        /// The OTHER half of "does the avatar behave the same wherever you stand": float32 precision.
        ///
        /// Equivariance above is a statement about the reference FRAME and is gated tightly. This gates
        /// DISTANCE, which is a genuinely different failure: a solve far from the world origin differences two
        /// large coordinates to recover a 30 cm bone, and the mantissa runs out. Nothing is wrong with the
        /// maths -- there is simply less of it left. Worth measuring rather than tolerating, because it is the
        /// number that says how large a world can get before the IK visibly degrades.
        ///
        /// Pure translation, so the solved pose must translate with it and every scalar must hold still.
        /// The measured curve is written to the test log.
        /// </summary>
        [Test]
        public void ArmSolve_DegradesGracefully_FarFromWorldOrigin()
        {
            BasisArmSolveInput baseIn = ArmInput();
            BasisArmSolveCore.Solve(baseIn, out BasisArmSolveResult base_);

            var log = new System.Text.StringBuilder("arm IK precision vs distance from world origin:\n");
            float worstPosMmAtOneKm = 0f;
            float worstAngDegAtOneKm = 0f;

            foreach (float d in new[] { 0f, 100f, 1_000f, 10_000f })
            {
                Vector3 t = new Vector3(d * 0.70710678f, 0f, d * 0.70710678f);

                BasisArmSolveInput i = baseIn;
                i.Shoulder = baseIn.Shoulder + t;
                i.Elbow = baseIn.Elbow + t;
                i.Hand = baseIn.Hand + t;
                i.TargetPosition = baseIn.TargetPosition + t;
                i.HintPosition = baseIn.HintPosition + t;
                // Rotations and PlayerUp are unaffected by a translation.

                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                float posMm = Vector3.Distance(r.ElbowSolved - t, base_.ElbowSolved) * 1000f;
                float angDeg = Mathf.Abs(r.ElbowAngleDeg - base_.ElbowAngleDeg);
                log.AppendLine($"  {d,7:F0} m -> elbow {posMm,9:F5} mm, elbow angle {angDeg,9:F5} deg");

                if (d <= 1_000f)
                {
                    worstPosMmAtOneKm = Mathf.Max(worstPosMmAtOneKm, posMm);
                    worstAngDegAtOneKm = Mathf.Max(worstAngDegAtOneKm, angDeg);
                }
            }

            TestContext.WriteLine(log.ToString());

            // A millimetre of elbow drift is far below anything visible; if this ever trips, the solve has
            // started differencing large coordinates somewhere it did not before.
            Assert.That(worstPosMmAtOneKm, Is.LessThan(1f),
                $"elbow drifted {worstPosMmAtOneKm:F3} mm within 1 km of the origin\n{log}");
            Assert.That(worstAngDegAtOneKm, Is.LessThan(0.5f),
                $"elbow angle drifted {worstAngDegAtOneKm:F3} deg within 1 km of the origin\n{log}");
        }
    }
}
