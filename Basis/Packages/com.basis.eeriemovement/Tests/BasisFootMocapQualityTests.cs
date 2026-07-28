using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    // Disambiguate against the SDK's global-namespace BasisMotionClip ScriptableObject (see
    // BasisMocapMotionQualityTests.cs). Must live INSIDE the namespace.
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    /// <summary>
    /// "Does the procedural foot stepper WALK like a human?" -- measured against real mocap.
    ///
    /// The stepper (BasisFootSimulateJob) synthesises feet from hips/head/chest; it never sees real feet. So
    /// this drives the REAL job from each walking clip's hips/head/chest and scores the SYNTHESISED feet
    /// against the clip's REAL feet, dimensionlessly (fractions of leg length / pendulum time) so the numbers
    /// transfer to any avatar size. It is the foot analog of BasisMocapMotionQualityTests (elbow/knee).
    ///
    /// Two things are gated, in order:
    ///  1. the MEASUREMENT LAYER -- the real-human numbers must match the biomechanics literature, or the
    ///     whole comparison is fiction (the recurring "don't tune against a broken metric" lesson);
    ///  2. the STEPPER -- its walk must fall inside the human envelope (double-support, clearance, slide, reach).
    ///
    /// See project_basis_foot_mocap_comparison_harness for the Python twin and the baseline this validates.
    /// </summary>
    public sealed class BasisFootMocapQualityTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~");

        const float WalkLo = 0.14f, WalkHi = 0.60f;   // v-hat range that is walking (Fr < 0.5)

        static List<BasisMotionClip> LoadWalks()
        {
            if (!Directory.Exists(CorpusDir))
                Assert.Ignore($"no mocap corpus: drop CMU .bvh files into {CorpusDir}");

            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            if (files.Length == 0)
                Assert.Ignore($"no mocap corpus: drop CMU .bvh files into {CorpusDir}");

            var walks = new List<BasisMotionClip>();
            foreach (string f in files)
            {
                if (!BasisBvhLoader.TryLoad(f, out BasisMotionClip clip, out string err))
                {
                    Debug.LogWarning($"[FootQuality] skipping {Path.GetFileName(f)}: {err}");
                    continue;
                }
                // cheap classification: run the harness, keep clips whose v-hat is in the walking band
                BasisFootQualitySummary s = BasisMocapFootQuality.Run(clip);
                if (s.Ok && s.VHat >= WalkLo && s.VHat <= WalkHi && clip.FrameCount * clip.FrameTime >= 2.5f)
                    walks.Add(clip);
            }
            if (walks.Count == 0) Assert.Ignore("no walking clips found in the corpus");
            return walks;
        }

        static string Header =>
            $"{"clip",-12}{"vhat",6} {"chir",5} | {"duty s/r",11} {"dsup s/r",11} {"clear s/r",13} " +
            $"{"stepL s/r",13} {"cad s/r",11} | {"extP95",7} {"extMax",7} {"slide",6}";

        static string Row(in BasisFootQualitySummary s) =>
            $"{s.Clip,-12}{s.VHat,6:F2} {(s.ChiralityOk ? "ok" : "X!"),5} | " +
            $"{s.DutySyn,4:F2}/{s.DutyReal,-4:F2}  {s.DoubleSupportSyn,4:F2}/{s.DoubleSupportReal,-4:F2}  " +
            $"{s.ClearSyn,5:F3}/{s.ClearReal,-5:F3}  {s.StepLenSyn,5:F2}/{s.StepLenReal,-5:F2}  " +
            $"{s.CadenceSyn,4:F2}/{s.CadenceReal,-4:F2} | {s.ExtP95,7:F2} {s.ExtMax,7:F2} {s.PlantedSlideSyn,6:F1}";

        /// <summary>
        /// The measurement layer must be trustworthy BEFORE the stepper is judged against it. Real human
        /// walking has well-known dimensionless numbers; if ours drift out of the literature band, the contact
        /// detector or the leg-length normalisation is broken -- fix THAT before reading any synth number.
        /// Literature: duty ~0.55-0.65, double-support ~0.18-0.30 of the cycle, clearance ~0.13-0.20 L.
        /// </summary>
        [Test]
        public void RealHumanGait_MatchesLiterature()
        {
            List<BasisMotionClip> walks = LoadWalks();
            var sb = new StringBuilder();
            sb.AppendLine("REAL human gait measured off the corpus (this guards the RULER, not the stepper).");
            sb.AppendLine($"{"clip",-12}{"vhat",6} {"duty",6} {"dsup",6} {"clear",7} {"stepL",7} {"cadence",8}");
            float dutySum = 0, dsSum = 0, clrSum = 0; int nn = 0;
            foreach (BasisMotionClip clip in walks)
            {
                BasisFootQualitySummary s = BasisMocapFootQuality.Run(clip);
                if (!s.Ok) continue;
                sb.AppendLine($"{s.Clip,-12}{s.VHat,6:F2} {s.DutyReal,6:F2} {s.DoubleSupportReal,6:F2} " +
                              $"{s.ClearReal,7:F3} {s.StepLenReal,7:F2} {s.CadenceReal,8:F2}");
                dutySum += s.DutyReal; dsSum += s.DoubleSupportReal; clrSum += s.ClearReal; nn++;
            }
            float duty = dutySum / nn, dsup = dsSum / nn, clr = clrSum / nn;
            sb.AppendLine($"\nMEAN duty {duty:F2}  double-support {dsup:F2}  clearance {clr:F3} L  (n={nn})");
            Debug.Log(sb.ToString());

            // Bands are wide on purpose -- they catch a BROKEN detector (duty=0, clearance=0), not subtle feel.
            // Double-support in particular varies with the corpus's speed mix (a fast walk near v-hat 0.5 has
            // almost none), so its floor is generous.
            Assert.That(duty, Is.InRange(0.48f, 0.72f), "real-human DUTY out of literature band -- contact detector is broken");
            Assert.That(dsup, Is.InRange(0.08f, 0.32f), "real-human DOUBLE-SUPPORT out of band -- contact detector is broken");
            Assert.That(clr, Is.InRange(0.11f, 0.22f), "real-human CLEARANCE out of band -- leg normalisation or detector is broken");
        }

        /// <summary>
        /// The stepper's walk, scored against the real human in each clip. Collects every failure so the whole
        /// table survives one bad clip -- the table is what tightens the thresholds (there is no honest gate
        /// until the number exists). dsup/clear were the two biggest gaps the 2026-07-18 tune closed.
        /// </summary>
        [Test]
        public void Stepper_WalksLikeAHuman_AcrossTheCorpus()
        {
            List<BasisMotionClip> walks = LoadWalks();
            var report = new StringBuilder();
            report.AppendLine("Procedural stepper vs the real human, per walking clip. s = synthesised, r = real.");
            report.AppendLine("dsup = double-support fraction (real ~0.2-0.25; the 'floaty' tell when near 0).");
            report.AppendLine("clear = swing foot clearance / L (real ~0.15-0.19). cad = f*sqrt(L/g).");
            report.AppendLine("ext = synth foot-to-hips / standing reach; the tail is the no-flight-phase ceiling.");
            report.AppendLine();
            report.AppendLine(Header);
            report.AppendLine(new string('-', Header.Length));

            var failures = new List<string>();
            foreach (BasisMotionClip clip in walks)
            {
                BasisFootQualitySummary s = BasisMocapFootQuality.Run(clip);
                if (!s.Ok) { report.AppendLine($"{clip.Name,-12} ERROR: {s.Error}"); failures.Add($"{clip.Name}: {s.Error}"); continue; }
                report.AppendLine(Row(s));
                (bool pass, string reason) = BasisMocapFootQuality.Gate(s);
                if (!pass) failures.Add($"{clip.Name}: {reason}");
            }
            Debug.Log(report.ToString());

            if (failures.Count > 0)
                Assert.Fail($"{failures.Count} of {walks.Count} walking clips fail the naturalness gate:\n  " +
                            string.Join("\n  ", failures) + "\n\n" + report);
        }
    }
}
