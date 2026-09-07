using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using Basis.IK.Motion;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    // Disambiguate against the SDK's global-namespace BasisMotionClip ScriptableObject. Must live INSIDE the
    // namespace: at file scope the alias itself sits in the global namespace and collides with the type it is
    // disambiguating (CS0576). See BasisMocapMotionQuality.cs.
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;
    public sealed class BasisMocapMotionQualityTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~");
        static List<BasisMotionClip> LoadCorpus()
        {
            var clips = new List<BasisMotionClip>();
            if (!Directory.Exists(CorpusDir))
                Assert.Ignore($"no mocap corpus: drop CMU .bvh files into {CorpusDir}");

            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            if (files.Length == 0)
                Assert.Ignore($"no mocap corpus: drop CMU .bvh files into {CorpusDir}");

            foreach (string f in files)
            {
                if (BasisBvhLoader.TryLoad(f, out BasisMotionClip clip, out string err))
                    clips.Add(clip);
                else
                    Debug.LogWarning($"[MotionQuality] skipping {Path.GetFileName(f)}: {err}");
            }
            return clips;
        }
        [Test]
        public void ShippedSolver_MovesLikeAHuman_AcrossTheCorpus()
        {
            List<BasisMotionClip> clips = LoadCorpus();

            var report = new StringBuilder();
            report.AppendLine("Motion quality of the SHIPPED solver (hint = Lookup), vs the real human in each clip.");
            report.AppendLine("jerk x  = solved jerk / human jerk. 1.00 = as alive as the human.");
            report.AppendLine("          BAND is two-sided: << 1 is mush (over-smoothed / plateaued / lerped),");
            report.AppendLine("          >> 1 is the solver inventing motion the human never made.");
            report.AppendLine("jit+    = buzz above 8 Hz the solver ADDS, as % of limb length.");
            report.AppendLine("shape   = spectral shape mismatch vs the human's own joint (0 = identical).");
            report.AppendLine();
            report.AppendLine("hintRaw / hintFlared = jitter of the elbow HINT itself, before and after the");
            report.AppendLine("          chicken-wing flare. Localises WHICH stage invents the buzz.");
            report.AppendLine();
            report.AppendLine($"{"clip",-12} {"frames",6} | {"elbow jerk x",12} {"jit+%L",7} {"pops+",5} " + $"| {"hintRaw",7} {"hintFlare",9} | {"engJit",9} {"dpP05",8} {"dpMin",8}");
            report.AppendLine(new string('-', 108));

            var failures = new List<string>();

            foreach (BasisMotionClip clip in clips)
            {
                BasisMocapMotionSummary s = BasisMocapMotionQuality.Run(clip, BasisMocapHintSource.Lookup);
                if (!s.Ok)
                {
                    report.AppendLine($"{clip.Name,-12} {clip.FrameCount,6} | ERROR: {s.Error}");
                    failures.Add($"{clip.Name}: {s.Error}");
                    continue;
                }

                report.AppendLine($"{clip.Name,-12} {s.Frames,6} | {s.ElbowJerkRatio,12:F2} {s.ElbowJitterExcess * 100f,7:F3} " + $"{s.ElbowPopExcess,5} | {s.HintRawJitter * 100f,7:F3} {s.HintFlaredJitter * 100f,9:F3} " + $"| {s.FlareEngageJitter,9:F5} {s.FlareDownProjP05,8:F3} {s.FlareDownProjMin,8:F3}");

                (bool pass, string reason) = BasisMocapMotionQuality.Gate(s);
                if (!pass) failures.Add($"{clip.Name}: {reason}");
            }

            Debug.Log(report.ToString());

            if (failures.Count > 0)
                Assert.Fail($"{failures.Count} of {clips.Count} clips fail the naturalness gate:\n  " + string.Join("\n  ", failures) + "\n\n" + report);
        }
        [Test]
        public void SolverDoesNotBuzz_WhenTheHumanIsStandingStill()
        {
            List<BasisMotionClip> clips = LoadCorpus();
            var idle = clips.FindAll(c => c.Name is "77_02" or "113_21" or "141_20");
            if (idle.Count == 0)
                Assert.Ignore("no idle clips in the corpus (expected 77_02 / 113_21 / 141_20)");

            var failures = new List<string>();
            foreach (BasisMotionClip clip in idle)
            {
                BasisMocapMotionSummary s = BasisMocapMotionQuality.Run(clip, BasisMocapHintSource.Lookup);
                if (!s.Ok) { failures.Add($"{clip.Name}: {s.Error}"); continue; }

                Debug.Log($"[idle] {clip.Name}: elbow jitter+{s.ElbowJitterExcess * 100f:F3}%L @ " + $"{s.SolvedElbow.JitterHz:F0}Hz, knee jitter+{s.KneeJitterExcess * 100f:F3}%L @ " + $"{s.SolvedKnee.JitterHz:F0}Hz");

                if (s.ElbowJitterExcess > BasisMocapMotionQuality.MaxJitterExcess)
                    failures.Add($"{clip.Name}: elbow buzzes {s.ElbowJitterExcess * 100f:F2}%L above 8 Hz " + $"at {s.SolvedElbow.JitterHz:F0}Hz while the human stands still");
                if (s.KneeJitterExcess > BasisMocapMotionQuality.MaxJitterExcess)
                    failures.Add($"{clip.Name}: knee buzzes {s.KneeJitterExcess * 100f:F2}%L above 8 Hz " + $"at {s.SolvedKnee.JitterHz:F0}Hz while the human stands still");
            }

            if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
        }
    }
}
