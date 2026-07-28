using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Regression tests for the scapulohumeral shoulder pre-solve (<see cref="BasisShoulderSolveCore"/>),
    /// the stream-free math the live FBIK SolveShoulder and the offline BasisShoulderSweep both drive.
    ///
    /// The shoulder girdle (clavicle/scapula bone) follows a coupled fraction of the upper-arm SWING away
    /// from the bind direction, measured in the chest frame, with the humeral axial twist removed. The
    /// upper-arm direction comes from the elbow tracker when present (shoulder->elbow, flexion-independent),
    /// else from the hand target. These tests pin the properties that make it look human and keep it safe
    /// to ship default-on:
    ///
    ///   * BIND REST     -- at the bind arm direction the girdle does not move (the authored pose is kept).
    ///   * ELEVATION     -- raising the arm elevates the shoulder, monotonically, in the elevation sense.
    ///   * PROTRACTION   -- reaching forward / across the body protracts it; left mirrors right.
    ///   * TWIST-FREE    -- the girdle never rotates about the arm axis (a twist-following clavicle was
    ///                      tried and reverted; this guards the regression).
    ///   * ELBOW DRIVE   -- a bent arm (elbow up, hand low) elevates the shoulder from the ELBOW tracker;
    ///                      the hand-target fallback, seeing only the low hand, cannot -- the headline win.
    ///   * FRAME RIGID   -- the solve lives in the chest frame: rotating chest+arm rotates the result rigidly.
    ///   * BOUNDED       -- clamped, smooth under smooth motion, and stable under a noisy elbow tracker.
    /// </summary>
    public class BasisShoulderDirectionTests
    {
        const float ArmLen = 0.54f;
        const float CoupleRatio = 0.8f;
        const float MaxShoulderDeg = 40f;
        static readonly Vector3 Shoulder = Vector3.zero;

        // ----------------------------------------------------------------- bind rest

        [Test]
        public void Shoulder_AtBindDirection_DoesNotMove()
        {
            foreach (bool isLeft in new[] { false, true })
            foreach (bool elbow in new[] { false, true })
            foreach (float reach in new[] { 0.6f, 0.8f, 1.0f })
            {
                var r = SolveDir(RestDirLocal(isLeft), reach, elbow, isLeft);
                Assert.That(r.Apply, Is.True);
                Assert.That(r.AppliedAngleDeg, Is.LessThan(1.5f),
                    $"side={(isLeft ? "L" : "R")} elbow={elbow} reach={reach}: girdle moved {r.AppliedAngleDeg:0.0} deg at the bind direction (idle shoulder would shift).");
            }
        }

        [Test]
        public void Shoulder_SmallSwingFromBind_StaysAtRest()
        {
            // A few degrees off the bind direction is inside the setting phase -- the girdle barely moves.
            var r = SolveDir(DirFromAzEl(5f, 4f, false), 0.9f, true, false);
            Assert.That(r.AppliedAngleDeg, Is.LessThan(2f),
                $"a tiny arm motion moved the girdle {r.AppliedAngleDeg:0.0} deg; the setting phase should hold it near rest.");
        }

        // ----------------------------------------------------------------- elevation

        [Test]
        public void Shoulder_RaisingArm_Elevates()
        {
            var r = SolveDir(DirFromAzEl(0f, 85f, false), 0.95f, true, false); // arm up the side
            Assert.That(r.AppliedAngleDeg, Is.GreaterThan(8f),
                $"raising the arm overhead barely moved the shoulder ({r.AppliedAngleDeg:0.0} deg).");
            Assert.That(r.Elevation, Is.GreaterThan(r.Protraction + 4f),
                $"a lateral raise should ELEVATE (elevation {r.Elevation:0.0}) more than protract ({r.Protraction:0.0}).");
        }

        [Test]
        public void Shoulder_Elevation_IsMonotonic_WhileRaising()
        {
            // Raising the arm up the side / front (abduction or forward flexion, within the physical range
            // -- not past vertical, where the arm crosses overhead) must never REDUCE girdle elevation
            // below the clamp. A reversal reads as the shoulder dropping while the arm goes up.
            foreach (float az in new[] { 0f, 45f, 90f })
            {
                float prevElev = -1f, prevApplied = -1f;
                for (int e = 0; e <= 60; e++)
                {
                    float el = Mathf.Lerp(0f, 88f, e / 60f);
                    var r = SolveDir(DirFromAzEl(az, el, false), 0.9f, true, false);
                    bool belowClamp = r.AppliedAngleDeg < MaxShoulderDeg - 1f && prevApplied < MaxShoulderDeg - 1f;
                    if (prevElev >= 0f && belowClamp)
                        Assert.That(r.Elevation, Is.GreaterThanOrEqualTo(prevElev - 0.75f),
                            $"az={az} el={el:0}: girdle elevation dropped {prevElev:0.0}->{r.Elevation:0.0} while raising the arm.");
                    prevElev = r.Elevation;
                    prevApplied = r.AppliedAngleDeg;
                }
            }
        }

        [Test]
        public void Shoulder_ArmsDown_DepressesFarLessThanRaising()
        {
            // Lowering the arm below bind must move the girdle much less than raising it (scapular
            // depression is a small range), so idle / arms-down poses stay near the authored bind.
            float down = SolveDir(DirFromAzEl(0f, -85f, false), 0.9f, true, false).AppliedAngleDeg;
            float up = SolveDir(DirFromAzEl(0f, 85f, false), 0.9f, true, false).AppliedAngleDeg;
            Assert.That(down, Is.LessThan(0.6f * up),
                $"arms-down girdle {down:0.0} deg is not far below the raised girdle {up:0.0} deg; idle would shift.");
        }

        // ----------------------------------------------------------------- protraction / symmetry

        [Test]
        public void Shoulder_ForwardReach_Protracts()
        {
            var fwd = SolveDir(DirFromAzEl(90f, 10f, false), 0.9f, true, false);   // straight forward
            Assert.That(fwd.Protraction, Is.GreaterThan(5f),
                $"a forward reach barely protracted the shoulder ({fwd.Protraction:0.0} deg).");
            var cross = SolveDir(DirFromAzEl(135f, 10f, false), 0.9f, true, false); // across the body
            Assert.That(cross.Protraction, Is.GreaterThan(fwd.Protraction - 1f),
                $"reaching across the body should protract at least as much as straight forward ({cross.Protraction:0.0} vs {fwd.Protraction:0.0}).");
        }

        [Test]
        public void Shoulder_LeftMirrorsRight()
        {
            foreach (var azel in SampleAzEl)
            foreach (bool elbow in new[] { false, true })
            {
                var R = SolveDir(DirFromAzEl(azel.x, azel.y, false), 0.9f, elbow, false);
                var L = SolveDir(DirFromAzEl(azel.x, azel.y, true), 0.9f, elbow, true);
                Assert.That(L.AppliedAngleDeg, Is.EqualTo(R.AppliedAngleDeg).Within(0.5f),
                    $"az={azel.x} el={azel.y} elbow={elbow}: left applied {L.AppliedAngleDeg:0.0} != right {R.AppliedAngleDeg:0.0}.");
                Assert.That(L.Elevation, Is.EqualTo(R.Elevation).Within(0.5f));
                Assert.That(L.Protraction, Is.EqualTo(R.Protraction).Within(0.5f));
            }
        }

        // ----------------------------------------------------------------- twist-free (the reverted artifact)

        [Test]
        public void Shoulder_NeverTwistsAboutTheArmAxis()
        {
            // A clavicle that follows the humeral roll was tried and reverted. The girdle delta must carry
            // no rotation about the arm axis anywhere in the workspace.
            float worst = 0f;
            for (float az = -120f; az <= 150f; az += 15f)
            for (float el = -70f; el <= 130f; el += 15f)
            {
                var r = SolveDir(DirFromAzEl(az, el, false), 0.9f, true, false);
                if (r.TwistLeakDeg > worst) worst = r.TwistLeakDeg;
            }
            Assert.That(worst, Is.LessThan(1f),
                $"girdle twisted up to {worst:0.0} deg about the arm axis; the clavicle is following humeral roll (reverted artifact).");
        }

        [Test]
        public void Shoulder_IgnoresHandRotation_FollowsUpperArmOnly()
        {
            // In elbow mode the solve reads only the elbow position -- the forearm/hand can rotate or move
            // (a pronating wrist, a bent elbow) without changing the shoulder. This is what makes the
            // shoulder track the upper arm, not the hand.
            Vector3 elbowPos = Shoulder + DirFromAzEl(30f, 60f, false) * (ArmLen * 0.5f);
            var a = SolveRaw(elbowPos, Shoulder + DirFromAzEl(80f, -40f, false) * ArmLen, hasElbow: true);
            var b = SolveRaw(elbowPos, Shoulder + DirFromAzEl(-30f, 20f, false) * (ArmLen * 0.7f), hasElbow: true);
            Assert.That(Quaternion.Angle(a.ShoulderRotation, b.ShoulderRotation), Is.LessThan(0.5f),
                "moving the hand while the elbow stays put changed the shoulder; it should follow the upper arm only.");
        }

        // ----------------------------------------------------------------- elbow tracker is the win

        [Test]
        public void Shoulder_ElbowTracker_ElevatesBentArm_HandFallbackCannot()
        {
            // Bent arm: elbow raised up/out, hand hanging low near the body. The elbow tracker sees the
            // true (raised) upper arm and elevates the shoulder; the hand-target fallback sees only the
            // low hand and cannot -- the case dedicated trackers used to be required for.
            Vector3 elbowPos = Shoulder + DirFromAzEl(35f, 75f, false) * (ArmLen * 0.5f);
            Vector3 handPos = Shoulder + DirFromAzEl(20f, -75f, false) * (ArmLen * 0.55f);

            float elbowElev = SolveRaw(elbowPos, handPos, hasElbow: true).Elevation;
            float handElev = SolveRaw(elbowPos, handPos, hasElbow: false).Elevation;

            Assert.That(elbowElev, Is.GreaterThan(8f),
                $"elbow tracker did not elevate the bent-arm shoulder ({elbowElev:0.0} deg).");
            Assert.That(elbowElev, Is.GreaterThan(handElev + 4f),
                $"elbow tracker ({elbowElev:0.0}) barely beat the hand fallback ({handElev:0.0}) on a bent-arm raise.");
        }

        // ----------------------------------------------------------------- chest-frame rigidity

        [Test]
        public void Shoulder_IsRigidInTheChestFrame()
        {
            Quaternion frame = Quaternion.Euler(17f, 40f, -23f);
            foreach (var azel in SampleAzEl)
            foreach (bool elbow in new[] { false, true })
            {
                Vector3 dir = DirFromAzEl(azel.x, azel.y, false);
                var baseR = SolveDir(dir, 0.9f, elbow, false, Quaternion.identity);
                var rotR = SolveDir(dir, 0.9f, elbow, false, frame);
                Assert.That(Quaternion.Angle(frame * baseR.ShoulderRotation, rotR.ShoulderRotation), Is.LessThan(0.5f),
                    $"az={azel.x} el={azel.y}: rotating the chest frame did not carry the shoulder rigidly.");
                Assert.That(rotR.SwingAngleDeg, Is.EqualTo(baseR.SwingAngleDeg).Within(0.5f));
                Assert.That(rotR.AppliedAngleDeg, Is.EqualTo(baseR.AppliedAngleDeg).Within(0.5f));
            }
        }

        // ----------------------------------------------------------------- bounded / safe

        [Test]
        public void Shoulder_NeverExceedsTheClamp()
        {
            float worst = 0f;
            for (float az = -150f; az <= 180f; az += 10f)
            for (float el = -90f; el <= 150f; el += 10f)
            foreach (bool elbow in new[] { false, true })
            {
                var r = SolveDir(DirFromAzEl(az, el, false), 1.0f, elbow, false);
                if (r.AppliedAngleDeg > worst) worst = r.AppliedAngleDeg;
            }
            Assert.That(worst, Is.LessThanOrEqualTo(MaxShoulderDeg + 0.5f),
                $"girdle reached {worst:0.0} deg, past the {MaxShoulderDeg} deg clamp.");
        }

        [Test]
        public void Shoulder_MovesSmoothly_OnSmoothArmMotion()
        {
            // The solve is a continuous function of the arm direction (no pole/swivel singularity), so a
            // smooth arm sweep gives a smooth girdle with no rate limiter needed.
            float worst = 0f;
            foreach (float el in new[] { -30f, 10f, 60f, 110f })
            {
                Quaternion prev = Quaternion.identity; bool has = false;
                for (int t = 0; t <= 200; t++)
                {
                    float az = Mathf.Lerp(-120f, 150f, t / 200f);
                    var r = SolveDir(DirFromAzEl(az, el, false), 0.9f, true, false);
                    if (has) worst = Mathf.Max(worst, Quaternion.Angle(prev, r.ShoulderRotation));
                    prev = r.ShoulderRotation; has = true;
                }
            }
            Assert.That(worst, Is.LessThan(8f),
                $"girdle jumped {worst:0.0} deg between adjacent samples on a smooth arm sweep (a pop).");
        }

        [Test]
        public void Shoulder_StaysStable_UnderNoisyElbowTracker()
        {
            var rng = new System.Random(4242);
            float worst = 0f;
            foreach (var azel in SampleAzEl)
            {
                Vector3 dir = DirFromAzEl(azel.x, azel.y, false);
                var clean = SolveDir(dir, 0.9f, true, false);
                for (int n = 0; n < 32; n++)
                {
                    Vector3 noise = new Vector3(
                        (float)(rng.NextDouble() * 2 - 1),
                        (float)(rng.NextDouble() * 2 - 1),
                        (float)(rng.NextDouble() * 2 - 1)) * 0.03f; // 3 cm elbow jitter
                    var noisy = SolveDir((dir + noise).normalized, 0.9f, true, false);
                    worst = Mathf.Max(worst, Quaternion.Angle(clean.ShoulderRotation, noisy.ShoulderRotation));
                }
            }
            Assert.That(worst, Is.LessThan(8f),
                $"a 3 cm elbow jitter swung the shoulder {worst:0.0} deg; it should stay steady.");
        }

        [Test]
        public void Shoulder_Degenerate_NoNaN()
        {
            // Zero arm vector, and arm along the chest-up axis -- must not produce NaN.
            var zero = SolveRaw(Shoulder, Shoulder, hasElbow: true);
            Assert.That(zero.Apply, Is.False, "a zero-length arm vector should no-op.");

            var up = SolveDir(new Vector3(0f, 1f, 0f), 0.9f, true, false);
            Assert.That(IsFinite(up.ShoulderRotation), Is.True, "arm straight up produced a non-finite rotation.");
            Assert.That(float.IsNaN(up.AppliedAngleDeg), Is.False);
        }

        [Test]
        public void Shoulder_WithTracker_TrackerStaysDominant()
        {
            Quaternion tracker = Quaternion.Euler(8f, 25f, -12f);

            // At the bind direction a shoulder-tracker user gets exactly their tracker (no anatomical add).
            var rest = SolveRawTracker(RestDirLocal(false), 0.9f, hasElbow: true, tracker);
            Assert.That(Quaternion.Angle(rest.ShoulderRotation, tracker), Is.LessThan(0.5f),
                "at the bind pose the shoulder tracker should be used verbatim.");

            // On a raised arm the anatomical refine is a gentle nudge -- the tracker still dominates.
            var raised = SolveRawTracker(DirFromAzEl(0f, 80f, false), 0.95f, hasElbow: true, tracker);
            float fromTracker = Quaternion.Angle(raised.ShoulderRotation, tracker);
            float noTrackerApplied = SolveDir(DirFromAzEl(0f, 80f, false), 0.95f, true, false).AppliedAngleDeg;
            Assert.That(fromTracker, Is.LessThan(noTrackerApplied),
                $"with a tracker the shoulder moved {fromTracker:0.0} deg off it -- more than the no-tracker girdle {noTrackerApplied:0.0}; the tracker should dominate.");
        }

        // ----------------------------------------------------------------- helpers

        static readonly Vector2[] SampleAzEl =
        {
            new Vector2(0f, 0f), new Vector2(0f, 60f), new Vector2(45f, 30f), new Vector2(90f, 10f),
            new Vector2(90f, 70f), new Vector2(120f, 40f), new Vector2(-60f, -20f), new Vector2(30f, 100f),
            new Vector2(60f, -40f), new Vector2(135f, 20f),
        };

        static Vector3 RestDirLocal(bool isLeft) => new Vector3(isLeft ? -1f : 1f, -0.05f, 0.05f).normalized;

        static Vector3 DirFromAzEl(float azDeg, float elDeg, bool isLeft)
        {
            float er = elDeg * Mathf.Deg2Rad, ar = azDeg * Mathf.Deg2Rad;
            float ch = Mathf.Cos(er);
            float x = Mathf.Cos(ar) * ch;
            float y = Mathf.Sin(er);
            float z = Mathf.Sin(ar) * ch;
            if (isLeft) x = -x;
            return new Vector3(x, y, z).normalized;
        }

        static BasisShoulderSolveResult SolveDir(Vector3 chestLocalDir, float reach, bool elbow, bool isLeft)
        {
            return SolveDir(chestLocalDir, reach, elbow, isLeft, Quaternion.identity);
        }

        static BasisShoulderSolveResult SolveDir(Vector3 chestLocalDir, float reach, bool elbow, bool isLeft, Quaternion chest)
        {
            Vector3 driver = Shoulder + (chest * chestLocalDir) * (reach * ArmLen);
            BasisShoulderSolveInput input = MakeInput(isLeft, chest);
            input.HandTargetPos = elbow ? Shoulder : driver;
            input.ElbowPos = elbow ? driver : Shoulder;
            input.HasElbow = elbow;
            BasisShoulderSolveCore.Solve(input, out var r);
            return r;
        }

        static BasisShoulderSolveResult SolveRaw(Vector3 elbowPos, Vector3 handPos, bool hasElbow)
        {
            BasisShoulderSolveInput input = MakeInput(false, Quaternion.identity);
            input.ElbowPos = elbowPos;
            input.HandTargetPos = handPos;
            input.HasElbow = hasElbow;
            BasisShoulderSolveCore.Solve(input, out var r);
            return r;
        }

        static BasisShoulderSolveResult SolveRawTracker(Vector3 chestLocalDir, float reach, bool hasElbow, Quaternion tracker)
        {
            Vector3 driver = Shoulder + chestLocalDir * (reach * ArmLen);
            BasisShoulderSolveInput input = MakeInput(false, Quaternion.identity);
            input.ElbowPos = driver;
            input.HandTargetPos = driver;
            input.HasElbow = hasElbow;
            input.HasShoulderTracker = true;
            input.TrackerFinal = tracker;
            BasisShoulderSolveCore.Solve(input, out var r);
            return r;
        }

        static BasisShoulderSolveInput MakeInput(bool isLeft, Quaternion chest)
        {
            BasisShoulderSolveInput input = default;
            input.ShoulderPos = Shoulder;
            input.HandTargetPos = Shoulder;
            input.ElbowPos = Shoulder;
            input.HasElbow = true;
            input.HasShoulderTracker = false;
            input.ChestRot = chest;
            input.TposeChestRot = Quaternion.identity;
            input.TposeShoulderRot = Quaternion.identity;
            input.TposeArmDirWorld = RestDirLocal(isLeft);
            input.TposeArmLength = ArmLen;
            input.ElevationFactor = 0.4f;
            input.ProtractionFactor = 0.3f;
            input.CoupleRatio = CoupleRatio;
            input.MaxShoulderDeg = MaxShoulderDeg;
            input.TrackerFinal = Quaternion.identity;
            input.IsLeft = isLeft;
            return input;
        }

        static bool IsFinite(Quaternion q) =>
            !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) ||
              float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w));
    }
}
