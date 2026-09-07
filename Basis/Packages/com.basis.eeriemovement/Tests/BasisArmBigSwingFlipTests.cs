using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmBigSwingFlipTests
    {
        const float k_ArmLen = 0.54f;
        const float healthyLever = 0.02f;   // outside the two topologically-required zero cores
        const float maxHealthyStepDeg = 30f;
        const int elevSteps = 320;          // +80 -> -80 at 0.5 deg per step
        static readonly Vector3 rightShoulder = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 leftShoulder = new Vector3(-0.17f, 1.40f, 0f);
        static BasisSwivelFrame Frame() => BasisSwivelHintCore.BuildFrame(leftShoulder, rightShoulder, new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));
        [Test]
        public void BigSwings_NeverTeleportTheElbow_WhereTheModelHasALever([Values(false, true)] bool isLeft)
        {
            BasisSwivelFrame frame = Frame();
            Vector3 shoulder = isLeft ? leftShoulder : rightShoulder;
            float worst = 0f; int worstAz = 0; float worstElev = 0f, worstCond = 0f;

            for (int azDeg = -180; azDeg < 180; azDeg += 5)
            {
                float az = azDeg * Mathf.Deg2Rad;
                Vector3 prevBend = default; float prevCond = 0f; bool has = false;
                foreach (float reach in new[] { 0.45f, 0.65f, 0.85f })
                {
                    has = false;
                    for (int s = 0; s <= elevSteps; s++)
                    {
                        float elevDeg = Mathf.Lerp(80f, -80f, s / (float)elevSteps), elev = elevDeg * Mathf.Deg2Rad;
                        // +x outward for the arm under test, so the two arms sweep mirrored paths.
                        float outward = Mathf.Sin(az) * Mathf.Cos(elev);
                        Vector3 dir = new Vector3(isLeft ? -outward : outward, Mathf.Sin(elev), Mathf.Cos(az) * Mathf.Cos(elev));
                        Vector3 hand = shoulder + dir * (reach * k_ArmLen);

                        Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, k_ArmLen, isLeft, out Vector3 hint, out float cond), $"ArmHint must produce a hint at az={azDeg} elev={elevDeg:F1} reach={reach:F2}");
                        Vector3 bend = hint - shoulder;
                        Assert.Greater(bend.sqrMagnitude, 1e-8f, "the hint must stand off the shoulder");
                        bend.Normalize();

                        if (has && cond >= healthyLever && prevCond >= healthyLever)
                        {
                            float stepDeg = Vector3.Angle(prevBend, bend);
                            if (stepDeg > worst) { worst = stepDeg; worstAz = azDeg; worstElev = elevDeg; worstCond = Mathf.Min(cond, prevCond); }
                        }
                        prevBend = bend; prevCond = cond; has = true;
                    }
                }
            }

            Assert.Less(worst, maxHealthyStepDeg, $"a 0.5-degree hand step rotated the hint {worst:F1} degrees at az={worstAz}, elev={worstElev:F1} " + $"with a healthy {worstCond:F3} lever. That is the big-swing teleport: the elbow flipping between " + "the model's answer and something else while the model still had a perfectly good opinion. The " +"faded model measured 73 degrees on exactly this sweep.");
        }
        [Test]
        public void HardHintRotations_OnlyHappenInsideTheZeroCores()
        {
            BasisSwivelFrame frame = Frame();
            int hardSteps = 0;

            for (int azDeg = -180; azDeg < 180; azDeg += 5)
            {
                float az = azDeg * Mathf.Deg2Rad;
                foreach (float reach in new[] { 0.45f, 0.55f, 0.65f, 0.75f, 0.85f })
                {
                    Vector3 prevBend = default; float prevCond = 0f; bool has = false;
                    for (int s = 0; s <= elevSteps; s++)
                    {
                        float elev = Mathf.Lerp(80f, -80f, s / (float)elevSteps) * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(Mathf.Sin(az) * Mathf.Cos(elev), Mathf.Sin(elev), Mathf.Cos(az) * Mathf.Cos(elev));
                        Vector3 hand = rightShoulder + dir * (reach * k_ArmLen);

                        if (!BasisSwivelHintCore.ArmHint(frame, rightShoulder, hand, k_ArmLen, false, out Vector3 hint, out float cond)) { has = false; continue; }
                        Vector3 bend = (hint - rightShoulder).normalized;
                        if (has)
                        {
                            float stepDeg = Vector3.Angle(prevBend, bend);
                            if (stepDeg > maxHealthyStepDeg)
                            {
                                hardSteps++;
                                Assert.Less(Mathf.Min(cond, prevCond), healthyLever, $"a {stepDeg:F1}-degree hint rotation at az={azDeg} reach={reach:F2} sits at a " + $"lever of {Mathf.Min(cond, prevCond):F3} -- outside the zero cores. A hard " +"rotation is only permitted where the projected bend has genuinely collapsed.");
                            }
                        }
                        prevBend = bend; prevCond = cond; has = true;
                    }
                }
            }

            // 72 meridians x 5 shells: the two ~2-degree cores are each clipped by only a handful of paths.
            Assert.Less(hardSteps, 40, $"{hardSteps} hard hint rotations across the sweep -- the zero cores should be clipped by only " + "a few meridians. A count this size means a new singular structure, not the two Poincare-Hopf " +"cores this model is allowed.");
        }
    }
}
