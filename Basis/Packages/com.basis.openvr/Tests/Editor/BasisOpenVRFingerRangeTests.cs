using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.OpenVR;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI.UI_Panels;
using Basis.Tests.Sync;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Rig = Basis.Tests.Sync.BasisFingerTestRig;
namespace Basis.OpenVR.Tests
{
    public class BasisOpenVRFingerRangeTests
    {
        const string LibraryEnv = "BASIS_FINGER_LIBRARY";
        const float OldBias = -0.8f, Step = 0.02f;
        static readonly string[] FingerNames = Rig.FingerNames;
        static BasisFingerPose Map(bool newMapping, float[] curls, float thumbIndex, float indexMiddle, float middleRing, float ringPinky)
        {
            BasisFingerPose pose = new BasisFingerPose();
            BasisOpenVRInputController.ApplySkeletalSummary(pose, curls, thumbIndex, indexMiddle, middleRing, ringPinky, OldBias);
            if (!newMapping)
            {
                pose.ThumbPercentage.y = thumbIndex * 2f - 1f + OldBias;
                pose.IndexPercentage.y = 0.5f * (thumbIndex + indexMiddle) * 2f - 1f + OldBias;
            }
            return pose;
        }
        static Vector2 Get(BasisFingerPose pose, int finger)
        {
            switch (finger % 5)
            {
                case 0: return pose.ThumbPercentage;
                case 1: return pose.IndexPercentage;
                case 2: return pose.MiddlePercentage;
                case 3: return pose.RingPercentage;
                default: return pose.LittlePercentage;
            }
        }
        struct Sweep
        {
            public float Total, MaxStep, MeanStep, DeadFraction, MaxBacktrack;
            public override string ToString() => $"range {Total,6:F1} deg  dead {DeadFraction * 100f,5:F1}%  maxStep {MaxStep,5:F2}  meanStep {MeanStep,5:F2}  backtrack {MaxBacktrack,5:F2}";
        }
        static Sweep Measure(List<Vector3> tips)
        {
            Sweep s = new Sweep();
            int dead = 0;
            float sum = 0f, farthest = 0f;
            for (int i = 1; i < tips.Count; i++)
            {
                float step = Vector3.Angle(tips[i - 1], tips[i]);
                sum += step;
                s.MaxStep = Mathf.Max(s.MaxStep, step);
                if (step < 0.05f) dead++;
                float fromStart = Vector3.Angle(tips[0], tips[i]);
                farthest = Mathf.Max(farthest, fromStart);
                s.MaxBacktrack = Mathf.Max(s.MaxBacktrack, farthest - fromStart);
                s.Total = Mathf.Max(s.Total, fromStart);
            }
            s.MeanStep = sum / (tips.Count - 1);
            s.DeadFraction = dead / (float)(tips.Count - 1);
            return s;
        }
        static Sweep SweepThumbSplay(Rig rig, int finger, bool newMapping, float curl)
        {
            List<Vector3> tips = new List<Vector3>();
            for (float v = 0f; v <= 1.0001f; v += Step) tips.Add(rig.Tip(finger, Get(Map(newMapping, new[] { curl, 0.2f, 0.2f, 0.2f, 0.2f }, v, 0.5f, 0.5f, 0.5f), finger)));
            return Measure(tips);
        }
        static Sweep SweepCurl(Rig rig, int finger, float thumbIndex)
        {
            List<Vector3> tips = new List<Vector3>();
            for (float v = 0f; v <= 1.0001f; v += Step)
            {
                float[] curls = { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
                curls[finger % 5] = v;
                tips.Add(rig.Tip(finger, Get(Map(true, curls, thumbIndex, 0.5f, 0.5f, 0.5f), finger)));
            }
            return Measure(tips);
        }
        static float Coverage(Rig rig, int finger, bool newMapping)
        {
            List<Vector3> tips = new List<Vector3>();
            for (float c = 0f; c <= 1.0001f; c += 0.1f)
                for (float s = 0f; s <= 1.0001f; s += 0.1f)
                    tips.Add(rig.Tip(finger, Get(Map(newMapping, new[] { c, 0.2f, 0.2f, 0.2f, 0.2f }, s, 0.5f, 0.5f, 0.5f), finger)));
            float widest = 0f;
            for (int i = 0; i < tips.Count; i++)
                for (int j = i + 1; j < tips.Count; j++) widest = Mathf.Max(widest, Vector3.Angle(tips[i], tips[j]));
            return widest;
        }
        static IEnumerable<string> Rigs() => Rig.Rigs();
        static IEnumerable<string> AnatomicalRigs() => Rig.AnatomicalRigs();
        static void SummaryChecks(Rig rig, StringBuilder report, bool assert)
        {
            string n = rig.Name;
            report.AppendLine($"[FingerROM] {n} thumb spread sweep, thumbIndex 0..1");
            foreach (int finger in new[] { 0, 5 })
            {
                foreach (float curl in new[] { 0f, 0.3f, 0.6f })
                {
                    Sweep before = SweepThumbSplay(rig, finger, false, curl), after = SweepThumbSplay(rig, finger, true, curl);
                    report.AppendLine($"  {(finger == 0 ? "L" : "R")} curl {curl:F1}  old: {before}");
                    report.AppendLine($"  {(finger == 0 ? "L" : "R")} curl {curl:F1}  new: {after}");
                    if (!assert) continue;
                    Assert.Less(after.DeadFraction, 0.05f, $"{n} finger {finger} curl {curl}: new mapping has a dead zone");
                    Assert.Greater(after.Total, before.Total, $"{n} finger {finger} curl {curl}: new mapping does not widen thumb spread");
                    Assert.Less(after.MaxStep, after.MeanStep * 4f + 0.1f, $"{n} finger {finger} curl {curl}: thumb spread jumps");
                    Assert.Less(after.MaxBacktrack, 0.5f, $"{n} finger {finger} curl {curl}: thumb spread is not monotonic");
                }
            }
            float leftOld = Coverage(rig, 0, false), leftNew = Coverage(rig, 0, true), rightOld = Coverage(rig, 5, false), rightNew = Coverage(rig, 5, true);
            report.AppendLine($"[FingerROM] {n} thumb tip reach (widest angle between reachable tip directions): L old {leftOld:F1} new {leftNew:F1}, R old {rightOld:F1} new {rightNew:F1}");
            report.AppendLine($"[FingerROM] {n} index tip motion while only thumbIndex sweeps 0..1");
            foreach (int finger in new[] { 1, 6 })
            {
                foreach (bool newMapping in new[] { false, true })
                {
                    List<Vector3> tips = new List<Vector3>();
                    for (float v = 0f; v <= 1.0001f; v += Step) tips.Add(rig.Tip(finger, Get(Map(newMapping, new[] { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f }, v, 0.7f, 0.5f, 0.5f), finger)));
                    Sweep s = Measure(tips);
                    report.AppendLine($"  {(finger == 1 ? "L" : "R")} {(newMapping ? "new" : "old")}: {s}");
                    if (assert && newMapping) Assert.Less(s.Total, 0.01f, $"{n} index {finger} follows the thumb");
                }
            }
            report.AppendLine($"[FingerROM] {n} curl sweep 0..1 per finger");
            for (int finger = 0; finger < 10; finger++)
            {
                Sweep s = SweepCurl(rig, finger, 0.5f);
                report.AppendLine($"  {(finger < 5 ? "L" : "R")} {FingerNames[finger % 5],-6} {s}");
                if (!assert) continue;
                Assert.Greater(s.Total, 20f, $"{n} finger {finger} barely curls");
                Assert.Less(s.DeadFraction, 0.05f, $"{n} finger {finger} curl has a dead zone");
                Assert.Less(s.MaxBacktrack, 0.5f, $"{n} finger {finger} curl is not monotonic");
                Assert.Less(s.MaxStep, s.MeanStep * 4f + 0.1f, $"{n} finger {finger} curl jumps");
            }
            if (!assert) return;
            Assert.Greater(leftNew, leftOld);
            Assert.Greater(rightNew, rightOld);
        }
        static string Describe(Vector3 d) => $"fwd {Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg,6:F1} up {Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg,6:F1}";
        static void ValveChecks(Rig rig, List<BasisValvePoses.Pose> poses, StringBuilder report, bool assert)
        {
            string n = rig.Name;
            Vector2 rest = new Vector2(0.75f, 0f);
            report.AppendLine($"[FingerROM] {n} Valve pose replay through the tip retarget (error = angle between avatar fingertip and tracked fingertip, palm frame)");
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                for (int f = 0; f < 5; f++)
                {
                    int finger = (left ? 0 : 5) + f;
                    List<float> errors = new List<float>();
                    foreach (BasisValvePoses.Pose pose in poses)
                    {
                        Vector3 target = BasisValvePoses.Target(pose.Points, f, left);
                        errors.Add(Vector3.Angle(rig.PalmTip(finger, rig.Retarget.Solve(finger, target, rest)), target));
                    }
                    errors.Sort();
                    float mean = 0f;
                    foreach (float e in errors) mean += e;
                    mean /= errors.Count;
                    report.AppendLine($"  {(left ? "L" : "R")} {FingerNames[f],-6} mean {mean,5:F1}  p95 {errors[(int)(errors.Count * 0.95f)],5:F1}  max {errors[errors.Count - 1],5:F1}");
                    if (assert) Assert.Less(mean, 12f, $"{n} {(left ? "L" : "R")} {FingerNames[f]} mean retarget error");
                }
            }
            report.AppendLine($"[FingerROM] {n} labelled Touch thumb poses (right hand): tracked vs avatar thumb direction, solved curl/splay");
            Dictionary<string, Vector3> trackedByLabel = new Dictionary<string, Vector3>(), avatarByLabel = new Dictionary<string, Vector3>();
            foreach (BasisValvePoses.Pose pose in poses)
            {
                if (pose.Set != "touch" || string.IsNullOrEmpty(pose.Label)) continue;
                Vector3 target = BasisValvePoses.Target(pose.Points, 0, false);
                Vector2 solved = rig.Retarget.Solve(5, target, rest);
                Vector3 achieved = rig.PalmTip(5, solved);
                trackedByLabel[pose.Label] = target;
                avatarByLabel[pose.Label] = achieved;
                report.AppendLine($"  {pose.Label,-14} tracked {Describe(target)}  avatar {Describe(achieved)}  curl {solved.x,5:F2} splay {solved.y,5:F2}  err {Vector3.Angle(achieved, target),5:F1}");
            }
            string[][] pairs = { new[] { "thumbrest", "A" }, new[] { "thumbrest", "B" }, new[] { "thumbrest", "stick centre" }, new[] { "A", "B" }, new[] { "lifted", "thumbrest" }, new[] { "stick back", "stick fwd" } };
            report.AppendLine($"[FingerROM] {n} thumb separation between Touch poses (tracked vs avatar)");
            foreach (string[] pair in pairs)
            {
                if (!trackedByLabel.ContainsKey(pair[0]) || !trackedByLabel.ContainsKey(pair[1])) continue;
                report.AppendLine($"  {pair[0],-12} -> {pair[1],-12} tracked {Vector3.Angle(trackedByLabel[pair[0]], trackedByLabel[pair[1]]),5:F1}  avatar {Vector3.Angle(avatarByLabel[pair[0]], avatarByLabel[pair[1]]),5:F1}");
            }
            report.AppendLine($"[FingerROM] {n} thumb joint bends, avatar vs tracked (all Valve poses, right hand): mean |difference| and the thumbrest -> A change");
            float mcpDiff = 0f, ipDiff = 0f;
            int count = 0;
            float[] restA = new float[4];
            foreach (BasisValvePoses.Pose pose in poses)
            {
                Vector2 solved = rig.Retarget.Solve(5, BasisValvePoses.Target(pose.Points, 0, false), rest);
                ThumbBends(rig, solved, out float aMcp, out float aIp);
                Vector3[] p = pose.Points;
                float tMcp = Vector3.Angle(p[2] - p[1], p[3] - p[2]), tIp = Vector3.Angle(p[3] - p[2], p[4] - p[3]);
                mcpDiff += Mathf.Abs(aMcp - tMcp);
                ipDiff += Mathf.Abs(aIp - tIp);
                count++;
                if (pose.Set == "touch" && pose.Label == "thumbrest") { restA[0] -= tMcp; restA[1] -= tIp; restA[2] -= aMcp; restA[3] -= aIp; }
                if (pose.Set == "touch" && pose.Label == "A") { restA[0] += tMcp; restA[1] += tIp; restA[2] += aMcp; restA[3] += aIp; }
            }
            report.AppendLine($"  middle joint mean |diff| {mcpDiff / count,5:F1}  tip joint mean |diff| {ipDiff / count,5:F1}");
            report.AppendLine($"  thumbrest -> A: tracked middle {restA[0],6:F1} tip {restA[1],6:F1}   avatar middle {restA[2],6:F1} tip {restA[3],6:F1}");
            float worstJump = 0f;
            foreach (string[] pair in pairs)
            {
                BasisValvePoses.Pose a = poses.Find(p => p.Set == "touch" && p.Label == pair[0]), b = poses.Find(p => p.Set == "touch" && p.Label == pair[1]);
                if (a.Points == null || b.Points == null) continue;
                Vector2 previous = rig.Retarget.Solve(5, BasisValvePoses.Target(a.Points, 0, false), rest);
                Vector3 last = rig.PalmTip(5, previous);
                for (int i = 1; i <= 30; i++)
                {
                    Vector3[] mixed = new Vector3[a.Points.Length];
                    for (int k = 0; k < mixed.Length; k++) mixed[k] = Vector3.Lerp(a.Points[k], b.Points[k], i / 30f);
                    previous = rig.Retarget.Solve(5, BasisValvePoses.Target(mixed, 0, false), previous);
                    Vector3 now = rig.PalmTip(5, previous);
                    worstJump = Mathf.Max(worstJump, Vector3.Angle(last, now));
                    last = now;
                }
            }
            report.AppendLine($"[FingerROM] {n} worst single-step thumb jump while blending between Touch poses in 30 steps: {worstJump:F2} deg");
            if (assert) Assert.Less(worstJump, 6f, $"{n} thumb retarget jumps while blending between poses");
        }
        static void ThumbBends(Rig rig, Vector2 solved, out float middle, out float tip)
        {
            rig.PalmTip(5, solved);
            Vector3 j0 = rig.Joints[15].position, j1 = rig.Joints[16].position, j2 = rig.Joints[17].position, t = rig.Joints[17].TransformPoint(rig.TipLocal[5]);
            middle = Vector3.Angle(j1 - j0, j2 - j1);
            tip = Vector3.Angle(j2 - j1, t - j2);
        }
        [Test]
        public void ControllerThumb_MatchesTheTrackedTouchPoses()
        {
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            if (poses == null || poses.Count == 0) Assert.Ignore($"set {BasisValvePoses.Env} to an exported Valve pose json");
            (string label, BasisFingerTipRetarget.ThumbTouch touch)[] map = { ("lifted", BasisFingerTipRetarget.ThumbTouch.Lifted), ("thumbrest", BasisFingerTipRetarget.ThumbTouch.Thumbrest), ("A", BasisFingerTipRetarget.ThumbTouch.Primary), ("B", BasisFingerTipRetarget.ThumbTouch.Secondary), ("stick centre", BasisFingerTipRetarget.ThumbTouch.Stick) };
            StringBuilder report = new StringBuilder("[FingerROM] controller thumb constants vs tracked Touch poses\n");
            foreach ((string label, BasisFingerTipRetarget.ThumbTouch touch) in map)
            {
                BasisValvePoses.Pose pose = poses.Find(p => p.Set == "touch" && p.Label == label);
                Assert.IsNotNull(pose.Points, label);
                foreach (bool left in new[] { false, true })
                {
                    float error = Vector3.Angle(BasisFingerTipRetarget.ControllerThumb(touch, left), BasisValvePoses.Target(pose.Points, 0, left));
                    report.AppendLine($"  {label,-12} {(left ? "L" : "R")} {error,5:F2} deg");
                    Assert.Less(error, 1.5f, $"{label} {(left ? "left" : "right")} controller thumb direction");
                }
            }
            Debug.Log(report.ToString());
        }
        [TestCaseSource(nameof(AnatomicalRigs))]
        public void ControllerThumb_ReachesEveryTouchPose(string rigName)
        {
            using Rig rig = Rig.Build(rigName);
            StringBuilder report = new StringBuilder($"[FingerROM] {rigName} controller thumb on the avatar\n");
            foreach (BasisFingerTipRetarget.ThumbTouch touch in (BasisFingerTipRetarget.ThumbTouch[])Enum.GetValues(typeof(BasisFingerTipRetarget.ThumbTouch)))
            {
                foreach (bool left in new[] { false, true })
                {
                    Vector3 target = BasisFingerTipRetarget.ControllerThumb(touch, left);
                    int finger = left ? 0 : 5;
                    float error = Vector3.Angle(rig.PalmTip(finger, rig.Retarget.Solve(finger, target, new Vector2(0.75f, 0f))), target);
                    report.AppendLine($"  {touch,-10} {(left ? "L" : "R")} {error,5:F2} deg");
                    Assert.Less(error, 3f, $"{rigName} {touch} {(left ? "left" : "right")}");
                }
            }
            Debug.Log(report.ToString());
        }
        static float Closure(Vector3[] points)
        {
            float sum = 0f;
            for (int f = 1; f < 5; f++) sum += Vector3.Angle(BasisValvePoses.Target(points, f, false), Vector3.forward);
            return sum;
        }
        [TestCaseSource(nameof(AnatomicalRigs))]
        public void TipRetarget_FollowsAFullOpenAndClose(string rigName)
        {
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            if (poses == null || poses.Count == 0) Assert.Ignore($"set {BasisValvePoses.Env} to an exported Valve pose json");
            using Rig rig = Rig.Build(rigName);
            List<BasisValvePoses.Pose> generic = poses.FindAll(p => p.Set == "generic");
            generic.Sort((x, y) => Closure(x.Points).CompareTo(Closure(y.Points)));
            Vector3[] open = generic[0].Points, fist = generic[generic.Count - 1].Points;
            StringBuilder report = new StringBuilder($"[FingerROM] {rigName} open (frame {generic[0].Frame}) -> fist (frame {generic[generic.Count - 1].Frame}) -> open in 30 steps each, carried solve vs fresh solve\n");
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                Vector2[] carried = BasisFingerTipRetarget.NewState();
                float worst = 0f;
                Vector2[] atFist = new Vector2[5];
                foreach ((Vector3[] from, Vector3[] to) in new[] { (open, fist), (fist, open) })
                {
                    for (int i = 0; i <= 30; i++)
                    {
                        Vector3[] mixed = new Vector3[from.Length];
                        for (int k = 0; k < mixed.Length; k++) mixed[k] = Vector3.Lerp(from[k], to[k], i / 30f);
                        for (int f = 0; f < 5; f++)
                        {
                            int finger = (left ? 0 : 5) + f;
                            Vector3 target = BasisValvePoses.Target(mixed, f, left);
                            carried[f] = rig.Retarget.Solve(finger, target, carried[f]);
                            Vector2 fresh = rig.Retarget.Solve(finger, target, new Vector2(0.75f, 0f));
                            worst = Mathf.Max(worst, Vector3.Angle(rig.PalmTip(finger, carried[f]), target) - Vector3.Angle(rig.PalmTip(finger, fresh), target));
                        }
                    }
                    if (to == fist) Array.Copy(carried, atFist, 5);
                }
                report.AppendLine($"  {(left ? "L" : "R")} worst extra error from carrying the solve {worst:F2} deg; curl at fist: thumb {atFist[0].x:F2} index {atFist[1].x:F2} middle {atFist[2].x:F2} ring {atFist[3].x:F2} little {atFist[4].x:F2}");
                Assert.Less(worst, 2f, $"{rigName} {(left ? "left" : "right")} carried solve got stuck");
                for (int f = 1; f < 5; f++) Assert.Less(atFist[f].x, -0.5f, $"{rigName} {(left ? "left" : "right")} {FingerNames[f]} does not close at the fist");
            }
            Debug.Log(report.ToString());
        }
        [TestCaseSource(nameof(Rigs))]
        public void GridCells_AreFiniteUnitQuaternions(string rigName)
        {
            using Rig rig = Rig.Build(rigName);
            for (int i = 0; i < rig.Grid.Cells.Length; i++)
            {
                float4 q = rig.Grid.Cells[i].value;
                Assert.IsTrue(math.all(math.isfinite(q)), $"cell {i} not finite");
                Assert.AreEqual(1f, math.length(q), 1e-3f, $"cell {i} not unit");
            }
        }
        [TestCaseSource(nameof(Rigs))]
        public void SummaryMapping_RangeOfMotion(string rigName)
        {
            using Rig rig = Rig.Build(rigName);
            StringBuilder report = new StringBuilder();
            SummaryChecks(rig, report, true);
            Debug.Log(report.ToString());
        }
        [TestCaseSource(nameof(Rigs))]
        public void TipRetarget_TableMatchesTheRealBones(string rigName)
        {
            using Rig rig = Rig.Build(rigName);
            float worst = 0f;
            for (int finger = 0; finger < 10; finger++)
                for (float c = -1f; c <= 1.0001f; c += 0.25f)
                    for (float s = -1f; s <= 1.0001f; s += 0.25f)
                    {
                        Vector3 real = rig.PalmTip(finger, new Vector2(c, s));
                        worst = Mathf.Max(worst, Vector3.Angle(real, rig.PalmTip(finger, rig.Retarget.Solve(finger, real, new Vector2(c, s)))));
                    }
            Debug.Log($"[FingerROM] {rigName} retarget round trip worst error {worst:F2} deg");
            Assert.Less(worst, 1.5f, "retarget cannot reproduce poses the avatar itself can make");
        }
        [TestCaseSource(nameof(AnatomicalRigs))]
        public void TipRetarget_ReplaysValvePoses(string rigName)
        {
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            if (poses == null || poses.Count == 0) Assert.Ignore($"set {BasisValvePoses.Env} to an exported Valve pose json");
            using Rig rig = Rig.Build(rigName);
            StringBuilder report = new StringBuilder();
            ValveChecks(rig, poses, report, true);
            Debug.Log(report.ToString());
        }
        [Test]
        public void TipRetarget_SolveCost()
        {
            using Rig rig = Rig.Build(Rig.LankyPath);
            const int frames = 2000;
            Vector3[] targets = new Vector3[frames * 10];
            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)frames * Mathf.PI * 8f;
                for (int f = 0; f < 10; f++) targets[i * 10 + f] = rig.PalmTip(f, new Vector2(Mathf.Sin(t + f) * 0.9f, Mathf.Cos(t * 0.7f + f) * 0.9f));
            }
            Vector2[] previous = new Vector2[10];
            for (int f = 0; f < 10; f++) previous[f] = new Vector2(0.75f, 0f);
            for (int f = 0; f < 10; f++) previous[f] = rig.Retarget.Solve(f, targets[f], previous[f]);
            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.Retarget.FullScans = 0;
            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
                for (int f = 0; f < 10; f++) previous[f] = rig.Retarget.Solve(f, targets[i * 10 + f], previous[f]);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double perFrameUs = watch.Elapsed.TotalMilliseconds * 1000.0 / frames;
            int scans = rig.Retarget.FullScans;
            for (int f = 0; f < 10; f++) previous[f] = new Vector2(0.75f, 0f);
            rig.Retarget.FullScans = 0;
            Stopwatch cold = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
                for (int f = 0; f < 10; f++) rig.Retarget.Solve(f, targets[(i * 37 % frames) * 10 + f], new Vector2(0.75f, 0f));
            cold.Stop();
            Stopwatch build = Stopwatch.StartNew();
            rig.Retarget.Build(rig.Animator, rig.Grid);
            build.Stop();
            Debug.Log($"[FingerROM] solve cost: tracked motion {perFrameUs:F1} us per frame for both hands (10 fingers), full scans {scans} of {frames * 10}, cold solve {cold.Elapsed.TotalMilliseconds * 1000.0 / 200:F1} us per frame, allocated {allocated} bytes, table build {build.Elapsed.TotalMilliseconds:F1} ms");
            Assert.AreEqual(0, allocated, "solve allocates");
            Assert.Less(perFrameUs, 100.0, "solve is too slow for every frame");
        }
        static IEnumerator Await(Task task, float seconds)
        {
            double deadline = EditorApplication.timeSinceStartup + seconds;
            while (!task.IsCompleted)
            {
                while (BasisDeviceManagement.mainThreadActions.TryDequeue(out Action action)) action();
                if (EditorApplication.timeSinceStartup > deadline) yield break;
                yield return null;
            }
        }
        [UnityTest, Timeout(3600000)]
        public IEnumerator LibraryAvatars_RangeOfMotion()
        {
            if (Environment.GetEnvironmentVariable(LibraryEnv) != "1") Assert.Ignore($"set {LibraryEnv}=1 to load the local avatar library");
            LogAssert.ignoreFailingMessages = true;
            typeof(BasisIOManagement).GetMethod("CacheUnityMainThreadValues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            string store = Path.Combine(BasisIOManagement.PersistentDataPath, "ItemKeyStore.json");
            Assert.IsTrue(File.Exists(store), store);
            List<BasisValvePoses.Pose> poses = BasisValvePoses.Load();
            List<(string url, string pass)> avatars = new List<(string, string)>();
            foreach (Match m in Regex.Matches(File.ReadAllText(store), "\\{\"Mode\":0,[^{}]*?\"Url\":\"((?:[^\"\\\\]|\\\\.)*)\",\"Pass\":\"((?:[^\"\\\\]|\\\\.)*)\"")) avatars.Add((Regex.Unescape(m.Groups[1].Value), Regex.Unescape(m.Groups[2].Value)));
            Debug.Log($"[FingerROM] library avatars found: {avatars.Count}");
            int tested = 0;
            List<string> skipped = new List<string>();
            foreach ((string url, string pass) in avatars)
            {
                string shortName = Uri.UnescapeDataString(url.Length > 60 ? url.Substring(url.Length - 60) : url);
                BasisDataStoreItemKeys.ItemKey item = new BasisDataStoreItemKeys.ItemKey { Mode = BundledContentHolder.Mode.Avatar, Url = url, Pass = pass };
                LibraryProvider.BasisLoadableBundleWrapper wrapper = LibraryProvider.CreateNewWrapperFromItem(item);
                Task<LibraryProvider.BasisLoadableBundleWrapper> fromDisc = LibraryProvider.LoadWrapperFromDisc(item, wrapper);
                yield return Await(fromDisc, 60f);
                if (!fromDisc.IsCompleted || fromDisc.IsFaulted || fromDisc.Result == null)
                {
                    skipped.Add($"{shortName}: not cached");
                    continue;
                }
                GameObject holder = new GameObject("ZZFingerRomHolder");
                holder.SetActive(false);
                Task<GameObject> load = BasisLoadHandler.LoadGameObjectBundle(holder, fromDisc.Result.BasisLoadableBundle, false, new BasisProgressReport(), CancellationToken.None, Vector3.zero, Quaternion.identity, Vector3.one, false, BundledContentHolder.Selector.Avatar, holder.transform);
                yield return Await(load, 120f);
                try
                {
                    if (!load.IsCompleted || load.IsFaulted || load.Result == null)
                    {
                        skipped.Add($"{shortName}: load failed {(load.IsFaulted ? load.Exception.GetBaseException().Message : "")}");
                        continue;
                    }
                    using Rig rig = Rig.TryFromAnimator(shortName, load.Result.GetComponentInChildren<Animator>(true), out string reason);
                    if (rig == null)
                    {
                        skipped.Add($"{shortName}: {reason}");
                        continue;
                    }
                    StringBuilder report = new StringBuilder($"[FingerROM] ===== library avatar {shortName} =====\n");
                    SummaryChecks(rig, report, false);
                    if (poses != null) ValveChecks(rig, poses, report, false);
                    tested++;
                    Debug.Log(report.ToString());
                }
                finally
                {
                    if (load.IsCompleted && !load.IsFaulted && load.Result != null) UnityEngine.Object.DestroyImmediate(load.Result);
                    UnityEngine.Object.DestroyImmediate(holder);
                }
                Task release = BasisLoadHandler.RequestDeIncrementOfBundle(fromDisc.Result.BasisLoadableBundle);
                yield return Await(release, 30f);
            }
            Debug.Log($"[FingerROM] library avatars tested {tested}, skipped {skipped.Count}\n  " + string.Join("\n  ", skipped));
            Assert.Greater(tested, 0, "no library avatar could be tested");
        }
    }
}
