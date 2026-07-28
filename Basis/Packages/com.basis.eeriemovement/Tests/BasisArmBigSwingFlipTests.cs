using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "Doing big arm swing motions (like up down for example) it flips drastically."
    ///
    /// The whole suite gated flips by WORKSPACE AREA (fraction of random poses over an elbow-gain
    /// threshold) and the flip passed every one of those gates, because a flip is not an area -- it is
    /// a SURFACE a moving hand crosses. A big up-down swing is a meridian path, so these tests sweep
    /// MERIDIANS: every azimuth around the shoulder, elevation +80 to -80, through the LIVE hint entry
    /// (BasisSwivelHintCore.ArmHint -- frame, mirror, field model, tuck), the same call SolveHand makes.
    ///
    /// WHAT THE FLIP WAS. BasisElbowFieldModel used to fade its projected bend toward a fixed rest pole
    /// below a 0.10 lever: normalizesafe(primary*w + rest*(1-w), rest). A lerp of two unit directions
    /// passes through ZERO where they are antipodal and w crosses 0.5 -- normalize(~0) -- and that
    /// cancellation surface cut through HEALTHY workspace (lever 0.05-0.07): hand-across-the-body at
    /// 0.45 reach teleported the elbow 28.7 cm in one 0.5-degree hand step; the down-back follow-through
    /// of a swing at 0.65 reach did the same at 108 degrees of hint rotation. Deterministic, no noise
    /// involved: it is math, and these sweeps reproduce it on the old code and pin it dead on the new.
    ///
    /// WHAT CANNOT BE PINNED. The bend is a tangent field on the sphere of hand directions, so by
    /// Poincare-Hopf it has total index 2: TWO zero cores per reach shell (across-body-up at ~0.75
    /// reach, down-back at ~0.65). Near a core the direction spins fast -- that is topology, not a bug,
    /// and no stateless formula removes it (the deleted fade just traded it for the teleport surface
    /// above). So the contract is: WHEREVER THE MODEL HAS A REAL LEVER (conditioning >= 0.02, which is
    /// everywhere outside ~2-degree cones around the two cores), a 0.5-degree hand step may not rotate
    /// the hint more than 30 degrees. The old fade fails this at 73 degrees; the projection passes with
    /// a worst of ~25.
    /// </summary>
    public class BasisArmBigSwingFlipTests
    {
        const float k_ArmLen = 0.54f;
        const float k_HealthyLever = 0.02f;   // outside the two topologically-required zero cores
        const float k_MaxHealthyStepDeg = 30f;
        const int k_ElevSteps = 320;          // +80 -> -80 at 0.5 deg per step

        static readonly Vector3 k_RightShoulder = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 k_LeftShoulder = new Vector3(-0.17f, 1.40f, 0f);

        static BasisSwivelFrame Frame() => BasisSwivelHintCore.BuildFrame(
            k_LeftShoulder, k_RightShoulder, new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));

        /// <summary>
        /// ⭐⭐ THE SWING. Every meridian, three reaches, both arms. Wherever the model's lever is real,
        /// no single hand step may rotate the hint by more than 30 degrees. This is the test that was
        /// missing: on the faded model it fails at az -90 (hand across the body), reach 0.45, elevation
        /// ~37 -- hint rotating 73 degrees in one 0.5-degree step with a HEALTHY 0.057 lever.
        /// </summary>
        [Test]
        public void BigSwings_NeverTeleportTheElbow_WhereTheModelHasALever([Values(false, true)] bool isLeft)
        {
            BasisSwivelFrame frame = Frame();
            Vector3 shoulder = isLeft ? k_LeftShoulder : k_RightShoulder;
            float worst = 0f; int worstAz = 0; float worstElev = 0f, worstCond = 0f;

            for (int azDeg = -180; azDeg < 180; azDeg += 5)
            {
                float az = azDeg * Mathf.Deg2Rad;
                Vector3 prevBend = default; float prevCond = 0f; bool has = false;
                foreach (float reach in new[] { 0.45f, 0.65f, 0.85f })
                {
                    has = false;
                    for (int s = 0; s <= k_ElevSteps; s++)
                    {
                        float elevDeg = Mathf.Lerp(80f, -80f, s / (float)k_ElevSteps);
                        float elev = elevDeg * Mathf.Deg2Rad;
                        // +x outward for the arm under test, so the two arms sweep mirrored paths.
                        float outward = Mathf.Sin(az) * Mathf.Cos(elev);
                        Vector3 dir = new Vector3(isLeft ? -outward : outward, Mathf.Sin(elev), Mathf.Cos(az) * Mathf.Cos(elev));
                        Vector3 hand = shoulder + dir * (reach * k_ArmLen);

                        Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, k_ArmLen, isLeft,
                                                                  out Vector3 hint, out float cond),
                            $"ArmHint must produce a hint at az={azDeg} elev={elevDeg:F1} reach={reach:F2}");
                        Vector3 bend = hint - shoulder;
                        Assert.Greater(bend.sqrMagnitude, 1e-8f, "the hint must stand off the shoulder");
                        bend.Normalize();

                        if (has && cond >= k_HealthyLever && prevCond >= k_HealthyLever)
                        {
                            float stepDeg = Vector3.Angle(prevBend, bend);
                            if (stepDeg > worst) { worst = stepDeg; worstAz = azDeg; worstElev = elevDeg; worstCond = Mathf.Min(cond, prevCond); }
                        }
                        prevBend = bend; prevCond = cond; has = true;
                    }
                }
            }

            Assert.Less(worst, k_MaxHealthyStepDeg,
                $"a 0.5-degree hand step rotated the hint {worst:F1} degrees at az={worstAz}, elev={worstElev:F1} " +
                $"with a healthy {worstCond:F3} lever. That is the big-swing teleport: the elbow flipping between " +
                "the model's answer and something else while the model still had a perfectly good opinion. The " +
                "faded model measured 73 degrees on exactly this sweep.");
        }

        /// <summary>
        /// The two zero cores are REQUIRED to exist (total index 2), but they must stay CONFINED: any step
        /// that does rotate the hint hard must sit at a collapsed lever, i.e. inside a core -- never out in
        /// workspace the model is confident about. On the faded model 4-7 hard steps per reach shell sat at
        /// levers of 0.05-0.07; here every one must be under 0.02, and there must be FEW of them in total
        /// (the cores subtend ~2 degrees; a fat count means a new singular structure has crept in).
        /// </summary>
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
                    for (int s = 0; s <= k_ElevSteps; s++)
                    {
                        float elev = Mathf.Lerp(80f, -80f, s / (float)k_ElevSteps) * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(Mathf.Sin(az) * Mathf.Cos(elev), Mathf.Sin(elev), Mathf.Cos(az) * Mathf.Cos(elev));
                        Vector3 hand = k_RightShoulder + dir * (reach * k_ArmLen);

                        if (!BasisSwivelHintCore.ArmHint(frame, k_RightShoulder, hand, k_ArmLen, false,
                                                         out Vector3 hint, out float cond)) { has = false; continue; }
                        Vector3 bend = (hint - k_RightShoulder).normalized;
                        if (has)
                        {
                            float stepDeg = Vector3.Angle(prevBend, bend);
                            if (stepDeg > k_MaxHealthyStepDeg)
                            {
                                hardSteps++;
                                Assert.Less(Mathf.Min(cond, prevCond), k_HealthyLever,
                                    $"a {stepDeg:F1}-degree hint rotation at az={azDeg} reach={reach:F2} sits at a " +
                                    $"lever of {Mathf.Min(cond, prevCond):F3} -- outside the zero cores. A hard " +
                                    "rotation is only permitted where the projected bend has genuinely collapsed.");
                            }
                        }
                        prevBend = bend; prevCond = cond; has = true;
                    }
                }
            }

            // 72 meridians x 5 shells: the two ~2-degree cores are each clipped by only a handful of paths.
            Assert.Less(hardSteps, 40,
                $"{hardSteps} hard hint rotations across the sweep -- the zero cores should be clipped by only " +
                "a few meridians. A count this size means a new singular structure, not the two Poincare-Hopf " +
                "cores this model is allowed.");
        }
    }
}
