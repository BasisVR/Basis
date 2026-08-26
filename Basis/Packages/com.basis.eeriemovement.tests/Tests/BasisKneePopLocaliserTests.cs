using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using Basis.IK.Motion;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;
    public sealed class BasisKneePopLocaliserTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.eeriemovement.tests/Tests/MocapCorpus~");
        static List<BasisMotionClip> LoadCorpus()
        {
            var clips = new List<BasisMotionClip>();
            if (!Directory.Exists(CorpusDir)) Assert.Ignore($"no mocap corpus at {CorpusDir}");
            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            if (files.Length == 0) Assert.Ignore($"no mocap corpus at {CorpusDir}");
            foreach (string f in files)
            {
                if (BasisBvhLoader.TryLoad(f, out BasisMotionClip clip, out string err)) clips.Add(clip);
            }
            return clips;
        }
        static void Steps(Vector3[] p, out float[] d, out float med)
        {
            int n = p.Length - 1;
            d = new float[Mathf.Max(n, 0)];
            for (int i = 0; i < n; i++) d[i] = Vector3.Distance(p[i + 1], p[i]);
            med = n > 0 ? BasisMotionSignal.Quantile(d, 0.5f) : 0f;
            if (med <= 1e-9f) med = 1e-9f;
        }
        [Test]
        public void KneePops_Localised_AcrossTheCorpus()
        {
            List<BasisMotionClip> clips = LoadCorpus();
            var report = new StringBuilder();

            report.AppendLine("KNEE POPS -- localised. A 'pop' is a frame step > 8x the MEDIAN step of that same track.");
            report.AppendLine();
            report.AppendLine("Columns: reach = hip->foot / max reach at the popping frame (1.0 = the pole singularity).");
            report.AppendLine("         axis  = BasisLegSolveResult.AxisSource: 0 plane-normal 1 hint 2 target");
            report.AppendLine("                 3 bend-normal 4 pole-blended 5 pole-clamped-anterior");
            report.AppendLine("         human = did the HUMAN'S OWN knee also pop on this frame? If yes, the 'pop' is");
            report.AppendLine("                 in the SOURCE DATA and the solver is faithfully reproducing it.");
            report.AppendLine();

            foreach (BasisMocapHintSource hint in new[] { BasisMocapHintSource.None, BasisMocapHintSource.SwivelModel })
            {
                int total = 0, atSingularity = 0, humanPoppedToo = 0, tinyMedian = 0;
                var axisHist = new Dictionary<byte, int>();
                float worstStepM = 0f;

                report.AppendLine($"=== hint = {hint} ===");
                report.AppendLine($"{"clip",-10} {"frame",6} {"step m",8} {"ratio",7} {"reach",7} {"axis",5} {"human?",7}");

                foreach (BasisMotionClip clip in clips)
                {
                    var tracks = new BasisMocapAccuracy.BasisMocapTracks();
                    BasisMocapAccuracySummary acc = BasisMocapAccuracy.Run(clip, hint, null, tracks);
                    if (!acc.Ok || tracks.SolvedKnee == null) continue;

                    Steps(tracks.SolvedKnee, out float[] dSolved, out float medSolved);
                    Steps(tracks.TruthKnee, out float[] dTruth, out float medTruth);

                    // The confound this metric can hide: a knee that barely moves has a tiny median, so any real
                    // motion clears 8x and registers as a "pop". Flag it rather than pretend it cannot happen.
                    bool medianIsTiny = medSolved < 0.001f * tracks.LegLen;

                    for (int i = 0; i < dSolved.Length; i++)
                    {
                        if (!(dSolved[i] / medSolved > BasisMotionQuality.PopRatio)) continue;

                        total++;
                        bool human = dTruth[i] / medTruth > BasisMotionQuality.PopRatio;
                        if (human) humanPoppedToo++;
                        if (medianIsTiny) tinyMedian++;

                        float reach = tracks.KneeReach != null ? tracks.KneeReach[i + 1] : float.NaN;
                        byte axis = tracks.KneeAxis != null ? tracks.KneeAxis[i + 1] : (byte)255;
                        if (reach > 0.99f) atSingularity++;
                        axisHist.TryGetValue(axis, out int c);
                        axisHist[axis] = c + 1;
                        worstStepM = Mathf.Max(worstStepM, dSolved[i]);

                        if (total <= 60)   // keep the log readable; the aggregate below covers the rest
                        {
                            report.AppendLine($"{clip.Name,-10} {i + 1,6} {dSolved[i],8:F4} " + $"{dSolved[i] / medSolved,7:F1} {reach,7:F3} {axis,5} {(human ? "YES" : "no"),7}");
                        }
                    }
                }

                report.AppendLine();
                report.AppendLine($"  pops total ................. {total}");
                report.AppendLine($"  ...of which the HUMAN also popped on the same frame : {humanPoppedToo}");
                report.AppendLine($"  ...at reach > 0.99 (the pole singularity) .......... {atSingularity}");
                report.AppendLine($"  ...on a track whose median step is ~0 (metric artefact) : {tinyMedian}");
                report.AppendLine($"  worst single step .......... {worstStepM:F4} m");
                var axes = new List<byte>(axisHist.Keys);
                axes.Sort();
                foreach (byte a in axes) report.AppendLine($"  axis {a} ..................... {axisHist[a]}");
                report.AppendLine();
            }

            Debug.Log(report.ToString());
            Assert.Pass("report-only; read the log");
        }
    }
}
