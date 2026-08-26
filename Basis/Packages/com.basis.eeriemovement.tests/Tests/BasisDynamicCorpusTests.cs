using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisDynamicCorpusTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.eeriemovement.tests/Tests/MocapCorpus~/dynamic");
        static List<BasisMotionClip> RequireCorpus()
        {
            var clips = new List<BasisMotionClip>();
            if (!Directory.Exists(CorpusDir)) { Assert.Ignore($"no dynamic corpus: {CorpusDir}"); return clips; }

            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            foreach (string f in files)
            {
                Assert.That(BasisBvhLoader.TryLoad(f, out BasisMotionClip clip, out string err), Is.True, $"failed to load {Path.GetFileName(f)}: {err}");
                clips.Add(clip);
            }
            if (clips.Count == 0) Assert.Ignore($"no .bvh in {CorpusDir}");
            return clips;
        }
        [Test]
        public void DynamicCorpus_EveryClipIsAnatomicallySane()
        {
            int n = 0;
            foreach (BasisMotionClip clip in RequireCorpus())
            {
                Assert.That(BasisBvhLoader.Validate(clip, out string why), Is.True, $"{clip.Name}: {why}");
                n++;
            }
            Assert.That(n, Is.GreaterThan(20), $"expected a substantial dynamic corpus, loaded {n}");
        }
        [Test]
        public void TheSolver_StaysReachPreservingRigidAndFinite_AcrossDynamicMotion()
        {
            var log = new StringBuilder("\nIK on DYNAMIC / arms-up real human motion (CMU dance / sport / calisthenics)\n");
            log.AppendLine("  clip        frames   elbow mean/p95/max cm   knee mean cm   handMiss  footSlip   rigid   pops(e/k)");

            var failures = new List<string>();
            int measured, totalElbowPops = 0, totalKneePops = 0;
            var elbowMeans = new List<float>();

            List<BasisMotionClip> clips = RequireCorpus();
            measured = 0;
            foreach (BasisMotionClip clip in clips)
            {
                string csv = Path.Combine(Application.persistentDataPath, "DynamicCorpus", $"{clip.Name}_Lookup.csv");
                BasisMocapAccuracySummary s = BasisMocapAccuracy.Run(clip, BasisMocapHintSource.Lookup, csv);
                Assert.That(s.Ok, Is.True, $"{clip.Name}: {s.Error}");   // Ok = false includes a NaN/parse blow-up

                log.AppendLine($"  {clip.Name,-10}  {s.Frames,6}   {s.ElbowMeanM * 100f,5:F1}/{s.ElbowP95M * 100f,4:F1}/{s.ElbowMaxM * 100f,4:F1}      " + $"{s.KneeMeanM * 100f,5:F1}      {s.HandMaxM * 1000f,6:F2}mm {s.FootMaxM * 1000f,6:F2}mm {s.RigidityMaxM * 1000f,5:F2}mm  {s.ElbowPops}/{s.KneePops}");

                // HARD invariants (must hold on any motion). Bounds match BasisMocapAccuracy.Gate, loosened a
                // hair for the extra excursion of dance/throws so they fail ONLY on a real regression.
                if (s.HandMaxM > 0.01f)
                    failures.Add($"{clip.Name}: the arm missed the hand it was handed by {s.HandMaxM * 100f:F1} cm -- reach not preserved on dynamic motion");
                if (s.RigidityMaxM > 0.003f)
                    failures.Add($"{clip.Name}: the solved rotations rebuild the joint {s.RigidityMaxM * 1000f:F1} mm off its solved position");

                elbowMeans.Add(s.ElbowMeanM);
                totalElbowPops += s.ElbowPops;
                totalKneePops += s.KneePops;
                measured++;
            }

            log.AppendLine($"\n  {measured} clips, elbow-pops total {totalElbowPops}, knee-pops total {totalKneePops}. " + $"CSVs: {Path.Combine(Application.persistentDataPath, "DynamicCorpus")}");
            TestContext.WriteLine(log.ToString());

            Assert.That(measured, Is.GreaterThan(20), $"measured only {measured} dynamic clips -- corpus not found?");
            Assert.That(failures, Is.Empty, "the solver broke a hard invariant on dynamic real-human motion:\n" + string.Join("\n", failures));
        }
        [Test]
        public void ElbowAnatomyGuard_ReproducesLegitimateOverheadElbows_DoesNotClampThemDown()
        {
            string[] armsUp = { "49_09", "49_12", "33_02", "06_14" };
            var log = new StringBuilder("\nOVERHEAD-elbow reproduction (the guard must not clamp a high elbow when the hand is high):\n");
            int checkedClips = 0;

            foreach (BasisMotionClip clip in RequireCorpus())
            {
                if (System.Array.IndexOf(armsUp, clip.Name) < 0) continue;

                string csv = Path.Combine(Application.persistentDataPath, "DynamicCorpus", $"{clip.Name}_Truth.csv");
                BasisMocapAccuracySummary s = BasisMocapAccuracy.Run(clip, BasisMocapHintSource.TruthJoint, csv);
                Assert.That(s.Ok, Is.True, $"{clip.Name}: {s.Error}");

                log.AppendLine($"  {clip.Name}: handed the TRUE (overhead) elbow, reproduced to mean {s.ElbowMeanM * 100f:F1} cm, max {s.ElbowMaxM * 100f:F1} cm");

                // Handed the true elbow, the solver must land essentially on it. 5 cm is generous headroom over
                // the ~1 cm this produces; a guard fighting the overhead pose would blow well past it.
                Assert.That(s.ElbowMeanM, Is.LessThan(0.05f), $"{clip.Name}: the solver placed the elbow {s.ElbowMeanM * 100f:F1} cm from the TRUE overhead elbow -- " +"the anatomy guard is fighting a legitimate arms-up pose (it should only forbid a HIGH elbow when the hand is LOW).");
                checkedClips++;
            }

            TestContext.WriteLine(log.ToString());
            Assert.That(checkedClips, Is.GreaterThan(0), "the arms-up clips (49_09, 49_12, 33_02, 06_14) were not found in the dynamic corpus");
        }
    }
}
