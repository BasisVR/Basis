using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// BasisLocalFootDriver.InitializeVariables measures the leg segments off the authored T-pose, and
    /// BasisLocalRigDriver.RefreshBodyFit rescales those same segments AFTERWARDS (BasisBodyFitApply bones
    /// 8-11 are the lower legs and feet, whose LOCAL POSITIONS are the thigh and shin). Until the driver's
    /// only rescale hook learned about AppliedBodyFit.LegScale it applied ScaledToMatchValue alone, so the
    /// recorded leg length described a body the avatar no longer had.
    ///
    /// BasisFootSimulateJob derives bendRatio = 1 - saturate(hipFootUpDist / avgLeg) and multiplies
    /// kneeSplay by it, so understating avgLeg narrows the knees. MEASURED against the real job at the
    /// reference avatar (0.87 m leg, 0.18 m stance): the loss is 12-31% of the splay, NOT a collapse --
    /// kneeMinSide floors the separation at half stance (9.00 cm) and bendRatio does not fully saturate
    /// at plausible reach/scale pairs. reach 0.78 + legScale 1.30 => 13.29 -> 9.18 cm (30.9% lost);
    /// reach 0.82 + legScale 1.20 => 12.57 -> 9.69 cm (23.0%); reach 0.95 + legScale 1.20 => 12.0%.
    /// LegScale is derived per avatar, so the error is re-rolled on every swap.
    /// </summary>
    public sealed class BasisFootBodyFitStaleLegTests
    {
        const float k_LegScale = 1.30f;
        const float k_ReachFrac = 0.78f;

        static BasisFootSimParams Understate(BasisFootSimParams p, float legScale)
        {
            p.leftLegLen /= legScale;
            p.rightLegLen /= legScale;
            p.leftThighLen /= legScale;
            p.rightThighLen /= legScale;
            p.leftShinLen /= legScale;
            p.rightShinLen /= legScale;
            return p;
        }

        static float CrouchFracFor(in BasisFootSimParams p, float reachFrac)
        {
            float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
            return reachFrac * avgLeg / (p.hipToFoot + p.ankleHeight);
        }

        [Test]
        public void StaleLegLength_NarrowsKneeSplay()
        {
            BasisFootSimParams fitted = BasisMocapFootQuality.BuildReferenceParams(1f);
            BasisFootSimParams stale = Understate(fitted, k_LegScale);
            float crouch = CrouchFracFor(fitted, k_ReachFrac);

            float fittedSep = BasisMocapFootQuality.RunCrouchKneeSplay(fitted, crouch);
            float staleSep = BasisMocapFootQuality.RunCrouchKneeSplay(stale, crouch);

            Debug.Log($"[stale-leg] legScale {k_LegScale:F2} reach {k_ReachFrac:F2} crouchFrac {crouch:F3}  " +
                      $"kneeSep fitted {fittedSep * 100f:F2} cm  stale {staleSep * 100f:F2} cm  " +
                      $"ratio {(staleSep > 1e-5f ? fittedSep / staleSep : float.PositiveInfinity):F2}x");

            Assert.Greater(fittedSep, 0f, "fitted knees must splay apart at all");
            Assert.Greater(staleSep, 0f, "knees must never cross even when splay is lost");
            Assert.Greater(fittedSep, staleSep * 1.25f,
                $"a leg length understated by {k_LegScale:F2}x must measurably narrow knee splay " +
                $"(measured 1.45x at this reach; fitted {fittedSep * 100f:F2} cm vs stale {staleSep * 100f:F2} cm)");
        }

        [Test]
        public void UnitLegScale_IsBitIdentical()
        {
            BasisFootSimParams fitted = BasisMocapFootQuality.BuildReferenceParams(1f);
            BasisFootSimParams same = Understate(fitted, 1f);
            float crouch = CrouchFracFor(fitted, k_ReachFrac);

            float a = BasisMocapFootQuality.RunCrouchKneeSplay(fitted, crouch);
            float b = BasisMocapFootQuality.RunCrouchKneeSplay(same, crouch);

            Assert.AreEqual(a, b, 0f,
                "a legScale of 1 must change nothing -- otherwise the probe itself is manufacturing the gap");
        }

        [Test]
        public void KneeSplay_GrowsAsTheKneeBends()
        {
            BasisFootSimParams p = BasisMocapFootQuality.BuildReferenceParams(1f);
            float shallow = BasisMocapFootQuality.RunCrouchKneeSplay(p, CrouchFracFor(p, 0.95f));
            float deep = BasisMocapFootQuality.RunCrouchKneeSplay(p, CrouchFracFor(p, 0.70f));

            Debug.Log($"[splay-monotone] shallow {shallow * 100f:F2} cm  deep {deep * 100f:F2} cm");
            Assert.Greater(deep, shallow,
                "splayWhenCrouched must widen the knees as the crouch deepens -- if this fails the " +
                "collapse test above is measuring something other than the splay term");
        }
    }
}
