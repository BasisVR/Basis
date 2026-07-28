using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    public enum BasisIKHintMode { None, Truth }

    // Clip-driven evaluation of the extracted limb solvers against human ground truth.
    // Two instances of a humanoid model: one sampled from the clip (truth + targets), one
    // solved each frame by BasisArmSolveCore/BasisLegSolveCore on its real Transform hierarchy
    // (setting Transform.rotation reproduces the AnimationStream hierarchy coupling). Logs
    // solved-vs-truth per limb per frame. Edit-mode, deterministic; covers arms/legs/shoulders.
    public struct BasisIKClipEvalConfig
    {
        public GameObject Model;
        public List<AnimationClip> Clips;
        public int Fps;
        public BasisIKHintMode HintMode;
        public bool SolveShoulders;

        public static BasisIKClipEvalConfig Default()
        {
            return new BasisIKClipEvalConfig
            {
                Model = null,
                Clips = new List<AnimationClip>(),
                Fps = 30,
                HintMode = BasisIKHintMode.None,
                SolveShoulders = true,
            };
        }
    }

    public struct BasisIKClipEvalSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;
        public int Frames;
        public float MeanElbowErrM;
        public float MeanElbowSwivelErrDeg;
        public float MeanKneeErrM;
        public float MeanKneeSwivelErrDeg;
        public string Error;
    }

    public static class BasisIKClipEval
    {
        public const string DefaultFileName = "BasisIKClipEval.csv";

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        struct Limb
        {
            public Transform Root, Mid, Tip; // solve instance
            public Transform TRoot, TMid, TTip; // truth instance
        }

        public static BasisIKClipEvalSummary Run(BasisIKClipEvalConfig cfg, string path)
        {
            var s = new BasisIKClipEvalSummary { Ok = false, Path = path };
            if (cfg.Model == null) { s.Error = "No model assigned"; return s; }
            if (cfg.Clips == null || cfg.Clips.Count == 0) { s.Error = "No clips assigned"; return s; }

            GameObject truth = null, solve = null;
            try
            {
                truth = Object.Instantiate(cfg.Model);
                solve = Object.Instantiate(cfg.Model);
                truth.name = "IKEval_Truth";
                solve.name = "IKEval_Solve";
                HideRenderers(truth);
                HideRenderers(solve);

                var ta = truth.GetComponentInChildren<Animator>();
                var sa = solve.GetComponentInChildren<Animator>();
                if (ta == null || sa == null || !ta.isHuman || !sa.isHuman)
                {
                    s.Error = "Model has no humanoid Animator";
                    return s;
                }

                Transform[] tracked = TrackedChain(sa);
                Transform[] trackedTruth = TrackedChain(ta);

                var arms = new[]
                {
                    MakeLimb(sa, ta, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
                    MakeLimb(sa, ta, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
                };
                var legs = new[]
                {
                    MakeLimb(sa, ta, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                    MakeLimb(sa, ta, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                };
                Transform tHips = ta.GetBoneTransform(HumanBodyBones.Hips);
                Transform sHips = sa.GetBoneTransform(HumanBodyBones.Hips);
                Transform tChest = ta.GetBoneTransform(HumanBodyBones.UpperChest) ?? ta.GetBoneTransform(HumanBodyBones.Chest);
                Transform[] sShoulders = { sa.GetBoneTransform(HumanBodyBones.LeftShoulder), sa.GetBoneTransform(HumanBodyBones.RightShoulder) };
                Transform sChest = sa.GetBoneTransform(HumanBodyBones.UpperChest) ?? sa.GetBoneTransform(HumanBodyBones.Chest);

                int fps = Mathf.Max(1, cfg.Fps);

                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                int rows = 0, frames = 0;
                double elbowErr = 0, elbowSw = 0, kneeErr = 0, kneeSw = 0;
                int limbCount = 0;

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisIKClipEval " + System.DateTime.UtcNow.ToString("o") +
                                " hint=" + cfg.HintMode + " solveShoulders=" + cfg.SolveShoulders);
                    w.WriteLine("clip,frame,time_s,limb,is_leg," +
                                "truth_mid_x,truth_mid_y,truth_mid_z,solved_mid_x,solved_mid_y,solved_mid_z," +
                                "mid_pos_err,mid_rot_err_deg,swivel_err_deg," +
                                "truth_tip_x,truth_tip_y,truth_tip_z,solved_tip_x,solved_tip_y,solved_tip_z,tip_pos_err," +
                                "reach_ratio");

                    var sb = new StringBuilder(256);

                    foreach (var clip in cfg.Clips)
                    {
                        if (clip == null) continue;
                        int nFrames = Mathf.Max(1, Mathf.RoundToInt(clip.length * fps));
                        // seed solve instance at clip start
                        clip.SampleAnimation(solve, 0f);

                        for (int f = 0; f < nFrames; f++)
                        {
                            float t = f / (float)fps;
                            clip.SampleAnimation(truth, t);

                            AlignRoot(solve.transform, truth.transform);
                            CopyLocal(sHips, tHips);
                            for (int b = 0; b < tracked.Length; b++)
                            {
                                CopyLocalRot(tracked[b], trackedTruth[b]);
                            }

                            if (cfg.SolveShoulders)
                            {
                                SolveShoulderOn(sShoulders[0], sChest, arms[0], true);
                                SolveShoulderOn(sShoulders[1], sChest, arms[1], false);
                            }

                            SolveArmOn(arms[0], cfg.HintMode);
                            SolveArmOn(arms[1], cfg.HintMode);
                            Vector3 hipsRight = sHips != null ? sHips.right : Vector3.right;
                            for (int l = 0; l < legs.Length; l++)
                            {
                                SolveLegOn(legs[l], cfg.HintMode, hipsRight);
                            }

                            string cn = clip.name;
                            for (int a = 0; a < arms.Length; a++)
                            {
                                rows += WriteLimb(w, sb, cn, f, t, a == 0 ? "leftArm" : "rightArm", false, arms[a], ref elbowErr, ref elbowSw);
                            }
                            for (int l = 0; l < legs.Length; l++)
                            {
                                rows += WriteLimb(w, sb, cn, f, t, l == 0 ? "leftLeg" : "rightLeg", true, legs[l], ref kneeErr, ref kneeSw);
                            }
                            limbCount += 2;
                            frames++;
                        }
                    }

                    s.MeanElbowErrM = limbCount > 0 ? (float)(elbowErr / limbCount) : 0f;
                    s.MeanElbowSwivelErrDeg = limbCount > 0 ? (float)(elbowSw / limbCount) : 0f;
                    s.MeanKneeErrM = limbCount > 0 ? (float)(kneeErr / limbCount) : 0f;
                    s.MeanKneeSwivelErrDeg = limbCount > 0 ? (float)(kneeSw / limbCount) : 0f;
                }

                s.Ok = true;
                s.Rows = rows;
                s.Frames = frames;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message + "\n" + e.StackTrace;
            }
            finally
            {
                if (truth != null) Object.DestroyImmediate(truth);
                if (solve != null) Object.DestroyImmediate(solve);
            }

            return s;
        }

        static Limb MakeLimb(Animator sa, Animator ta, HumanBodyBones root, HumanBodyBones mid, HumanBodyBones tip)
        {
            return new Limb
            {
                Root = sa.GetBoneTransform(root),
                Mid = sa.GetBoneTransform(mid),
                Tip = sa.GetBoneTransform(tip),
                TRoot = ta.GetBoneTransform(root),
                TMid = ta.GetBoneTransform(mid),
                TTip = ta.GetBoneTransform(tip),
            };
        }

        static Transform[] TrackedChain(Animator a)
        {
            return new[]
            {
                a.GetBoneTransform(HumanBodyBones.Spine),
                a.GetBoneTransform(HumanBodyBones.Chest),
                a.GetBoneTransform(HumanBodyBones.UpperChest),
                a.GetBoneTransform(HumanBodyBones.Neck),
                a.GetBoneTransform(HumanBodyBones.Head),
                a.GetBoneTransform(HumanBodyBones.LeftShoulder),
                a.GetBoneTransform(HumanBodyBones.RightShoulder),
            };
        }

        static void SolveArmOn(Limb limb, BasisIKHintMode hintMode)
        {
            if (limb.Root == null || limb.Mid == null || limb.Tip == null || limb.TTip == null) return;
            bool hasHint = hintMode == BasisIKHintMode.Truth && limb.TMid != null;

            BasisArmSolveInput input = default;
            input.Shoulder = limb.Root.position;
            input.Elbow = limb.Mid.position;
            input.Hand = limb.Tip.position;
            input.RootRotation = limb.Root.rotation;
            input.MidRotation = limb.Mid.rotation;
            input.TargetPosition = limb.TTip.position;
            input.TargetRotation = limb.TTip.rotation;
            input.HintPosition = hasHint ? limb.TMid.position : Vector3.zero;
            input.HintWeight = hasHint;
            input.HintIsTracker = false;
            input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up;
            input.HintMaxStepDeg = float.MaxValue;

            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            ApplyDeltas(limb, r.MidDelta, r.RootDelta, r.HintDelta, r.TipRotation);
        }

        static void SolveLegOn(Limb limb, BasisIKHintMode hintMode, Vector3 bendNormal)
        {
            if (limb.Root == null || limb.Mid == null || limb.Tip == null || limb.TTip == null) return;
            bool hasHint = hintMode == BasisIKHintMode.Truth && limb.TMid != null;

            BasisLegSolveInput input = default;
            input.Root = limb.Root.position;
            input.Mid = limb.Mid.position;
            input.Tip = limb.Tip.position;
            input.RootRotation = limb.Root.rotation;
            input.MidRotation = limb.Mid.rotation;
            input.TargetPosition = limb.TTip.position;
            input.TargetRotation = limb.TTip.rotation;
            input.HintPosition = hasHint ? limb.TMid.position : Vector3.zero;
            input.HintWeight = hasHint ? 1f : 0f;
            input.TargetOffset = Quaternion.identity;
            input.BendNormal = bendNormal;

            BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
            ApplyDeltas(limb, r.MidDelta, r.RootDelta, r.HintDelta, r.TipRotation);
        }

        static void SolveShoulderOn(Transform shoulder, Transform chest, Limb arm, bool isLeft)
        {
            if (shoulder == null || arm.TTip == null) return;
            Quaternion chestRot = chest != null ? chest.rotation : Quaternion.identity;
            BasisShoulderSolveInput input = default;
            input.ShoulderPos = shoulder.position;
            input.HandTargetPos = arm.TTip.position;
            input.ElbowPos = arm.Mid != null ? arm.Mid.position : shoulder.position;
            input.HasElbow = arm.Mid != null;
            input.HasShoulderTracker = false;
            input.ChestRot = chestRot;
            input.TposeChestRot = chestRot;
            input.TposeShoulderRot = shoulder.rotation;
            input.TposeArmDirWorld = chestRot * (isLeft ? Vector3.left : Vector3.right);
            input.TposeArmLength = ArmLength(arm);
            input.ElevationFactor = 0.4f;
            input.ProtractionFactor = 0.3f;
            input.CoupleRatio = 0.8f;
            input.MaxShoulderDeg = 40f;
            input.TrackerFinal = shoulder.rotation;
            input.IsLeft = isLeft;
            BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult r);
            if (r.Apply) shoulder.rotation = r.ShoulderRotation;
        }

        static float ArmLength(Limb arm)
        {
            if (arm.Root == null || arm.Mid == null || arm.Tip == null) return 0.5f;
            return (arm.Mid.position - arm.Root.position).magnitude + (arm.Tip.position - arm.Mid.position).magnitude;
        }

        static void ApplyDeltas(Limb limb, Quaternion midDelta, Quaternion rootDelta, Quaternion hintDelta, Quaternion tipRot)
        {
            limb.Mid.rotation = midDelta * limb.Mid.rotation;
            limb.Root.rotation = rootDelta * limb.Root.rotation;
            limb.Root.rotation = hintDelta * limb.Root.rotation;
            limb.Tip.rotation = tipRot;
        }

        static int WriteLimb(StreamWriter w, StringBuilder sb, string clip, int frame, float t, string name, bool isLeg, Limb limb,
            ref double midErrAccum, ref double swivelAccum)
        {
            if (limb.Mid == null || limb.TMid == null || limb.Root == null || limb.TRoot == null || limb.Tip == null || limb.TTip == null)
            {
                return 0;
            }

            Vector3 truthMid = limb.TMid.position;
            Vector3 solvedMid = limb.Mid.position;
            float midErr = (solvedMid - truthMid).magnitude;
            float midRotErr = Quaternion.Angle(limb.TMid.rotation, limb.Mid.rotation);

            Vector3 truthTip = limb.TTip.position;
            Vector3 solvedTip = limb.Tip.position;
            float tipErr = (solvedTip - truthTip).magnitude;

            float swivelErr = SwivelError(limb.TRoot.position, truthTip, truthMid, solvedMid);
            float reach = ReachRatio(limb);

            midErrAccum += midErr;
            swivelAccum += float.IsNaN(swivelErr) ? 0.0 : Mathf.Abs(swivelErr);

            sb.Clear();
            sb.Append(clip).Append(',').Append(frame).Append(',').Append(F(t)).Append(',').Append(name).Append(',');
            sb.Append(isLeg ? '1' : '0').Append(',');
            Append(sb, truthMid.x); Append(sb, truthMid.y); Append(sb, truthMid.z);
            Append(sb, solvedMid.x); Append(sb, solvedMid.y); Append(sb, solvedMid.z);
            Append(sb, midErr); Append(sb, midRotErr); Append(sb, swivelErr);
            Append(sb, truthTip.x); Append(sb, truthTip.y); Append(sb, truthTip.z);
            Append(sb, solvedTip.x); Append(sb, solvedTip.y); Append(sb, solvedTip.z);
            Append(sb, tipErr);
            sb.Append(F(reach));
            w.WriteLine(sb.ToString());
            return 1;
        }

        static float ReachRatio(Limb limb)
        {
            float len = (limb.Mid.position - limb.Root.position).magnitude + (limb.Tip.position - limb.Mid.position).magnitude;
            if (len < 1e-5f) return 0f;
            return (limb.TTip.position - limb.Root.position).magnitude / len;
        }

        // Signed angle between the truth elbow/knee pole and the solved pole around the root->tip axis.
        static float SwivelError(Vector3 root, Vector3 tip, Vector3 truthMid, Vector3 solvedMid)
        {
            Vector3 axis = tip - root;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 pt = Vector3.ProjectOnPlane(truthMid - root, axis);
            Vector3 ps = Vector3.ProjectOnPlane(solvedMid - root, axis);
            if (pt.sqrMagnitude < 1e-8f || ps.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.SignedAngle(pt, ps, axis);
        }

        static void AlignRoot(Transform solveRoot, Transform truthRoot)
        {
            solveRoot.position = truthRoot.position;
            solveRoot.rotation = truthRoot.rotation;
        }

        static void CopyLocal(Transform dst, Transform src)
        {
            if (dst == null || src == null) return;
            dst.localPosition = src.localPosition;
            dst.localRotation = src.localRotation;
        }

        static void CopyLocalRot(Transform dst, Transform src)
        {
            if (dst == null || src == null) return;
            dst.localRotation = src.localRotation;
        }

        static void HideRenderers(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
