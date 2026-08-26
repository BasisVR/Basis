using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisKneeSwivelHoldTests
    {
        const float k_Dt = 1f / 90f, thighLen = 0.45f, shinLen = 0.45f;
        // Live tracked-knee cutoffs (BasisFullIKConstraintJob.k_TrackedKneeSwivel*), mirrored.
        const float minCutoffHz = 1.5f, beta = 0.20f, derivCutoffHz = 1.0f;
        static BasisSwivelSmootherInput MakeLeg(float reach, float swivelDeg, bool hold)
        {
            float full = thighLen + shinLen, d = reach * full;
            Vector3 root = Vector3.zero, tip = new Vector3(0f, -d, 0f), axis = Vector3.down;
            float along = (thighLen * thighLen - shinLen * shinLen + d * d) / (2f * d);
            float lever = Mathf.Sqrt(Mathf.Max(0f, thighLen * thighLen - along * along));
            Vector3 refDir = Vector3.forward, perp = Quaternion.AngleAxis(swivelDeg, axis) * refDir;
            Vector3 mid = root + axis * along + perp * lever;

            return new BasisSwivelSmootherInput
            {
                Root = root,
                Mid = mid,
                Tip = tip,
                BodyRotation = Quaternion.identity,
                ReferenceLocal = Vector3.forward,
                FallbackLocal = Vector3.right,
                Dt = k_Dt,
                MinCutoffHz = minCutoffHz,
                Beta = beta,
                DerivCutoffHz = derivCutoffHz,
                ConditionOnPole = false,   // the tracked-knee path (the case the user reported)
                SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz,
                GuardAnteriorHalfSpace = true,
                AnteriorSoftDeg = BasisLegSolveCore.KneeAnteriorSoftDeg,
                AnteriorHardDeg = BasisLegSolveCore.KneeAnteriorHardDeg,
                HoldWhenSingular = hold,
                HoldCondLo = BasisSwivelSmootherCore.DefaultHoldCondLo,
                HoldCondHi = BasisSwivelSmootherCore.DefaultHoldCondHi,
            };
        }
        static float SteadyOutputP2P(float reach, bool hold, float amp, float hz, float seconds)
        {
            int n = (int)(seconds / k_Dt);
            BasisSwivelSmootherInput seed = MakeLeg(reach, 0f, hold);
            BasisSwivelSmootherCore.Solve(seed, out BasisSwivelSmootherResult seeded);
            BasisSwivelFilterState state = seeded.State;
            float min = float.MaxValue, max = float.MinValue;
            for (int f = 0; f < n; f++)
            {
                float t = f * k_Dt, sw = amp * Mathf.Sin(2f * Mathf.PI * hz * t);
                BasisSwivelSmootherInput step = MakeLeg(reach, sw, hold);
                step.State = state;
                step.Seeded = true;
                BasisSwivelSmootherCore.Solve(step, out BasisSwivelSmootherResult r);
                state = r.State;

                if (t >= seconds * 0.5f)
                {
                    min = Mathf.Min(min, r.SmoothSwivelDeg);
                    max = Mathf.Max(max, r.SmoothSwivelDeg);
                }
            }
            return max - min;
        }
        // HoldGate is a per-STEP quantity (the seed frame passes through and reports 1), so advance one frame.
        static BasisSwivelSmootherResult SteppedGate(float reach)
        {
            BasisSwivelSmootherCore.Solve(MakeLeg(reach, 0f, hold: true), out BasisSwivelSmootherResult seeded);
            BasisSwivelSmootherInput step = MakeLeg(reach, 20f, hold: true);
            step.State = seeded.State; step.Seeded = true;
            BasisSwivelSmootherCore.Solve(step, out BasisSwivelSmootherResult r);
            return r;
        }
        [Test]
        public void HoldGate_IsClosedStraight_AndOpenBent()
        {
            // The property the whole fix rests on: the gate closes exactly where the swivel stops carrying
            // information, and is fully open where a bent knee's pole is real.
            BasisSwivelSmootherResult straight = SteppedGate(0.999f), bent = SteppedGate(0.85f);

            Assert.Less(straight.HoldGate, 0.05f, $"a standing (near-straight) leg must be HELD -- gate should be ~0 (got {straight.HoldGate:F3}, cond {straight.Conditioning:F4})");
            Assert.Greater(bent.HoldGate, 0.999f, $"a bent knee has a real lever arm -- the hold must be fully released (got {bent.HoldGate:F3}, cond {bent.Conditioning:F4})");
        }
        [Test]
        public void SlowOscillation_IsFrozen_WhenLegIsStraight()
        {
            // A 5 deg, 0.5 Hz sway at a standing leg -- the reported bug. The hold must freeze it to ~nothing.
            float p2p = SteadyOutputP2P(reach: 0.999f, hold: true, amp: 5f, hz: 0.5f, seconds: 3f);

            Assert.Less(p2p, 0.30f, $"the singularity hold must freeze the standing swivel -- a slow sway should not roll the leg " + $"(leaked {p2p:F2} deg peak-to-peak)");
        }
        [Test]
        public void SlowOscillation_LeaksThrough_WithoutTheHold()
        {
            // THE ANTI-TAUTOLOGY GATE. With the hold OFF (the current live tracked path), that same slow sway
            // passes almost straight through the responsive One-Euro -- which is exactly the visible bug. If this
            // ever stops failing-loudly on a revert, the gate above has stopped measuring anything.
            float held = SteadyOutputP2P(reach: 0.999f, hold: true, amp: 5f, hz: 0.5f, seconds: 3f);
            float leaked = SteadyOutputP2P(reach: 0.999f, hold: false, amp: 5f, hz: 0.5f, seconds: 3f);

            Assert.Greater(leaked, 3f, $"the legacy responsive filter is expected to pass a slow sway (leaked {leaked:F2} deg). " +"If this now passes, the One-Euro was retuned and the hold band must be re-derived.");
            Assert.Less(held, leaked - 3f, $"the hold must materially kill the oscillation (held {held:F2} vs leaked {leaked:F2} deg p2p)");
        }
        [Test]
        public void BentKnee_ResponseIsBitwiseUnchanged_ByTheHold()
        {
            // Above HoldCondHi the hold is the exact identity, so a genuinely bent, tracker-driven knee -- the
            // regime the 07-17 "6x faster" fix serves -- must be byte-for-byte the same with the hold on or off.
            BasisSwivelSmootherInput seedOn = MakeLeg(0.85f, 0f, hold: true);
            BasisSwivelSmootherCore.Solve(seedOn, out BasisSwivelSmootherResult sOn);
            BasisSwivelSmootherInput stepOn = MakeLeg(0.85f, 45f, hold: true);
            stepOn.State = sOn.State; stepOn.Seeded = true;
            BasisSwivelSmootherCore.Solve(stepOn, out BasisSwivelSmootherResult on);

            BasisSwivelSmootherInput seedOff = MakeLeg(0.85f, 0f, hold: false);
            BasisSwivelSmootherCore.Solve(seedOff, out BasisSwivelSmootherResult sOff);
            BasisSwivelSmootherInput stepOff = MakeLeg(0.85f, 45f, hold: false);
            stepOff.State = sOff.State; stepOff.Seeded = true;
            BasisSwivelSmootherCore.Solve(stepOff, out BasisSwivelSmootherResult off);

            Assert.That(on.SmoothSwivelDeg, Is.EqualTo(off.SmoothSwivelDeg).Within(1e-5f), $"a bent knee must be untouched by the hold (on {on.SmoothSwivelDeg:F5}, off {off.SmoothSwivelDeg:F5})");
        }
        [Test]
        public void Release_EasesIn_WithoutASnap_FromStraightToBent()
        {
            // The documented trap: freezing while straight must leave the swivel SANE, so the release as the knee
            // bends eases in rather than whipping. Sweep reach 0.999 -> 0.85 while the commanded swivel goes
            // 0 -> 40, and assert the hold adds no single-frame jump the legacy path did not already have.
            float jumpOn = MaxFrameJump(hold: true), jumpOff = MaxFrameJump(hold: false);

            Assert.LessOrEqual(jumpOn, jumpOff + 0.5f, $"releasing the hold must not snap -- worst per-frame jump held {jumpOn:F2} vs legacy {jumpOff:F2} deg");
        }
        static float MaxFrameJump(bool hold)
        {
            int n = (int)(1.5f / k_Dt);
            BasisSwivelSmootherInput seed = MakeLeg(0.999f, 0f, hold);
            BasisSwivelSmootherCore.Solve(seed, out BasisSwivelSmootherResult seeded);
            BasisSwivelFilterState state = seeded.State;
            float prev = seeded.SmoothSwivelDeg, worst = 0f;

            for (int f = 0; f < n; f++)
            {
                float u = Mathf.Clamp01(f / (float)n), reach = Mathf.Lerp(0.999f, 0.85f, u);
                float sw = Mathf.Lerp(0f, 40f, u);
                BasisSwivelSmootherInput step = MakeLeg(reach, sw, hold);
                step.State = state; step.Seeded = true;
                BasisSwivelSmootherCore.Solve(step, out BasisSwivelSmootherResult r);
                state = r.State;
                worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(prev, r.SmoothSwivelDeg)));
                prev = r.SmoothSwivelDeg;
            }
            return worst;
        }
    }
}
