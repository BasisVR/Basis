using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    /// <summary>
    /// THE POSTURE CORPUS: what a real pelvis does, across every way a human gets their head low.
    ///
    /// The 20-clip corpus next door is calibrated for the ARM and the KNEE and its numbers are quoted all over
    /// this project, so it is left BYTE-IDENTICAL. These clips live in a subfolder that the other tests'
    /// non-recursive GetFiles cannot see, and they were chosen from the CMU index BY DESCRIPTION to span the
    /// one axis that matters:
    ///
    ///   SQUAT / SIT      pelvis rides the head down (13_29 squats, 75_17..75_20 sits graded by seat height,
    ///                    64_26..64_30 picking a ball off the floor, 139_16 getting up off the ground)
    ///   WAIST-BEND       pelvis stays high, spine folds (26_10/11 bend+lift, 02_06 bend over and scoop up,
    ///                    13_23/14_16 sweeping a floor, 77_06/07/08 poking about on the ground)
    ///   UPRIGHT          walks and stands -- and these are the most important clips in the folder. Without
    ///                    them the fit has never seen "doing nothing" and would cheerfully drop the pelvis of
    ///                    a user who is just standing there. The model is fitted THROUGH THE ORIGIN for the
    ///                    same reason: at zero head drop and zero lean it must return exactly zero, as an
    ///                    algebraic identity rather than as a hope.
    ///
    /// This file DUMPS the features and REPORTS the coverage. The fit runs on this dump and nothing else --
    /// the same one-pipeline rule the swivel models are under, and for the same reason: a model fitted in one
    /// frame and evaluated in another does not degrade, it produces confident garbage. It has already cost
    /// this project one full cycle (3.77% in Python, 31% in the harness, a mirror the two disagreed about).
    /// </summary>
    public sealed class BasisPostureCorpusTests
    {
        internal static string PostureDir => Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~/posture");

        internal static List<BasisMotionClip> LoadPostureCorpus()
        {
            if (!Directory.Exists(PostureDir)) Assert.Ignore($"no posture corpus at {PostureDir}");
            string[] files = Directory.GetFiles(PostureDir, "*.bvh");
            System.Array.Sort(files);
            if (files.Length == 0) Assert.Ignore($"no posture corpus at {PostureDir}");

            var clips = new List<BasisMotionClip>();
            foreach (string f in files)
            {
                if (BasisBvhLoader.TryLoad(f, out BasisMotionClip c, out string err)) clips.Add(c);
                else Debug.LogWarning($"[posture] skipping {Path.GetFileName(f)}: {err}");
            }
            return clips;
        }

        static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        [Test]
        public void PostureCorpus_LoadsAndCoversBothWaysOfGettingLow()
        {
            List<BasisMotionClip> clips = LoadPostureCorpus();
            Assert.Greater(clips.Count, 20, "the posture corpus should be substantially bigger than the 4 bend clips it replaces");

            var dump = new StringBuilder("clip,drop,fwd,pitch,hipsDrop,hipsFwd\n");
            var report = new StringBuilder();
            report.AppendLine("POSTURE CORPUS -- coverage. All quantities are fractions of the subject's STANDING HEAD HEIGHT.");
            report.AppendLine();
            report.AppendLine($"{"clip",-11} {"frames",7} {"maxDrop",8} {"maxFwd",8} {"dHips/dHead",12} {"hipsFwd@maxLean",16} verdict");
            report.AppendLine(new string('-', 84));

            int total = 0, deep = 0, squatish = 0, bendish = 0;
            float coverDropMax = 0f, coverFwdMax = 0f;

            foreach (BasisMotionClip c in clips)
            {
                BasisPostureFeatures.StandingReference(c, out float standHeadY, out float standHipsY);
                if (!(standHeadY > 0.1f)) continue;

                double vn = 0, vd = 0;
                float maxDrop = 0f, maxFwd = 0f, hipsFwdAtMaxLean = 0f;
                int frames = 0;

                for (int f = 0; f < c.FrameCount; f++)
                {
                    BasisPostureSample s = BasisPostureFeatures.Extract(c, f, standHeadY, standHipsY);
                    if (!s.Valid) continue;

                    dump.Append(c.Name).Append(',').Append(F(s.HeadDrop)).Append(',').Append(F(s.HeadFwd))
                        .Append(',').Append(F(s.HeadPitch))
                        .Append(',').Append(F(s.HipsDrop)).Append(',').Append(F(s.HipsFwd)).Append('\n');
                    frames++; total++;

                    if (s.HeadDrop > 0.05f) { vn += s.HeadDrop * s.HipsDrop; vd += s.HeadDrop * s.HeadDrop; deep++; }
                    if (s.HeadDrop > maxDrop) maxDrop = s.HeadDrop;
                    if (s.HeadFwd > maxFwd) { maxFwd = s.HeadFwd; hipsFwdAtMaxLean = s.HipsFwd; }
                }

                float slope = vd > 1e-9 ? (float)(vn / vd) : float.NaN;
                coverDropMax = Mathf.Max(coverDropMax, maxDrop);
                coverFwdMax = Mathf.Max(coverFwdMax, maxFwd);

                string verdict = float.IsNaN(slope) ? "upright (anchor)"
                               : slope > 0.60f ? "SQUAT-like (pelvis rides the head down)"
                               : slope > 0.30f ? "mixed"
                               : "WAIST-BEND-like (pelvis stays high)";
                if (!float.IsNaN(slope) && slope > 0.60f) squatish++;
                if (!float.IsNaN(slope) && slope <= 0.30f) bendish++;

                report.AppendLine($"{c.Name,-11} {frames,7} {maxDrop,8:F3} {maxFwd,8:F3} {slope,12:F3} {hipsFwdAtMaxLean,16:F3} {verdict}");
            }

            report.AppendLine(new string('-', 84));
            report.AppendLine($"clips {clips.Count}, frames {total}, of which {deep} have the head >5% of body height below standing.");
            report.AppendLine($"coverage: head drop up to {coverDropMax:F3}, head lean up to {coverFwdMax:F3} (fractions of standing head height)");
            report.AppendLine($"SQUAT-like clips: {squatish}    WAIST-BEND-like clips: {bendish}");
            report.AppendLine();
            report.AppendLine("BOTH FAMILIES MUST BE PRESENT or the fit cannot learn to tell them apart, which is the");
            report.AppendLine("entire point -- a corpus of squats alone would just relearn the constant the rig already has.");

            string path = Path.Combine(Path.GetTempPath(), "basis_posture_train.csv");
            File.WriteAllText(path, dump.ToString());
            report.AppendLine();
            report.AppendLine($"[POSTURE TRAIN] wrote {total} rows -> {path}");

            Debug.Log(report.ToString());

            Assert.Greater(deep, 2000, "not enough genuinely-bent frames to fit anything");
            Assert.Greater(squatish, 2, "the corpus must contain squat-like clips");
            Assert.Greater(bendish, 2, "the corpus must contain waist-bend-like clips -- without both, there is nothing to discriminate");
        }
    }
}
