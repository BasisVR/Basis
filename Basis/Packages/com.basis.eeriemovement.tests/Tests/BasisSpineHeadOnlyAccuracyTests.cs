using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Basis.IK;
using Basis.IK.Mocap;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;
    public sealed class BasisSpineHeadOnlyAccuracyTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.eeriemovement.tests/Tests/MocapCorpus~");
        static readonly (string dir, string name)[] clipsWanted =
        {
            ("", "26_09"), ("", "143_11"), ("", "69_70"), ("", "143_18"),
            ("posture", "13_04"), ("posture", "14_20"), ("posture", "26_10"),
            ("posture", "56_07"), ("posture", "77_06"), ("posture", "82_05"),
        };
        static readonly BasisMocapJoint[] chainJoints = { BasisMocapJoint.Hips, BasisMocapJoint.Spine, BasisMocapJoint.Chest, BasisMocapJoint.UpperChest, BasisMocapJoint.Neck, BasisMocapJoint.Head };
        static readonly BasisMocapJoint[] measured = { BasisMocapJoint.Spine, BasisMocapJoint.Chest, BasisMocapJoint.UpperChest, BasisMocapJoint.Neck };
        const int vsHead = 0, vsNeck = 1, vsChest = 2, vsSpine = 3, vsHips = 4, vsCount = 5;
        const float movingSpeed = 0.3f;
        struct Rest
        {
            public float3 Hips, Spine, Chest, UpperChest, Neck, Head;
            public Quaternion Gaze;
        }
        struct Law
        {
            public string Name;
            public bool VirtualSpine, AuthoredRestDrop;
        }
        sealed class Rig : System.IDisposable
        {
            public GameObject Root;
            public Transform[] Bones;
            public BasisPoseSkeleton Skeleton;
            public NativeArray<BasisBoneHandle> Chain;
            public BasisEerieMovement Job;
            public int[] MeasuredStreamIndex;
            public Rest Rest;
            public void Dispose()
            {
                if (Chain.IsCreated) Chain.Dispose();
                Skeleton?.Dispose();
                if (Root != null) Object.DestroyImmediate(Root);
            }
        }
        static List<BasisMotionClip> LoadClips()
        {
            if (!Directory.Exists(CorpusDir)) Assert.Ignore($"no mocap corpus at {CorpusDir}");
            var clips = new List<BasisMotionClip>();
            foreach ((string dir, string name) in clipsWanted)
            {
                string path = Path.Combine(CorpusDir, dir, name + ".bvh");
                if (File.Exists(path) && BasisBvhLoader.TryLoad(path, out BasisMotionClip c, out _) && chainJoints.All(c.Has) && c.Has(BasisMocapJoint.LeftUpperLeg) && c.Has(BasisMocapJoint.RightUpperLeg))
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
        static Rig BuildRig(BasisMotionClip clip, int restFrame)
        {
            var rig = new Rig { Root = new GameObject($"HeadOnlyRig_{clip.Name}") };
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
                rig.MeasuredStreamIndex[i] = rig.Skeleton.Bind(rig.Bones[System.Array.IndexOf(chainJoints, measured[i])]).Index;
            }
            Rest rest;
            rest.Hips = clip.Get(restFrame, BasisMocapJoint.Hips).Position;
            rest.Spine = clip.Get(restFrame, BasisMocapJoint.Spine).Position;
            rest.Chest = clip.Get(restFrame, BasisMocapJoint.Chest).Position;
            rest.UpperChest = clip.Get(restFrame, BasisMocapJoint.UpperChest).Position;
            rest.Neck = clip.Get(restFrame, BasisMocapJoint.Neck).Position;
            rest.Head = clip.Get(restFrame, BasisMocapJoint.Head).Position;
            rest.Gaze = GazeFrame(rest.Neck, rest.Head, PelvisForward(clip, restFrame, Vector3.forward));
            rig.Rest = rest;

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
                offsetRotationChest = Quaternion.identity,
                playerUp = Vector3.up,
                ikLockMode = BasisIKLockMode.LockHead,
                minHeadSpineHeight = math.distance(rest.Head, rest.Hips),
                chestIkTarget = true,
                spineAnatomicalRom = false,
                anatCervicalLordosis = false,
                anatDifferentialStiffness = true,
                anatPelvicTwistRouting = true,
                tposeHeadToNeckLocal = Quaternion.Inverse(rest.Gaze) * (Vector3)(rest.Neck - rest.Head),
                tposeLengthNeckToHips = rest.Neck - rest.Hips,
                spineMaxIterations = 20, spineTolerance = 0.001f, spineCCDRelax = 1.0f,
                spineTwistKeep = 0.25f, spineNeckTwistKeep = 0.9f, neckMaxConeDeg = 45f, maxChestDeltaDeg = 90f,
                spineBendPitch = 0.45f, spineBendYaw = 0.10f, spineBendRoll = 0.35f,
                upperChestBendPitch = 0.25f, upperChestBendYaw = 0.30f, upperChestBendRoll = 0.20f,
                spineMaxForwardDeg = 60f, spineMaxBackwardDeg = 25f, spineMaxLateralDeg = 25f,
                spineSquishBoost = 0.5f, spineGazeFollow = 0.25f, neckGazeFollow = 0.3f, neckGazeFollowMaxDeg = 18f,
                neckExtensionDamp = 0.65f, neckFlexionDamp = 0.5f,
                spineTautBandFrac = 0.015f, bendTwistCoupling = 0.15f,
                chestIkWeight = 0.5f, chestIkIterations = 8, chestIkHeadRestoreSweeps = 2, chestPosPullMaxDeg = 20f, chestPullMaxDist = 0.5f,
                hipHingeStartDeg = 40f, hipHingeMaxAddDeg = 52f,
                moveBodyBackWhenCrouching = 1f, trunkCounterbalance = 0.38f, trunkCounterbalanceMaxSpineFrac = 0.45f,
                standingHeadHeight = rest.Head.y,
            };
            BasisEeriePlanner.Bind(ref rig.Job);
            BasisEeriePlanner.Frame(ref rig.Job, default);
            return rig;
        }
        static BasisVirtualSpineCore.SpineSolveParams Params(in Rest rest, float dt, float3 eyePos, quaternion eyeRot, bool authoredDrop, bool moving)
        {
            float neckChest = math.distance(rest.Neck, rest.Chest), chestSpine = math.distance(rest.Chest, rest.Spine);
            float lenTotal = math.max(1e-4f, neckChest + chestSpine + math.distance(rest.Spine, rest.Hips)), restDrop = rest.Neck.y - rest.Hips.y;
            return new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = dt, Scale = 1f, TrackingLiftY = 0f, ParentMatrix = float4x4.identity, ParentRotation = quaternion.identity, EyeRot = eyeRot,
                HeadTargetPos = eyePos, HeadTargetRot = eyeRot, NeckTargetPos = eyePos, NeckTargetRot = eyeRot,
                ChestTargetPos = eyePos, ChestTargetRot = eyeRot, SpineTargetPos = eyePos, SpineTargetRot = eyeRot,
                HeadScaledOffset = float3.zero, NeckScaledOffset = rest.Neck - rest.Head, ChestScaledOffset = rest.Chest - rest.Head, SpineScaledOffset = rest.Spine - rest.Head,
                ChestTposeY = rest.Chest.y, SpineTposeY = rest.Spine.y, TposeHips = rest.Hips,
                ChestPitchFrac = 0.30f, ChestRollFrac = 0.30f, SpinePitchFrac = 0.10f, SpineRollFrac = 0.10f,
                NeckRotationSpeed = 40f, ChestRotationSpeed = 25f, SpineRotationSpeed = 30f, HipsRotationSpeed = 20f,
                GazeSwingLever = float3.zero, TposeNeckMinusEyeY = rest.Neck.y - rest.Head.y, GazeSwingRemoval = 1f, HipsForwardBias = 0f,
                NeckExtensionDamp = 0.65f, NeckFlexionDamp = 0.5f, TorsoYawDeadzoneDeg = 30f, TorsoYawBlendSpeed = 8f,
                HipsFreeze = 0, IsLocomoting = (byte)(moving ? 1 : 0),
                LenTotal = lenTotal, TChest = math.saturate(neckChest / lenTotal), TSpine = math.saturate((neckChest + chestSpine) / lenTotal),
                StandingHipsLocalY = authoredDrop ? rest.Hips.y : rest.Neck.y - lenTotal, StandingHeadLocalY = rest.Head.y,
                EyePos = eyePos, HipsAnchorOffsetLocal = new float3(rest.Hips.x - rest.Head.x, 0f, rest.Hips.z - rest.Head.z),
                HeadRestFromEyeLocal = float3.zero, YawPivotFromEyeLocal = new float3(rest.Neck.x - rest.Head.x, 0f, rest.Neck.z - rest.Head.z),
                PostureModel = 1, HipsCompressionStrength = 0.85f, HipsMaxDropMeters = 0.3f, HipsRestDropY = authoredDrop ? restDrop : 0f,
            };
        }
        static void RunClip(Rig rig, BasisMotionClip clip, in Law law, List<float> pelvisErrors, List<float> spineErrors)
        {
            int stride = Mathf.Max(1, clip.FrameCount / 120);
            float dt = Mathf.Max(1e-3f, clip.FrameTime);
            Vector3 fallbackFwd = Vector3.forward, prevHead = clip.Get(0, BasisMocapJoint.Head).Position;
            var states = new NativeArray<BasisBoneSimState>(vsCount, Allocator.Temp);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Temp);
            try
            {
                for (int i = 0; i < vsCount; i++) states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                solve[0] = default;
                for (int f = 0; f < clip.FrameCount; f++)
                {
                    Vector3 headPos = clip.Get(f, BasisMocapJoint.Head).Position, neckPos = clip.Get(f, BasisMocapJoint.Neck).Position, hipsPos = clip.Get(f, BasisMocapJoint.Hips).Position;
                    Vector3 fwd = PelvisForward(clip, f, fallbackFwd);
                    fallbackFwd = fwd;
                    Quaternion gaze = GazeFrame(neckPos, headPos, fwd);
                    Vector3 headStep = headPos - prevHead;
                    headStep.y = 0f;
                    bool moving = headStep.magnitude / dt > movingSpeed;
                    prevHead = headPos;

                    Vector3 hipsTarget;
                    Quaternion hipsRot;
                    if (law.VirtualSpine)
                    {
                        new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                        {
                            States = states, State = solve, P = Params(rig.Rest, dt, headPos, gaze, law.AuthoredRestDrop, moving),
                            IdxHead = vsHead, IdxNeck = vsNeck, IdxChest = vsChest, IdxSpine = vsSpine, IdxHips = vsHips,
                        }.Execute();
                        hipsTarget = states[vsHips].OutgoingPosition;
                        hipsRot = states[vsHips].OutgoingRotation;
                    }
                    else
                    {
                        Vector3 gazeFwd = gaze * Vector3.forward;
                        gazeFwd.y = 0f;
                        Quaternion yaw = gazeFwd.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(gazeFwd.normalized, Vector3.up) : Quaternion.identity;
                        Quaternion restYaw = Quaternion.LookRotation(Vector3.ProjectOnPlane(rig.Rest.Gaze * Vector3.forward, Vector3.up).normalized, Vector3.up);
                        hipsTarget = headPos + yaw * Quaternion.Inverse(restYaw) * (Vector3)(rig.Rest.Hips - rig.Rest.Head);
                        hipsRot = yaw * Quaternion.Inverse(restYaw);
                    }

                    if (f % stride != 0) continue;

                    rig.Job.targetPositionHips = hipsTarget;
                    rig.Job.targetRotationHips = hipsRot;
                    rig.Job.targetPositionHead = headPos;
                    rig.Job.targetRotationHead = gaze;
                    rig.Job.crouchDepth = Mathf.Max(0f, rig.Rest.Head.y - headPos.y);
                    rig.Skeleton.GatherNow();
                    rig.Job.poseStream = rig.Skeleton.Stream;
                    rig.Job.SolveSpine();

                    pelvisErrors.Add((rig.Skeleton.Stream.GetPosition(rig.Job.handleHips) - hipsPos).magnitude);
                    for (int j = 0; j < measured.Length; j++)
                    {
                        spineErrors.Add((rig.Skeleton.Stream.GetWorldPosition(rig.MeasuredStreamIndex[j]) - clip.Get(f, measured[j]).Position).magnitude);
                    }
                }
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
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
        public void HeadOnlySynthesis_PlacesTheBodyNearARealHuman()
        {
            List<BasisMotionClip> clips = LoadClips();
            var laws = new[]
            {
                new Law { Name = "rigid offset", VirtualSpine = false },
                new Law { Name = "vspine path-length rest", VirtualSpine = true, AuthoredRestDrop = false },
                new Law { Name = "vspine authored rest", VirtualSpine = true, AuthoredRestDrop = true },
            };
            var report = new StringBuilder();
            report.AppendLine($"HEAD-ONLY BODY ACCURACY ({clips.Count} clips; the whole pipeline driven from the mocap head alone; pelvis and Spine+Chest+UpperChest+Neck vs the human, mean/p95 cm)");
            report.AppendLine();
            report.AppendLine($"{"clip",-10} | " + string.Join(" | ", laws.Select(l => $"{l.Name,-27}")));
            var pooledPelvis = new List<float>[laws.Length];
            var pooledSpine = new List<float>[laws.Length];
            for (int l = 0; l < laws.Length; l++) { pooledPelvis[l] = new List<float>(); pooledSpine[l] = new List<float>(); }
            foreach (BasisMotionClip clip in clips)
            {
                var cells = new List<string>();
                for (int l = 0; l < laws.Length; l++)
                {
                    using Rig rig = BuildRig(clip, MostUprightFrame(clip));
                    var pelvis = new List<float>();
                    var spine = new List<float>();
                    RunClip(rig, clip, laws[l], pelvis, spine);
                    pooledPelvis[l].AddRange(pelvis);
                    pooledSpine[l].AddRange(spine);
                    (float pm, float pp) = Stats(pelvis);
                    (float sm, float sp) = Stats(spine);
                    cells.Add($"pelvis {pm * 100f,5:F2} {pp * 100f,5:F2} spine {sm * 100f,5:F2} {sp * 100f,5:F2}");
                }
                report.AppendLine($"{clip.Name,-10} | " + string.Join(" | ", cells));
            }
            var pooledCells = new List<string>();
            for (int l = 0; l < laws.Length; l++)
            {
                (float pm, float pp) = Stats(pooledPelvis[l]);
                (float sm, float sp) = Stats(pooledSpine[l]);
                pooledCells.Add($"pelvis {pm * 100f,5:F2} {pp * 100f,5:F2} spine {sm * 100f,5:F2} {sp * 100f,5:F2}");
            }
            report.AppendLine($"{"POOLED",-10} | " + string.Join(" | ", pooledCells));
            TestContext.WriteLine(report.ToString());

            int shipped = laws.Length - 1;
            (float shippedPelvisMean, _) = Stats(pooledPelvis[shipped]);
            (float shippedSpineMean, _) = Stats(pooledSpine[shipped]);
            foreach (float e in pooledPelvis[shipped]) Assert.IsFalse(float.IsNaN(e) || float.IsInfinity(e), "pelvis error must be finite");
            foreach (float e in pooledSpine[shipped]) Assert.IsFalse(float.IsNaN(e) || float.IsInfinity(e), "spine error must be finite");
            Assert.Less(shippedPelvisMean, 0.25f, "the synthesized pelvis must land within a sane bound of the human's");
            Assert.Less(shippedSpineMean, 0.15f, "the solved spine must land within a sane bound of the human's");
        }
    }
}
