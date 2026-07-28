using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    /// <summary>
    /// Headless entry points for the IK sweeps, so a gate can be checked without opening an editor
    /// window. The sweeps themselves are unchanged -- this only drives them.
    ///
    /// Why: BasisIKTestGates carries pass/fail thresholds that are only reachable through
    /// BasisElbowProtectSweepWindow / BasisIKSweepRunnerWindow, i.e. by hand. That means the numbers
    /// they encode drift out of date silently. ElbowMinClearedFraction is the worked example -- its
    /// comment claims a ~0.64 ceiling that was measured before several collider and arm-solve changes.
    ///
    ///   Unity.exe -runTests is for the NUnit suite; this is for the sweeps:
    ///   Unity.exe -batchmode -quit -projectPath &lt;p&gt; -executeMethod Basis.IK.Debugging.BasisIKSweepBatch.ElbowProtect
    /// </summary>
    public static class BasisIKSweepBatch
    {
        public static void ElbowProtect() => RunElbow(false);

        public static void ElbowProtectFullCircle() => RunElbow(true);

        static void RunElbow(bool fullCircle)
        {
            var cfg = BasisElbowProtectSweepConfig.Default();
            cfg.FullCircleSearch = fullCircle;
            string path = BasisElbowProtectSweep.DefaultPath();

            BasisElbowProtectSweepSummary s = BasisElbowProtectSweep.Run(cfg, path);
            if (!s.Ok)
            {
                Debug.LogError($"[ElbowProtectSweep] FAILED to run, path {s.Path}");
                return;
            }

            float clearedFrac = s.EngagedPoints > 0 ? (float)s.ClearedPoints / s.EngagedPoints : 1f;
            (bool pass, string reason) = BasisIKTestGates.GateElbow(s);

            Debug.Log(
                $"[ElbowProtectSweep] mode={(fullCircle ? "FULLCIRCLE" : "legacy")} " +
                $"points={s.Points} reachable={s.ReachablePoints} engaged={s.EngagedPoints} " +
                $"cleared={s.ClearedPoints} clearedFrac={clearedFrac:F4} " +
                $"couldNotClear={s.WrongSideFlipCount} " +
                $"meanResidualPenMm={s.MeanResidualPenMm:F2} maxResidualPenMm={s.MaxResidualPenMm:F2} " +
                $"meanSwingDeg={s.MeanSwingDeg:F2} maxSwingDeg={s.MaxSwingDeg:F2} " +
                $"meanSensDegPerCm={s.MeanSensDegPerCm:F3} maxSensDegPerCm={s.MaxSensDegPerCm:F3} " +
                $"GATE={(pass ? "PASS" : "FAIL")} ({reason})");
        }
    }
}
