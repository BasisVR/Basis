using System.Collections.Generic;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    /// <summary>
    /// "LOOKING DOWN FORCES CHEST TO ROTATE" -- is the rig reading GAZE as a LEAN?
    ///
    /// ================================================================================================
    /// THE SUSPECT, in one line. BasisSpineBendCore.Solve:
    ///
    ///     bendPitchDeg = atan2(headDir.z, headDir.y) - atan2(chestDir.z, chestDir.y)     // headDir = hips->HEAD
    ///
    /// The spine's forward bend is the angle between "hips to chest" and "hips to HEAD". But the head is not
    /// on the spine -- it sits on the END of the neck, and it ORBITS that neck when you nod. So a user who
    /// gazes down without moving their torso an inch still swings the head TARGET forward and down, the
    /// hips->head vector tips over, and the solver reads a forward lean that never happened. It pitches the
    /// chest to match.
    ///
    /// That is the report, verbatim: "looking down forces chest to rotate not position issue we need a more
    /// stable looking down".
    /// ================================================================================================
    ///
    /// This file does not argue about that. It measures it TWICE, from two independent directions, because a
    /// mechanism I can only demonstrate one way is a mechanism I have probably talked myself into:
    ///
    ///   1. GEOMETRY -- feed the shipping core a head that has ONLY rotated about the neck, with the torso
    ///      byte-identical, and read out how much chest bend it invents. No corpus, no fitting, no opinion.
    ///
    ///   2. REAL HUMANS -- and this is the part that says what the answer SHOULD be. A human looking down
    ///      does flex their upper spine a little; the question was never "zero", it is "how much", and only
    ///      the corpus can say. If the rig's invented bend is within a couple of degrees of what a human
    ///      actually does, there is no bug here and I should go and look somewhere else.
    /// </summary>
    public sealed class BasisSpineGazeContaminationTests
    {
        // A T-posed adult, in the hips' own frame: +Y up, +Z forward. These are the only numbers that matter
        // -- the lever arm from the neck to the head is what converts a nod into a phantom lean.
        static readonly Vector3 k_Hips = new Vector3(0f, 0.95f, 0f);
        static readonly Vector3 k_Chest = new Vector3(0f, 1.25f, 0f);
        static readonly Vector3 k_Neck = new Vector3(0f, 1.45f, 0f);
        static readonly Vector3 k_Head = new Vector3(0f, 1.55f, 0f);   // 10 cm above the neck: the lever arm

        static BasisSpineBendInput BaseInput()
        {
            BasisSpineBendInput i = default;
            i.HipsRot = Quaternion.identity;
            i.HipsPos = k_Hips;
            i.ChestPos = k_Chest;
            i.HipsBind = Quaternion.identity;
            i.SpineMaxForwardDeg = 60f;
            i.SpineMaxBackwardDeg = 25f;
            i.SpineMaxLateralDeg = 30f;
            i.SpineBendPitch = 0.5f;
            i.UpperBendPitch = 0.5f;
            i.SpineBendYaw = 0.5f; i.SpineBendRoll = 0.5f;
            i.UpperBendYaw = 0.5f; i.UpperBendRoll = 0.5f;
            i.SquishBoost = 0f;              // isolated: the squish coupling is measured on its own, below
            i.RestLen = (k_Head - k_Hips).magnitude;
            i.HasSpine = true;
            i.HasUpper = true;
            return i;
        }

        /// <summary>The head, having ONLY nodded about the neck. The torso has not moved by one float.</summary>
        static void GazeDown(float deg, out Vector3 headPos, out Quaternion headRot)
        {
            Quaternion q = Quaternion.AngleAxis(deg, Vector3.right);   // +X = pitch the face down toward +Z
            headPos = k_Neck + q * (k_Head - k_Neck);
            headRot = q;
        }

        /// <summary>
        /// ⭐ THE PHANTOM LEAN, MEASURED. Nothing but the head has moved, and the spine bends anyway.
        /// </summary>
        [Test]
        public void PureGaze_MakesTheShippedCore_InventAForwardLean()
        {
            var report = new StringBuilder();
            report.AppendLine("PURE GAZE -> PHANTOM SPINE BEND (the torso is byte-identical on every row)");
            report.AppendLine($"{"gaze down",10} {"raw bend",10} {"spine",8} {"upperChest",11} {"TOTAL chest pitch",18}");
            report.AppendLine(new string('-', 62));

            float worst = 0f;
            foreach (float gaze in new[] { 10f, 20f, 30f, 45f, 60f, 75f })
            {
                GazeDown(gaze, out Vector3 headPos, out Quaternion headRot);

                BasisSpineBendInput i = BaseInput();
                i.SmoothedHead = headPos;
                i.HeadTargetRot = headRot;

                BasisSpineBendCore.Solve(i, out BasisSpineBendResult r);
                Assert.IsFalse(r.EarlyOut);

                float total = r.SpineEuler.x + r.UpperEuler.x;   // the chest rides both
                worst = Mathf.Max(worst, total);
                report.AppendLine($"{gaze,10:F0} {r.BendPitchDeg,10:F2} {r.SpineEuler.x,8:F2} {r.UpperEuler.x,11:F2} {total,18:F2}");
            }

            report.AppendLine();
            report.AppendLine("The torso did not move. Every one of those degrees is invented, and it is invented");
            report.AppendLine("because the bend cue is hips->HEAD and the head orbits the neck.");
            Debug.Log(report.ToString());

            Assert.Greater(worst, 3f,
                $"the shipped core must be shown to invent a real amount of chest pitch from pure gaze " +
                $"(worst {worst:F2} deg) -- if it does not, this whole diagnosis is wrong and the fix below " +
                "is solving a problem that does not exist");
        }

        /// <summary>
        /// THE SQUISH COUPLING: compression is turned into MORE ROTATION. This is "the torso rotates when it
        /// should translate", as a single multiplier, and it multiplies the phantom lean above.
        /// </summary>
        [Test]
        public void TheSquishMultiplier_AmplifiesTheBend_ExactlyWhenTheTorsoIsCompressed()
        {
            float restLen = (k_Head - k_Hips).magnitude;
            const float boost = 0.5f;

            float atRest = BasisSpineBendCore.ComputeSquishMultiplier(k_Head - k_Hips, restLen, boost);
            // The head 25% closer to the hips -- which is exactly what BENDING OVER does.
            float compressed = BasisSpineBendCore.ComputeSquishMultiplier((k_Head - k_Hips) * 0.75f, restLen, boost);

            Debug.Log($"squish multiplier: upright {atRest:F3}, torso compressed 25% {compressed:F3} " +
                      $"(x{compressed / atRest:F2} more spine rotation, purely because the head got closer)");

            Assert.AreEqual(1f, atRest, 0.05f, "upright must be a no-op");
            Assert.Greater(compressed, 1.2f * atRest,
                "the shipped coupling amplifies the spine's ROTATION as the torso COMPRESSES -- which is the " +
                "complaint, written as a multiplier: a body that should be translating down is instead being " +
                "told to rotate harder");
        }

        // =========================================================================================
        // AND NOW WHAT A REAL HUMAN ACTUALLY DOES, because "some" is not the same as "this much".
        // =========================================================================================

        /// <summary>
        /// GROUND TRUTH. For every frame in both corpora: how much of the rig's bend cue is explained by GAZE
        /// rather than by the torso? Measured as a partial slope, controlling for the real torso lean.
        ///
        /// A real human looking down DOES flex their upper spine somewhat, so the honest question is never
        /// "is the rig's number zero" -- it is "is the rig's number the HUMAN's number". This is that number.
        /// </summary>
        [Test]
        public void HowMuchAHumansChestActuallyPitches_PerDegreeOfGaze()
        {
            var clips = new List<BasisMotionClip>();
            clips.AddRange(BasisPostureCorpusTests.LoadPostureCorpus());

            // Pooled normal equations for   y = a*torsoLean + b*gaze   (through the origin, both cues are
            // deviations from upright). `b` IS the contamination: degrees of cue per degree of gaze.
            double[,] A = new double[2, 2];
            double[] rhsRig = new double[2], rhsNeck = new double[2], rhsChest = new double[2];
            int n = 0;
            float maxGaze = 0f;

            foreach (BasisMotionClip c in clips)
            {
                for (int f = 0; f < c.FrameCount; f++)
                {
                    Vector3 hips = c.Get(f, BasisMocapJoint.Hips).Position;
                    Vector3 chest = c.Get(f, BasisMocapJoint.Chest).Position;
                    Vector3 neck = c.Get(f, BasisMocapJoint.Neck).Position;
                    Vector3 head = c.Get(f, BasisMocapJoint.Head).Position;
                    Quaternion hipsRot = c.Get(f, BasisMocapJoint.Hips).Rotation;
                    Quaternion neckRot = c.Get(f, BasisMocapJoint.Neck).Rotation;
                    Quaternion headRot = c.Get(f, BasisMocapJoint.Head).Rotation;

                    Quaternion inv = Quaternion.Inverse(hipsRot);
                    Vector3 dChest = inv * (chest - hips);
                    Vector3 dNeck = inv * (neck - hips);
                    Vector3 dHead = inv * (head - hips);
                    if (dChest.sqrMagnitude < 1e-8f || dNeck.sqrMagnitude < 1e-8f || dHead.sqrMagnitude < 1e-8f) continue;

                    // The rig's own formula, verbatim (BasisSpineBendCore line 80).
                    float pitchOf(Vector3 v) { v = v.normalized; return Mathf.Atan2(v.z, v.y) * Mathf.Rad2Deg; }
                    float chestPitch = pitchOf(dChest);
                    float rigCue = pitchOf(dHead) - chestPitch;    // what the shipped core bends by
                    float neckCue = pitchOf(dNeck) - chestPitch;   // what it would bend by, cued off the NECK

                    // The torso's real lean: the NECK over the hips. Independent of where the head is looking.
                    float torsoLean = pitchOf(dNeck);

                    // The gaze: the head's pitch RELATIVE TO THE NECK. Pure cervical/skull rotation.
                    Quaternion rel = Quaternion.Inverse(neckRot) * headRot;
                    Vector3 fwd = rel * Vector3.forward;
                    float gaze = Mathf.Atan2(fwd.y, Mathf.Sqrt(fwd.x * fwd.x + fwd.z * fwd.z)) * Mathf.Rad2Deg;
                    gaze = -gaze;   // +gaze = looking DOWN
                    if (float.IsNaN(gaze)) continue;
                    maxGaze = Mathf.Max(maxGaze, Mathf.Abs(gaze));

                    A[0, 0] += torsoLean * torsoLean; A[0, 1] += torsoLean * gaze;
                    A[1, 0] += gaze * torsoLean;      A[1, 1] += gaze * gaze;
                    rhsRig[0] += torsoLean * rigCue;   rhsRig[1] += gaze * rigCue;
                    rhsNeck[0] += torsoLean * neckCue; rhsNeck[1] += gaze * neckCue;
                    rhsChest[0] += torsoLean * chestPitch; rhsChest[1] += gaze * chestPitch;
                    n++;
                }
            }

            Assert.Greater(n, 5000, "not enough frames");

            (double a, double b) Solve2(double[] rhs)
            {
                double det = A[0, 0] * A[1, 1] - A[0, 1] * A[1, 0];
                if (System.Math.Abs(det) < 1e-9) return (double.NaN, double.NaN);
                return ((rhs[0] * A[1, 1] - A[0, 1] * rhs[1]) / det,
                        (A[0, 0] * rhs[1] - rhs[0] * A[1, 0]) / det);
            }

            (double _, double bRig) = Solve2(rhsRig);
            (double __, double bNeck) = Solve2(rhsNeck);
            (double ___, double bChest) = Solve2(rhsChest);

            var report = new StringBuilder();
            report.AppendLine("HOW MUCH DOES GAZE DRIVE THE SPINE? (partial slope, controlling for the real torso lean)");
            report.AppendLine($"  {n} frames, gaze ranging to {maxGaze:F0} deg");
            report.AppendLine();
            report.AppendLine($"  a REAL HUMAN's chest pitch, per degree of gaze ......... {bChest,7:F3} deg/deg");
            report.AppendLine($"  the SHIPPED core's bend cue (hips->HEAD), per deg gaze . {bRig,7:F3} deg/deg  <-- the bug");
            report.AppendLine($"  the same cue taken off the NECK instead ................ {bNeck,7:F3} deg/deg  <-- the fix");
            report.AppendLine();
            report.AppendLine("The rig's cue is multiplied by the spine+upperChest pitch weights (~0.5 each) and then");
            report.AppendLine("written into the bones, so a slope near 0.5 deg/deg means a 60-degree glance down");
            report.AppendLine("pitches the chest by roughly 30 degrees of pure fiction.");
            Debug.Log(report.ToString());

            // ⭐ THE GROUND TRUTH, AND THE ONLY THING THIS CORPUS IS ENTITLED TO ASSERT.
            //
            // A real human's chest does NOT pitch when they look down: -0.05 deg per degree of gaze, i.e.
            // nothing. So the correct amount of chest rotation to produce from a pure gaze is ZERO -- not
            // "a little", not "damped". That is what makes the exact algebraic cancellation in the fix the
            // right shape of answer rather than merely a tidy one.
            Assert.Less(System.Math.Abs(bChest), 0.10,
                $"a real human's chest barely moves with gaze ({bChest:F3} deg/deg). If this corpus said " +
                "otherwise, the fix would be wrong and the rig's phantom lean would be a feature.");

            // ⚠ WHAT THIS CORPUS CANNOT TELL YOU, AND WHY THE GEOMETRY TEST ABOVE EXISTS.
            //
            // The rig's cue measures 0.064 deg/deg of gaze contamination HERE, which is small -- and that
            // number is an artefact of the MOCAP SKELETON, not a reprieve for the rig. CMU's Head joint sits
            // almost on top of its Neck joint, so the lever arm that converts a nod into a phantom lean is
            // tiny in this data. A real avatar's head bone is ~10 cm above the neck and the HMD is further
            // forward still, which is why the geometric test -- run on realistic avatar proportions, with the
            // torso held byte-identical -- measures 8-10 degrees of invented pitch where this corpus implies
            // barely one.
            //
            // So: the CORPUS says what the answer should be (zero). The GEOMETRY says what the rig actually
            // does (8-10 deg). Neither alone is the case; both together are. I originally asserted the corpus
            // would show a large contamination, it did not, and the assertion was wrong rather than the data.
            Assert.Less(System.Math.Abs(bNeck), System.Math.Abs(bRig),
                $"even on a skeleton whose head barely leaves its neck, cueing off the NECK must be less " +
                $"gaze-contaminated than cueing off the HEAD (neck {bNeck:F3} vs head {bRig:F3} deg/deg)");
        }

        /// <summary>
        /// ⭐ THE FIX, PINNED AT EXACTLY ZERO.
        ///
        /// The same pure-gaze sweep that makes the shipped core invent 10.4 degrees of chest pitch, run
        /// against the cue the rig now actually uses: the NECK, reconstructed rigidly off the head.
        ///
        /// The two lever arms cancel algebraically -- the head's orbit about the neck, and the neck's offset
        /// carried back along the rotated head -- so this is not damped or faded or clamped to something
        /// small. It is ZERO, for any gaze, at any angle, on any rig. There is nothing here left to tune,
        /// which is the entire point: a fade would just relocate the problem, as it did for the elbow.
        /// </summary>
        [Test]
        public void TheNeckCue_InventsNoLeanAtAll_ForAnyGaze()
        {
            // Exactly what BasisFullIKConstraintJob.Create bakes: the neck's offset from the head, in the
            // head's own rest frame. Rest rotation is identity here, so it is simply (neck - head).
            Vector3 tposeHeadToNeckLocal = k_Neck - k_Head;

            var report = new StringBuilder();
            report.AppendLine("THE FIX: the same pure-gaze sweep, cued off the reconstructed NECK");
            report.AppendLine($"{"gaze down",10} {"raw bend",10} {"squish",8} {"TOTAL chest pitch",18}");
            report.AppendLine(new string('-', 50));

            for (float gaze = -80f; gaze <= 80f; gaze += 5f)
            {
                GazeDown(gaze, out Vector3 headPos, out Quaternion headRot);

                // The rig's line, verbatim: neckCue = headTargetPos + headWorldRot * tposeHeadToNeckLocal.
                Vector3 neckCue = headPos + headRot * tposeHeadToNeckLocal;

                // It must reconstruct the neck EXACTLY -- that is the whole mechanism.
                Assert.AreEqual(0f, Vector3.Distance(neckCue, k_Neck), 1e-5f,
                    $"the reconstructed neck must land on the real neck at gaze {gaze:F0} deg");

                BasisSpineBendInput i = BaseInput();
                i.SmoothedHead = neckCue;
                i.HeadTargetRot = headRot;
                i.SquishBoost = 0.5f;                              // the shipped default -- ON, deliberately
                i.RestLen = (k_Neck - k_Hips).magnitude;           // hips->NECK, as the rig now bakes it

                BasisSpineBendCore.Solve(i, out BasisSpineBendResult r);
                Assert.IsFalse(r.EarlyOut);

                float total = r.SpineEuler.x + r.UpperEuler.x;
                if (Mathf.Abs(gaze) % 20f < 1f)
                {
                    report.AppendLine($"{gaze,10:F0} {r.BendPitchDeg,10:F3} {r.SquishMult,8:F3} {total,18:F3}");
                }

                Assert.AreEqual(0f, r.BendPitchDeg, 1e-3f,
                    $"a pure gaze of {gaze:F0} deg must bend the spine by EXACTLY nothing");
                Assert.AreEqual(0f, total, 1e-3f,
                    $"...and therefore pitch the chest by exactly nothing ({total:F4} deg at gaze {gaze:F0})");

                // And the squish coupling must not fire either: the neck did not move, so the spine did not
                // compress, so there is nothing to amplify. Gazing down used to shorten hips->HEAD and drive
                // this to 1.42x -- multiplying a phantom bend by a phantom squish.
                Assert.AreEqual(1f, r.SquishMult, 1e-3f,
                    $"a gaze must not compress the spine (squish {r.SquishMult:F3} at gaze {gaze:F0})");
            }

            report.AppendLine();
            report.AppendLine("Zero. Not faded, not damped, not clamped -- CANCELLED, for every gaze, on every rig.");
            Debug.Log(report.ToString());
        }
    }
}
