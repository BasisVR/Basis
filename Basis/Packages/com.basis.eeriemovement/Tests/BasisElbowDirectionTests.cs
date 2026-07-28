using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Elbow-direction regression tests for the arm IK sweep system.
    ///
    /// These exercise the pure, stream-free elbow math directly -- <see cref="BasisArmBendLookup"/>
    /// (the chest-relative bend-direction lookup) and <see cref="BasisArmSolveCore"/> (the two-bone
    /// solve, a fork of Unity's SolveTwoBoneIK with the Basis pole-flip / rate-limit handling). They
    /// guard the failure modes the elbow used to have:
    ///
    ///   1. ELBOW FLIP   -- the pole snapping to the opposite side on smooth motion / tracker noise.
    ///   2. ELBOW IN FRONT -- the elbow winging up and forward instead of tucking naturally behind
    ///                        and below the hand.
    ///   3. FULL-EXTENSION FLIP -- as the arm extends out fully the elbow pole collapses onto the
    ///                        shoulder->hand axis; the elbow must ride along, not snap to the wrong side.
    ///
    /// Thresholds are intentionally looser than the values the current solver produces (each test
    /// notes the measured headroom) so they fail only on a real regression, not on float noise.
    ///
    /// This is the same geometry the offline BasisArmIKSweep CSV harness scans, asserted here so the
    /// "elbow behind the hand, never flipped" property is checked in CI instead of by eyeballing a CSV.
    /// </summary>
    public class BasisElbowDirectionTests
    {
        // Adult-ish arm, matching BasisArmIKSweepConfig.Default().
        const float Upper = 0.28f;
        const float Lower = 0.26f;
        const float ArmLen = Upper + Lower;

        static readonly Vector3 Shoulder = Vector3.zero;

        // Rest pose: elbow hangs down and slightly forward/out, forearm points forward (matches the sweep).
        static Vector3 RestElbow => Shoulder + new Vector3(0.15f, -0.95f, 0.27f).normalized * Upper;
        static Vector3 RestHand => RestElbow + new Vector3(0f, -0.30f, 0.95f).normalized * Lower;

        // Live solver rate limit: 540 deg/s. At 90 Hz that is 6 deg/frame.
        const float Dt = 1f / 90f;
        const float RateDegPerFrame = 540f * Dt;

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

        // ----------------------------------------------------------------- lookup-table invariants

        [Test]
        public void BendTable_NeverPointsUp_SoElbowDoesNotChickenWing()
        {
            var table = BasisArmBendLookup.GenerateDefaultTable();
            float maxY = float.MinValue;
            foreach (var bend in table)
                if (bend.y > maxY) maxY = bend.y;

            // Every entry's Y must be <= 0: an up-pointing bend would wing the elbow above the
            // shoulder-hand line (chicken-wing). Measured max is about -0.21, far below the bound.
            Assert.That(maxY, Is.LessThanOrEqualTo(1e-3f),
                $"At least one bend direction points upward (max Y = {maxY:0.000}); the elbow would chicken-wing.");
        }

        [Test]
        public void BendTable_NeverPointsForward_SoElbowStaysBehindHand()
        {
            var table = BasisArmBendLookup.GenerateDefaultTable();
            float maxZ = float.MinValue;
            foreach (var bend in table)
                if (bend.z > maxZ) maxZ = bend.z;

            // Every entry's Z must be <= 0 (Z+ is forward). A forward-pointing bend is exactly the
            // "elbow out in front of where it should be" artifact. Measured max is about -0.42.
            Assert.That(maxZ, Is.LessThanOrEqualTo(1e-3f),
                $"At least one bend direction points forward (max Z = {maxZ:0.000}); the elbow would lead the hand.");
        }

        [Test]
        public void BendTable_EntriesAreUnitLength()
        {
            var table = BasisArmBendLookup.GenerateDefaultTable();
            Assert.That(table.Length, Is.EqualTo(BasisArmBendLookup.TotalEntries));
            float maxErr = 0f;
            foreach (var bend in table)
                maxErr = Mathf.Max(maxErr, Mathf.Abs(bend.magnitude - 1f));

            Assert.That(maxErr, Is.LessThanOrEqualTo(1e-3f),
                $"Bend directions must be normalized (worst |len-1| = {maxErr:0.000000}).");
        }

        [Test]
        public void BendTable_ForwardReachSamples_PointBackAndDown()
        {
            // Sample the trilinear lookup at a spread of "hand reaching forward" positions and confirm
            // the returned bend tucks the elbow back (Z < 0) and down (Y < 0) -- never forward or up.
            for (float fz = 0.4f; fz <= 1.0f; fz += 0.2f)
            for (float fx = -0.6f; fx <= 0.6f; fx += 0.3f)
            for (float fy = -0.4f; fy <= 0.4f; fy += 0.2f)
            {
                Vector3 bend = SampleLookup(new Vector3(fx, fy, fz));
                Assert.That(bend.z, Is.LessThan(-0.2f),
                    $"Forward reach ({fx:0.0},{fy:0.0},{fz:0.0}) gave a non-backward bend {bend}; elbow would not stay behind the hand.");
                Assert.That(bend.y, Is.LessThan(-0.1f),
                    $"Forward reach ({fx:0.0},{fy:0.0},{fz:0.0}) gave a non-down bend {bend}; elbow would rise.");
            }
        }

        [Test]
        public void BendTable_Samples_VarySmoothly_NoDiscontinuity()
        {
            // A discontinuity in the lookup is a pole flip waiting to happen. Sweep the hand across the
            // body and assert the bend direction never jumps between adjacent samples. Measured worst
            // step is about 0.24 deg, so 5 deg is a wide regression guard.
            const int steps = 200;
            Vector3 from = new Vector3(0.7f, -0.1f, 0.6f);
            Vector3 to = new Vector3(-0.7f, -0.1f, 0.6f);
            float worst = 0f;
            Vector3 prev = Vector3.zero;
            for (int s = 0; s <= steps; s++)
            {
                Vector3 bend = SampleLookup(Vector3.Lerp(from, to, s / (float)steps));
                if (s > 0) worst = Mathf.Max(worst, Vector3.Angle(prev, bend));
                prev = bend;
            }

            Assert.That(worst, Is.LessThan(5f),
                $"Bend direction jumps {worst:0.0} deg between adjacent hand positions; that discontinuity flips the elbow.");
        }

        // ----------------------------------------------------------------- natural elbow placement

        [Test]
        public void Elbow_StaysBehindHand_OnForwardReach()
        {
            foreach (var frac in ForwardReachTargets())
            {
                var r = SolveWithLookupHint(frac, out _);
                Assert.That(r.ElbowSolved.z, Is.LessThan(r.HandSolved.z + 1e-3f),
                    $"Forward reach {frac}: elbow Z {r.ElbowSolved.z:0.000} is in front of hand Z {r.HandSolved.z:0.000}.");
            }
        }

        [Test]
        public void Elbow_HangsBelowShoulder_OnForwardReach()
        {
            foreach (var frac in ForwardReachTargets())
            {
                var r = SolveWithLookupHint(frac, out _);
                Assert.That(r.ElbowSolved.y, Is.LessThan(Shoulder.y),
                    $"Forward reach {frac}: elbow Y {r.ElbowSolved.y:0.000} is above the shoulder; it should hang below.");
            }
        }

        [Test]
        public void Elbow_PoleHangsDown_OnForwardReach()
        {
            foreach (var frac in ForwardReachTargets())
            {
                var r = SolveWithLookupHint(frac, out _);
                Vector3 axis = (r.HandSolved - Shoulder).normalized;
                Vector3 pole = Vector3.ProjectOnPlane(r.ElbowSolved - Shoulder, axis).normalized;

                // The elbow pole (its direction off the shoulder-hand axis) must hang DOWN. A forward
                // reach lets the elbow lean mildly forward (reaching up-and-out tucks it slightly ahead,
                // measured up to ~0.5) which is natural -- but it must never point grossly forward (~1),
                // which is the elbow leading the hand. "Behind the hand" itself is asserted in
                // Elbow_StaysBehindHand_OnForwardReach; here we pin the down-tuck.
                Assert.That(pole.y, Is.LessThan(-0.3f),
                    $"Forward reach {frac}: elbow pole {pole} does not hang down.");
                Assert.That(pole.z, Is.LessThan(0.6f),
                    $"Forward reach {frac}: elbow pole {pole} points grossly forward; elbow is leading the hand.");
            }
        }

        [Test]
        public void Elbow_SwivelStaysNatural_OnForwardReach()
        {
            // Swivel is the signed angle of the elbow around the shoulder->hand axis, measured from
            // straight-down. A natural elbow on a forward reach hangs near straight-down; a flipped /
            // "in front" elbow swivels toward +-180 (up). Measured worst is about 34 deg.
            foreach (var frac in ForwardReachTargets())
            {
                var r = SolveWithLookupHint(frac, out _);
                float swivel = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                Assert.That(Mathf.Abs(swivel), Is.LessThan(75f),
                    $"Forward reach {frac}: elbow swivel {swivel:0.0} deg is far from natural (hanging) -- elbow has winged up/forward.");
            }
        }

        [Test]
        public void Elbow_DoesNotFlipUp_OnExtendedForwardReaches()
        {
            // Guards the lookup-table fix: up-reaches bend down-and-OUT so the elbow pole stays
            // perpendicular to the arm. Across a dense sweep of EXTENDED forward, non-overhead reaches the
            // elbow must hang down/back -- never wing hard up (|swivel|>120). Folded reaches (hand near the
            // body, reach<=0.55) can legitimately raise the elbow, so they are excluded.
            int tested = 0, flips = 0;
            float worstSwivel = 0f;
            Vector3 worstFrac = Vector3.zero;
            for (float fx = -0.7f; fx <= 0.9f; fx += 0.1f)
            for (float fy = -0.8f; fy <= 0.39f; fy += 0.1f)   // non-overhead
            for (float fz = 0.25f; fz <= 0.95f; fz += 0.1f)   // forward
            {
                Vector3 fracDir = new Vector3(fx, fy, fz);
                float reachR = fracDir.magnitude;
                if (reachR <= 0.55f || reachR > 0.98f) continue; // extended & reachable
                var r = SolveWithLookupHint(Frac(fx, fy, fz), out _);
                float swivel = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                if (float.IsNaN(swivel)) continue;
                tested++;
                if (Mathf.Abs(swivel) > 120f)
                {
                    flips++;
                    if (Mathf.Abs(swivel) > worstSwivel) { worstSwivel = Mathf.Abs(swivel); worstFrac = fracDir; }
                }
            }

            Assert.That(tested, Is.GreaterThan(500), $"only {tested} extended forward reaches exercised; expected a dense sweep.");
            Assert.That(flips, Is.EqualTo(0),
                $"{flips}/{tested} extended forward reaches flip the elbow hard up (worst |swivel| {worstSwivel:0.0}° at {worstFrac}).");
        }

        [Test]
        public void Solve_PreservesHandReach_ForReachableTargets()
        {
            // The pole-flip handling must never move the hand off target: reach is primary, the elbow
            // yields. Sweep a coarse reachable workspace and assert the hand still lands on the target.
            float tol = 0.005f * ArmLen;
            int tested = 0;
            // Dense workspace sweep (step 0.1 arm-length ~ 5.4 cm) -- thousands of reach targets.
            for (float fx = -0.9f; fx <= 0.95f; fx += 0.1f)
            for (float fy = -1.0f; fy <= 0.8f; fy += 0.1f)
            for (float fz = -0.6f; fz <= 0.95f; fz += 0.1f)
            {
                Vector3 fracDir = new Vector3(fx, fy, fz);
                float reachR = fracDir.magnitude; // |target - shoulder| / armLen
                // Skip unreachable, and too-close targets that the anatomical flexion limit holds short of
                // (a folded arm cannot reach within ~0.2 arm-lengths of the shoulder).
                if (reachR > 0.98f || reachR < 0.25f) continue;
                var r = SolveWithLookupHint(Frac(fx, fy, fz), out _);
                Assert.That(r.HandError, Is.LessThanOrEqualTo(tol),
                    $"Reachable target {fracDir}: hand missed by {r.HandError:0.0000} m (> {tol:0.0000}); the elbow swivel disturbed reach.");
                tested++;
            }
            Assert.That(tested, Is.GreaterThan(1000),
                $"reach grid only exercised {tested} targets; expected a dense sweep.");
        }

        // ----------------------------------------------------------------- anatomical flexion limit

        [Test]
        public void Elbow_FlexionStaysWithinAnatomicalRange()
        {
            // The solved elbow angle (between upper arm and forearm) must never leave the human range:
            // never hyperextend past straight (max), nor fold the forearm past the min into the upper arm.
            const float eps = 1f;
            int tested = 0;
            float minAngle = 999f, maxAngle = -999f;
            for (float fx = -1.1f; fx <= 1.15f; fx += 0.1f)
            for (float fy = -1.1f; fy <= 0.9f; fy += 0.1f)
            for (float fz = -0.8f; fz <= 1.15f; fz += 0.1f)
            {
                Vector3 fracDir = new Vector3(fx, fy, fz);
                if (fracDir.magnitude > 1.3f) continue; // far beyond reach; still solved, just skip the dump
                var r = SolveWithLookupHint(Frac(fx, fy, fz), out _);
                tested++;
                minAngle = Mathf.Min(minAngle, r.ElbowAngleDeg);
                maxAngle = Mathf.Max(maxAngle, r.ElbowAngleDeg);
                Assert.That(r.ElbowAngleDeg, Is.InRange(BasisArmSolveCore.MinElbowAngleDeg - eps, BasisArmSolveCore.MaxElbowAngleDeg + eps),
                    $"Target {fracDir}: elbow angle {r.ElbowAngleDeg:0.0}° is outside the anatomical range [{BasisArmSolveCore.MinElbowAngleDeg}, {BasisArmSolveCore.MaxElbowAngleDeg}].");
            }
            Assert.That(tested, Is.GreaterThan(1000), $"only {tested} targets exercised.");
        }

        [Test]
        public void Elbow_DoesNotOverflex_OnTooCloseTarget()
        {
            // A target far inside the min-flex distance must NOT fold the elbow past the limit. The joint
            // holds at min flex and the hand falls short (pushed out along the target direction).
            Vector3 target = Shoulder + new Vector3(0.1f, -0.3f, 0.5f).normalized * 0.07f; // 7 cm: well inside ~11 cm min-flex
            var r = SolveWithLookupHint(target, out _);

            Assert.That(r.ElbowAngleDeg, Is.GreaterThanOrEqualTo(BasisArmSolveCore.MinElbowAngleDeg - 1f),
                $"elbow over-flexed to {r.ElbowAngleDeg:0.0}° (< min {BasisArmSolveCore.MinElbowAngleDeg}°).");
            Assert.That(r.ElbowAngleDeg, Is.LessThan(BasisArmSolveCore.MinElbowAngleDeg + 4f),
                $"elbow should hold at min flex for a too-close target; got {r.ElbowAngleDeg:0.0}°.");
            Assert.That(r.HandError, Is.GreaterThan(0.01f),
                "hand should fall short of a too-close target (pushed out), not fold the elbow to reach it.");
        }

        [Test]
        public void Elbow_DoesNotHyperextend_AtAndBeyondFullReach()
        {
            // At or beyond full reach the elbow straightens to (at most) the anatomical max -- never backward.
            foreach (float reachFrac in new[] { 0.97f, 1.0f, 1.2f })
            {
                Vector3 target = Shoulder + new Vector3(0.1f, -0.1f, 1f).normalized * (reachFrac * ArmLen);
                var r = SolveWithLookupHint(target, out _);
                Assert.That(r.ElbowAngleDeg, Is.LessThanOrEqualTo(BasisArmSolveCore.MaxElbowAngleDeg + 1f),
                    $"reach {reachFrac}: elbow angle {r.ElbowAngleDeg:0.0}° exceeds straight ({BasisArmSolveCore.MaxElbowAngleDeg}) -- hyperextension.");
            }
        }

        // ----------------------------------------------------------------- flip resistance

        [Test]
        public void Solve_RateLimit_BoundsSwivelStepToCap()
        {
            // Isolate the swivel: target == current hand, so the only thing the solve does is rotate the
            // elbow toward the hint about the shoulder->hand axis. A hint on the wrong (up) side wants a
            // ~150 deg swing; HintMaxStepDeg must bound the per-solve change to the cap. This IS the
            // mechanism that stops a one-frame flip.
            Vector3 wrongHint = Shoulder + 0.5f * ArmLen * new Vector3(0.1f, 0.9f, -0.2f).normalized;
            float before = Swivel(Shoulder, RestHand, RestElbow);

            foreach (float cap in new[] { 2f, 5f, 10f, 20f, 45f })
            {
                var r = SolveOne(Shoulder, RestElbow, RestHand, RestHand, wrongHint, true, cap);
                float after = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                float step = Mathf.Abs(Mathf.DeltaAngle(before, after));
                Assert.That(step, Is.LessThanOrEqualTo(cap + 0.6f),
                    $"Rate cap {cap} deg exceeded: elbow swivelled {step:0.0} deg in one solve (a flip is not being rate-limited).");
                Assert.That(r.HandError, Is.LessThanOrEqualTo(1e-3f),
                    $"Rate-limited solve moved the hand ({r.HandError:0.0000} m); the swivel must preserve reach.");
            }
        }

        [Test]
        public void Solve_WithoutRateLimit_ElbowCanFlip_WhichIsWhyTheCapExists()
        {
            // Documents the failure the cap guards against: with no rate limit, a wrong-side hint
            // snaps the elbow ~150 deg in a single solve. If this ever stops flipping, the
            // rate-limit tests above are no longer meaningful and should be revisited.
            Vector3 wrongHint = Shoulder + 0.5f * ArmLen * new Vector3(0.1f, 0.9f, -0.2f).normalized;
            float before = Swivel(Shoulder, RestHand, RestElbow);
            var r = SolveOne(Shoulder, RestElbow, RestHand, RestHand, wrongHint, true, float.MaxValue);
            float step = Mathf.Abs(Mathf.DeltaAngle(before, Swivel(Shoulder, r.HandSolved, r.ElbowSolved)));

            Assert.That(step, Is.GreaterThan(120f),
                $"Expected the unclamped wrong-side hint to flip the elbow (>120 deg); got {step:0.0} deg.");
        }

        [Test]
        public void Elbow_DoesNotPop_OnSmoothAcrossSweep()
        {
            AssertSmoothSweep(Frac(0.70f, -0.20f, 0.40f), Frac(-0.70f, -0.20f, 0.40f), 160, "across");
        }

        [Test]
        public void Elbow_DoesNotPop_OnSmoothVerticalSweep()
        {
            AssertSmoothSweep(Frac(0.10f, -0.80f, 0.30f), Frac(0.10f, 0.70f, 0.30f), 160, "vertical");
        }

        [Test]
        public void Elbow_DoesNotPop_OnReachUpAcrossSweep()
        {
            AssertSmoothSweep(Frac(0.60f, -0.60f, 0.20f), Frac(-0.50f, 0.50f, 0.30f), 160, "reach-up-across");
        }

        [Test]
        public void Elbow_DoesNotPop_OnCircleSweep()
        {
            // A circular hand path in front of the body -- the classic case where a naive pole flips
            // as the hand crosses the vertical. Build the circle and drive it temporally.
            const int steps = 200;
            Vector3 center = Frac(0f, -0.10f, 0.40f);
            float radius = 0.50f * ArmLen;
            var pts = new Vector3[steps];
            for (int s = 0; s < steps; s++)
            {
                float a = (s / (float)steps) * Mathf.PI * 2f;
                pts[s] = center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius;
            }
            AssertSmoothSweep(pts, "circle");
        }

        // ----------------------------------------------------------------- full extension (pole collapse)

        [Test]
        public void Elbow_DoesNotFlip_OnFullExtensionAcrossSweep()
        {
            // Arm extended to ~0.95 reach (nearly straight), hand swung horizontally across the front. The
            // shoulder->hand axis rotates while the elbow pole is nearly collapsed onto it -- the regime where
            // "extend the arm out fully and the elbow flips to the wrong side". The elbow must ride along.
            AssertFullExtensionArc(ExtArc(new Vector3(0.75f, -0.15f, 0.70f), new Vector3(-0.75f, -0.15f, 0.70f), 0.95f, 180), "ext-across");
        }

        [Test]
        public void Elbow_DoesNotFlip_OnFullExtensionVerticalSweep()
        {
            // Extended ~0.95, hand swung from low-forward up to (non-overhead) high-forward, passing through
            // straight-out where the pole is most ill-defined.
            AssertFullExtensionArc(ExtArc(new Vector3(0.10f, -0.65f, 0.60f), new Vector3(0.10f, 0.35f, 0.70f), 0.95f, 180), "ext-vertical");
        }

        [Test]
        public void Elbow_DoesNotFlip_OnReachOutToFullExtensionAndBack()
        {
            // The literal "extend the arm out fully and relax" motion: push a forward/slightly-down reach from
            // 0.6 out to 0.97 (nearly straight) and back. The pole collapses toward the far end and must
            // re-form on the SAME side -- not snap across as the arm straightens then unstraightens.
            AssertFullExtensionArc(ReachOutAndBack(new Vector3(0.12f, -0.18f, 1.0f), 0.6f, 0.97f, 90), "reach-out-back");
        }

        [Test]
        public void Elbow_DoesNotFlip_OnBackwardFullStretch()
        {
            // The live arm solve is STATELESS and unclamped (BasisFullBodyIK resets the bones each frame and
            // passes HintMaxStepDeg=MaxValue), so a smooth backward hand path must give a CONTINUOUS elbow
            // swivel solved fresh each sample -- a jump between adjacent samples IS the "fully stretched
            // backward, flips rapidly" artifact. Reaching behind, the lookup bend goes near-collinear with the
            // arm; the pole-collapse stabilizer must hold the elbow on the stable down pole instead. Solve
            // each sample the way the live rig does (fresh rest pose, no rate limit).
            foreach (var arc in new[]
            {
                (a: new Vector3(0.85f, -0.20f, -0.30f), b: new Vector3(0.10f, -0.20f, -0.85f), name: "back-swing"),
                (a: new Vector3(0.45f, -0.05f, -0.80f), b: new Vector3(0.30f, -0.75f, -0.55f), name: "back-lower"),
            })
            {
                Vector3[] pts = ExtArc(arc.a, arc.b, 0.92f, 200);
                float prev = float.NaN, worst = 0f;
                foreach (var pt in pts)
                {
                    var r = SolveWithLookupHint(pt, out _); // stateless, MaxValue -- exactly the live rig
                    float sw = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                    if (!float.IsNaN(sw) && !float.IsNaN(prev))
                        worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(prev, sw)));
                    prev = sw;
                }
                // A continuous backward sweep moves the swivel a few deg per sample; a flip is a >90 deg jump.
                // 45 deg matches the harness pop gate (BasisIKTestGates.TrajMaxPopDeg) and cleanly separates them.
                Assert.That(worst, Is.LessThan(45f),
                    $"'{arc.name}' backward full-stretch: stateless elbow swivel jumps {worst:0.0} deg between adjacent samples (>45) -- the arm flips rapidly reaching behind.");
            }
        }

        [Test]
        public void Elbow_StaysContinuous_AcrossReachableWorkspace()
        {
            // The strongest pole-flip guard: sweep the hand a FULL 360 deg around the body at several
            // elevations and reaches and assert the stateless (live-model) elbow swivel never jumps. Each
            // revolution passes through forward, both sides AND fully behind, so a pole flip anywhere in the
            // reachable workspace -- not just the backward case -- trips this. Reaches stay below the genuine
            // dead-straight singularity (>0.95) and elevations off the vertical (straight up/down, where the
            // down pole is undefined).
            float worst = 0f; string where = "";
            foreach (float reach in new[] { 0.70f, 0.85f, 0.90f })
            foreach (float ev in new[] { 0.30f, 0.0f, -0.30f, -0.60f })
            {
                float j = StatelessWorstArcJump(LatitudeCircle(ev, reach, 220));
                if (j > worst) { worst = j; where = $"reach {reach:0.00}, elevation {ev:0.0}"; }
            }
            // Continuous motion moves the swivel a few deg per sample; a flip is a >90 deg jump. 45 matches the
            // harness pop gate (BasisIKTestGates.TrajMaxPopDeg); measured worst is single digits.
            Assert.That(worst, Is.LessThan(45f),
                $"stateless elbow swivel jumps {worst:0.0} deg between adjacent samples at {where} -- a pole flip in the reachable workspace.");
        }

        [Test]
        public void Elbow_DoesNotFlip_AcrossBackwardWorkspace()
        {
            // Dense backward-hemisphere lock-in for the fully-stretched-backward fix: azimuth sweeps (out to
            // the side -> straight behind) at several elevations, and elevation sweeps (level-back -> down-back)
            // at several azimuths, each at several near-full reaches. Stateless (live model); the swivel must
            // stay continuous everywhere behind the body. Tighter bound than the workspace test -- this is the
            // region we fixed (measured worst ~3-4 deg).
            float worst = 0f; string where = "";
            foreach (float reach in new[] { 0.85f, 0.90f, 0.92f })
            {
                foreach (float ev in new[] { 0.05f, -0.30f, -0.65f })
                {
                    float j = StatelessWorstArcJump(ExtArc(new Vector3(0.90f, ev, -0.30f), new Vector3(0.05f, ev, -0.95f), reach, 140));
                    if (j > worst) { worst = j; where = $"azimuth reach {reach:0.00} ev {ev:0.0}"; }
                }
                foreach (float az in new[] { 0.10f, 0.45f, 0.85f })
                {
                    float j = StatelessWorstArcJump(ExtArc(new Vector3(az, 0.05f, -0.90f), new Vector3(az, -0.85f, -0.45f), reach, 140));
                    if (j > worst) { worst = j; where = $"elevation reach {reach:0.00} az {az:0.0}"; }
                }
            }
            Assert.That(worst, Is.LessThan(25f),
                $"backward elbow swivel jumps {worst:0.0} deg between adjacent samples at {where} (>25) -- the arm flips reaching behind.");
        }

        [Test]
        public void Elbow_DoesNotFlip_AcrossForwardWorkspace()
        {
            // Forward twin of Elbow_DoesNotFlip_AcrossBackwardWorkspace: dense FRONT-hemisphere lock-in for
            // the straight-forward flip. Azimuth sweeps (out to the side -> straight in front) at several
            // elevations, and elevation sweeps (level-front -> down-front) at several azimuths, each at
            // several near-full reaches. Stateless (live model); the swivel must stay continuous everywhere
            // in front of the body, ending at straight-forward where the user reports the flip. Same tight
            // bound as the backward region (measured worst single digits there).
            float worst = 0f; string where = "";
            foreach (float reach in new[] { 0.85f, 0.90f, 0.92f })
            {
                foreach (float ev in new[] { 0.05f, -0.30f, -0.65f })
                {
                    float j = StatelessWorstArcJump(ExtArc(new Vector3(0.90f, ev, 0.30f), new Vector3(0.05f, ev, 0.95f), reach, 140));
                    if (j > worst) { worst = j; where = $"azimuth reach {reach:0.00} ev {ev:0.0}"; }
                }
                foreach (float az in new[] { 0.10f, 0.45f, 0.85f })
                {
                    float j = StatelessWorstArcJump(ExtArc(new Vector3(az, 0.05f, 0.90f), new Vector3(az, -0.85f, 0.45f), reach, 140));
                    if (j > worst) { worst = j; where = $"elevation reach {reach:0.00} az {az:0.0}"; }
                }
            }
            Assert.That(worst, Is.LessThan(25f),
                $"forward elbow swivel jumps {worst:0.0} deg between adjacent samples at {where} (>25) -- the arm flips reaching straight forward.");
        }

        [Test]
        public void Elbow_DoesNotFlip_OnForwardFullStretch()
        {
            // Forward twin of Elbow_DoesNotFlip_OnBackwardFullStretch. The live arm solve is STATELESS and
            // unclamped, so a smooth straight-out-FRONT hand path must give a CONTINUOUS elbow swivel AND a
            // continuous forearm orientation. The forearm carries the hand, so a 180 deg pole flip rolls the
            // forearm (and the hand mesh) over -- the wrist "flips" the user sees is the same event as the
            // elbow flip, so both are asserted on the same path. Solve each sample the way the live rig does
            // (fresh rest pose, no rate limit).
            foreach (var arc in new[]
            {
                (a: new Vector3(0.85f, -0.20f, 0.30f), b: new Vector3(0.10f, -0.20f, 0.85f), name: "fwd-swing"),
                (a: new Vector3(0.45f, -0.05f, 0.80f), b: new Vector3(0.30f, -0.75f, 0.55f), name: "fwd-lower"),
            })
            {
                Vector3[] pts = ExtArc(arc.a, arc.b, 0.92f, 200);
                float prevSw = float.NaN, worstSw = 0f;
                Quaternion prevMid = Quaternion.identity; bool hasMid = false; float worstRoll = 0f;
                foreach (var pt in pts)
                {
                    var r = SolveWithLookupHint(pt, out _); // stateless, MaxValue -- exactly the live rig
                    float sw = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                    if (!float.IsNaN(sw) && !float.IsNaN(prevSw))
                        worstSw = Mathf.Max(worstSw, Mathf.Abs(Mathf.DeltaAngle(prevSw, sw)));
                    prevSw = sw;
                    if (hasMid) worstRoll = Mathf.Max(worstRoll, Quaternion.Angle(prevMid, r.MidRotationSolved));
                    prevMid = r.MidRotationSolved; hasMid = true;
                }
                Assert.That(worstSw, Is.LessThan(45f),
                    $"'{arc.name}' forward full-stretch: elbow swivel jumps {worstSw:0.0} deg between adjacent samples (>45) -- the arm flips reaching straight forward.");
                Assert.That(worstRoll, Is.LessThan(45f),
                    $"'{arc.name}' forward full-stretch: forearm orientation jumps {worstRoll:0.0} deg between adjacent samples (>45) -- the wrist/forearm rolls over as the elbow flips.");
            }
        }

        // ----------------------------------------------------------------- elbow tracker placement / body size

        [Test]
        public void Elbow_TrackerReproducesNaturalBend_AcrossPlacementsAndSizes()
        {
            // A REAL elbow tracker feeds the lower-arm tracker WORLD POSITION as the hint -- and unlike the
            // knees (which ComputeHints biases "out and up" so they look natural), elbows get NO hint bias:
            // BasisLocalRigDriver forwards data.LeftLowerArmPosition raw. The two-bone solve projects
            // (hint - shoulder) onto the swing plane for the pole, so the MOUNT (upper-arm vs forearm), how
            // far along the segment it sits, how far it stands off the bone, and the body's arm proportions
            // all decide how well-conditioned that pole is. Strap a tracker rigidly to the NATURAL (production
            // lookup) arm across a spread of placements and sizes: the solved elbow must reproduce the natural
            // pose -- a tracker must never make the bend LESS natural than no tracker. A large deviation is the
            // "looks unnatural in some setups": a weak pole fades out (hintFade) or trips the pole-collapse
            // stabilizer, which drags the elbow toward world-down instead of where the tracker actually is.
            Vector3 restElbowDir = new Vector3(0.15f, -0.95f, 0.27f).normalized;
            Vector3 restForearmDir = new Vector3(0f, -0.30f, 0.95f).normalized;

            float worst = 0f; string where = "";
            int tested = 0;
            foreach (var size in new[] { (up: 0.22f, lo: 0.20f), (up: 0.28f, lo: 0.26f), (up: 0.34f, lo: 0.30f), (up: 0.32f, lo: 0.22f) })
            {
                float upper = size.up, lower = size.lo, armLen = upper + lower;
                Vector3 restElbow = Shoulder + restElbowDir * upper;
                Vector3 restHand = restElbow + restForearmDir * lower;

                foreach (var dirReach in new[]
                {
                    (d: new Vector3(0.05f, -0.05f, 1f), r: 0.82f),    // straight forward
                    (d: new Vector3(0.55f, -0.10f, 0.70f), r: 0.85f), // forward + out to the side
                    (d: new Vector3(0.10f, 0.40f, 0.80f), r: 0.85f),  // up-forward (raised reach)
                    (d: new Vector3(-0.35f, -0.10f, 0.65f), r: 0.80f),// across the body
                    (d: new Vector3(0.10f, -0.55f, 0.70f), r: 0.82f), // low-forward
                })
                {
                    Vector3 target = Shoulder + dirReach.d.normalized * (dirReach.r * armLen);
                    // Natural (production) elbow: lookup hint, no tracker.
                    Vector3 bend = BasisArmBendLookup.SampleTrilinear(_table, (target - Shoulder) / armLen).normalized;
                    Vector3 lookupHint = Shoulder + 0.5f * armLen * bend;
                    var nat = SolveOne(Shoulder, restElbow, restHand, target, lookupHint, true, float.MaxValue);
                    Vector3 axis = nat.HandSolved - Shoulder;
                    if (axis.sqrMagnitude < 1e-8f) continue;
                    axis.Normalize();
                    Vector3 natPole = Vector3.ProjectOnPlane(nat.ElbowSolved - Shoulder, axis);
                    if (natPole.sqrMagnitude < 1e-6f) continue; // natural elbow on the axis (singular) -- skip
                    Vector3 poleDir = natPole.normalized;
                    float natSwivel = Swivel(Shoulder, nat.HandSolved, nat.ElbowSolved);
                    if (float.IsNaN(natSwivel)) continue;

                    foreach (bool upperMount in new[] { true, false })
                    foreach (float frac in new[] { 0.4f, 0.5f, 0.6f })          // realistic mid-bone mounts
                    foreach (float radius in new[] { 0.05f, 0.07f, 0.09f })     // realistic strap stand-off
                    {
                        // Tracker strapped to the natural arm: a point along the upper-arm or forearm segment,
                        // standing off the bone along the elbow pole by the limb radius + strap (the tracker
                        // sits proud of the bone on the elbow side). Fed raw as the hint, exactly like the rig.
                        Vector3 bonePoint = upperMount
                            ? Vector3.Lerp(Shoulder, nat.ElbowSolved, frac)
                            : Vector3.Lerp(nat.ElbowSolved, nat.HandSolved, frac);
                        Vector3 trackerPos = bonePoint + poleDir * radius;

                        var s = SolveOne(Shoulder, restElbow, restHand, target, trackerPos, true, float.MaxValue, hintIsTracker: true);
                        float sw = Swivel(Shoulder, s.HandSolved, s.ElbowSolved);
                        if (float.IsNaN(sw)) continue;
                        tested++;
                        float dev = Mathf.Abs(Mathf.DeltaAngle(natSwivel, sw));
                        if (dev > worst)
                        {
                            worst = dev;
                            where = $"size({upper:0.00},{lower:0.00}) target({dirReach.d.x:0.0},{dirReach.d.y:0.0},{dirReach.d.z:0.0})@{dirReach.r:0.00} mount={(upperMount ? "upper" : "fore")} frac={frac:0.00} radius={radius * 100f:0}cm (natSwivel {natSwivel:0.0} -> {sw:0.0})";
                        }
                    }
                }
            }

            Assert.That(tested, Is.GreaterThan(300), $"only {tested} placement/size combos exercised.");
            // A real elbow tracker (HintIsTracker) must reproduce the natural pose across realistic mounts/sizes.
            // BasisArmSolveCore re-conditions the short-but-physical tracker pole (positions-only -> pronation-
            // safe), so it follows -- measured worst 0 deg / mean 0 over these combos. 15 is a regression guard
            // (a broken floor drags the elbow ~40-50 deg off toward world-down).
            Assert.That(worst, Is.LessThan(15f),
                $"elbow tracker moves the bend {worst:0.0} deg off the natural pose at {where} (>15) -- the strapped tracker is being dragged off natural for that placement/body size (re-conditioning floor regressed?).");
        }

        [Test]
        public void Elbow_TrackerNotDraggedToDown_WhenPoleModeratelyCollapsed()
        {
            // The pole-collapse stabilizer eases the elbow toward world-DOWN when the hint pole is weakly
            // conditioned. For the lookup (no tracker) that is correct, but a REAL tracker IS the user's elbow,
            // so HintIsTracker must trust it further before the down-drag takes over. Build an elbow clearly OUT
            // to the side (not down) at an extended reach, feed a weakly-conditioned tracker pole on that out
            // side, and confirm the tracker path (HintIsTracker=true) follows it BETTER than the lookup path
            // (false) -- and never worse. This is the solver half of the tracker-naturalness fix.
            Vector3 hand = Shoulder + new Vector3(0.15f, -0.05f, 0.9f).normalized * (0.9f * ArmLen);
            Vector3 axis = (hand - Shoulder).normalized;
            Vector3 downPole = Vector3.ProjectOnPlane(Vector3.down, axis).normalized;
            Vector3 outPole = (Quaternion.AngleAxis(70f, axis) * downPole).normalized; // elbow flared OUT, not down

            // Seed the commanded pose with a strong (well-conditioned) out hint -- no override either way.
            var seed = SolveOne(Shoulder, RestElbow, RestHand, hand, Shoulder + 0.5f * ArmLen * outPole, true, float.MaxValue, hintIsTracker: true);
            float commanded = Swivel(Shoulder, seed.HandSolved, seed.ElbowSolved);

            // Weakly conditioned tracker pole: small perpendicular projection (~0.18 arm-len) on the out side --
            // inside the lookup stabilizer's collapse window but within the tracker-trust window.
            Vector3 weakHint = Shoulder + 0.5f * (hand - Shoulder) + 0.18f * ArmLen * outPole;
            var tracked = SolveOne(Shoulder, RestElbow, RestHand, hand, weakHint, true, float.MaxValue, hintIsTracker: true);
            var lookupish = SolveOne(Shoulder, RestElbow, RestHand, hand, weakHint, true, float.MaxValue, hintIsTracker: false);

            float devTracked = Mathf.Abs(Mathf.DeltaAngle(commanded, Swivel(Shoulder, tracked.HandSolved, tracked.ElbowSolved)));
            float devLookup = Mathf.Abs(Mathf.DeltaAngle(commanded, Swivel(Shoulder, lookupish.HandSolved, lookupish.ElbowSolved)));

            Assert.That(devTracked, Is.LessThanOrEqualTo(devLookup + 1f),
                $"HintIsTracker followed the tracker WORSE than the lookup path (tracked {devTracked:0.0} vs lookup {devLookup:0.0}) -- the tracker-trust window regressed.");
            Assert.That(devTracked, Is.LessThan(35f),
                $"a real tracker pole was not followed (off by {devTracked:0.0} deg) -- the elbow was dragged toward world-down instead of the tracker.");
            Assert.That(tracked.HandError, Is.LessThanOrEqualTo(0.01f * ArmLen),
                $"reach must be preserved: the tracker swivel moved the hand by {tracked.HandError:0.0000} m.");
        }

        [Test]
        public void Elbow_StraightArmInput_PicksStableDownPole_NoThrash()
        {
            // Locks the dead-straight axis fallback (Part A): when the INPUT arm is exactly straight the
            // shoulder-elbow-hand plane is undefined, so Cross(ab,bc) collapses. The old code then thrashed
            // between the collinear hint/target/player-up fallbacks (the backward flip at full extension); it
            // must instead seed a stable down pole and stay continuous as the target sweeps behind the body.
            Vector3 dir0 = new Vector3(0.10f, -0.20f, -1f).normalized;
            Vector3 straightElbow = Shoulder + dir0 * Upper;          // elbow + hand collinear with the
            Vector3 straightHand = Shoulder + dir0 * (Upper + Lower); // shoulder -> Cross(ab,bc) = 0
            float prev = float.NaN, worst = 0f;
            for (int s = 0; s <= 160; s++)
            {
                Vector3 tdir = Quaternion.AngleAxis(Mathf.Lerp(-60f, 60f, s / 160f), Vector3.up) * dir0;
                Vector3 target = Shoulder + tdir * (0.9f * ArmLen);
                Vector3 hint = Shoulder + 0.5f * ArmLen * LookupBend(target - Shoulder);
                var r = SolveOne(Shoulder, straightElbow, straightHand, target, hint, true, float.MaxValue);
                float sw = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                if (!float.IsNaN(sw) && !float.IsNaN(prev))
                    worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(prev, sw)));
                prev = sw;
            }
            Assert.That(worst, Is.LessThan(25f),
                $"straight-arm input thrashes the bend plane: swivel jumps {worst:0.0} deg as the target sweeps (>25) -- the dead-straight fallback is not stable.");
        }

        [Test]
        public void Elbow_StaysStable_UnderNoisyTrackerHint()
        {
            // Simulate a jittery elbow tracker: the hand is held still and forward while the hint gets
            // +-8% arm-length random noise each frame, rate-limited at 6 deg/frame. The elbow must stay
            // near straight-down and never pop. Measured: worst step ~6 deg, max |swivel| ~8.5 deg.
            Vector3 hand = Shoulder + new Vector3(0.05f, -0.05f, 0.85f) * ArmLen;
            var seed = SolveOne(Shoulder, RestElbow, RestHand, hand, Shoulder + 0.5f * ArmLen * Vector3.down, true, float.MaxValue);
            Vector3 e = seed.ElbowSolved, h = seed.HandSolved;

            var rng = new System.Random(1234);
            float prev = float.NaN, worstStep = 0f, maxAbsSwivel = 0f;
            int pops = 0;
            for (int s = 0; s < 240; s++)
            {
                Vector3 noise = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1)) * (0.08f * ArmLen);
                Vector3 hint = Shoulder + 0.5f * ArmLen * Vector3.down + noise;
                var r = SolveOne(Shoulder, e, h, hand, hint, true, RateDegPerFrame);
                e = r.ElbowSolved; h = r.HandSolved;

                float swivel = Swivel(Shoulder, h, e);
                if (!float.IsNaN(swivel))
                {
                    maxAbsSwivel = Mathf.Max(maxAbsSwivel, Mathf.Abs(swivel));
                    if (!float.IsNaN(prev))
                    {
                        float d = Mathf.Abs(Mathf.DeltaAngle(prev, swivel));
                        worstStep = Mathf.Max(worstStep, d);
                        if (d > 8f) pops++;
                    }
                    prev = swivel;
                }
            }

            Assert.That(pops, Is.EqualTo(0), $"Elbow popped {pops} times under tracker noise.");
            Assert.That(maxAbsSwivel, Is.LessThan(25f),
                $"Elbow wandered {maxAbsSwivel:0.0} deg from straight-down under tracker noise; it should stay tucked.");
        }

        [Test]
        public void Elbow_FollowsCrossSideHint_WithoutPopping()
        {
            // A hint pole that sweeps a full half-turn around the shoulder->hand axis (a tracker
            // crossing from the correct side to the wrong side). The elbow may follow, but the
            // rate limiter must make it ease over -- never a single-frame snap. Measured worst ~1.7 deg.
            Vector3 hand = Shoulder + new Vector3(0.05f, -0.05f, 0.85f) * ArmLen;
            var seed = SolveOne(Shoulder, RestElbow, RestHand, hand, Shoulder + 0.5f * ArmLen * Vector3.down, true, float.MaxValue);
            Vector3 e = seed.ElbowSolved, h = seed.HandSolved;
            Vector3 axis = (hand - Shoulder).normalized;
            Vector3 downPole = Vector3.ProjectOnPlane(Vector3.down, axis).normalized;

            float prev = float.NaN, worstStep = 0f;
            int pops = 0;
            for (int s = 0; s <= 120; s++)
            {
                float ang = (s / 120f) * 200f; // 0 deg (down) sweeping past 180 deg (up)
                Vector3 pole = Quaternion.AngleAxis(ang, axis) * downPole;
                Vector3 hint = Shoulder + 0.5f * ArmLen * pole;
                var r = SolveOne(Shoulder, e, h, hand, hint, true, RateDegPerFrame);
                e = r.ElbowSolved; h = r.HandSolved;

                float swivel = Swivel(Shoulder, h, e);
                if (!float.IsNaN(swivel) && !float.IsNaN(prev))
                {
                    float d = Mathf.Abs(Mathf.DeltaAngle(prev, swivel));
                    worstStep = Mathf.Max(worstStep, d);
                    if (d > 8f) pops++;
                }
                prev = swivel;
            }

            Assert.That(pops, Is.EqualTo(0), $"Elbow popped {pops} times while the hint crossed sides.");
            Assert.That(worstStep, Is.LessThan(RateDegPerFrame + 6f),
                $"Elbow stepped {worstStep:0.0} deg in one frame while the hint crossed sides; the swivel is not eased.");
        }

        // ----------------------------------------------------------------- helpers (test scaffolding)

        // Hand targets, in shoulder-local arm-length fractions, that are clearly "reaching forward".
        static Vector3[] ForwardReachTargets() => new[]
        {
            Frac(0.05f, -0.05f, 0.75f),
            Frac(0.10f, -0.45f, 0.70f),
            Frac(0.10f, 0.45f, 0.65f),
            Frac(0.05f, -0.05f, 0.95f),
            Frac(0.40f, -0.10f, 0.55f),
            Frac(-0.35f, -0.05f, 0.55f),
        };

        static Vector3 Frac(float fx, float fy, float fz) => Shoulder + new Vector3(fx, fy, fz) * ArmLen;

        // Great-circle arc on the FULL-EXTENSION sphere (radius reach*ArmLen about the shoulder), sweeping
        // direction a -> b. Mirror of BasisIKTrajectoryScan.Arc / the sweep's ext-* paths: |hand-shoulder|
        // stays fixed so only the pole rotates, holding the arm at constant (near-straight) extension.
        static Vector3[] ExtArc(Vector3 a, Vector3 b, float reach, int steps)
        {
            Vector3 ua = a.normalized, ub = b.normalized;
            var pts = new Vector3[steps];
            for (int s = 0; s < steps; s++)
                pts[s] = Shoulder + Vector3.Slerp(ua, ub, s / (float)(steps - 1)) * (reach * ArmLen);
            return pts;
        }

        // Radial push out to (near) full extension and back along a fixed direction: reach ramps lo->hi->lo.
        // Exercises the pole collapsing as the arm straightens and re-forming as it folds.
        static Vector3[] ReachOutAndBack(Vector3 dir, float reachLo, float reachHi, int stepsEachWay)
        {
            Vector3 u = dir.normalized;
            var pts = new Vector3[stepsEachWay * 2];
            for (int s = 0; s < stepsEachWay; s++)
                pts[s] = Shoulder + u * (Mathf.Lerp(reachLo, reachHi, s / (float)(stepsEachWay - 1)) * ArmLen);
            for (int s = 0; s < stepsEachWay; s++)
                pts[stepsEachWay + s] = Shoulder + u * (Mathf.Lerp(reachHi, reachLo, s / (float)(stepsEachWay - 1)) * ArmLen);
            return pts;
        }

        // Worst adjacent-sample elbow-swivel jump along a hand path, solved the way the LIVE rig does:
        // stateless (fresh rest pose each sample) with no rate limit. A jump is a pole flip.
        float StatelessWorstArcJump(Vector3[] pts)
        {
            float prev = float.NaN, worst = 0f;
            foreach (var pt in pts)
            {
                var r = SolveWithLookupHint(pt, out _);
                float sw = Swivel(Shoulder, r.HandSolved, r.ElbowSolved);
                if (!float.IsNaN(sw) && !float.IsNaN(prev))
                    worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(prev, sw)));
                prev = sw;
            }
            return worst;
        }

        // A full 360 deg azimuth circle of hand targets at a fixed elevation (y fraction) and reach, on the
        // sphere about the shoulder -- one revolution passes through forward, both sides and fully behind.
        static Vector3[] LatitudeCircle(float elevationY, float reach, int n)
        {
            float c = Mathf.Sqrt(Mathf.Max(0f, 1f - elevationY * elevationY));
            var pts = new Vector3[n];
            for (int s = 0; s < n; s++)
            {
                float th = (s / (float)(n - 1)) * 2f * Mathf.PI;
                pts[s] = Shoulder + new Vector3(Mathf.Sin(th) * c, elevationY, Mathf.Cos(th) * c) * (reach * ArmLen);
            }
            return pts;
        }

        Vector3 SampleLookup(Vector3 normalizedHandPos) => BasisArmBendLookup.SampleTrilinear(_table, normalizedHandPos);

        // Mirror of BasisArmIKSweep.ComputeLookupBend (right arm: no mirror).
        Vector3 LookupBend(Vector3 shoulderToHand) =>
            BasisArmBendLookup.SampleTrilinear(_table, shoulderToHand / ArmLen).normalized;

        // Solve a fresh (rest-pose) reach to an absolute WORLD target using the lookup-derived elbow hint.
        BasisArmSolveResult SolveWithLookupHint(Vector3 worldTarget, out Vector3 hint)
        {
            Vector3 bend = LookupBend(worldTarget - Shoulder);
            hint = Shoulder + 0.5f * ArmLen * bend;
            return SolveOne(Shoulder, RestElbow, RestHand, worldTarget, hint, true, float.MaxValue);
        }

        // Mirror of BasisArmIKSweep.SolveOne: drive BasisArmSolveCore with identity rotations (positions
        // are unaffected by the rest rotations) so the result is pure geometry.
        static BasisArmSolveResult SolveOne(Vector3 shoulder, Vector3 elbow, Vector3 hand,
            Vector3 target, Vector3 hint, bool hintOn, float maxStep, bool hintIsTracker = false)
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
            input.HintWeight = hintOn;
            input.HintIsTracker = hintIsTracker;
            input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up;
            input.HintMaxStepDeg = maxStep;
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            return r;
        }

        // Mirror of BasisArmIKSweep.Swivel: signed elbow angle around the shoulder->hand axis from
        // straight-down. The headline "is the elbow where a human's would be" metric.
        static float Swivel(Vector3 shoulder, Vector3 hand, Vector3 elbow)
        {
            Vector3 axis = hand - shoulder;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 refVec = Vector3.ProjectOnPlane(Vector3.down, axis);
            Vector3 poleVec = Vector3.ProjectOnPlane(elbow - shoulder, axis);
            if (refVec.sqrMagnitude < 1e-8f || poleVec.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.SignedAngle(refVec, poleVec, axis);
        }

        void AssertSmoothSweep(Vector3 from, Vector3 to, int steps, string name)
        {
            var pts = new Vector3[steps];
            for (int s = 0; s < steps; s++) pts[s] = Vector3.Lerp(from, to, s / (float)(steps - 1));
            AssertSmoothSweep(pts, name);
        }

        // Drive a continuous hand path through the production (lookup-hint, no tracker) elbow solve,
        // feeding the previous frame's solved pose back in with the live 6 deg/frame rate limit -- the
        // same temporal model as BasisArmIKSweep.RunTemporal. A pole flip shows up as a swivel pop.
        void AssertSmoothSweep(Vector3[] pts, string name)
        {
            Vector3 e = RestElbow, h = RestHand;
            float prev = float.NaN, worstStep = 0f;
            int pops = 0;
            for (int s = 0; s < pts.Length; s++)
            {
                Vector3 bend = LookupBend(pts[s] - Shoulder);
                Vector3 hint = Shoulder + 0.5f * ArmLen * bend;
                var r = SolveOne(Shoulder, e, h, pts[s], hint, true, RateDegPerFrame);
                e = r.ElbowSolved; h = r.HandSolved;

                float swivel = Swivel(Shoulder, h, e);
                if (!float.IsNaN(swivel) && !float.IsNaN(prev))
                {
                    float d = Mathf.Abs(Mathf.DeltaAngle(prev, swivel));
                    worstStep = Mathf.Max(worstStep, d);
                    if (d > 8f) pops++;
                }
                prev = swivel;
            }

            Assert.That(pops, Is.EqualTo(0),
                $"'{name}' sweep: elbow popped {pops} times on smooth motion (pole flip).");
            Assert.That(worstStep, Is.LessThan(15f),
                $"'{name}' sweep: worst per-frame swivel step {worstStep:0.0} deg (rate limit is {RateDegPerFrame:0.0}); elbow is jumping.");
        }

        // Same temporal lookup-hint drive as AssertSmoothSweep, but for a FULL-EXTENSION path where the elbow
        // pole collapses onto the shoulder->hand axis -- the case the user sees as "extend the arm out fully
        // and the elbow flips". Three guards: the live rate limiter must keep each step small (no snap), the
        // swivel must not pop, and the elbow must never wing hard UP/to the wrong side (|swivel| < 120, the
        // same flip threshold as Elbow_DoesNotFlipUp_OnExtendedForwardReaches) -- it stays tucked behind the hand.
        void AssertFullExtensionArc(Vector3[] pts, string name)
        {
            Vector3 e = RestElbow, h = RestHand;
            float prev = float.NaN, worstStep = 0f, maxAbsSwivel = 0f;
            int pops = 0;
            for (int s = 0; s < pts.Length; s++)
            {
                Vector3 bend = LookupBend(pts[s] - Shoulder);
                Vector3 hint = Shoulder + 0.5f * ArmLen * bend;
                var r = SolveOne(Shoulder, e, h, pts[s], hint, true, RateDegPerFrame);
                e = r.ElbowSolved; h = r.HandSolved;

                float swivel = Swivel(Shoulder, h, e);
                if (!float.IsNaN(swivel))
                {
                    maxAbsSwivel = Mathf.Max(maxAbsSwivel, Mathf.Abs(swivel));
                    if (!float.IsNaN(prev))
                    {
                        float d = Mathf.Abs(Mathf.DeltaAngle(prev, swivel));
                        worstStep = Mathf.Max(worstStep, d);
                        if (d > 8f) pops++;
                    }
                    prev = swivel;
                }
            }

            Assert.That(pops, Is.EqualTo(0),
                $"'{name}' full-extension sweep: elbow popped {pops} times (pole flip as the arm extends).");
            Assert.That(worstStep, Is.LessThan(RateDegPerFrame + 6f),
                $"'{name}' full-extension sweep: worst per-frame swivel step {worstStep:0.0} deg (rate limit {RateDegPerFrame:0.0}); the swivel is snapping, not easing.");
            Assert.That(maxAbsSwivel, Is.LessThan(120f),
                $"'{name}' full-extension sweep: elbow swung to |swivel| {maxAbsSwivel:0.0} deg (>120) -- it flipped up/wrong-side instead of staying tucked behind the hand.");
        }
    }
}
