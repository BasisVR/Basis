using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Guards <see cref="BasisElbowProtectCore"/> against spinning the arm about its own long axis.
    ///
    /// ================================================================================================
    /// THE BUG, IN THE USER'S OWN WORDS: "if i fully extend my arm and then cross over my body all the
    /// way to the left as much as humanly possible the elbow does a nasty rotation."
    ///
    /// Both halves of that sentence are load-bearing, and neither alone reproduces it:
    ///
    ///   CROSS THE BODY -> the upper arm presses into the chest, so the protect ENGAGES. It only ever
    ///                     runs on a torso penetration, which is why nothing else in the workspace shows
    ///                     this.
    ///   FULLY EXTEND   -> the elbow's circle radius rho collapses to nothing.
    ///
    /// The protect clears the torso by SWIVELLING the elbow about the shoulder->hand axis. That rotation
    /// buys elbow displacement of rho * theta -- and it costs upper-arm ROLL of theta, which does NOT
    /// scale with rho. So as the arm straightens the benefit goes to zero and the cost stays whole: the
    /// swivel axis IS the arm's own long axis by then, and the entire correction lands as spin.
    ///
    /// MEASURED on the shipped code, along exactly that motion: a steady ~47 degree commanded swing, and
    /// at extension >= 0.999 it becomes 47-51 DEGREES OF UPPER-ARM ROLL PER MILLIMETRE OF HAND TRAVEL.
    /// Disable the protect and the same sweep measures 0.0. It is unambiguously this file.
    ///
    /// ⭐ WHY NOTHING CAUGHT IT: ROLL MOVES NO JOINT. Rotating the arm about shoulder->hand leaves the
    /// hand exactly on target and the elbow (at full extension, on the axis) exactly put. Every gate in
    /// this repo scores POSITIONS -- hand error, elbow travel, reach, clearance, mocap accuracy. All of
    /// them are structurally incapable of seeing a limb spin. This is the same blind spot that hid the
    /// solve cores' 180-degree roll (BasisLimbRollConditioningTests), and this is a third instance of it.
    ///
    /// ⭐ AND WHY THE OBVIOUS FIX IS WRONG: fading on rho does NOT work. rho = sqrt(upper^2 - (d/2)^2) is
    /// SQUARE-ROOT SINGULAR in the hand position, so its own gradient blows up exactly where the fade has
    /// to be gentle -- it converts the cliff into a steep ramp and measures 9-10 deg/mm. The fade must key
    /// on the REACH RATIO, whose derivative is bounded by 1/armLen. That is the difference between a fix
    /// and a relocation, and this codebase has relocated a discontinuity and called it a fix before.
    /// ================================================================================================
    /// </summary>
    public class BasisElbowProtectRollTests
    {
        const float k_Arm = 0.60f, k_Upper = 0.30f, k_Fore = 0.30f;

        static readonly Vector3 k_Shoulder = new Vector3(0.17f, 1.40f, 0f);   // right shoulder
        static readonly Vector3 k_Chest = new Vector3(0f, 1.25f, 0f);
        static readonly Vector3 k_Neck = new Vector3(0f, 1.50f, 0f);
        static readonly Vector3 k_Spine = new Vector3(0f, 1.10f, 0f);
        static readonly Vector3 k_Hips = new Vector3(0f, 0.95f, 0f);

        /// <summary>The elbow the arm solve produces: on its circle, at the shipped model's pole.</summary>
        static Vector3 SolvedElbow(Vector3 hand)
        {
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(
                new Vector3(-0.17f, 1.40f, 0f), k_Shoulder, k_Chest, k_Neck);
            Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, k_Shoulder, hand, k_Arm, false,
                                                      out Vector3 hint, out _),
                          "the hint model must answer for every reachable hand");

            Vector3 ac = hand - k_Shoulder;
            float d = Mathf.Min(ac.magnitude, k_Arm - 1e-6f);
            Vector3 axis = ac.normalized;
            Vector3 pole = (hint - k_Shoulder);
            pole -= axis * Vector3.Dot(pole, axis);
            pole = pole.normalized;

            float along = (k_Upper * k_Upper - k_Fore * k_Fore + d * d) / (2f * d);
            float rho = Mathf.Sqrt(Mathf.Max(k_Upper * k_Upper - along * along, 0f));
            return k_Shoulder + axis * along + pole * rho;
        }

        static BasisElbowProtectInput Input(Vector3 hand)
        {
            BasisElbowProtectInput i = default;
            i.Shoulder = k_Shoulder;
            i.Elbow = SolvedElbow(hand);
            i.Hand = hand;
            i.HasHips = true;
            i.HasSpine = true;
            i.HipsPos = k_Hips;
            i.SpinePos = k_Spine;
            i.ChestPos = k_Chest;
            i.NeckPos = k_Neck;
            i.ChestRadiusBase = 0.11f;
            i.CollisionSkin = 0.01f;
            i.HandRadius = 0.045f;
            i.HandSkin = 0.01f;
            i.PlayerUp = Vector3.up;
            return i;
        }

        /// <summary>
        /// The angle BasisFullBodyIK.SwingElbowAroundAC will rotate the UPPER ARM by, about the
        /// shoulder->hand axis, to honour this protect result. At full extension that axis IS the arm's
        /// own long axis, so this number is the arm's ROLL. Reproduced here rather than called because
        /// SwingElbowAroundAC takes an AnimationStream, which an EditMode test has no way to build.
        /// </summary>
        static float AppliedRollDeg(in BasisElbowProtectInput i, in BasisElbowProtectResult r)
        {
            if (!r.Engaged) return 0f;

            Vector3 ac = i.Hand - i.Shoulder;
            if (ac.sqrMagnitude <= 1e-8f) return 0f;
            Vector3 n = ac.normalized;

            Vector3 v1 = i.Elbow - i.Shoulder; v1 -= n * Vector3.Dot(v1, n);
            Vector3 v2 = r.DesiredElbow - i.Shoulder; v2 -= n * Vector3.Dot(v2, n);
            if (v1.sqrMagnitude <= 1e-8f || v2.sqrMagnitude <= 1e-8f) return 0f;

            float dot = Mathf.Clamp(Vector3.Dot(v1.normalized, v2.normalized), -1f, 1f);
            float ang = Mathf.Acos(dot) * Mathf.Rad2Deg;
            return ang * Mathf.Sign(Vector3.Dot(Vector3.Cross(v1, v2), n));
        }

        /// <summary>Degrees of upper-arm roll per millimetre of hand travel — the quantity that was 47-51.</summary>
        static float RollPerMillimetre(Vector3 hand)
        {
            BasisElbowProtectInput i0 = Input(hand);
            BasisElbowProtectCore.Solve(i0, out BasisElbowProtectResult r0);
            float baseRoll = AppliedRollDeg(i0, r0);

            float worst = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 h = hand;
                    if (axis == 0) h.x += s * 0.001f;
                    else if (axis == 1) h.y += s * 0.001f;
                    else h.z += s * 0.001f;

                    BasisElbowProtectInput i2 = Input(h);
                    BasisElbowProtectCore.Solve(i2, out BasisElbowProtectResult r2);
                    float d = Mathf.Abs(Mathf.DeltaAngle(baseRoll, AppliedRollDeg(i2, r2)));
                    worst = Mathf.Max(worst, d);
                }
            }
            return worst;
        }

        /// <summary>
        /// ⭐ THE USER'S MOTION. Arm fully extended, hand swept across the body to the far left. The protect
        /// engages the whole way (that is the point of crossing), and the arm must not spin.
        ///
        /// The gate is 5 deg/mm. Below 95% extension the protect runs at FULL authority and measures
        /// 0.0-0.2; through the fade band it peaks at 2.6; past 99.5% it is off and measures exactly 0.0.
        /// The shipped code measured 47-51 across the last three rows of this same sweep.
        /// </summary>
        [Test]
        public void TheArm_DoesNotSpin_WhenFullyExtendedAndCrossedOverTheBody()
        {
            const float gate = 5.0f;

            foreach (float ext in new[] { 0.85f, 0.90f, 0.95f, 0.97f, 0.98f, 0.99f, 0.995f, 0.999f, 0.9999f })
            {
                foreach (float cross in new[] { -0.55f, -0.75f, -0.90f })
                {
                    // shoulder-local: -x is INBOARD (across the body) for the right arm, +z is forward
                    float z = Mathf.Sqrt(Mathf.Max(1f - cross * cross - 0.0025f, 0.01f));
                    Vector3 dir = new Vector3(cross, -0.05f, z).normalized;
                    Vector3 hand = k_Shoulder + dir * (ext * k_Arm);

                    float roll = RollPerMillimetre(hand);

                    Assert.Less(roll, gate,
                        $"the upper arm rolled {roll:F1} deg about its OWN long axis for ONE MILLIMETRE of " +
                        $"hand travel, at {ext:P1} extension crossing {cross:F2} over the body. The protect " +
                        "swivels the elbow about the shoulder->hand axis, and at full extension that axis IS " +
                        "the arm's long axis -- so a correction it can no longer use (rho -> 0) lands entirely " +
                        "as SPIN. It must fade out with its own authority.");
                }
            }
        }

        /// <summary>
        /// The other half of the contract: the fade must not quietly disable the protect where it still
        /// works. Below the fade window the swing must be EXACTLY what it always was -- if this drifts, the
        /// fix has been paid for with the feature.
        /// </summary>
        [Test]
        public void TheProtect_IsUntouched_WhereItStillHasAuthority()
        {
            foreach (float ext in new[] { 0.60f, 0.75f, 0.85f, 0.95f })
            {
                Vector3 dir = new Vector3(-0.75f, -0.05f, 0.659f).normalized;
                Vector3 hand = k_Shoulder + dir * (ext * k_Arm);

                BasisElbowProtectInput i = Input(hand);
                BasisElbowProtectCore.Solve(i, out BasisElbowProtectResult r);

                Assert.IsTrue(r.Engaged,
                    $"crossing the body at {ext:P0} must still engage the protect -- if it does not, this test " +
                    "is no longer exercising the thing it guards");
                Assert.Greater(r.SwingAngleDeg, 1f,
                    $"at {ext:P0} extension the elbow's circle radius is still centimetres wide, so the protect " +
                    "has real positional authority and must still USE it. A swing of ~0 here means the " +
                    "authority fade has eaten the feature instead of the singularity.");
            }
        }

        /// <summary>
        /// Past the fade the protect must be the EXACT identity, not merely small. If DesiredElbow comes back
        /// bit-for-bit as the elbow we handed in, then SwingElbowAroundAC sees v1 == v2 and applies
        /// Quaternion.identity -- there is no residual roll to leak, structurally rather than by tolerance.
        /// </summary>
        [Test]
        public void PastFullExtension_TheProtect_IsTheExactIdentity()
        {
            foreach (float ext in new[] { 0.996f, 0.998f, 0.9999f })
            {
                Vector3 dir = new Vector3(-0.75f, -0.05f, 0.659f).normalized;
                Vector3 hand = k_Shoulder + dir * (ext * k_Arm);

                BasisElbowProtectInput i = Input(hand);
                BasisElbowProtectCore.Solve(i, out BasisElbowProtectResult r);

                if (!r.Engaged) continue;   // not penetrating here is fine; there is nothing to guard

                Assert.AreEqual(0f, r.SwingAngleDeg, 1e-4f,
                    $"at {ext:P2} extension the protect must command NO swing: its lever arm is gone, so every " +
                    "degree it asks for is paid entirely in arm roll and buys no elbow displacement at all");
                Assert.AreEqual(0f, Vector3.Distance(i.Elbow, r.DesiredElbow), 1e-6f,
                    "DesiredElbow must be the elbow we handed in, exactly -- so the swing that realises it is " +
                    "the identity and cannot leak roll");
            }
        }
    }
}
