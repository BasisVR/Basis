using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;
    public sealed class BasisSpineCorpusAccuracyTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.eeriemovement.tests/Tests/MocapCorpus~");
        // Bend/squat clips (the spine actually works) + a posture spread (ordinary standing/gesturing).
        static readonly (string dir, string name)[] k_Clips =
        {
            ("", "26_09"), ("", "143_11"), ("", "69_70"), ("", "143_18"),
            ("posture", "13_04"), ("posture", "14_20"), ("posture", "26_10"),
            ("posture", "56_07"), ("posture", "77_06"), ("posture", "82_05"),
        };
        static readonly BasisMocapJoint[] measured =
        {
            BasisMocapJoint.Spine, BasisMocapJoint.Chest, BasisMocapJoint.UpperChest, BasisMocapJoint.Neck,
        };
        static readonly BasisMocapJoint[] chainJoints =
        {
            BasisMocapJoint.Hips, BasisMocapJoint.Spine, BasisMocapJoint.Chest,
            BasisMocapJoint.UpperChest, BasisMocapJoint.Neck, BasisMocapJoint.Head,
        };
        struct SpineConfig
        {
            public string Name;
            public bool PreBend;
            public float Relax, TwistKeep, NeckTwistKeep, ConeDeg, Tolerance, PitchScale;
            public int Iters;
            public static SpineConfig Shipped(string name) => new SpineConfig
            {
                Name = name, PreBend = true, Relax = 1.0f, TwistKeep = 0.25f, NeckTwistKeep = 0.9f,
                ConeDeg = 45f, Tolerance = 0.001f, PitchScale = 1f, Iters = 20,
            };
        }
        static List<BasisMotionClip> LoadClips()
        {
            if (!Directory.Exists(CorpusDir)) Assert.Ignore($"no mocap corpus at {CorpusDir}");
            var clips = new List<BasisMotionClip>();
            foreach ((string dir, string name) in k_Clips)
            {
                string path = Path.Combine(CorpusDir, dir, name + ".bvh");
                if (File.Exists(path) && BasisBvhLoader.TryLoad(path, out BasisMotionClip c, out _) && chainJoints.All(c.Has))
                {
                    clips.Add(c);
                }
            }
            if (clips.Count == 0) Assert.Ignore("none of the accuracy clips are present in the corpus");
            return clips;
        }
        static int MostUprightFrame(BasisMotionClip c)
        {
            int best = 0;
            float bestY = float.MinValue;
            for (int f = 0; f < c.FrameCount; f++)
            {
                float y = c.Get(f, BasisMocapJoint.Head).Position.y;
                if (y > bestY) { bestY = y; best = f; }
            }
            return best;
        }
        static Vector3 PelvisForward(BasisMotionClip c, int f, Vector3 fallback)
        {
            Vector3 right = c.Get(f, BasisMocapJoint.RightUpperLeg).Position - c.Get(f, BasisMocapJoint.LeftUpperLeg).Position;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-8f) return fallback;
            return Vector3.Cross(right.normalized, Vector3.up);
        }
        // Gaze frame from pure geometry — up along the live neck→head axis, forward from the pelvis
        // facing — so nothing depends on the BVH's joint bind conventions.
        static Quaternion GazeFrame(Vector3 neckPos, Vector3 headPos, Vector3 pelvisFwd)
        {
            Vector3 up = headPos - neckPos;
            if (up.sqrMagnitude < 1e-10f) return Quaternion.identity;
            up.Normalize();
            Vector3 rightAxis = Vector3.Cross(up, pelvisFwd);
            if (rightAxis.sqrMagnitude < 1e-8f) return Quaternion.identity;
            Vector3 fwd = Vector3.Cross(rightAxis.normalized, up);
            return Quaternion.LookRotation(fwd, up);
        }
        sealed class Rig : System.IDisposable
        {
            public GameObject Root;
            public Transform[] Bones;
            public BasisPoseSkeleton Skeleton;
            public NativeArray<BasisBoneHandle> Chain;
            public BasisEerieMovement Job;
            public int[] MeasuredStreamIndex;   // parallel to measured
            public Quaternion RestGaze;
            public void Dispose()
            {
                if (Chain.IsCreated) Chain.Dispose();
                Skeleton?.Dispose();
                if (Root != null) Object.DestroyImmediate(Root);
            }
        }
        static Rig BuildRig(BasisMotionClip clip, int restFrame)
        {
            var rig = new Rig { Root = new GameObject($"CorpusRig_{clip.Name}") };
            rig.Bones = new Transform[chainJoints.Length];
            Transform parent = rig.Root.transform;
            Vector3 prev = clip.Get(restFrame, BasisMocapJoint.Hips).Position;
            for (int i = 0; i < chainJoints.Length; i++)
            {
                var go = new GameObject(chainJoints[i].ToString());
                go.transform.SetParent(parent, false);
                Vector3 world = clip.Get(restFrame, chainJoints[i]).Position;
                go.transform.localPosition = i == 0 ? world : world - prev;
                prev = world;
                rig.Bones[i] = go.transform;
                parent = go.transform;
            }

            rig.Skeleton = new BasisPoseSkeleton();
            rig.Skeleton.Build(rig.Bones[0], rig.Bones);
            rig.Skeleton.GatherNow();

            rig.Chain = new NativeArray<BasisBoneHandle>(6, Allocator.Persistent);
            for (int i = 0; i < 6; i++) rig.Chain[i] = rig.Skeleton.Bind(rig.Bones[5 - i]);

            rig.MeasuredStreamIndex = new int[measured.Length];
            for (int i = 0; i < measured.Length; i++)
            {
                int boneIdx = System.Array.IndexOf(chainJoints, measured[i]);
                rig.MeasuredStreamIndex[i] = rig.Skeleton.Bind(rig.Bones[boneIdx]).Index;
            }

            Vector3 restNeck = clip.Get(restFrame, BasisMocapJoint.Neck).Position;
            Vector3 restHead = clip.Get(restFrame, BasisMocapJoint.Head).Position;
            Vector3 restFwd = PelvisForward(clip, restFrame, Vector3.forward);
            rig.RestGaze = GazeFrame(restNeck, restHead, restFwd);

            rig.Job = new BasisEerieMovement
            {
                chainHeadToSpine = rig.Chain,
                chainChestIdx = 3,
                handleHips = rig.Skeleton.Bind(rig.Bones[0]),
                handleSpine = rig.Skeleton.Bind(rig.Bones[1]),
                handleChest = rig.Skeleton.Bind(rig.Bones[2]),
                handleUpperChest = rig.Skeleton.Bind(rig.Bones[3]),
                handleNeck = rig.Skeleton.Bind(rig.Bones[4]),
                handleHead = rig.Skeleton.Bind(rig.Bones[5]),
                offsetRotationHead = Quaternion.identity,
                offsetRotationHips = Quaternion.identity,
                playerUp = Vector3.up,
                chestIkTarget = false,
                spineAnatomicalRom = false,
                // Rest anatomy for the pre-bend's neck cue and rest length.
                tposeHeadToNeckLocal = Quaternion.Inverse(rig.RestGaze) * (clip.Get(restFrame, BasisMocapJoint.Neck).Position - clip.Get(restFrame, BasisMocapJoint.Head).Position),
                tposeLengthNeckToHips = clip.Get(restFrame, BasisMocapJoint.Neck).Position - clip.Get(restFrame, BasisMocapJoint.Hips).Position,
                // Production bend weights / limits (BasisFullIKConstraintJob.SetDefaults).
                spineBendPitch = 0.45f, spineBendYaw = 0.10f, spineBendRoll = 0.35f,
                upperChestBendPitch = 0.25f, upperChestBendYaw = 0.30f, upperChestBendRoll = 0.20f,
                spineMaxForwardDeg = 60f, spineMaxBackwardDeg = 25f, spineMaxLateralDeg = 25f,
                spineSquishBoost = 0.5f, spineGazeFollow = 0.25f,
                thoracicBendStiffen = 0.3f, spineTautBandFrac = 0.015f, bendTwistCoupling = 0.15f,
                chestIkWeight = 0.5f, chestIkIterations = 8, chestIkHeadRestoreSweeps = 2,
                chestPosPullMaxDeg = 20f, chestPullMaxDist = 0.5f, chestFollowChestShare = 0.6f,
                trunkCounterbalanceMaxSpineFrac = 0.45f, neckGazeFollowMaxDeg = 18f,
            };
            BasisEeriePlanner.Bind(ref rig.Job);
            BasisEeriePlanner.Frame(ref rig.Job, default);
            return rig;
        }
        static void ApplyConfig(ref BasisEerieMovement job, in SpineConfig cfg)
        {
            job.spineCCDRelax = cfg.Relax;
            job.spineTwistKeep = cfg.TwistKeep;
            job.spineNeckTwistKeep = cfg.NeckTwistKeep;
            job.neckMaxConeDeg = cfg.ConeDeg;
            job.spineTolerance = cfg.Tolerance;
            job.spineMaxIterations = cfg.Iters;
            job.spineBendPitch = 0.45f * cfg.PitchScale;
            job.upperChestBendPitch = 0.25f * cfg.PitchScale;
        }
        // Runs one configuration over one clip; appends each measured joint's error (metres).
        static void RunClip(Rig rig, BasisMotionClip clip, in SpineConfig cfg, bool solve, List<float> errors)
        {
            ApplyConfig(ref rig.Job, cfg);
            int stride = Mathf.Max(1, clip.FrameCount / 120);
            Vector3 fallbackFwd = Vector3.forward;
            int restFrame = MostUprightFrame(clip);
            Vector3 restFwd = PelvisForward(clip, restFrame, Vector3.forward);
            Quaternion restYaw = Quaternion.LookRotation(new Vector3(restFwd.x, 0f, restFwd.z).normalized, Vector3.up);

            for (int f = 0; f < clip.FrameCount; f += stride)
            {
                Vector3 hipsPos = clip.Get(f, BasisMocapJoint.Hips).Position;
                Vector3 neckPos = clip.Get(f, BasisMocapJoint.Neck).Position;
                Vector3 headPos = clip.Get(f, BasisMocapJoint.Head).Position, fwd = PelvisForward(clip, f, fallbackFwd);
                fallbackFwd = fwd;

                Vector3 fwdFlat = new Vector3(fwd.x, 0f, fwd.z);
                Quaternion liveYaw = fwdFlat.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(fwdFlat.normalized, Vector3.up) : restYaw;
                Quaternion deltaYaw = liveYaw * Quaternion.Inverse(restYaw);

                rig.Skeleton.GatherNow();
                var hips = rig.Skeleton.Bind(rig.Bones[0]);
                rig.Skeleton.Stream.SetPosition(hips, hipsPos);
                rig.Skeleton.Stream.SetRotation(hips, deltaYaw);

                if (solve)
                {
                    Quaternion gaze = GazeFrame(neckPos, headPos, fwd);
                    rig.Job.targetRotationHead = gaze;
                    rig.Job.targetPositionHead = headPos;
                    if (cfg.PreBend)
                    {
                        rig.Job.poseStream = rig.Skeleton.Stream;
                        rig.Job.DistributeSpineBend(headPos);
                    }
                    rig.Job.poseStream = rig.Skeleton.Stream;
                    rig.Job.SolveSequentialSpineIK(headPos, gaze);
                }

                for (int j = 0; j < measured.Length; j++)
                {
                    Vector3 solved = rig.Skeleton.Stream.GetWorldPosition(rig.MeasuredStreamIndex[j]);
                    errors.Add((solved - clip.Get(f, measured[j]).Position).magnitude);
                }
            }
        }
        static (float mean, float p95) Stats(List<float> e)
        {
            if (e.Count == 0) return (0f, 0f);
            var s = new List<float>(e);
            s.Sort();
            return (e.Average(), s[(int)(s.Count * 0.95f)]);
        }
        [Test]
        public void TheSolvedSpine_TracksARealHumans_AndTheTrialTableSaysWhatWouldHelp()
        {
            List<BasisMotionClip> clips = LoadClips();
            var report = new StringBuilder();
            report.AppendLine($"SPINE-VS-HUMAN ACCURACY ({clips.Count} clips, mean/p95 cm over Spine+Chest+UpperChest+Neck)");
            report.AppendLine();

            // ---------------- per-clip table: rigid vs CCD-only vs shipped (preBend+CCD) ----------------
            var pooledRigid = new List<float>();
            var pooledCcd = new List<float>();
            var pooledShipped = new List<float>();
            SpineConfig shipped = SpineConfig.Shipped("shipped preBend+CCD"), ccdOnly = SpineConfig.Shipped("CCD only");
            ccdOnly.PreBend = false;

            report.AppendLine($"{"clip",-10} | {"rigid",-13} | {"CCD only",-13} | shipped preBend+CCD");
            foreach (BasisMotionClip clip in clips)
            {
                int restFrame = MostUprightFrame(clip);
                using Rig rig = BuildRig(clip, restFrame);

                var rigid = new List<float>(); var ccd = new List<float>(); var ship = new List<float>();
                RunClip(rig, clip, shipped, solve: false, rigid);
                RunClip(rig, clip, ccdOnly, solve: true, ccd);
                RunClip(rig, clip, shipped, solve: true, ship);
                pooledRigid.AddRange(rigid); pooledCcd.AddRange(ccd); pooledShipped.AddRange(ship);

                (float rm, float rp) = Stats(rigid);
                (float cm, float cp) = Stats(ccd);
                (float sm, float sp) = Stats(ship);
                report.AppendLine($"{clip.Name,-10} | {rm * 100f,5:F2} {rp * 100f,5:F2} | {cm * 100f,5:F2} {cp * 100f,5:F2} | {sm * 100f,5:F2} {sp * 100f,5:F2}");
            }
            (float prm, float prp) = Stats(pooledRigid);
            (float pcm, float pcp) = Stats(pooledCcd);
            (float psm, float psp) = Stats(pooledShipped);
            report.AppendLine($"{"POOLED",-10} | {prm * 100f,5:F2} {prp * 100f,5:F2} | {pcm * 100f,5:F2} {pcp * 100f,5:F2} | {psm * 100f,5:F2} {psp * 100f,5:F2}");
            report.AppendLine();

            // ------------------------------- the trial: variants, pooled --------------------------------
            var variants = new List<SpineConfig>();
            SpineConfig V(string name) { var c = SpineConfig.Shipped(name); return c; }
            var v1 = V("relax 0.6"); v1.Relax = 0.6f; variants.Add(v1);
            var v2 = V("relax 0.8 (old)"); v2.Relax = 0.8f; variants.Add(v2);
            var v3 = V("twistKeep 0.5"); v3.TwistKeep = 0.5f; variants.Add(v3);
            var v4 = V("neckCone 30"); v4.ConeDeg = 30f; variants.Add(v4);
            var v5 = V("neckCone 60"); v5.ConeDeg = 60f; variants.Add(v5);
            var v6 = V("iters 40"); v6.Iters = 40; variants.Add(v6);
            var v7 = V("tol 0.5mm"); v7.Tolerance = 0.0005f; variants.Add(v7);
            var v8 = V("bendPitch x1.5"); v8.PitchScale = 1.5f; variants.Add(v8);
            var v9 = V("bendPitch x0.5"); v9.PitchScale = 0.5f; variants.Add(v9);

            report.AppendLine($"TRIAL (pooled, vs shipped {psm * 100f:F2} mean / {psp * 100f:F2} p95 cm):");
            foreach (SpineConfig cfg in variants)
            {
                var e = new List<float>();
                foreach (BasisMotionClip clip in clips)
                {
                    using Rig rig = BuildRig(clip, MostUprightFrame(clip));
                    RunClip(rig, clip, cfg, solve: true, e);
                }
                (float m, float p) = Stats(e);
                report.AppendLine($"  {cfg.Name,-16} {m * 100f,5:F2} {p * 100f,5:F2}  ({(m - psm) * 100f:+0.00;-0.00} mean)");
            }

            TestContext.WriteLine(report.ToString());

            Assert.Less(psm, prm, "the shipped spine solve must beat doing nothing at all");
            Assert.Less(psm, 0.12f, "pooled mean spine error must stay inside a sane absolute bound");
            foreach (float e in pooledShipped) Assert.IsFalse(float.IsNaN(e) || float.IsInfinity(e));
        }
    }
}
