using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisKneeSwivelWrapTests
    {
        const float k_Dt = 1f / 90f, thigh = 0.42f, shin = 0.42f;
        static readonly Vector3 hip = new Vector3(0.09f, 0.90f, 0f);
        static float Soft => BasisLegSolveCore.KneeAnteriorSoftDeg;
        static float Hard => BasisLegSolveCore.KneeAnteriorHardDeg;
        static float Clamp(float deg) => BasisLegSolveCore.ClampKneeSwivelDeg(deg, Soft, Hard);
        static float LegacyClamp(float swivelDeg)
        {
            float mag = Mathf.Abs(swivelDeg);
            if (!(mag > Soft)) return swivelDeg;
            float maxExcess = Hard - Soft, excess = mag - Soft;
            float compressed = Soft + maxExcess * excess / (maxExcess + excess);
            return swivelDeg < 0f ? -compressed : compressed;
        }
        // ── 1. THE GUARD IS CONTINUOUS ALL THE WAY ROUND ────────────────────────────────────────────────
        [Test]
        public void Guard_DoesNotFlip_AcrossThePosteriorRay()
        {
            float plus = Clamp(179.9f), minus = Clamp(-179.9f);

            Assert.Less(Mathf.Abs(plus - minus), 1f, $"clamp(+179.9) = {plus:F2} but clamp(-179.9) = {minus:F2}. Those are the same knee direction to " +"within 0.2deg, so that difference is exactly the click the user reported.");
        }
        [Test]
        public void LegacyGuard_DoesFlip_AcrossThePosteriorRay()
        {
            // ANTI-TAUTOLOGY. Without this, the gate above could be passing because the sweep never reaches the
            // cut, or because the constants happen to line up -- rather than because the fold was removed.
            float plus = LegacyClamp(179.9f), minus = LegacyClamp(-179.9f);

            Assert.Greater(Mathf.Abs(plus - minus), 170f, "the pre-fix guard was supposed to flip ~178deg here; if it no longer does, this gate has stopped " +"measuring the defect and the one above proves nothing");
        }
        [Test]
        public void Guard_HasBoundedGain_EverywhereOnTheCircle()
        {
            // The property that actually matters in a headset: no input step produces an outsized output step,
            // anywhere, including across the join.
            const float step = 0.05f;
            float worstGain = 0f, worstAt = 0f;
            float prev = Clamp(-180f);

            for (float deg = -180f + step; deg <= 180f + step; deg += step)
            {
                float sample = deg > 180f ? deg - 360f : deg, cur = Clamp(sample), gain = Mathf.Abs(cur - prev) / step;
                if (gain > worstGain) { worstGain = gain; worstAt = sample; }
                prev = cur;
            }

            // The taper window is narrow, so the ease is steeper inside it -- ~6.7x, reached only past 160deg.
            // 10 leaves room to widen the window without going stale. It is 500x below the 714x fold either way;
            // the property being defended is "bounded and smooth", not a specific number.
            Assert.Less(worstGain, 10f, $"guard gain peaked at {worstGain:F1}x near {worstAt:F1}deg -- a knee that moves several degrees " +"per degree of pole motion reads as a snap");
        }
        [Test]
        public void Guard_IsBitIdentical_BelowTheTaperWindow()
        {
            // ⚠️ THE REGRESSION THIS EXISTS TO STOP, and it took three separate in-headset reports to find.
            //
            // The first cut tapered across the whole 85..180 band. Closing the circle forces f(180) = 0, but
            // spreading that descent downward broke three things at once, because the old FLAT asymptote was
            // doing far more than bounding the angle:
            //   posture -- a 120deg pole went 89 (lateral) -> 77, a 140deg one 89 -> 59: "not looking human"
            //   noise   -- flat was a DEAD ZONE; giving it slope handed pole jitter to the bone: "jelly knees"
            //   hips    -- a guarded knee is rebuilt from `anterior` (hips-right), so the deeper the guard bites
            //              the more the knee follows the PELVIS instead of the shin tracker: "moves with my hip"
            //
            // So below the taper window the guard must be the ORIGINAL function, exactly -- not close, exactly.
            foreach (float deg in new[] { 86f, 90f, 100f, 110f, 120f, 140f, 155f, BasisLegSolveCore.KneeAnteriorTaperStartDeg })
            {
                Assert.AreEqual(LegacyClamp(deg), Clamp(deg), 1e-4f, $"a {deg}deg pole no longer matches the pre-fix guard. The click fix is not licensed to " + "re-pose the leg, re-couple it to the hips, or make it track noise -- raise " +"BasisLegSolveCore.KneeAnteriorTaperStartDeg.");
            }
        }
        [Test]
        public void Guard_StillRejectsPoleNoise_WhereItUsedTo()
        {
            // "Jelly knees" stated as a measurement rather than a vibe: degrees of KNEE per degree of pole
            // JITTER. The old flat asymptote scored ~0.01 through the posterior band -- the guard swallowed the
            // noise whole. A whole-band taper scored 0.72 at 140deg, which is what reached the bone.
            //
            // Compared against the legacy guard rather than an absolute, so this measures the REGRESSION and
            // cannot drift if the constants are retuned.
            foreach (float mean in new[] { 100f, 120f, 140f })
            {
                float legacy = Amplification(mean, LegacyClamp), now = Amplification(mean, Clamp);
                Assert.Less(now, legacy + 0.05f, $"at a {mean}deg pole the guard now passes {now:F2}deg of knee per degree of pole jitter " + $"(was {legacy:F2}). That is the knee tracking noise it used to swallow -- jelly.");
            }
        }
        static float Amplification(float meanDeg, System.Func<float, float> f)
        {
            const float jitter = 2f;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int k = 0; k < 64; k++)
            {
                float v = f(meanDeg + jitter * Mathf.Sin(2f * Mathf.PI * k / 64f));
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            return (hi - lo) / (2f * jitter);
        }
        [Test]
        public void Guard_IsStillTheExactIdentity_InTheFreeBand()
        {
            // The whole point of a soft edge is that a legal pose is not perturbed at all. Bit-for-bit, because
            // "close enough" here would silently re-tune butterfly and hip abduction.
            float[] legal = { 0f, 1f, -1f, 15f, -15f, 45f, -45f, 60f, -60f, 84.9f, -84.9f, Soft, -Soft };
            foreach (float deg in legal)
            {
                Assert.AreEqual(deg, Clamp(deg), 0f, $"guard perturbed a legitimate {deg}deg pose");
            }
        }
        [Test]
        public void Guard_StaysInsideTheAnteriorHalfSpace_EverywhereOnTheCircle()
        {
            // Closing the circle must not buy continuity by letting the knee through the joint. The taper only
            // ever REDUCES the compressed magnitude, so the bound is tighter than before, not looser.
            for (float deg = -180f; deg <= 180f; deg += 0.25f)
            {
                Assert.Less(Mathf.Abs(Clamp(deg)), 90f, $"{deg}deg escaped the anterior half-space");
            }
        }
        [Test]
        public void Guard_StillBarelyTouches_ADeadLateralKnee()
        {
            // BasisLegHintReachTests.KneeHint_StillActuallySwivelsTheKnee parks a hint 90deg off anterior and
            // demands the knee follow within 15deg. Closing the circle costs 0.24deg of that budget (2.63 -> 2.87).
            float perturbation = 90f - Clamp(90f);
            Assert.Greater(perturbation, 0f, "a dead-lateral knee must still be pulled inside the half-space");
            Assert.Less(perturbation, 5f, $"guard bent a LEGAL lateral knee by {perturbation:F1}deg");
        }
        // ── 2. THE SOLVE CORE NO LONGER SNAPS THE KNEE ACROSS THE BACK OF THE LEG ───────────────────────
        [Test]
        public void Solve_DoesNotSnapTheKnee_AsTheHintCrossesBehindTheLeg()
        {
            // Sweep a lower-leg tracker the whole way round the leg axis and watch the SOLVED knee. The hint sits
            // well off the axis (poleSin > 0.5) so the near-colinear pole blend never engages -- what this measures
            // is the guard and nothing else.
            foreach (float reach in new[] { 0.70f, 0.85f, 0.95f, 1.00f })
            {
                float worstGain = 0f, worstAt = 0f;
                const float step = 0.25f;
                Vector3 prev = Vector3.zero;
                bool have = false;

                for (float phi = 0f; phi <= 360f; phi += step)
                {
                    if (!SolveKneeDir(reach, 0.30f, phi, out Vector3 dir)) continue;
                    if (have)
                    {
                        float gain = Vector3.Angle(prev, dir) / step;
                        if (gain > worstGain) { worstGain = gain; worstAt = phi; }
                    }
                    prev = dir;
                    have = true;
                }

                Assert.Less(worstGain, 5f, $"at reach {reach:F2} the knee moved {worstGain:F0}deg per degree of hint motion near " + $"{worstAt:F0}deg. Note this fires at EVERY reach ratio, so it is the posterior-ray fold, " +"not the full-extension pole singularity.");
            }
        }
        [Test]
        public void Solve_DoesNotSnapTheKnee_WhenTheHintSitsNearTheLegAxis()
        {
            // DEFECT 3, and the one most likely to be hit by an actual FBT rig. hintRadius 0.15 puts a lower-leg
            // tracker at poleSin ~0.34 on a standing leg -- INSIDE k_PoleColinearSin (0.5), so the near-colinear
            // ease runs. That ease is a Vector3.Slerp toward bendPole, and as the pole crosses the far side the
            // great circle flips: measured 610x knee gain before the guard-first reorder.
            //
            // Kept as its own test rather than folded into the sweep above because the mechanism is different --
            // that one is the guard's own transfer function, this one is an anti-parallel slerp INPUT that the
            // guard now makes unreachable by running first.
            foreach (float reach in new[] { 0.70f, 0.85f, 0.95f, 1.00f })
            {
                float worstGain = 0f, worstAt = 0f;
                const float step = 0.25f;
                Vector3 prev = Vector3.zero;
                bool have = false;

                for (float phi = 0f; phi <= 360f; phi += step)
                {
                    if (!SolveKneeDir(reach, 0.15f, phi, out Vector3 dir)) continue;
                    if (have)
                    {
                        float gain = Vector3.Angle(prev, dir) / step;
                        if (gain > worstGain) { worstGain = gain; worstAt = phi; }
                    }
                    prev = dir;
                    have = true;
                }

                Assert.Less(worstGain, 5f, $"at reach {reach:F2} a near-axis hint moved the knee {worstGain:F0}deg per degree near " + $"{worstAt:F0}deg -- the near-colinear ease is being handed an anti-parallel slerp again");
            }
        }
        static bool SolveKneeDir(float reachRatio, float hintRadius, float phiDeg, out Vector3 kneeDir)
        {
            kneeDir = Vector3.zero;
            float dist = reachRatio * (thigh + shin);
            Vector3 down = Vector3.down, target = hip + down * dist, anterior = Vector3.forward;
            Vector3 perp = (Quaternion.AngleAxis(phiDeg, down) * anterior).normalized;
            float half = 0.5f * dist, rho = Mathf.Sqrt(Mathf.Max(thigh * thigh - half * half, 1e-6f));
            BasisLegSolveInput i = default;
            i.Root = hip;
            i.Mid = hip + down * half + anterior * rho;
            i.Tip = target;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hip + down * half + perp * hintRadius;
            i.HintWeight = 1f;
            i.BendNormal = Vector3.right;
            i.AnteriorNormal = Vector3.right;
            i.HintIsTracker = true;
            i.HintRotation = Quaternion.identity;
            i.HasHintRotation = true;

            BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);

            Vector3 ac = r.FootSolved - hip;
            if (ac.sqrMagnitude < 1e-10f) return false;
            Vector3 acN = ac.normalized, lever = r.KneeSolved - hip;
            lever -= acN * Vector3.Dot(lever, acN);
            if (lever.sqrMagnitude < 1e-12f) return false;
            kneeDir = lever.normalized;
            return true;
        }
        // ── 3. THE SMOOTHER'S REFERENCE NO LONGER REVERSES ──────────────────────────────────────────────
        [Test]
        public void TransportedReference_DoesNotReverse_AsTheLegSweepsThroughBodyForward()
        {
            float worst = WorstKneeJumpAcrossLegPitch(transport: true, out float at);
            Assert.Less(worst, 5f, $"the knee jumped {worst:F1}deg for half a degree of leg pitch at {at:F1}deg -- that is the leg " +"passing through body-forward, i.e. sitting on the floor with the legs out, or a front kick");
        }
        [Test]
        public void ProjectedReference_DoesReverse_AsTheLegSweepsThroughBodyForward()
        {
            // ANTI-TAUTOLOGY for the gate above: with the transport off (zero TransportHomeLocal = the shipping
            // projection) the reversal must still be there, or the test is measuring nothing.
            float worst = WorstKneeJumpAcrossLegPitch(transport: false, out _);
            Assert.Greater(worst, 45f, "the projected reference was supposed to reverse through body-forward; if it no longer does, the " +"gate above has stopped measuring the defect");
        }
        static float WorstKneeJumpAcrossLegPitch(bool transport, out float worstAt)
        {
            const float step = 0.5f;
            float worst = 0f;
            worstAt = 0f;
            Vector3 prevDir = Vector3.zero;
            bool have = false;

            // 0 = leg straight down, 90 = straight out along body-forward/back. Stop at 140, past the limit of
            // hip flexion+extension -- beyond that lies the transport's own singularity (thigh straight up out of
            // the pelvis), which a hip cannot reach.
            for (float pitch = 0f; pitch <= 140f; pitch += step)
            {
                Vector3 axis = (Quaternion.AngleAxis(pitch, Vector3.right) * Vector3.down).normalized;
                Vector3 kneeOut = Vector3.Cross(axis, Vector3.right).normalized;
                const float half = 0.40f;
                float rho = Mathf.Sqrt(Mathf.Max(thigh * thigh - half * half, 1e-8f));
                Vector3 root = hip, tip = hip + axis * 0.80f, mid = hip + axis * half + kneeOut * rho;
                BasisSwivelSmootherInput i = MakeInput(root, mid, tip, transport);
                // Seed ON the current measurement so this isolates the GEOMETRY from filter memory.
                i.State = BasisSwivelFilterCore.Seed(RawSwivel(root, mid, tip, transport));
                i.Seeded = true;

                BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);
                if (!r.Valid) continue;

                Vector3 lever = r.DesiredMid - root;
                lever -= axis * Vector3.Dot(lever, axis);
                if (lever.sqrMagnitude < 1e-12f) continue;
                Vector3 dir = lever.normalized;

                if (have)
                {
                    float jump = Vector3.Angle(prevDir, dir);
                    if (jump > worst) { worst = jump; worstAt = pitch; }
                }
                prevDir = dir;
                have = true;
            }
            return worst;
        }
        [Test]
        public void TransportedReference_IsTheExactProjection_ForEverySagittalPose()
        {
            // Standing, walking, squatting, sitting in a chair, kneeling, lunging -- everything in the sagittal
            // plane. The two constructions must agree EXACTLY there or the change re-tunes the leg.
            for (float pitch = -80f; pitch <= 80f; pitch += 2f)
            {
                Vector3 axis = (Quaternion.AngleAxis(pitch, Vector3.right) * Vector3.down).normalized;
                Vector3 proj = Vector3.ProjectOnPlane(Vector3.forward, axis);
                if (proj.sqrMagnitude < 1e-6f) continue;
                proj.Normalize();

                Vector3 sx = Vector3.Cross(Vector3.down, axis);
                float sw = 1f + Vector3.Dot(Vector3.down, axis), ssq = sx.sqrMagnitude + sw * sw;
                if (!(sw > 1e-4f) || ssq < 1e-8f) continue;
                float inv = 1f / Mathf.Sqrt(ssq);
                Quaternion swing = new Quaternion(sx.x * inv, sx.y * inv, sx.z * inv, sw * inv);
                Vector3 tr = swing * Vector3.forward;
                tr -= axis * Vector3.Dot(tr, axis);
                tr.Normalize();

                Assert.Less(Vector3.Angle(proj, tr), 0.05f, $"transport and projection disagree by more than a rounding error at leg pitch {pitch}deg, " +"which is an ordinary sagittal pose -- the leg tuning would move");
            }
        }
        [Test]
        public void ButterflyKnee_StaysInsideTheGuardsFreeBand_UnderTheTransportedReference()
        {
            // The transport moves the measured swivel for OUT-OF-sagittal poses (~13deg on a cobbler sit). That is
            // only acceptable while it leaves legal poses inside the free band, where the guard is the identity --
            // otherwise the fix trades a click for a compressed butterfly.
            Vector3 axis = (Quaternion.AngleAxis(55f, Vector3.up) * (Quaternion.AngleAxis(55f, Vector3.right) * Vector3.down)).normalized;
            Vector3 solveAnterior = Vector3.Cross(axis, Vector3.right);
            solveAnterior -= axis * Vector3.Dot(solveAnterior, axis);
            solveAnterior.Normalize();
            Vector3 pole = Quaternion.AngleAxis(BasisButterflyKneeCore.DefaultMaxOpenDeg, axis) * solveAnterior;
            Vector3 sx = Vector3.Cross(Vector3.down, axis);
            float sw = 1f + Vector3.Dot(Vector3.down, axis), inv = 1f / Mathf.Sqrt(sx.sqrMagnitude + sw * sw);
            Quaternion swing = new Quaternion(sx.x * inv, sx.y * inv, sx.z * inv, sw * inv);
            Vector3 refDir = swing * Vector3.forward;
            refDir -= axis * Vector3.Dot(refDir, axis);
            refDir.Normalize();

            float swivel = Vector3.SignedAngle(refDir, pole, axis);
            Assert.Less(Mathf.Abs(swivel), Soft, $"a full butterfly splay reads {swivel:F1}deg against the transported reference, which is past the " + $"{Soft}deg soft edge -- the guard would start compressing a legal cobbler sit");
        }
        // ── 4. END TO END: A NOISY POLE PARKED BEHIND THE LEG MUST NOT CLICK ────────────────────────────
        [Test]
        public void ANoisyPoleParkedBehindTheLeg_ProducesNoClicks()
        {
            // The user's actual report, reproduced: a knee whose pole sits near the back of the leg with ordinary
            // tracker noise on it. Before the fix this fired 8 times in 3 seconds, each a ~174deg instantaneous
            // snap -- and every one of them ALSO cleared the smoother's 25deg reseed threshold, so the One-Euro
            // was discarded rather than damping it.
            const int frames = 270;
            Vector3 axis = Vector3.down;
            const float cond = 0.35f;
            float rho = cond * thigh, along = Mathf.Sqrt(Mathf.Max(thigh * thigh - rho * rho, 1e-8f));
            Vector3 root = hip, tip = hip + axis * 0.8395f;
            BasisSwivelFilterState state = default;
            bool seeded = false;
            int clicks = 0;
            float worst = 0f;
            Vector3 prevDir = Vector3.zero;
            bool have = false;

            for (int f = 0; f < frames; f++)
            {
                float t = f / (float)(frames - 1), phi = 178f + Mathf.Sin(t * 61f) * 1.6f + Mathf.Sin(t * 23.7f) * 0.9f;
                Vector3 mid = root + axis * along + (Quaternion.AngleAxis(phi, axis) * Vector3.forward).normalized * rho;
                BasisSwivelSmootherInput i = MakeInput(root, mid, tip, transport: true);
                i.State = state;
                i.Seeded = seeded;

                BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);
                if (r.WriteState) { state = r.State; seeded = r.Seeded; }
                if (!r.Valid) continue;

                Vector3 lever = r.DesiredMid - root;
                lever -= axis * Vector3.Dot(lever, axis);
                if (lever.sqrMagnitude < 1e-12f) continue;
                Vector3 dir = lever.normalized;

                if (have)
                {
                    float jump = Vector3.Angle(prevDir, dir);
                    if (jump > 20f) clicks++;
                    if (jump > worst) worst = jump;
                }
                prevDir = dir;
                have = true;
            }

            Assert.AreEqual(0, clicks, $"{clicks} frames moved the knee more than 20deg (worst {worst:F1}deg). A human knee tops out near " +"500deg/s, which is 5.6deg per frame at 90fps.");
        }
        static float RawSwivel(Vector3 root, Vector3 mid, Vector3 tip, bool transport)
        {
            Vector3 axis = (tip - root).normalized, refDir;
            if (transport)
            {
                Vector3 sx = Vector3.Cross(Vector3.down, axis);
                float sw = 1f + Vector3.Dot(Vector3.down, axis), inv = 1f / Mathf.Sqrt(sx.sqrMagnitude + sw * sw);
                Quaternion swing = new Quaternion(sx.x * inv, sx.y * inv, sx.z * inv, sw * inv);
                refDir = swing * Vector3.forward;
                refDir -= axis * Vector3.Dot(refDir, axis);
            }
            else
            {
                refDir = Vector3.ProjectOnPlane(Vector3.forward, axis);
            }
            if (refDir.sqrMagnitude < 1e-8f) return 0f;
            refDir.Normalize();
            return Clamp(Vector3.SignedAngle(refDir, Vector3.ProjectOnPlane(mid - root, axis), axis));
        }
        static BasisSwivelSmootherInput MakeInput(Vector3 root, Vector3 mid, Vector3 tip, bool transport)
        {
            BasisSwivelSmootherInput i = default;
            i.Root = root;
            i.Mid = mid;
            i.Tip = tip;
            i.BodyRotation = Quaternion.identity;
            i.ReferenceLocal = Vector3.forward;
            i.FallbackLocal = Vector3.right;
            i.TransportHomeLocal = transport ? Vector3.down : Vector3.zero;
            i.Dt = k_Dt;
            i.MinCutoffHz = 1.5f;
            i.Beta = 0.20f;
            i.DerivCutoffHz = 1.0f;
            i.ConditionOnPole = false;
            i.SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;
            i.GuardAnteriorHalfSpace = true;
            i.AnteriorSoftDeg = Soft;
            i.AnteriorHardDeg = Hard;
            i.HoldWhenSingular = true;
            i.HoldCondLo = BasisSwivelSmootherCore.DefaultHoldCondLo;
            i.HoldCondHi = BasisSwivelSmootherCore.DefaultHoldCondHi;
            return i;
        }
    }
}
