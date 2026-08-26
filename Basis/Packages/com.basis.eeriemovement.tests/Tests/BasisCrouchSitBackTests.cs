using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisCrouchSitBackTests
    {
        const float StandH = 1.60f;
        const float Rest = 0.55f; // typical humanoid hips->head chain, ~0.34*S
        // Cross-clip median hips-behind-head (/S) per head-drop bucket (/S). The 0.06 bucket is omitted:
        // its clip spread straddles gait noise and its median is not load-bearing.
        static readonly float[,] Corpus =
        {
            { 0.10f, 0.114f }, { 0.14f, 0.114f }, { 0.18f, 0.185f }, { 0.22f, 0.209f },
            { 0.26f, 0.216f }, { 0.30f, 0.237f }, { 0.34f, 0.237f }, { 0.38f, 0.252f },
            { 0.42f, 0.232f }, { 0.46f, 0.238f }, { 0.50f, 0.249f }, { 0.54f, 0.225f },
            { 0.58f, 0.264f },
        };
        static BasisCrouchOffsetResult Solve(float depth, float factor = 1f, float fade = 1f)
        {
            Vector3 head = new Vector3(0f, StandH - depth, 0f);
            BasisCrouchOffsetInput i;
            i.HeadTargetPos = head;
            i.HipsPos = head - Vector3.up * Rest; // as the LockHead stage leaves them: rest length, vertical
            i.HipsRot = Quaternion.identity;
            i.Bind = Quaternion.identity;
            i.PlayerUp = Vector3.up;
            i.Factor = factor;
            i.RestDist = Rest;
            i.CrouchDepth = depth;
            i.StandingHeadHeight = StandH;
            i.Fade = fade;
            BasisCrouchOffsetCore.Solve(i, out BasisCrouchOffsetResult r);
            return r;
        }
        [Test]
        public void Curve_TracksTheMocapCorpus()
        {
            int n = Corpus.GetLength(0);
            float sumDev = 0f;
            for (int k = 0; k < n; k++)
            {
                float dhat = Corpus[k, 0], expected = Corpus[k, 1];
                float shat = BasisCrouchOffsetCore.EvaluateSetback(dhat * StandH, StandH, 1f, 1f, Rest) / StandH;
                float dev = Mathf.Abs(shat - expected);
                sumDev += dev;
                Assert.That(dev, Is.LessThan(0.045f), $"setback at drop {dhat:F2}*S is {shat:F3}*S, corpus median {expected:F3}*S (dev {dev:F3}).");
            }
            Assert.That(sumDev / n, Is.LessThan(0.02f), $"mean deviation from the corpus buckets is {sumDev / n:F4}*S -- curve has drifted from the fit.");
        }
        [Test]
        public void SitBack_IsFrontLoaded_LikeAHuman()
        {
            // The defining feature of a real crouch: the pelvis travels back from the FIRST bit of descent.
            // At 10% drop a human is already ~46% of the way to their full-depth setback; the old
            // separation-driven signal was quadratic at onset and delivered ~nothing until very deep.
            float early = Solve(0.10f * StandH).SetbackMeters, deep = Solve(0.50f * StandH).SetbackMeters;
            Assert.That(deep, Is.GreaterThan(0.2f * StandH), "deep-crouch setback lost the corpus plateau.");
            Assert.That(early / deep, Is.GreaterThan(0.40f), $"early sit-back is only {early / deep:P0} of deep -- onset is no longer front-loaded.");
        }
        [Test]
        public void Setback_IsMonotone_InDepth()
        {
            float prev = -1f;
            for (float depth = 0f; depth <= 0.9f * StandH; depth += 0.005f)
            {
                float s = Solve(depth).SetbackMeters;
                Assert.That(s, Is.GreaterThanOrEqualTo(prev - 1e-5f), $"setback shrank as depth grew at {depth:F3} m.");
                prev = s;
            }
        }
        [Test]
        public void Engaged_HipsRideTheRestSphere()
        {
            foreach (float dhat in new[] { 0.20f, 0.35f, 0.50f })
            {
                var r = Solve(dhat * StandH);
                Assert.That(r.Applied, Is.True);
                float dist = (r.HipsPos - new Vector3(0f, StandH - dhat * StandH, 0f)).magnitude;
                Assert.That(Mathf.Abs(dist - Rest), Is.LessThan(1e-4f), $"at drop {dhat:F2}*S the hips are {dist:F4} m from the head, not the rest {Rest} m -- " + "the spine would stretch (giraffe neck) or compress.");
            }
        }
        [Test]
        public void Standing_AndBelowDeadzone_AreUntouched()
        {
            foreach (float depth in new[] { 0f, 0.025f, 0.045f }) // 0.045 = dhat 0.028, inside the 0.03 deadzone
            {
                var r = Solve(depth);
                Assert.That(r.Applied, Is.False, $"crouch fired at {depth:F3} m of drop (walk-bob territory).");
                Vector3 expected = new Vector3(0f, StandH - depth, 0f) - Vector3.up * Rest;
                Assert.That((r.HipsPos - expected).magnitude, Is.EqualTo(0f), "no-op path must not move the hips at all.");
            }
        }
        [Test]
        public void FactorZero_AndFadeZero_AreTrueNoOps()
        {
            var byFactor = Solve(0.35f * StandH, factor: 0f);
            var byFade = Solve(0.35f * StandH, fade: 0f);
            Assert.That(byFactor.Applied, Is.False);
            Assert.That(byFade.Applied, Is.False);
        }
        [Test]
        public void Fade_HandsThePelvisToTheCounterbalance_Linearly()
        {
            // Composition contract: a fold (flexion) fades the crouch term out so the counterbalance owns the
            // pelvis travel. Below the lean-cap knee the hand-off is exactly linear.
            float full = BasisCrouchOffsetCore.EvaluateSetback(0.20f * StandH, StandH, 1f, 1f, Rest);
            float half = BasisCrouchOffsetCore.EvaluateSetback(0.20f * StandH, StandH, 1f, 0.5f, Rest);
            Assert.That(half, Is.EqualTo(0.5f * full).Within(1e-5f));
        }
        [Test]
        public void Continuity_NoPops_AcrossDeadzoneEngageAndCap()
        {
            // 1 mm depth steps across the whole travel: the hips must never step more than a few mm --
            // C1 everywhere (deadzone entry, sphere engage blend, rational lean cap).
            Vector3 prev = Solve(0f).HipsPos;
            float maxStep = 0f;
            for (float depth = 0.001f; depth <= 0.9f * StandH; depth += 0.001f)
            {
                Vector3 cur = Solve(depth).HipsPos;
                // remove the 1 mm the hips inherit from the head dropping between samples
                Vector3 step = cur - prev + Vector3.up * 0.001f;
                maxStep = Mathf.Max(maxStep, step.magnitude);
                prev = cur;
            }
            Assert.That(maxStep, Is.LessThan(0.006f), $"hips stepped {maxStep * 1000f:F2} mm on a 1 mm depth step -- a visible pop.");
        }
        [Test]
        public void ExtremeDepth_HoldsADeepSquat_InsteadOfRunningAway()
        {
            // A VR user sitting on their real floor reports a depth far past any squat. The lean saturates
            // at the corpus deep-squat ceiling instead of folding the avatar in half.
            var r = Solve(1.2f * StandH);
            Assert.That(r.SetbackMeters, Is.LessThanOrEqualTo(BasisCrouchOffsetCore.maxLeanSin * Rest + 1e-4f));
            Assert.That(r.LeanDeg, Is.LessThan(63f), $"chord lean {r.LeanDeg:F1} deg exceeds the corpus ceiling.");
        }
    }
}
