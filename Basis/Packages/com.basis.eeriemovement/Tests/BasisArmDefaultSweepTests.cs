using System.Collections.Generic;
using System.IO;
using Basis.IK;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisArmDefaultSweepTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~");
        static List<BasisMotionClip> Corpus()
        {
            var clips = new List<BasisMotionClip>();
            if (!Directory.Exists(CorpusDir))
            {
                Assert.Ignore($"no mocap corpus at {CorpusDir}");
            }
            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            foreach (string f in files)
            {
                if (BasisBvhLoader.TryLoad(f, out BasisMotionClip clip, out _)) clips.Add(clip);
            }
            if (clips.Count == 0)
            {
                Assert.Ignore("corpus empty");
            }
            return clips;
        }
        [TearDown]
        public void ClearKnobs() => BasisMocapAccuracy.ArmKnobs = default;
        static void Row(string label, List<BasisMotionClip> clips, BasisMocapArmKnobs knobs)
        {
            BasisMocapAccuracy.ArmKnobs = knobs;
            float elbow = 0f, p95 = 0f, handInReach = 0f, hand = 0f;
            int pops = 0, n = 0;
            foreach (BasisMotionClip clip in clips)
            {
                BasisMocapAccuracySummary s = BasisMocapAccuracy.Run(clip, BasisMocapHintSource.Model, null);
                if (!s.Ok) continue;
                elbow += s.ElbowMeanFracArm;
                p95 += s.ElbowP95M;
                handInReach = Mathf.Max(handInReach, s.HandInReachMaxM);
                hand = Mathf.Max(hand, s.HandMaxM);
                pops += s.ElbowPops;
                n++;
            }
            Assert.That(n, Is.GreaterThan(0));
            TestContext.WriteLine($"SWEEP {label,-34} elbow {elbow / n * 100f,6:F2}% p95 {p95 / n * 100f,6:F2}cm  pops {pops,5}  handInReach {handInReach * 1000f,6:F2}mm  handMax {hand * 100f,6:F2}cm  clips {n}");
        }
        [Test, Explicit]
        public void Sweep_PriorAndStickiness()
        {
            List<BasisMotionClip> clips = Corpus();
            foreach (float prior in new[] { 0.25f, 0.5f, 1f, 2f })
                foreach (float previous in new[] { 0f, 0.15f, 0.25f, 0.5f })
                    Row($"prior {prior:F2} previous {previous:F2}", clips, new BasisMocapArmKnobs { Active = true, PriorWeight = prior, PreviousWeight = previous, ReachSoftness = -1f, PronationMaxDeg = -1f, SupinationMaxDeg = -1f, SmoothTime = -1f });
        }
        [Test, Explicit]
        public void Sweep_ReachSoftness()
        {
            List<BasisMotionClip> clips = Corpus();
            foreach (float softness in new[] { 0.02f, 0.03f, 0.04f, 0.06f, 0.08f })
                Row($"softness {softness:F3}", clips, new BasisMocapArmKnobs { Active = true, PriorWeight = -1f, PreviousWeight = -1f, ReachSoftness = softness, PronationMaxDeg = -1f, SupinationMaxDeg = -1f, SmoothTime = -1f });
        }
        [Test, Explicit]
        public void Sweep_ForearmCeiling()
        {
            List<BasisMotionClip> clips = Corpus();
            foreach (float ceiling in new[] { 80f, 85f, 90f, 95f, 120f })
                Row($"pronation/supination {ceiling:F0}", clips, new BasisMocapArmKnobs { Active = true, PriorWeight = -1f, PreviousWeight = -1f, ReachSoftness = -1f, PronationMaxDeg = ceiling, SupinationMaxDeg = ceiling, SmoothTime = -1f });
        }
        [Test, Explicit]
        public void Sweep_SwivelSmoothing()
        {
            List<BasisMotionClip> clips = Corpus();
            foreach (float smooth in new[] { 0f, 0.04f, 0.08f, 0.12f, 0.2f })
                Row($"smoothTime {smooth:F3}", clips, new BasisMocapArmKnobs { Active = true, PriorWeight = -1f, PreviousWeight = -1f, ReachSoftness = -1f, PronationMaxDeg = -1f, SupinationMaxDeg = -1f, SmoothTime = smooth });
        }
    }
}
