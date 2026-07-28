using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// The elbow tuck: a small styling lean of the NO-TRACKER elbow pole toward the body, requested from
    /// the headset ("elbow tuck a little bit"). The field model predicts the corpus-MEAN elbow; the tuck
    /// biases it a few degrees inward-down on top.
    ///
    /// What these tests hold it to: it is SMALL (bounded by its weight, everywhere on the sphere of hand
    /// directions), it points INWARD on both arms (one mirrored-frame pole, no handedness flag — the
    /// mirror test is what makes that falsifiable), it preserves the bend's perpendicularity to the
    /// shoulder→hand axis exactly (the solver relies on it), and it FADES ITSELF OUT as the arm approaches
    /// the tuck pole's own direction instead of snapping — the blend term's magnitude is the projection,
    /// so the degeneracy is its own fade.
    /// </summary>
    public class BasisElbowTuckTests
    {
        const float ArmLen = 0.55f;
        static readonly Vector3 ShoulderR = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 ShoulderL = new Vector3(-0.17f, 1.40f, 0f);

        static BasisSwivelFrame Frame() => BasisSwivelHintCore.BuildFrame(
            ShoulderL, ShoulderR, new Vector3(0f, 1.30f, 0f), new Vector3(0f, 1.50f, 0f));

        /// <summary>ArmHint's own construction minus the tuck — the untucked reference.</summary>
        static Vector3 RawBend(in BasisSwivelFrame fr, Vector3 shoulder, Vector3 hand, bool isLeft)
        {
            BasisSwivelHintCore.Features(fr, shoulder, hand, ArmLen, isLeft, out var tip);
            var el = BasisElbowFieldModel.Elbow(tip);
            var bend = BasisElbowFieldModel.BendDirection(tip, el, out _);
            Vector3 bOut = isLeft ? -fr.Right : fr.Right;
            return bend.x * bOut + bend.y * fr.Up + bend.z * fr.Forward;
        }

        static Vector3 TuckedBend(in BasisSwivelFrame fr, Vector3 shoulder, Vector3 hand, bool isLeft)
        {
            Assert.That(BasisSwivelHintCore.ArmHint(fr, shoulder, hand, ArmLen, isLeft, out Vector3 hint, out _),
                Is.True, "ArmHint must produce a hint for an ordinary reach");
            return (hint - shoulder) / (0.5f * ArmLen);
        }

        [Test]
        public void Tuck_LeansThePoleInward_ALittle_OnBothArms()
        {
            BasisSwivelFrame fr = Frame();

            Vector3 handR = ShoulderR + new Vector3(0.05f, -0.10f, 0.40f);
            Vector3 raw = RawBend(fr, ShoulderR, handR, false);
            Vector3 tucked = TuckedBend(fr, ShoulderR, handR, false);

            float angle = Vector3.Angle(raw, tucked);
            Assert.That(angle, Is.InRange(2f, 8.5f), "the tuck must be a little bit — present, and only a little");
            Assert.That(Vector3.Dot(tucked, fr.Right), Is.LessThan(Vector3.Dot(raw, fr.Right) + 1e-5f),
                "a right-arm tuck moves the pole INWARD (less +Right), not out");
            Assert.That(Mathf.Abs(Vector3.Dot(tucked, (handR - ShoulderR).normalized)), Is.LessThan(1e-3f),
                "the tuck must preserve the bend's perpendicularity to shoulder→hand");

            Vector3 handL = ShoulderL + new Vector3(-0.05f, -0.10f, 0.40f);
            Vector3 tuckedL = TuckedBend(fr, ShoulderL, handL, true);
            Assert.That(Vector3.Distance(tuckedL, new Vector3(-tucked.x, tucked.y, tucked.z)), Is.LessThan(1e-4f),
                "the left arm's tuck must be the exact mirror of the right's — one pole, no handedness flag");
        }

        [Test]
        public void Tuck_FadesItselfOut_AlongItsOwnPole()
        {
            BasisSwivelFrame fr = Frame();
            // The tuck pole is (-1, -0.35, 0) in the mirrored frame; for the RIGHT arm that is world
            // (-Right, -0.35 Up). Point the arm along it: the perpendicular projection vanishes and the
            // tuck must go quietly, not snap to a fallback.
            Vector3 poleWorld = (fr.Right * -1f + fr.Up * -0.35f).normalized;
            Vector3 hand = ShoulderR + poleWorld * (0.85f * ArmLen);

            Vector3 raw = RawBend(fr, ShoulderR, hand, false);
            Vector3 tucked = TuckedBend(fr, ShoulderR, hand, false);

            Assert.That(Vector3.Angle(raw, tucked), Is.LessThan(0.6f),
                "with the arm along the tuck pole there is no perpendicular left to blend — the tuck must vanish");
        }

        [Test]
        public void Tuck_IsBounded_AcrossTheWholeSphere()
        {
            BasisSwivelFrame fr = Frame();
            int probes = 0;
            float worst = 0f;
            for (float el = -75f; el <= 75f; el += 15f)
                for (float az = 0f; az < 360f; az += 15f)
                {
                    Vector3 dir = Quaternion.AngleAxis(az, Vector3.up) * (Quaternion.AngleAxis(el, Vector3.right) * Vector3.forward);
                    Vector3 hand = ShoulderR + dir * (0.7f * ArmLen);
                    if (!BasisSwivelHintCore.ArmHint(fr, ShoulderR, hand, ArmLen, false, out Vector3 hint, out _)) continue;
                    Vector3 tucked = (hint - ShoulderR) / (0.5f * ArmLen);
                    Vector3 raw = RawBend(fr, ShoulderR, hand, false);

                    worst = Mathf.Max(worst, Vector3.Angle(raw, tucked));
                    Assert.That(float.IsFinite(tucked.x) && float.IsFinite(tucked.y) && float.IsFinite(tucked.z),
                        Is.True, $"non-finite tucked bend at el {el}, az {az}");
                    Assert.That(Mathf.Abs(Vector3.Dot(tucked, dir)), Is.LessThan(1e-3f),
                        $"perpendicularity lost at el {el}, az {az}");
                    probes++;
                }

            Assert.That(probes, Is.GreaterThan(200), "the sweep must actually cover the sphere");
            Assert.That(worst, Is.LessThan(8.5f), "the tuck's rotation is bounded by its weight, everywhere");
        }
    }
}
