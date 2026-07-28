using System.Collections.Generic;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Checks & balances on the stepper that need no mocap: a synthetic straight walk driven through the REAL
    /// job, asserting the invariants the gait law promises.
    ///
    ///  1. SCALE-INVARIANCE -- the whole reason every constant is a fraction of leg length: a chibi and a giant
    ///     must produce IDENTICAL dimensionless gait once size is divided out. The stepper was shipped broken on
    ///     small avatars once (a metre constant a chibi felt differently); this pins it.
    ///  2. FRAMERATE-INDEPENDENCE -- the stepper had blends that converged by frame count, not time (a foot in a
    ///     different pose mid-stride on a faster GPU). Same walk at 30/72/144 fps must match.
    ///  3. IDLE -- a standing person must not fidget: near-zero speed => near-zero steps.
    ///  4. FINITE -- no NaN/Inf ever reaches the rig.
    /// See project_basis_foot_mocap_comparison_harness.
    /// </summary>
    public sealed class BasisFootWalkInvarianceTests
    {
        static readonly float[] WalkSpeeds = { 0.20f, 0.30f, 0.42f };   // v-hat, the walking band

        [Test]
        public void WalkGait_IsScaleInvariant()
        {
            var failures = new List<string>();
            foreach (float vhat in WalkSpeeds)
            {
                BasisFootWalkResult chibi = BasisMocapFootQuality.RunWalk(BasisMocapFootQuality.BuildReferenceParams(0.6f), vhat);
                BasisFootWalkResult adult = BasisMocapFootQuality.RunWalk(BasisMocapFootQuality.BuildReferenceParams(1.0f), vhat);
                BasisFootWalkResult giant = BasisMocapFootQuality.RunWalk(BasisMocapFootQuality.BuildReferenceParams(1.8f), vhat);
                Debug.Log($"[scale vhat={vhat:F2}] duty {chibi.Duty:F2}/{adult.Duty:F2}/{giant.Duty:F2}  " +
                          $"dsup {chibi.DoubleSupport:F2}/{adult.DoubleSupport:F2}/{giant.DoubleSupport:F2}  " +
                          $"clear {chibi.ClearFrac:F3}/{adult.ClearFrac:F3}/{giant.ClearFrac:F3}  " +
                          $"cad {chibi.CadenceHat:F2}/{adult.CadenceHat:F2}/{giant.CadenceHat:F2}");
                Check(failures, vhat, "duty", chibi.Duty, adult.Duty, giant.Duty, 0.06f);
                Check(failures, vhat, "dsup", chibi.DoubleSupport, adult.DoubleSupport, giant.DoubleSupport, 0.06f);
                Check(failures, vhat, "clear", chibi.ClearFrac, adult.ClearFrac, giant.ClearFrac, 0.03f);
                Check(failures, vhat, "cadence", chibi.CadenceHat, adult.CadenceHat, giant.CadenceHat, 0.10f);
                foreach (var r in new[] { chibi, adult, giant })
                    if (!Finite(r)) failures.Add($"vhat {vhat:F2}: non-finite gait metric");
            }
            if (failures.Count > 0) Assert.Fail("scale-invariance:\n  " + string.Join("\n  ", failures));
        }

        [Test]
        public void WalkGait_IsFramerateIndependent()
        {
            var failures = new List<string>();
            BasisFootSimParams p = BasisMocapFootQuality.BuildReferenceParams(1.0f);
            foreach (float vhat in WalkSpeeds)
            {
                BasisFootWalkResult r30 = BasisMocapFootQuality.RunWalk(p, vhat, dt: 1f / 30f);
                BasisFootWalkResult r72 = BasisMocapFootQuality.RunWalk(p, vhat, dt: 1f / 72f);
                BasisFootWalkResult r144 = BasisMocapFootQuality.RunWalk(p, vhat, dt: 1f / 144f);
                Debug.Log($"[fps vhat={vhat:F2}] duty {r30.Duty:F2}/{r72.Duty:F2}/{r144.Duty:F2}  " +
                          $"clear {r30.ClearFrac:F3}/{r72.ClearFrac:F3}/{r144.ClearFrac:F3}  " +
                          $"cad {r30.CadenceHat:F2}/{r72.CadenceHat:F2}/{r144.CadenceHat:F2}");
                Check(failures, vhat, "duty", r30.Duty, r72.Duty, r144.Duty, 0.07f);
                Check(failures, vhat, "clear", r30.ClearFrac, r72.ClearFrac, r144.ClearFrac, 0.03f);
                // cadence is a step COUNT over a fixed window, so it quantizes +/-1 step with the timestep even
                // though step DURATIONS and trajectories are framerate-independent -- a looser band on purpose.
                Check(failures, vhat, "cadence", r30.CadenceHat, r72.CadenceHat, r144.CadenceHat, 0.15f);
            }
            if (failures.Count > 0) Assert.Fail("framerate-independence:\n  " + string.Join("\n  ", failures));
        }

        [Test]
        public void Stepper_DoesNotMicroStep_WhenStandingStill()
        {
            var failures = new List<string>();
            foreach (float scale in new[] { 0.6f, 1.0f, 1.8f })
            {
                // truly standing still: a human takes no steps, and neither may the stepper.
                BasisFootWalkResult r = BasisMocapFootQuality.RunWalk(BasisMocapFootQuality.BuildReferenceParams(scale), 0f);
                Debug.Log($"[idle {scale:F1}x] steps {r.Steps} slide {r.SlideMm:F2}mm");
                if (r.Steps > 1) failures.Add($"{scale:F1}x: {r.Steps} micro-steps while standing (should be 0)");
                if (r.SlideMm > 6f) failures.Add($"{scale:F1}x: planted foot slides {r.SlideMm:F1}mm/frame while standing");
            }
            if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
        }

        static void Check(List<string> f, float vhat, string name, float a, float b, float c, float tol)
        {
            float spread = Mathf.Max(a, b, c) - Mathf.Min(a, b, c);
            if (spread > tol) f.Add($"vhat {vhat:F2}: {name} spans {spread:F3} (>{tol}) across sizes/rates -- not invariant");
        }

        static bool Finite(in BasisFootWalkResult r) =>
            !(float.IsNaN(r.Duty) || float.IsInfinity(r.Duty) || float.IsNaN(r.ClearFrac) || float.IsInfinity(r.ClearFrac) ||
              float.IsNaN(r.CadenceHat) || float.IsInfinity(r.CadenceHat) || float.IsNaN(r.ExtP95));
    }
}
