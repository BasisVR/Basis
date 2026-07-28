using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// DOES THE SOLVER ACTUALLY OBEY THE ELBOW HINT? -- at every extension, not just the comfortable ones.
    ///
    /// ================================================================================================
    /// THE BUG THIS EXISTS TO PIN, in the user's own words: "a lot of the time the elbow is on the wrong
    /// side; sometimes it's perfect, then I assign trackers again and it's on the wrong side."
    ///
    /// BasisArmSolveCore faded the hint out on `abProj` -- the ELBOW'S OWN LEVER ARM, which collapses to zero
    /// as the arm STRAIGHTENS:
    ///
    ///     bendNorm  = abProj.magnitude / totalLen
    ///     hintFade *= Clamp01((bendNorm - 0.0316) / (0.12 - 0.0316))
    ///
    /// So past ~97% extension the commanded pole was progressively ignored, and past ~99.8% it was ignored
    /// ENTIRELY -- leaving the elbow wherever the base animation put it. The swivel model could be perfect and
    /// never reach the bone.
    ///
    /// AND THAT IS WHY IT TRACKED CALIBRATION. Give the avatar arms shorter than the user's and their hand
    /// sits at full reach on nearly every frame, so the fade is pinned near zero and the model never drives
    /// the elbow at all. Re-assign trackers, the arm scale changes, and the elbow flips sides. "Sometimes it's
    /// perfect" was simply the calibrations where the arm happened to stay bent.
    ///
    /// The whole POINT of predicting a swivel ANGLE is that the hint lands ON the elbow's circle, so it stays
    /// meaningful at every extension and there is nothing to fade. The solver was fading it anyway.
    /// BasisLegSolveCore had already worked this out and deleted its copy; the arm's survived.
    /// ================================================================================================
    /// </summary>
    public sealed class BasisArmHintAuthorityTests
    {
        const float k_Upper = 0.30f;
        const float k_Lower = 0.30f;
        const float k_ArmLen = k_Upper + k_Lower;
        static readonly Vector3 k_Shoulder = new Vector3(0.17f, 1.40f, 0f);

        /// <summary>Builds the solve input the JOB builds: the arm starts in its ANIMATED pose (elbow bent
        /// somewhere) and is asked to reach `target` with the elbow put on `hintPos`.</summary>
        static BasisArmSolveInput Input(Vector3 animElbow, Vector3 animHand, Vector3 target, Vector3 hintPos)
        {
            BasisArmSolveInput i = default;
            i.Shoulder = k_Shoulder;
            i.Elbow = animElbow;
            i.Hand = animHand;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hintPos;
            i.HintWeight = true;
            i.HintIsTracker = false;          // THE MODEL PATH -- this is the one that must have full authority
            i.HintMaxStepDeg = float.MaxValue; // the live job passes MaxValue (stateless solve, no rate limit)
            i.PlayerUp = Vector3.up;
            return i;
        }

        /// <summary>Where the swivel model would put the HINT: half an arm off the shoulder, perpendicular to
        /// shoulder->hand. Exactly BasisSwivelHintCore.ArmHint's convention. The solver only reads this hint's
        /// DIRECTION (its projection onto the swing plane), so its distance is a convention, not a claim.</summary>
        static Vector3 HintOnCircle(Vector3 target, float swivelDeg)
        {
            float3 bend = BasisArmSwivelModel.BendDirection(
                new float3(target.x - k_Shoulder.x, target.y - k_Shoulder.y, target.z - k_Shoulder.z),
                new float3(0f, -1f, 0f),                      // body DOWN -- the model's swivel zero
                swivelDeg * Mathf.Deg2Rad);
            return k_Shoulder + 0.5f * k_ArmLen * new Vector3(bend.x, bend.y, bend.z);
        }

        /// <summary>
        /// A GEOMETRICALLY REAL ELBOW at swivel `swivelDeg`: on the actual circle of radius r, at the bone
        /// distances the skeleton demands (|shoulder->elbow| == upper, |elbow->hand| == lower).
        ///
        /// ⚠ THIS DISTINCTION IS THE WHOLE TEST. My first version handed the solver the HINT position as the
        /// arm's starting elbow -- half an arm out, perpendicular -- which is not an elbow any skeleton could
        /// hold. The arm therefore never actually approached full extension, its lever arm never collapsed, the
        /// fade under test never fired, and the test passed a bug it was written to catch. A test that cannot
        /// reach the failing state is worse than no test, because it reports safety.
        /// </summary>
        static Vector3 ElbowOnCircle(Vector3 target, float swivelDeg)
        {
            Vector3 sa = target - k_Shoulder;
            float d = sa.magnitude;
            Vector3 axis = sa / d;

            // Standard two-bone circle: the elbow rides a circle of radius `radius`, centred `a` along the axis.
            float a = (k_Upper * k_Upper - k_Lower * k_Lower + d * d) / (2f * d);
            float radius = Mathf.Sqrt(Mathf.Max(k_Upper * k_Upper - a * a, 0f));
            Vector3 centre = k_Shoulder + axis * a;

            // The same in-plane basis BasisArmSwivelModel.BendDirection uses, so `swivelDeg` means the same
            // thing here as it does to the model. Reference = body DOWN.
            Vector3 refDown = Vector3.down;
            Vector3 u = (refDown - axis * Vector3.Dot(refDown, axis)).normalized;
            Vector3 v = Vector3.Cross(axis, u);

            float rad = swivelDeg * Mathf.Deg2Rad;
            return centre + radius * (u * Mathf.Cos(rad) + v * Mathf.Sin(rad));
        }

        /// <summary>
        /// ⭐ THE TEST THAT WAS MISSING. The solver must put the elbow ON the commanded pole at EVERY
        /// extension -- and especially at the near-straight extensions a VR user spends most of their time in.
        ///
        /// The arm's ANIMATED pose deliberately starts the elbow on the WRONG SIDE (swivel 180 deg from the
        /// command), which is exactly what the base animation does to a fully-extended arm. If the solver has
        /// any authority at all, it must drag the elbow all the way across. Before the fix, at 99% extension it
        /// dragged it 43% of the way and at 99.9% not at all -- so the elbow simply STAYED on the wrong side.
        /// </summary>
        [Test]
        public void TheHint_LandsTheElbowOnItsPole_AtEveryExtension()
        {
            const float commandedSwivel = 30f;   // elbow down and a bit back: where a real one hangs

            foreach (float reach in new[] { 0.50f, 0.80f, 0.95f, 0.97f, 0.99f, 0.995f, 0.999f })
            {
                // Hand out to the side and slightly forward -- the canonical VR reach.
                Vector3 dir = new Vector3(0.92f, 0f, 0.39f).normalized;
                Vector3 target = k_Shoulder + dir * (reach * k_ArmLen);

                Vector3 hintPos = HintOnCircle(target, commandedSwivel);

                // The ANIMATED arm starts with its elbow on the OPPOSITE side of its circle -- a REAL elbow at
                // the real bone distances, just on the wrong side. That is exactly what an idle animation hands
                // the solver for an extended arm, and it is the state the solver must be able to rescue.
                Vector3 animElbow = ElbowOnCircle(target, commandedSwivel + 180f);
                Vector3 animHand = target;

                BasisArmSolveCore.Solve(Input(animElbow, animHand, target, hintPos), out BasisArmSolveResult r);

                // 1. The hand must land on its target. Everything else is meaningless if it does not.
                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f,
                    $"the hand must reach its target at {reach:P1} extension");

                // 2. THE ELBOW MUST BE ON THE COMMANDED SIDE. Measured as the signed swivel about the
                //    shoulder->hand axis, which is the only thing the pole actually controls.
                Vector3 axis = (r.HandSolved - k_Shoulder).normalized;
                Vector3 got = r.ElbowSolved - k_Shoulder;
                got -= axis * Vector3.Dot(got, axis);
                Vector3 want = hintPos - k_Shoulder;
                want -= axis * Vector3.Dot(want, axis);

                Assert.Greater(want.sqrMagnitude, 1e-8f, "the commanded pole must be well-defined");

                // At full extension the elbow's circle has collapsed, so its POSITION barely moves -- but its
                // DIRECTION is exactly what the user sees as "which side is my elbow on", and it must be right.
                float offDeg = Vector3.Angle(got.normalized, want.normalized);
                Assert.Less(offDeg, 10f,
                    $"at {reach:P1} extension the elbow sits {offDeg:F1} deg off its commanded pole. " +
                    "The solver is fading the hint out on the elbow's own collapsing lever arm, which leaves " +
                    "the elbow wherever the ANIMATION put it -- on the wrong side. This is the bug.");
            }
        }

        /// <summary>
        /// ...AND IT MUST NOT COST REACH. The swivel is about the shoulder->hand axis by name, and the hand
        /// LIES on that axis, so full hint authority cannot move the hand. Structural, not tuned -- pinned so
        /// nobody "fixes" the elbow by trading the hand away.
        /// </summary>
        [Test]
        public void FullHintAuthority_DoesNotMoveTheHandOffItsTarget()
        {
            var rng = new System.Random(4242);

            for (int t = 0; t < 300; t++)
            {
                // Above the arm core's max-flexion floor: below it the hand is anatomically
                // unreachable and the solver correctly refuses, which is not what this test is about.
                float reach = Mathf.Lerp(0.35f, 0.999f, (float)rng.NextDouble());
                var dir = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();

                Vector3 target = k_Shoulder + dir * (reach * k_ArmLen);
                float swivel = (float)(rng.NextDouble() * 360.0 - 180.0);
                Vector3 hintPos = HintOnCircle(target, swivel);

                Vector3 animElbow = ElbowOnCircle(target, swivel + 137f);   // a real elbow, somewhere unhelpful
                BasisArmSolveCore.Solve(Input(animElbow, target, target, hintPos), out BasisArmSolveResult r);

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f,
                    $"the hand must stay on target at {reach:P1} extension with a {swivel:F0} deg pole (iter {t})");
                Assert.IsTrue(float.IsFinite(r.ElbowSolved.x) && float.IsFinite(r.ElbowSolved.y) && float.IsFinite(r.ElbowSolved.z),
                    "the elbow must stay finite");
            }
        }

        /// <summary>
        /// ⭐ A REAL ELBOW TRACKER GETS FULL AUTHORITY TOO -- and getting this wrong is what has been inverting
        /// the elbow for anyone who actually straps a tracker on.
        ///
        /// I first fixed the fade for the MODEL path only and deliberately LEFT THE TRACKER'S IN PLACE,
        /// reasoning that a tracker hint is "measured, a few centimetres off the bone, so it has noise to
        /// amplify". That reasoning was wrong, and BasisLegSolveCore had already said so in as many words:
        /// "the POLE is commanded, BY A TRACKER OR BY BendNormal, and it stays perfectly well-defined at every
        /// extension."
        ///
        /// The fade was never keyed on the tracker's noise. It was keyed on abProj -- the CURRENT ELBOW's lever
        /// arm -- which collapses as the arm straightens and has nothing whatsoever to do with the tracker. So
        /// the authority the solver actually granted a real tracker was:
        ///
        ///      94% extension -> 1.00     98.5% -> 0.62     99.5% -> 0.21     99.9% -> 0.00
        ///
        /// and 57% of the corpus sits above 95% extension. On most frames of most sessions the user's real
        /// elbow was being blended toward an idle animation they were not performing.
        /// </summary>
        [Test]
        public void ARealTracker_GetsFullAuthority_AtEveryExtension()
        {
            const float commanded = 35f;

            foreach (float reach in new[] { 0.80f, 0.94f, 0.97f, 0.985f, 0.995f, 0.999f })
            {
                Vector3 dir = new Vector3(0.92f, 0f, 0.39f).normalized;
                Vector3 target = k_Shoulder + dir * (reach * k_ArmLen);

                Vector3 hintPos = HintOnCircle(target, commanded);
                Vector3 animElbow = ElbowOnCircle(target, commanded + 180f);   // wrong side, as an idle anim leaves it

                BasisArmSolveInput i = Input(animElbow, target, target, hintPos);
                i.HintIsTracker = true;                       // A REAL TRACKER
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                Assert.AreEqual(1f, r.HintFade, 1e-4f,
                    $"a real elbow tracker must be obeyed at FULL weight at {reach:P1} extension -- it got " +
                    $"{r.HintFade:F2}. Partial weight lands the elbow neither on the tracker NOR on the " +
                    "animation, but BETWEEN them -- and that is the inversion.");

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f,
                    $"and the hand must stay on target at {reach:P1}");

                Vector3 axis = (r.HandSolved - k_Shoulder).normalized;
                Vector3 got = r.ElbowSolved - k_Shoulder; got -= axis * Vector3.Dot(got, axis);
                Vector3 want = hintPos - k_Shoulder; want -= axis * Vector3.Dot(want, axis);
                Assert.Less(Vector3.Angle(got.normalized, want.normalized), 10f,
                    $"the tracked elbow must actually land where the tracker says, at {reach:P1} extension");
            }
        }

        /// <summary>
        /// ⭐ AND THE SOLVER MUST NOT OVERRULE THE TRACKER WITH A GUESS.
        ///
        /// The pole-collapse stabilizer eases the elbow toward WORLD-DOWN. It was written for the old invented
        /// lookup pole, which genuinely could point backward along the arm and flip. A TRACKER is the user's
        /// actual elbow -- and because the stabilizer engages on |ahProj|, which collapses as the arm
        /// straightens, it engaged HARDEST exactly where the user spends most of their time.
        ///
        /// Here the tracker says "elbow out to the side, level", which is a perfectly ordinary human pose. The
        /// solver must not drag it downward.
        /// </summary>
        [Test]
        public void TheStabilizer_DoesNotDragATrackedElbowTowardWorldDown()
        {
            Vector3 dir = new Vector3(0.92f, 0f, 0.39f).normalized;
            Vector3 target = k_Shoulder + dir * (0.99f * k_ArmLen);

            Vector3 hintPos = HintOnCircle(target, 88f);      // elbow out to the side: 88 deg off body-down
            Vector3 animElbow = ElbowOnCircle(target, 0f);

            BasisArmSolveInput i = Input(animElbow, target, target, hintPos);
            i.HintIsTracker = true;
            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Vector3 axis = (r.HandSolved - k_Shoulder).normalized;
            Vector3 got = r.ElbowSolved - k_Shoulder; got -= axis * Vector3.Dot(got, axis);
            Vector3 want = hintPos - k_Shoulder; want -= axis * Vector3.Dot(want, axis);

            Assert.Less(Vector3.Angle(got.normalized, want.normalized), 12f,
                "the solver must not pull a TRACKED elbow toward world-down: the tracker is a measurement of " +
                "the user's actual arm, and overruling a measurement with a guess is exactly the 'not very " +
                "human movement' report");
        }

        /// <summary>
        /// ⭐ THE REALISTIC TRACKER: a puck strapped a few centimetres OFF the bone, with tracking jitter.
        ///
        /// The harness's TruthJoint row feeds the solver the EXACT elbow joint and scores 1.06% -- which is
        /// precisely why none of this ever showed up in the corpus numbers. A real tracker is not the joint. It
        /// stands off the bone (limb radius + strap), calibration leaves a residual, and it jitters.
        ///
        /// The property that MUST hold is this: the same tracker reading must produce the same elbow, no matter
        /// where the base ANIMATION happened to leave it. When it does not, the elbow flickers between the
        /// animation and the tracker frame to frame -- and that is the reported inversion.
        /// </summary>
        [Test]
        public void ARealisticOffsetTracker_GivesTheSameElbow_WhereverTheAnimationLeftIt()
        {
            var rng = new System.Random(6060);

            foreach (float reach in new[] { 0.85f, 0.94f, 0.975f, 0.99f })
            {
                Vector3 dir = new Vector3(0.85f, -0.20f, 0.49f).normalized;
                Vector3 target = k_Shoulder + dir * (reach * k_ArmLen);

                // The true elbow, and a tracker sitting ~2.5 cm off it (the residual a real calibration leaves)
                // plus 3 mm of jitter: a realistic strapped-on puck.
                Vector3 trueElbow = ElbowOnCircle(target, 30f);
                Vector3 off = new Vector3(0.018f, 0.014f, -0.008f);
                Vector3 jitter = new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 0.003f,
                    (float)(rng.NextDouble() - 0.5) * 0.003f,
                    (float)(rng.NextDouble() - 0.5) * 0.003f);
                Vector3 tracker = trueElbow + off + jitter;

                // THE SAME TRACKER READING, from four different animated starting poses.
                Vector3 first = Vector3.zero;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 animElbow = ElbowOnCircle(target, 30f + k * 90f);   // including the wrong side
                    BasisArmSolveInput i = Input(animElbow, target, target, tracker);
                    i.HintIsTracker = true;
                    BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                    if (k == 0) { first = r.ElbowSolved; continue; }

                    Assert.AreEqual(0f, Vector3.Distance(first, r.ElbowSolved), 2e-3f,
                        $"the same tracker reading must give the SAME elbow regardless of where the animation " +
                        $"left it (reach {reach:P1}, start {30f + k * 90f:F0} deg). If it does not, the elbow " +
                        "flickers between the animation and the tracker, which is the reported inversion.");
                }
            }
        }
    }
}
