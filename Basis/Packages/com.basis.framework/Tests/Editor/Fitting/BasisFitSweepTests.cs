using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public class BasisFitSweepTests
    {
        static readonly BasisFitPose[] Poses = { BasisFitPose.TPose, BasisFitPose.Idle, BasisFitPose.ReachForward, BasisFitPose.ReachUp, BasisFitPose.LookDown, BasisFitPose.LookAtFeet, BasisFitPose.Crouch, BasisFitPose.LookUp, BasisFitPose.HeadRoll, BasisFitPose.HeadYaw, BasisFitPose.Rise, BasisFitPose.Bow, BasisFitPose.StepUp };
        static readonly BasisSelectedHeightMode[] Modes = { BasisSelectedHeightMode.EyeHeight, BasisSelectedHeightMode.BestFit, BasisSelectedHeightMode.ArmSpan };
        static readonly BasisFitTrackers[] TrackerSets =
        {
            BasisFitTrackers.None,
            BasisFitTrackers.Hips,
            BasisFitTrackers.Hips | BasisFitTrackers.Feet,
            BasisFitTrackers.Hips | BasisFitTrackers.Feet | BasisFitTrackers.Knees,
            BasisFitTrackers.Hips | BasisFitTrackers.Feet | BasisFitTrackers.Knees | BasisFitTrackers.Elbows,
            BasisFitTrackers.Hips | BasisFitTrackers.Feet | BasisFitTrackers.Knees | BasisFitTrackers.Elbows | BasisFitTrackers.Chest,
        };
        struct Row
        {
            public BasisFitScenario Scenario;
            public BasisFitSetupResult Setup;
            public BasisFitPose Pose;
            public BasisFitMetrics Metrics;
        }
        const string Header = "pipeline,mode,trackers,player,avatar,k,lookdown_calib,pose,u,eye_ratio,device_scale,grounding,arm_scale,leg_scale,torso_scale,arm_residual,shoulder_mismatch,status,head_err,eye_err,neck_stretch,visible_neck_stretch,hand_err_l,hand_err_r,elbow_diff_deg,foot_gap_l,foot_gap_r,foot_err_l,foot_err_r,hips_err,shoulder_dy,hips_dy,posture_err,knee_bend";
        static string Csv(in Row r)
        {
            var c = CultureInfo.InvariantCulture;
            BasisFitScenario s = r.Scenario;
            BasisFitSetupResult u = r.Setup;
            BasisFitMetrics m = r.Metrics;
            return string.Join(",", s.Pipeline, s.Mode, BasisFitScenario.TrackerLabel(s.Trackers), s.Player.Name, s.Avatar.Name, s.AvatarScale.ToString("0.###", c), s.CalibrateLookingDown ? 1 : 0, r.Pose,
                u.UniformScale.ToString("0.####", c), u.EyeRatio.ToString("0.####", c), u.DeviceScale.ToString("0.####", c), u.Grounding.ToString("0.####", c), u.ArmScale.ToString("0.####", c), u.LegScale.ToString("0.####", c), u.TorsoScale.ToString("0.####", c), u.ArmResidual.ToString("0.####", c), u.ShoulderMismatch.ToString("0.####", c), u.Status,
                m.HeadError.ToString("0.#####", c), m.EyeError.ToString("0.#####", c), m.NeckStretch.ToString("0.#####", c), m.VisibleNeckStretch.ToString("0.#####", c), m.HandErrorL.ToString("0.#####", c), m.HandErrorR.ToString("0.#####", c), m.ElbowAngleDiff.ToString("0.##", c),
                m.FootGapL.ToString("0.#####", c), m.FootGapR.ToString("0.#####", c), m.FootErrorL.ToString("0.#####", c), m.FootErrorR.ToString("0.#####", c), m.HipsError.ToString("0.#####", c), m.ShoulderHeightError.ToString("0.#####", c), m.HipsHeightError.ToString("0.#####", c), m.PostureError.ToString("0.#####", c), m.KneeBend.ToString("0.##", c));
        }
        static List<Row> Run(IEnumerable<BasisFitScenario> scenarios)
        {
            var rows = new List<Row>();
            foreach (BasisFitScenario scenario in scenarios)
            {
                using (BasisFitSimulation sim = BasisFitSimulation.Create(scenario))
                {
                    foreach (BasisFitPose pose in Poses)
                    {
                        rows.Add(new Row { Scenario = scenario, Setup = sim.Setup, Pose = pose, Metrics = sim.Solve(pose) });
                    }
                }
            }
            return rows;
        }
        static string ReportDirectory()
        {
            string dir = Environment.GetEnvironmentVariable("BASIS_FIT_REPORT_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Application.temporaryCachePath, "BasisFitReport");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
        static void Report(string name, List<Row> rows)
        {
            var csv = new StringBuilder(Header).Append('\n');
            foreach (Row r in rows)
            {
                csv.Append(Csv(in r)).Append('\n');
            }
            File.WriteAllText(Path.Combine(ReportDirectory(), name + ".csv"), csv.ToString());
            var summary = new StringBuilder($"[BasisFit] {name}: {rows.Count} solves\n");
            foreach (var group in rows.GroupBy(r => (r.Scenario.Pipeline, r.Scenario.Mode)))
            {
                var standing = group.Where(r => r.Pose == BasisFitPose.TPose || r.Pose == BasisFitPose.Idle).ToList();
                summary.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-7} {1,-9} eyeErr max {2,6:0.000} | neck max {3,6:0.000} | visNeck max {4,6:0.000} | tposeHand max {5,6:0.000} | idleElbow mean {6,5:0.0} | footGap max {7,6:0.000} | standHips max {8,6:0.000} | posture max {9,6:0.000} | |u/eye-1| max {10,5:0.000} | |G| max {11,5:0.000}",
                    group.Key.Pipeline, group.Key.Mode, group.Max(r => Rel(r, r.Metrics.EyeError)), group.Max(r => r.Metrics.NeckStretch), standing.Select(r => Math.Abs(r.Metrics.VisibleNeckStretch)).DefaultIfEmpty().Max(),
                    group.Where(r => r.Pose == BasisFitPose.TPose).Select(r => Rel(r, r.Metrics.HandError)).DefaultIfEmpty().Max(), group.Where(r => r.Pose == BasisFitPose.Idle).Select(r => r.Metrics.ElbowAngleDiff).DefaultIfEmpty().Average(),
                    standing.Select(r => Rel(r, Math.Abs(r.Metrics.FootGap))).DefaultIfEmpty().Max(), standing.Select(r => Rel(r, Math.Abs(r.Metrics.HipsHeightError))).DefaultIfEmpty().Max(), standing.Select(r => Rel(r, r.Metrics.PostureError)).DefaultIfEmpty().Max(),
                    group.Max(r => Math.Abs(r.Setup.UniformScale / r.Setup.EyeRatio - 1f)), group.Max(r => Math.Abs(r.Setup.Grounding))));
            }
            Debug.Log(summary.ToString());
            TestContext.WriteLine(summary.ToString());
        }
        static float Size(in Row r) => r.Scenario.Avatar.EyeY * (r.Scenario.AvatarScale > 0f ? r.Scenario.AvatarScale : 1f);
        static float Rel(in Row r, float metres) => metres / Mathf.Max(1e-3f, Size(r));
        static BasisFitScenario Scenario(BasisFitPipeline pipeline, BasisSelectedHeightMode mode, BasisFitTrackers trackers, BasisFitBodySpec player, BasisFitBodySpec avatar, float k = 1f, bool lookingDown = false)
        {
            return new BasisFitScenario { Pipeline = pipeline, Mode = mode, Trackers = trackers, Player = player, Avatar = avatar, AvatarScale = k, FitDeviation = Basis.IK.BasisBodyFitCore.DefaultMaxDeviation, CalibrateLookingDown = lookingDown };
        }
        static void Check(List<Row> rows, List<string> failures, bool feetFromFloor)
        {
            foreach (Row r in rows.Where(r => r.Scenario.Pipeline == BasisFitPipeline.Current))
            {
                string label = r.Scenario.Label + "|" + r.Pose;
                bool standing = r.Pose == BasisFitPose.TPose || r.Pose == BasisFitPose.Idle || r.Pose == BasisFitPose.ReachForward || r.Pose == BasisFitPose.ReachUp;
                if (r.Scenario.Mode == BasisSelectedHeightMode.BestFit && Math.Abs(r.Setup.UniformScale / r.Setup.EyeRatio - 1f) > 0.0505f)
                {
                    failures.Add($"{label}: BestFit moved the scale {r.Setup.UniformScale / r.Setup.EyeRatio - 1f:P1} off the eye match");
                }
                if (r.Metrics.NeckStretch > 0.005f)
                {
                    failures.Add($"{label}: neck stretched {r.Metrics.NeckStretch:P1}");
                }
                if (standing && Rel(r, r.Metrics.EyeError) > 0.02f)
                {
                    failures.Add($"{label}: avatar eye {r.Metrics.EyeError * 100f:0.0} cm off the HMD");
                }
                if (r.Pose == BasisFitPose.TPose && Math.Abs(r.Metrics.VisibleNeckStretch) > 0.03f)
                {
                    failures.Add($"{label}: shoulder-to-head distance changed {r.Metrics.VisibleNeckStretch:P1}");
                }
                if (feetFromFloor && (r.Pose == BasisFitPose.TPose || r.Pose == BasisFitPose.Idle) && Rel(r, Math.Abs(r.Metrics.FootGap)) > 0.025f)
                {
                    failures.Add($"{label}: foot {r.Metrics.FootGap * 100f:0.0} cm off the floor");
                }
                if (feetFromFloor && r.Pose == BasisFitPose.StepUp && (Rel(r, Math.Abs(r.Metrics.FootGapR)) > 0.0025f || Rel(r, r.Metrics.FootErrorL) > 0.01f))
                {
                    failures.Add($"{label}: stepping left the standing foot {r.Metrics.FootGapR * 100f:0.0} cm off the floor, lifted foot {r.Metrics.FootErrorL * 100f:0.0} cm from its target");
                }
                if ((r.Pose == BasisFitPose.Rise || r.Pose == BasisFitPose.StepUp) && Rel(r, r.Metrics.EyeError) > 0.003f)
                {
                    failures.Add($"{label}: eye {r.Metrics.EyeError * 100f:0.0} cm off the camera without the head tilting");
                }
                if ((r.Pose == BasisFitPose.TPose || r.Pose == BasisFitPose.Idle) && r.Metrics.KneeBend > 10f)
                {
                    failures.Add($"{label}: standing knees bent {r.Metrics.KneeBend:0.0} degrees");
                }
                if ((r.Pose == BasisFitPose.TPose || r.Pose == BasisFitPose.Idle) && (Rel(r, Math.Abs(r.Metrics.HipsHeightError)) > 0.01f || Rel(r, r.Metrics.PostureError) > 0.01f))
                {
                    failures.Add($"{label}: standing hips {r.Metrics.HipsHeightError * 100f:0.0} cm from the avatar's own hips, hips-to-head off by {r.Metrics.PostureError * 100f:0.0} cm");
                }
                bool armsInBudget = (Math.Abs(r.Setup.ArmResidual - 1f) <= Basis.IK.BasisBodyFitCore.DefaultMaxDeviation + 1e-3f || r.Scenario.Mode == BasisSelectedHeightMode.ArmSpan) && Rel(r, Math.Abs(r.Setup.ShoulderMismatch)) <= 0.03f;
                if (r.Pose == BasisFitPose.TPose && armsInBudget && Rel(r, r.Metrics.HandError) > 0.015f)
                {
                    failures.Add($"{label}: T-pose hand {r.Metrics.HandError * 100f:0.0} cm from the controller with arms inside the fit budget (residual {r.Setup.ArmResidual:0.###})");
                }
            }
        }
        [Test]
        public void ProportionSweep_HeadNeckArmsAndFeetAcrossBodiesAndAvatars()
        {
            var scenarios = new List<BasisFitScenario>();
            foreach (BasisFitPipeline pipeline in new[] { BasisFitPipeline.Legacy, BasisFitPipeline.Current })
            {
                foreach (BasisSelectedHeightMode mode in Modes)
                {
                    foreach (BasisFitBodySpec player in BasisFitBodies.Players)
                    {
                        foreach (BasisFitBodySpec avatar in BasisFitBodies.Avatars)
                        {
                            scenarios.Add(Scenario(pipeline, mode, BasisFitTrackers.Hips | BasisFitTrackers.Feet, player, avatar));
                        }
                    }
                }
            }
            List<Row> rows = Run(scenarios);
            Report("proportions", rows);
            var failures = new List<string>();
            Check(rows, failures, feetFromFloor: true);
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)) + (failures.Count > 60 ? $"\n... {failures.Count - 60} more" : ""));
        }
        [Test]
        public void TrackerSweep_EveryTrackerCountAcrossAvatars()
        {
            var scenarios = new List<BasisFitScenario>();
            BasisFitBodySpec player = BasisFitBodies.Players[0];
            foreach (BasisFitPipeline pipeline in new[] { BasisFitPipeline.Legacy, BasisFitPipeline.Current })
            {
                foreach (BasisFitTrackers trackers in TrackerSets)
                {
                    foreach (BasisFitBodySpec avatar in BasisFitBodies.Avatars)
                    {
                        scenarios.Add(Scenario(pipeline, BasisSelectedHeightMode.BestFit, trackers, player, avatar));
                    }
                }
            }
            List<Row> rows = Run(scenarios);
            Report("trackers", rows);
            var failures = new List<string>();
            Check(rows, failures, feetFromFloor: true);
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)) + (failures.Count > 60 ? $"\n... {failures.Count - 60} more" : ""));
        }
        [Test]
        public void ScaleSweep_CustomAvatarScales()
        {
            var scenarios = new List<BasisFitScenario>();
            foreach (BasisFitPipeline pipeline in new[] { BasisFitPipeline.Legacy, BasisFitPipeline.Current })
            {
                foreach (BasisSelectedHeightMode mode in Modes)
                {
                    foreach (float k in new[] { 0.35f, 0.6f, 1f, 1.5f, 3f })
                    {
                        scenarios.Add(Scenario(pipeline, mode, BasisFitTrackers.Hips | BasisFitTrackers.Feet, BasisFitBodies.Players[0], BasisFitBodies.Realistic, k));
                        scenarios.Add(Scenario(pipeline, mode, BasisFitTrackers.None, BasisFitBodies.Players[0], BasisFitBodies.Avatars[2], k));
                    }
                }
            }
            List<Row> rows = Run(scenarios);
            Report("scales", rows);
            var failures = new List<string>();
            Check(rows, failures, feetFromFloor: true);
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)));
        }
        [Test]
        public void CalibratingWhileSlouchedNoLongerSinksTrackedFeet()
        {
            var scenarios = new List<BasisFitScenario>();
            foreach (BasisFitPipeline pipeline in new[] { BasisFitPipeline.Legacy, BasisFitPipeline.Current })
            {
                foreach (BasisFitBodySpec avatar in new[] { BasisFitBodies.Realistic, BasisFitBodies.Avatars[2], BasisFitBodies.Avatars[3] })
                {
                    scenarios.Add(Scenario(pipeline, BasisSelectedHeightMode.EyeHeight, BasisFitTrackers.Hips | BasisFitTrackers.Feet | BasisFitTrackers.Knees, BasisFitBodies.Players[0], avatar, lookingDown: true));
                }
            }
            List<Row> rows = Run(scenarios);
            Report("lookdown_calibration", rows);
            foreach (Row r in rows.Where(r => r.Pose == BasisFitPose.Crouch))
            {
                TestContext.WriteLine($"{r.Scenario.Pipeline} {r.Scenario.Avatar.Name} crouch foot gap {r.Metrics.FootGap * 100f:0.0} cm");
            }
            foreach (Row r in rows.Where(r => r.Scenario.Pipeline == BasisFitPipeline.Current && (r.Pose == BasisFitPose.TPose || r.Pose == BasisFitPose.Crouch)))
            {
                Assert.That(Math.Abs(r.Metrics.FootGap), Is.LessThan(0.01f), $"{r.Scenario.Label}|{r.Pose}: tracked feet must stay on the floor after a slouched calibration");
            }
        }
        [Test]
        public void ALowVrFloorFitsLikeALevelOne()
        {
            var scenarios = new List<BasisFitScenario>();
            foreach (BasisSelectedHeightMode mode in new[] { BasisSelectedHeightMode.EyeHeight, BasisSelectedHeightMode.BestFit })
            {
                foreach (BasisFitTrackers trackers in new[] { BasisFitTrackers.None, BasisFitTrackers.Hips })
                {
                    foreach (BasisFitBodySpec player in new[] { BasisFitBodies.Players[0], BasisFitBodies.Players[1], BasisFitBodies.Players[8] })
                    {
                        foreach (BasisFitBodySpec avatar in new[] { BasisFitBodies.Realistic, BasisFitBodies.Avatars[2], BasisFitBodies.Avatars[3], BasisFitBodies.Avatars[14] })
                        {
                            foreach (float floor in new[] { 0f, 0.5f })
                            {
                                BasisFitScenario s = Scenario(BasisFitPipeline.Current, mode, trackers, player, avatar);
                                s.FloorError = floor;
                                scenarios.Add(s);
                            }
                        }
                    }
                }
            }
            List<Row> rows = Run(scenarios);
            Report("low_floor", rows);
            var failures = new List<string>();
            Check(rows.Where(r => r.Scenario.FloorError > 0f).ToList(), failures, feetFromFloor: true);
            foreach (Row low in rows.Where(r => r.Scenario.FloorError > 0f && r.Pose == BasisFitPose.TPose))
            {
                Row level = rows.First(r => r.Scenario.FloorError == 0f && r.Pose == BasisFitPose.TPose && r.Scenario.Mode == low.Scenario.Mode && r.Scenario.Trackers == low.Scenario.Trackers && r.Scenario.Player.Name == low.Scenario.Player.Name && r.Scenario.Avatar.Name == low.Scenario.Avatar.Name);
                BasisFitBodySpec p = low.Scenario.Player;
                float estimateError = Math.Abs(p.EyeY / BasisCalibrationMath.SizingEyeHeight(p.EyeY + low.Scenario.FloorError, p.WristSpan, true, 0f) - 1f);
                float change = low.Setup.DeviceScale / level.Setup.DeviceScale - 1f;
                if (Math.Abs(change) > estimateError + 0.01f || Math.Abs(change) > 0.08f)
                {
                    failures.Add($"{low.Scenario.Label}: a 0.5 m low floor changed the player's size {change:P1} (reach estimate off by {estimateError:P1})");
                }
                TestContext.WriteLine($"{low.Scenario.Label}: size change {change:P1}, T-pose hand {low.Metrics.HandError * 100f:0.0} cm (level {level.Metrics.HandError * 100f:0.0} cm), arm scale {low.Setup.ArmScale:0.###}");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)));
        }
        [Test]
        public void TiltingTheHeadMovesTheBodySmoothly([Values(false, true)] bool desktop)
        {
            var failures = new List<string>();
            foreach (BasisFitBodySpec avatar in new[] { BasisFitBodies.Realistic, BasisFitBodies.Avatars[2], BasisFitBodies.Avatars[3] })
            {
                BasisFitScenario scenario = Scenario(BasisFitPipeline.Current, desktop ? BasisSelectedHeightMode.EyeHeight : BasisSelectedHeightMode.BestFit, BasisFitTrackers.None, BasisFitBodies.Players[0], avatar);
                using (BasisFitSimulation sim = BasisFitSimulation.Create(scenario))
                {
                    float size = avatar.EyeY, previousHips = 0f, previousEye = 0f;
                    for (int step = 0; step <= 60; step++)
                    {
                        float pitch = -40f + step * 2f;
                        BasisFitMetrics m = sim.Solve(desktop ? BasisFitPose.DesktopPitch : BasisFitPose.LookPitch, pitch);
                        string label = $"{avatar.Name} pitch {pitch:0}";
                        if (step > 0 && (Math.Abs(m.HipsHeightError - previousHips) > 0.01f * size || Math.Abs(m.EyeError - previousEye) > 0.01f * size))
                        {
                            failures.Add($"{label}: jumped hips {(m.HipsHeightError - previousHips) * 100f:0.00} cm, eye {(m.EyeError - previousEye) * 100f:0.00} cm in a 2 degree step");
                        }
                        previousHips = m.HipsHeightError;
                        previousEye = m.EyeError;
                    }
                }
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(60)));
        }
    }
}
