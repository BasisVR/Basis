using Basis.Tests.Sync;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Rig = Basis.Tests.Sync.BasisFingerTestRig;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeFingerFidelityTests
    {
        static class Old
        {
            static float Curl(Vector3[] lm, int a, int b, int c, int d, float maxAngle)
            {
                Vector3 s1 = lm[b] - lm[a], s2 = lm[c] - lm[b], s3 = lm[d] - lm[c];
                return 1f - Mathf.Clamp01((Vector3.Angle(s1, s2) + Vector3.Angle(s2, s3)) / maxAngle) * 2f;
            }
            static float Splay(Vector3[] lm, int baseMcp, int basePip, int refMcp, int refPip, bool isLeft)
            {
                Vector3 dir = lm[basePip] - lm[baseMcp], refDir = lm[refPip] - lm[refMcp];
                Vector3 palmNormal = Vector3.Cross(lm[5] - lm[0], lm[17] - lm[0]);
                float splay = Mathf.Clamp(Vector3.SignedAngle(refDir, dir, palmNormal) / 20f, -1f, 1f);
                return isLeft ? splay : -splay;
            }
            public static Vector2[] Measure(Vector3[] lm, bool isLeft) => new[]
            {
                new Vector2(Curl(lm, 1, 2, 3, 4, 100f), Splay(lm, 2, 3, 5, 6, isLeft)),
                new Vector2(Curl(lm, 5, 6, 7, 8, 160f), Splay(lm, 5, 6, 9, 10, isLeft)),
                new Vector2(Curl(lm, 9, 10, 11, 12, 160f), 0f),
                new Vector2(Curl(lm, 13, 14, 15, 16, 160f), Splay(lm, 13, 14, 9, 10, isLeft)),
                new Vector2(Curl(lm, 17, 18, 19, 20, 160f), Splay(lm, 17, 18, 13, 14, isLeft)),
            };
        }
        static Vector2[] New(MediaPipeHandConverter converter, Vector3[] lm)
        {
            Assert.IsTrue(converter.TryMeasure(lm, out Vector2 thumb, out Vector2 index, out Vector2 middle, out Vector2 ring, out Vector2 little));
            return new[] { thumb, index, middle, ring, little };
        }
        struct Stats
        {
            public float Mean, P95, Max;
            public static Stats Of(List<float> e)
            {
                e.Sort();
                float sum = 0f;
                foreach (float v in e) sum += v;
                return new Stats { Mean = sum / e.Count, P95 = e[(int)(e.Count * 0.95f)], Max = e[e.Count - 1] };
            }
            public override string ToString() => $"mean {Mean,5:F1} p95 {P95,5:F1} max {Max,5:F1}";
        }
        static Vector3[] Noisy(Vector3[] lm, System.Random random, float sigma)
        {
            Vector3[] o = new Vector3[lm.Length];
            for (int i = 0; i < lm.Length; i++)
            {
                Vector3 n = new Vector3(Gauss(random), Gauss(random), Gauss(random)) * sigma;
                o[i] = lm[i] + n;
            }
            return o;
        }
        static float Gauss(System.Random random) => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble()));
        static IEnumerable<string> Rigs() => Rig.AnatomicalRigs();
        [TestCaseSource(nameof(Rigs))]
        public void NewFingerMath_TracksValveHandsBetterThanOld(string rigName)
        {
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            if (poses == null || poses.Count == 0) Assert.Ignore($"set {BasisValvePoses.Env} to an exported Valve pose json");
            using Rig rig = Rig.Build(rigName);
            MediaPipeHandConverter converter = new MediaPipeHandConverter();
            StringBuilder report = new StringBuilder($"[FingerROM] {rigName} MediaPipe finger math vs Valve hands (error = avatar fingertip vs real fingertip, palm frame, degrees)\n");
            foreach (float sigma in new[] { 0f, 0.003f })
            {
                System.Random random = new System.Random(1234);
                List<float> oldAll = new List<float>(), newAll = new List<float>(), oldThumb = new List<float>(), newThumb = new List<float>(), retAll = new List<float>(), retThumb = new List<float>();
                List<float>[] oldBy = new List<float>[10], newBy = new List<float>[10], retBy = new List<float>[10];
                for (int i = 0; i < 10; i++)
                {
                    oldBy[i] = new List<float>();
                    newBy[i] = new List<float>();
                    retBy[i] = new List<float>();
                }
                foreach (BasisValvePoses.Pose pose in poses)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        bool left = side == 0;
                        Vector3[] lm = BasisValvePoses.MediaPipeLandmarks(pose.Points, left);
                        if (sigma > 0f) lm = Noisy(lm, random, sigma);
                        Vector2[] before = Old.Measure(lm, left), after = New(converter, lm), solved = { new Vector2(0.75f, 0f), new Vector2(0.75f, 0f), new Vector2(0.75f, 0f), new Vector2(0.75f, 0f), new Vector2(0.75f, 0f) };
                        Assert.IsTrue(MediaPipeHandConverter.TrySolve(lm, rig.Retarget, left ? 0 : 5, solved));
                        for (int f = 0; f < 5; f++)
                        {
                            int finger = (left ? 0 : 5) + f;
                            Vector3 target = BasisValvePoses.Target(pose.Points, f, left);
                            float eo = Vector3.Angle(rig.PalmTip(finger, before[f]), target), en = Vector3.Angle(rig.PalmTip(finger, after[f]), target), er = Vector3.Angle(rig.PalmTip(finger, solved[f]), target);
                            retBy[finger].Add(er);
                            retAll.Add(er);
                            if (f == 0) retThumb.Add(er);
                            oldBy[finger].Add(eo);
                            newBy[finger].Add(en);
                            oldAll.Add(eo);
                            newAll.Add(en);
                            if (f == 0)
                            {
                                oldThumb.Add(eo);
                                newThumb.Add(en);
                            }
                        }
                    }
                }
                report.AppendLine($"  landmark noise {sigma * 1000f:F0} mm");
                for (int finger = 0; finger < 10; finger++) report.AppendLine($"    {(finger < 5 ? "L" : "R")} {Rig.FingerNames[finger % 5],-6} old {Stats.Of(oldBy[finger])}   math {Stats.Of(newBy[finger])}   retarget {Stats.Of(retBy[finger])}");
                Stats oa = Stats.Of(oldAll), na = Stats.Of(newAll), ot = Stats.Of(oldThumb), nt = Stats.Of(newThumb), ra = Stats.Of(retAll), rt = Stats.Of(retThumb);
                report.AppendLine($"    ALL      old {oa}   math {na}   retarget {ra}");
                report.AppendLine($"    THUMBS   old {ot}   math {nt}   retarget {rt}");
                Assert.Less(ra.Mean, na.Mean, $"{rigName} noise {sigma}: retarget is not more accurate than the finger math");
                Assert.Less(rt.Mean, ot.Mean * 0.5f, $"{rigName} noise {sigma}: retarget thumb is not at least twice as accurate as the old math");
                Assert.Less(na.Mean, oa.Mean, $"{rigName} noise {sigma}: new finger math is not more accurate overall");
                Assert.Less(nt.Mean, ot.Mean, $"{rigName} noise {sigma}: new thumb math is not more accurate");
            }
            Debug.Log(report.ToString());
        }
        [Test]
        public void NewFingerMath_LeftAndRightAgree()
        {
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            if (poses == null || poses.Count == 0) Assert.Ignore($"set {BasisValvePoses.Env} to an exported Valve pose json");
            MediaPipeHandConverter converter = new MediaPipeHandConverter();
            float worstNew = 0f, worstOld = 0f;
            foreach (BasisValvePoses.Pose pose in poses)
            {
                Vector3[] left = BasisValvePoses.MediaPipeLandmarks(pose.Points, true), right = BasisValvePoses.MediaPipeLandmarks(pose.Points, false);
                Vector2[] nl = New(converter, left), nr = New(converter, right), ol = Old.Measure(left, true), or = Old.Measure(right, false);
                for (int f = 0; f < 5; f++)
                {
                    worstNew = Mathf.Max(worstNew, (nl[f] - nr[f]).magnitude);
                    worstOld = Mathf.Max(worstOld, (ol[f] - or[f]).magnitude);
                }
            }
            Debug.Log($"[FingerROM] MediaPipe mirrored hands: worst left/right channel difference old {worstOld:F3} new {worstNew:F3}");
            Assert.Less(worstNew, 1e-3f);
        }
        [Test]
        public void NewFingerMath_Cost()
        {
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            if (poses == null || poses.Count == 0) Assert.Ignore($"set {BasisValvePoses.Env} to an exported Valve pose json");
            MediaPipeHandConverter converter = new MediaPipeHandConverter();
            Vector3[][] hands = new Vector3[poses.Count][];
            for (int i = 0; i < poses.Count; i++) hands[i] = BasisValvePoses.MediaPipeLandmarks(poses[i].Points, false);
            converter.TryMeasure(hands[0], out _, out _, out _, out _, out _);
            const int rounds = 200;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch watch = Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++)
                for (int i = 0; i < hands.Length; i++) converter.TryMeasure(hands[i], out _, out _, out _, out _, out _);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double us = watch.Elapsed.TotalMilliseconds * 1000.0 / (rounds * hands.Length);
            Debug.Log($"[FingerROM] MediaPipe TryMeasure {us:F2} us per hand, allocated {allocated} bytes");
            Assert.AreEqual(0, allocated);
        }
    }
}
