using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisElbowProtectRollTests
    {
        const float arm = 0.60f, upper = 0.30f, k_Fore = 0.30f;
        static readonly Vector3 shoulder = new Vector3(0.17f, 1.40f, 0f);   // right shoulder
        static readonly Vector3 k_Chest = new Vector3(0f, 1.25f, 0f), k_Neck = new Vector3(0f, 1.50f, 0f);
        static readonly Vector3 spine = new Vector3(0f, 1.10f, 0f), k_Hips = new Vector3(0f, 0.95f, 0f);
        static Vector3 SolvedElbow(Vector3 hand)
        {
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(new Vector3(-0.17f, 1.40f, 0f), shoulder, k_Chest, k_Neck);
            Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, arm, false, out Vector3 hint, out _),"the hint model must answer for every reachable hand");

            Vector3 ac = hand - shoulder;
            float d = Mathf.Min(ac.magnitude, arm - 1e-6f);
            Vector3 axis = ac.normalized, pole = (hint - shoulder);
            pole -= axis * Vector3.Dot(pole, axis);
            pole = pole.normalized;

            float along = (upper * upper - k_Fore * k_Fore + d * d) / (2f * d);
            float rho = Mathf.Sqrt(Mathf.Max(upper * upper - along * along, 0f));
            return shoulder + axis * along + pole * rho;
        }
        static BasisElbowProtectInput Input(Vector3 hand)
        {
            BasisElbowProtectInput i = default;
            i.Shoulder = shoulder;
            i.Elbow = SolvedElbow(hand);
            i.Hand = hand;
            i.HasHips = true;
            i.HasSpine = true;
            i.HipsPos = k_Hips;
            i.SpinePos = spine;
            i.ChestPos = k_Chest;
            i.NeckPos = k_Neck;
            i.ChestRadiusBase = 0.11f;
            i.CollisionSkin = 0.01f;
            i.HandRadius = 0.045f;
            i.HandSkin = 0.01f;
            i.PlayerUp = Vector3.up;
            return i;
        }
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
        static float RollPerMillimetre(Vector3 hand)
        {
            BasisElbowProtectInput i0 = Input(hand);
            BasisElbowProtectCore.Solve(i0, out BasisElbowProtectResult r0);
            float baseRoll = AppliedRollDeg(i0, r0), worst = 0f;
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
                    Vector3 dir = new Vector3(cross, -0.05f, z).normalized, hand = shoulder + dir * (ext * arm);
                    float roll = RollPerMillimetre(hand);

                    Assert.Less(roll, gate, $"the upper arm rolled {roll:F1} deg about its OWN long axis for ONE MILLIMETRE of " + $"hand travel, at {ext:P1} extension crossing {cross:F2} over the body. The protect " + "swivels the elbow about the shoulder->hand axis, and at full extension that axis IS " + "the arm's long axis -- so a correction it can no longer use (rho -> 0) lands entirely " +"as SPIN. It must fade out with its own authority.");
                }
            }
        }
        [Test]
        public void TheProtect_IsUntouched_WhereItStillHasAuthority()
        {
            foreach (float ext in new[] { 0.60f, 0.75f, 0.85f, 0.95f })
            {
                Vector3 dir = new Vector3(-0.75f, -0.05f, 0.659f).normalized, hand = shoulder + dir * (ext * arm);
                BasisElbowProtectInput i = Input(hand);
                BasisElbowProtectCore.Solve(i, out BasisElbowProtectResult r);

                Assert.IsTrue(r.Engaged, $"crossing the body at {ext:P0} must still engage the protect -- if it does not, this test " +"is no longer exercising the thing it guards");
                Assert.Greater(r.SwingAngleDeg, 1f, $"at {ext:P0} extension the elbow's circle radius is still centimetres wide, so the protect " + "has real positional authority and must still USE it. A swing of ~0 here means the " +"authority fade has eaten the feature instead of the singularity.");
            }
        }
        [Test]
        public void PastFullExtension_TheProtect_IsTheExactIdentity()
        {
            foreach (float ext in new[] { 0.996f, 0.998f, 0.9999f })
            {
                Vector3 dir = new Vector3(-0.75f, -0.05f, 0.659f).normalized, hand = shoulder + dir * (ext * arm);
                BasisElbowProtectInput i = Input(hand);
                BasisElbowProtectCore.Solve(i, out BasisElbowProtectResult r);

                if (!r.Engaged) continue;   // not penetrating here is fine; there is nothing to guard

                Assert.AreEqual(0f, r.SwingAngleDeg, 1e-4f, $"at {ext:P2} extension the protect must command NO swing: its lever arm is gone, so every " +"degree it asks for is paid entirely in arm roll and buys no elbow displacement at all");
                Assert.AreEqual(0f, Vector3.Distance(i.Elbow, r.DesiredElbow), 1e-6f, "DesiredElbow must be the elbow we handed in, exactly -- so the swing that realises it is " +"the identity and cannot leak roll");
            }
        }
    }
}
