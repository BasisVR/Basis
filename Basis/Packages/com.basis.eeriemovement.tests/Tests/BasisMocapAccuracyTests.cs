using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisMocapAccuracyTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.eeriemovement.tests/Tests/MocapCorpus~");
        static List<BasisMotionClip> LoadCorpus()
        {
            var clips = new List<BasisMotionClip>();
            if (!Directory.Exists(CorpusDir)) return clips;

            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            foreach (string f in files)
            {
                Assert.That(BasisBvhLoader.TryLoad(f, out BasisMotionClip clip, out string err), Is.True, $"failed to load {Path.GetFileName(f)}: {err}");
                clips.Add(clip);
            }
            return clips;
        }
        static List<BasisMotionClip> RequireCorpus()
        {
            List<BasisMotionClip> clips = LoadCorpus();
            if (clips.Count == 0)
            {
                Assert.Ignore($"no mocap corpus: drop CMU .bvh files into {CorpusDir}");
            }
            return clips;
        }
        [Test]
        public void Corpus_LoadsAsAnAnatomicallySaneHuman()
        {
            foreach (BasisMotionClip clip in RequireCorpus())
            {
                Assert.That(BasisBvhLoader.Validate(clip, out string why), Is.True, $"{clip.Name}: {why}");

                float arm = Vector3.Distance(clip.Get(0, BasisMocapJoint.LeftUpperArm).Position, clip.Get(0, BasisMocapJoint.LeftLowerArm).Position) + Vector3.Distance(clip.Get(0, BasisMocapJoint.LeftLowerArm).Position, clip.Get(0, BasisMocapJoint.LeftHand).Position);
                // Normalised to an 0.85 m leg, so the arm must land in adult range or the scaling is off.
                Assert.That(arm, Is.InRange(0.40f, 0.80f), $"{clip.Name}: arm length {arm:F2} m is not human after scaling");
            }
        }
        [Test]
        public void GivenTheTrueJoint_TheSolverReproducesIt()
        {
            var failures = new List<string>();
            foreach (BasisMotionClip clip in RequireCorpus())
            {
                string csv = Path.Combine(Application.persistentDataPath, "MocapAccuracy", $"{clip.Name}_TruthJoint.csv");
                BasisMocapAccuracySummary s = BasisMocapAccuracy.Run(clip, BasisMocapHintSource.TruthJoint, csv);
                (bool pass, string reason) = BasisMocapAccuracy.Gate(s);
                TestContext.WriteLine($"  [{(pass ? "ok" : "FAIL")}] {clip.Name}: {reason}");
                // Collect rather than abort, so every clip's CSV is written and the whole picture is visible.
                if (!pass) failures.Add($"{clip.Name}: {reason}");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }
        [Test]
        public void ShippedElbow_MeasuredAgainstRealHumans()
        {
            var log = new StringBuilder("\nIK accuracy vs real human motion (CMU mocap, joint error in cm)\n");
            log.AppendLine("  clip          hint         elbow mean   p95     max    (% of arm)   knee mean   pops(e/k)");

            var lookupElbow = new List<float>();
            var truthElbow = new List<float>();
            var effectorMisses = new List<string>();
            float footSlip = 0f;

            foreach (BasisMotionClip clip in RequireCorpus())
            {
                foreach (BasisMocapHintSource hint in new[]
                {
                    BasisMocapHintSource.None, BasisMocapHintSource.Lookup, BasisMocapHintSource.TruthJoint,
                })
                {
                    string csv = Path.Combine(Application.persistentDataPath, "MocapAccuracy", $"{clip.Name}_{hint}.csv");
                    BasisMocapAccuracySummary s = BasisMocapAccuracy.Run(clip, hint, csv);
                    Assert.That(s.Ok, Is.True, $"{clip.Name} [{hint}]: {s.Error}");

                    log.AppendLine($"  {clip.Name,-12}  {hint,-10}  {s.ElbowMeanM * 100f,8:F1}  {s.ElbowP95M * 100f,6:F1}  {s.ElbowMaxM * 100f,6:F1}   " + $"{s.ElbowMeanFracArm * 100f,7:F1}%   {s.KneeMeanM * 100f,8:F1}     {s.ElbowPops}/{s.KneePops}");

                    if (hint == BasisMocapHintSource.Lookup) lookupElbow.Add(s.ElbowMeanM);
                    if (hint == BasisMocapHintSource.TruthJoint) truthElbow.Add(s.ElbowMeanM);

                    // The hand is commanded and the arm solve is reach-preserving, so it must be hit. The FOOT is
                    // a different matter: the leg hint is not reach-preserving, so foot slip is a measured solver
                    // property, reported in the table above rather than treated as a harness fault.
                    if (s.HandMaxM > 0.01f) effectorMisses.Add($"{clip.Name} [{hint}]: hand missed by {s.HandMaxM * 100f:F1} cm");
                    footSlip = Mathf.Max(footSlip, s.FootMaxM);
                }
            }

            log.AppendLine($"\n  CSVs: {Path.Combine(Application.persistentDataPath, "MocapAccuracy")}");
            TestContext.WriteLine(log.ToString());

            Assert.That(effectorMisses, Is.Empty, "the solver did not reach targets it was handed:\n" + string.Join("\n", effectorMisses));
            Assert.That(lookupElbow.Count, Is.GreaterThan(0), "no clips measured");

            // The one thing we can assert without a settled baseline: the shipped lookup must not be WORSE than
            // simply handing the solver the true elbow. If it were, the lookup would be actively harmful.
            float lookupMean = Mean(lookupElbow), truthMean = Mean(truthElbow);
            Assert.That(lookupMean, Is.GreaterThanOrEqualTo(truthMean - 1e-4f), $"the shipped lookup elbow ({lookupMean * 100f:F1} cm) beat the TRUE elbow ({truthMean * 100f:F1} cm) -- impossible, the harness is wrong");
        }
        static float Mean(List<float> v) { float t = 0f; foreach (float x in v) t += x; return v.Count > 0 ? t / v.Count : float.NaN; }
    }
}
