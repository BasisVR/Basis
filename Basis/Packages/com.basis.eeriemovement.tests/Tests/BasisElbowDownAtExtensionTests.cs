using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisElbowDownAtExtensionTests
    {
        const float Upper = 0.28f, Fore = 0.26f;
        static readonly Vector3 Shoulder = new Vector3(-0.17f, 1.40f, 0f);
        static readonly Vector3 LateralDir = new Vector3(-1f, 0f, 0f);   // left arm straight out: the T-pose
        static BasisSwivelFrame Frame() => new BasisSwivelFrame
        {
            Right = Vector3.right, Up = Vector3.up, Forward = Vector3.forward, Valid = true,
        };
        static void Bind(float bindAzDeg, float bendDeg, out Vector3 elbow, out Vector3 hand, out Quaternion rootRot)
        {
            Vector3 bulge = (Mathf.Cos(bindAzDeg * Mathf.Deg2Rad) * Vector3.down + Mathf.Sin(bindAzDeg * Mathf.Deg2Rad) * Vector3.forward).normalized;
            Vector3 upperDir = (LateralDir * Mathf.Cos(bendDeg * Mathf.Deg2Rad) + bulge * Mathf.Sin(bendDeg * Mathf.Deg2Rad)).normalized;
            Vector3 lowerDir = (LateralDir * Mathf.Cos(bendDeg * Mathf.Deg2Rad) - bulge * Mathf.Sin(bendDeg * Mathf.Deg2Rad)).normalized;
            elbow = Shoulder + upperDir * Upper;
            hand = elbow + lowerDir * Fore;
            rootRot = Quaternion.FromToRotation(Vector3.up, upperDir);
        }
        static float HumeralRollDeg(float bindAzDeg, float reach)
        {
            float armLen = Upper + Fore;
            Bind(bindAzDeg, 8f, out Vector3 elbow, out Vector3 hand, out Quaternion rootRot);
            Vector3 target = Shoulder + LateralDir * (armLen * reach);
            bool hasHint = BasisSwivelHintCore.ArmHint(Frame(), Shoulder, target, armLen, isLeft: true, out Vector3 hint, out float conf, useNeural: false);
            BasisArmSolveInput i = default;
            i.Shoulder = Shoulder; i.Elbow = elbow; i.Hand = hand;
            i.RootRotation = rootRot; i.MidRotation = rootRot;
            i.TargetPosition = target; i.TargetRotation = rootRot;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hint; i.HintWeight = hasHint; i.HintIsTracker = false;
            i.PlayerUp = Vector3.up; i.HintMaxStepDeg = float.MaxValue;
            i.TipRotation = default; i.HintRotation = default;

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Vector3 humerus = (r.ElbowSolved - Shoulder).normalized;
            Quaternion delta = r.RootRotationSolved * Quaternion.Inverse(rootRot);
            float s = delta.x * humerus.x + delta.y * humerus.y + delta.z * humerus.z, c = delta.w;
            if (c < 0f) { s = -s; c = -c; }
            return 2f * Mathf.Atan2(s, c) * Mathf.Rad2Deg;
        }
        [Test]
        public void OnAStraightLateralArm_TheModelWantsTheElbowRoughlyDown()
        {
            float armLen = Upper + Fore;
            Vector3 target = Shoulder + LateralDir * (armLen * 0.98f);

            Assert.That(BasisSwivelHintCore.ArmHint(Frame(), Shoulder, target, armLen, isLeft: true, out Vector3 hint, out float conf, useNeural: false), Is.True);

            Vector3 ac = (target - Shoulder).normalized;
            Vector3 perp = (hint - Shoulder) - ac * Vector3.Dot(hint - Shoulder, ac);
            Vector3 down = (Vector3.down - ac * Vector3.Dot(Vector3.down, ac)).normalized;
            Vector3 fwd = Vector3.Cross(ac, down).normalized;
            float sgn = Vector3.Dot(fwd, Vector3.forward) >= 0f ? 1f : -1f;
            float azDeg = Mathf.Atan2(Vector3.Dot(perp.normalized, fwd) * sgn, Vector3.Dot(perp.normalized, down)) * Mathf.Rad2Deg;

            Assert.That(Mathf.Abs(azDeg), Is.LessThan(40f), $"the model wants the elbow {azDeg:F1} deg off straight-down on a T-pose; it was 52 deg back before the down bias");
        }
        [Test]
        public void AStraightArm_DoesNotCarryAHugeHumeralTwist([Values(0.97f, 0.99f, 1.00f)] float reach)
        {
            float roll = HumeralRollDeg(0f, reach);   // avatar bound with the elbow straight down

            Assert.That(Mathf.Abs(roll), Is.LessThan(40f), $"an elbow-down bind carries {Mathf.Abs(roll):F1} deg of humeral twist at reach {reach:F2} (was 53 deg)");
        }
        [Test]
        public void BelowTheReachGate_TheFieldIsUntouched()
        {
            // 0.90 is ElbowDownReachStart, where the smoothstep is exactly 0.
            float roll = HumeralRollDeg(0f, BasisSwivelHintCore.ElbowDownReachStart);
            Assert.That(Mathf.Abs(roll), Is.EqualTo(46.62f).Within(0.1f),"at the gate's start the solve must be bit-identical to the un-biased field");
        }
        [Test]
        public void TheRollStaysWellConditioned([Values(0.95f, 0.98f, 1.00f)] float reach)
        {
            float armLen = Upper + Fore, r0 = HumeralRollDeg(0f, reach);
            float r1 = HumeralRollDeg(0f, reach - 0.001f / armLen);   // ~1 mm of hand travel

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(r0, r1)), Is.LessThan(1f),"a millimetre of hand travel must not swing the humerus: that would be a singularity, not an offset");
        }
        [Test]
        public void NoDiscontinuityAcrossTheGate()
        {
            float prev = float.NaN;
            for (float reach = 0.85f; reach <= 1.0001f; reach += 0.005f)
            {
                float roll = HumeralRollDeg(0f, reach);
                if (!float.IsNaN(prev))
                {
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(prev, roll)), Is.LessThan(6f), $"humeral roll jumped crossing reach {reach:F3} -- the down bias must ramp, never switch");
                }
                prev = roll;
            }
        }
    }
}
