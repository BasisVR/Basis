using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public class BasisRemoteLimbIKTests
    {
        const int root = 0, hips = 1, neck = 5, head = 6, leftUpperArm = 8, leftLowerArm = 9, leftHand = 10, rightUpperArm = 12, rightLowerArm = 13, rightHand = 14;
        const int leftUpperLeg = 15, leftLowerLeg = 16, leftFoot = 17, rightUpperLeg = 19, rightLowerLeg = 20, rightFoot = 21;
        static readonly int[] noiseBones = { 2, 3, 4, 5, 7, 8, 9, 11, 12, 13, 15, 16, 19, 20 };
        static readonly BasisFitPose[] armPoses = { BasisFitPose.TPose, BasisFitPose.Idle, BasisFitPose.ReachForward, BasisFitPose.ReachUp, BasisFitPose.Crouch, BasisFitPose.Bow };
        static BasisFitBodySpec[] Avatars => new[] { BasisFitBodies.Realistic, BasisFitBodySpec.Anime("AvatarAnime"), BasisFitBodySpec.Chibi("AvatarChibi"), BasisFitBodies.Realistic.With("AvatarAPose", s => { s.ArmDropDeg = 40f; return s; }), BasisFitBodySpec.Human("AvatarLongArms", 1.75f, 1.35f) };
        sealed class Remote : System.IDisposable
        {
            public BasisFitTestRig Rig;
            public Transform[] Bones, Sender;
            public Quaternion[] Rest, Axis;
            public void Dispose() => Rig?.Dispose();
        }
        struct ArmError
        {
            public float Hand, HandRot, Elbow, Upper, Lower;
        }
        bool previousSynchronous;
        [OneTimeSetUp]
        public void EnableSynchronousBurst()
        {
            previousSynchronous = BurstCompiler.Options.EnableBurstCompileSynchronously;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
        }
        [OneTimeTearDown]
        public void RestoreBurst() => BurstCompiler.Options.EnableBurstCompileSynchronously = previousSynchronous;
        static BasisFitScenario Scenario(BasisFitBodySpec avatar, BasisFitTrackers trackers) => new BasisFitScenario { Player = BasisFitBodySpec.Human("PlayerAverage", 1.75f), Avatar = avatar, Trackers = trackers, Mode = BasisSelectedHeightMode.EyeHeight, Pipeline = BasisFitPipeline.Current, AvatarScale = 1f, FitDeviation = 0.1f };
        static Transform[] Bones(BasisFitTestRig r) => new[] { r.Root, r.Hips, r.Spine, r.Chest, r.UpperChest, r.Neck, r.Head, r.LeftShoulder, r.LeftUpperArm, r.LeftLowerArm, r.LeftHand, r.RightShoulder, r.RightUpperArm, r.RightLowerArm, r.RightHand, r.LeftUpperLeg, r.LeftLowerLeg, r.LeftFoot, r.LeftToe, r.RightUpperLeg, r.RightLowerLeg, r.RightFoot, r.RightToe };
        static Remote CreateRemote(BasisFitSimulation sim, int axisSeed, bool twistNodes)
        {
            var remote = new Remote { Rig = BasisFitTestRig.Build(sim.Scenario.Avatar, withSolver: false) };
            remote.Bones = Bones(remote.Rig);
            remote.Sender = Bones(sim.Avatar);
            remote.Rig.Wrapper.transform.localScale = sim.Avatar.Wrapper.transform.localScale;
            for (int i = 0; i < remote.Bones.Length; i++)
            {
                if (remote.Bones[i] == null) continue;
                remote.Bones[i].SetLocalPositionAndRotation(remote.Sender[i].localPosition, remote.Sender[i].localRotation);
                remote.Bones[i].localScale = remote.Sender[i].localScale;
            }
            if (twistNodes)
            {
                InsertTwist(remote.Bones[leftUpperArm], remote.Bones[leftLowerArm], Quaternion.Euler(12f, 70f, -25f));
                InsertTwist(remote.Bones[rightUpperArm], remote.Bones[rightLowerArm], Quaternion.Euler(-30f, 15f, 40f));
                InsertTwist(remote.Bones[leftLowerArm], remote.Bones[leftHand], Quaternion.Euler(5f, -60f, 20f));
                InsertTwist(remote.Bones[rightLowerArm], remote.Bones[rightHand], Quaternion.Euler(-45f, 10f, -5f));
                InsertTwist(remote.Bones[leftUpperLeg], remote.Bones[leftLowerLeg], Quaternion.Euler(20f, -35f, 10f));
            }
            remote.Axis = new Quaternion[remote.Bones.Length];
            remote.Rest = new Quaternion[remote.Bones.Length];
            var random = new System.Random(axisSeed);
            for (int i = 0; i < remote.Bones.Length; i++)
            {
                remote.Axis[i] = Quaternion.identity;
                if (remote.Bones[i] != null && axisSeed != 0 && i != root)
                {
                    remote.Axis[i] = RandomRotation(random, 180f);
                    Reaxis(remote.Bones[i], remote.Axis[i]);
                }
            }
            for (int i = 0; i < remote.Bones.Length; i++)
            {
                remote.Rest[i] = remote.Bones[i] != null ? remote.Bones[i].localRotation : Quaternion.identity;
            }
            return remote;
        }
        static void InsertTwist(Transform parent, Transform child, Quaternion rotation)
        {
            Transform twist = new GameObject(child.name + "Twist").transform;
            twist.SetParent(parent, false);
            twist.SetPositionAndRotation(Vector3.Lerp(parent.position, child.position, 0.5f), parent.rotation * rotation);
            child.SetParent(twist, true);
        }
        static void Reaxis(Transform bone, Quaternion axis)
        {
            int count = bone.childCount;
            var positions = new Vector3[count];
            var rotations = new Quaternion[count];
            for (int c = 0; c < count; c++) bone.GetChild(c).GetPositionAndRotation(out positions[c], out rotations[c]);
            bone.rotation = bone.rotation * axis;
            for (int c = 0; c < count; c++) bone.GetChild(c).SetPositionAndRotation(positions[c], rotations[c]);
        }
        static Quaternion RandomRotation(System.Random random, float maxDeg)
        {
            Vector3 axis = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;
            return Quaternion.AngleAxis(((float)random.NextDouble() * 2f - 1f) * maxDeg, axis.normalized);
        }
        static void CopyPose(Remote remote)
        {
            for (int i = 0; i < remote.Bones.Length; i++)
            {
                Transform bone = remote.Bones[i];
                if (bone == null) continue;
                if (i <= hips) bone.position = remote.Sender[i].position;
                bone.rotation = remote.Sender[i].rotation * remote.Axis[i];
            }
            for (int i = 0; i < remote.Bones.Length; i++)
            {
                if (remote.Bones[i] == null) continue;
                Assert.That(Vector3.Distance(remote.Bones[i].position, remote.Sender[i].position), Is.LessThan(1e-4f), $"harness: copied pose of {remote.Bones[i].name}");
            }
        }
        static void AddNoise(Remote remote, System.Random random, float maxDeg)
        {
            foreach (int i in noiseBones)
            {
                Transform bone = remote.Bones[i];
                if (bone != null) bone.localRotation = bone.localRotation * RandomRotation(random, maxDeg);
            }
        }
        static BasisRemoteLimbPose Limb(Remote remote, int rootIndex, int jointIndex, int tipIndex)
        {
            Transform a = remote.Bones[rootIndex], b = remote.Bones[jointIndex], c = remote.Bones[tipIndex];
            return new BasisRemoteLimbPose { Root = a.position, Joint = b.position, Tip = c.position, RootWorld = a.rotation, JointWorld = b.rotation, TipWorld = c.rotation, RootLocal = a.localRotation, JointLocal = b.localRotation, TipLocal = c.localRotation, RootRest = remote.Rest[rootIndex], JointRest = remote.Rest[jointIndex], TipRest = remote.Rest[tipIndex] };
        }
        static BasisSwivelFrame ArmFrame(Remote remote) => BasisSwivelHintCore.BuildFrame(remote.Bones[leftUpperArm].position, remote.Bones[rightUpperArm].position, remote.Bones[hips].position, (remote.Bones[neck] != null ? remote.Bones[neck] : remote.Bones[head]).position);
        static Vector3 LegRight(Remote remote) => (remote.Bones[rightUpperLeg].position - remote.Bones[leftUpperLeg].position).normalized;
        static void Arm(bool isLeft, out int upper, out int lower, out int hand)
        {
            upper = isLeft ? leftUpperArm : rightUpperArm;
            lower = isLeft ? leftLowerArm : rightLowerArm;
            hand = isLeft ? leftHand : rightHand;
        }
        static void Leg(bool isLeft, out int upper, out int lower, out int foot)
        {
            upper = isLeft ? leftUpperLeg : rightUpperLeg;
            lower = isLeft ? leftLowerLeg : rightLowerLeg;
            foot = isLeft ? leftFoot : rightFoot;
        }
        static Quaternion TargetRotation(Remote remote, int tip) => remote.Sender[tip].rotation * remote.Axis[tip];
        static bool SolveArm(Remote remote, bool isLeft)
        {
            Arm(isLeft, out int upper, out int lower, out int hand);
            bool solved = BasisRemoteLimbIK.SolveArm(Limb(remote, upper, lower, hand), isLeft, remote.Sender[hand].position, TargetRotation(remote, hand), ArmFrame(remote), out Quaternion a, out Quaternion b, out Quaternion c);
            remote.Bones[upper].localRotation = a;
            remote.Bones[lower].localRotation = b;
            remote.Bones[hand].localRotation = c;
            return solved;
        }
        static bool SolveLeg(Remote remote, bool isLeft)
        {
            Leg(isLeft, out int upper, out int lower, out int foot);
            bool solved = BasisRemoteLimbIK.SolveLeg(Limb(remote, upper, lower, foot), remote.Sender[foot].position, TargetRotation(remote, foot), LegRight(remote), out Quaternion a, out Quaternion b, out Quaternion c);
            remote.Bones[upper].localRotation = a;
            remote.Bones[lower].localRotation = b;
            remote.Bones[foot].localRotation = c;
            return solved;
        }
        static float AngleDeg(Quaternion a, Quaternion b)
        {
            Quaternion d = Quaternion.Inverse(a) * b;
            return 2f * Mathf.Atan2(new Vector3(d.x, d.y, d.z).magnitude, Mathf.Abs(d.w)) * Mathf.Rad2Deg;
        }
        static float RotationError(Remote remote, int i) => AngleDeg(remote.Bones[i].rotation, remote.Sender[i].rotation * remote.Axis[i]);
        static float PositionError(Remote remote, int i) => Vector3.Distance(remote.Bones[i].position, remote.Sender[i].position);
        static ArmError Error(Remote remote, int upper, int lower, int tip) => new ArmError { Hand = PositionError(remote, tip), HandRot = RotationError(remote, tip), Elbow = PositionError(remote, lower), Upper = RotationError(remote, upper), Lower = RotationError(remote, lower) };
        static float Shortfall(Remote remote, int upper, int lower, int tip)
        {
            float chain = Vector3.Distance(remote.Bones[upper].position, remote.Bones[lower].position) + Vector3.Distance(remote.Bones[lower].position, remote.Bones[tip].position);
            return Mathf.Max(0f, Vector3.Distance(remote.Bones[upper].position, remote.Sender[tip].position) - chain);
        }
        static void AssertMatches(in ArmError e, string label)
        {
            Assert.That(e.Hand, Is.LessThan(2e-4f), $"{label}: remote tip lands on the sender's tip");
            Assert.That(e.HandRot, Is.LessThan(0.05f), $"{label}: remote tip rotation matches the sender");
            Assert.That(e.Elbow, Is.LessThan(1e-3f), $"{label}: remote joint lands where the sender's joint is");
            Assert.That(e.Upper, Is.LessThan(0.25f), $"{label}: remote upper bone matches the sender (roll included)");
            Assert.That(e.Lower, Is.LessThan(0.25f), $"{label}: remote lower bone matches the sender (forearm roll included)");
        }
        static void Worst(ref ArmError worst, in ArmError e)
        {
            worst.Hand = Mathf.Max(worst.Hand, e.Hand);
            worst.HandRot = Mathf.Max(worst.HandRot, e.HandRot);
            worst.Elbow = Mathf.Max(worst.Elbow, e.Elbow);
            worst.Upper = Mathf.Max(worst.Upper, e.Upper);
            worst.Lower = Mathf.Max(worst.Lower, e.Lower);
        }
        static void Report(string label, in ArmError w) => TestContext.Out.WriteLine($"{label}: tip {w.Hand * 1000f:F3} mm / {w.HandRot:F3} deg, joint {w.Elbow * 1000f:F3} mm, upper {w.Upper:F3} deg, lower {w.Lower:F3} deg");
        static float RayDistance(Remote remote, int upper, int tip) => Vector3.Cross((remote.Sender[tip].position - remote.Bones[upper].position).normalized, remote.Bones[tip].position - remote.Bones[upper].position).magnitude;
        [Test]
        public void RemoteArmsRebuildTheSendersArms([Values(0, 1, 2, 3, 4)] int avatar, [Values] bool elbowTrackers, [Values(0, 7)] int axisSeed)
        {
            BasisFitBodySpec spec = Avatars[avatar];
            using var sim = BasisFitSimulation.Create(Scenario(spec, elbowTrackers ? BasisFitTrackers.Elbows : BasisFitTrackers.None));
            using var remote = CreateRemote(sim, axisSeed, false);
            ArmError worst = default;
            foreach (BasisFitPose pose in armPoses)
            {
                sim.Solve(pose);
                CopyPose(remote);
                for (int side = 0; side < 2; side++)
                {
                    bool isLeft = side == 0;
                    Arm(isLeft, out int upper, out int lower, out int hand);
                    Assert.That(SolveArm(remote, isLeft), Is.True, $"{spec.Name} {pose} {(isLeft ? "L" : "R")}: solved");
                    ArmError e = Error(remote, upper, lower, hand);
                    Worst(ref worst, e);
                    AssertMatches(e, $"{spec.Name} {pose} {(isLeft ? "L" : "R")} seed{axisSeed}");
                }
            }
            Report($"{spec.Name} elbowTrackers={elbowTrackers} seed{axisSeed}", worst);
        }
        [Test]
        public void TwistNodesBetweenTheLimbBonesStayRigid([Values(0, 3)] int axisSeed)
        {
            using var sim = BasisFitSimulation.Create(Scenario(BasisFitBodies.Realistic, BasisFitTrackers.Feet));
            using var remote = CreateRemote(sim, axisSeed, true);
            foreach (BasisFitPose pose in new[] { BasisFitPose.Idle, BasisFitPose.ReachUp, BasisFitPose.Crouch, BasisFitPose.StepUp })
            {
                sim.Solve(pose);
                CopyPose(remote);
                for (int side = 0; side < 2; side++)
                {
                    bool isLeft = side == 0;
                    Arm(isLeft, out int upper, out int lower, out int hand);
                    Assert.That(SolveArm(remote, isLeft), Is.True);
                    AssertMatches(Error(remote, upper, lower, hand), $"twist arm {pose} {(isLeft ? "L" : "R")}");
                }
                if (pose == BasisFitPose.Crouch || pose == BasisFitPose.StepUp)
                {
                    Leg(true, out int upper, out int lower, out int foot);
                    Assert.That(SolveLeg(remote, true), Is.True);
                    AssertMatches(Error(remote, upper, lower, foot), $"twist leg {pose}");
                }
            }
        }
        [Test]
        public void RemoteArmsKeepTheHandOnTargetThroughWireNoise([Values(0, 1, 2)] int avatar)
        {
            BasisFitBodySpec spec = Avatars[avatar];
            using var sim = BasisFitSimulation.Create(Scenario(spec, BasisFitTrackers.None));
            using var remote = CreateRemote(sim, 11, false);
            var random = new System.Random(5);
            ArmError worst = default;
            float worstFk = 0f;
            foreach (BasisFitPose pose in armPoses)
            {
                sim.Solve(pose);
                for (int trial = 0; trial < 8; trial++)
                {
                    CopyPose(remote);
                    AddNoise(remote, random, 0.3f);
                    for (int side = 0; side < 2; side++)
                    {
                        bool isLeft = side == 0;
                        Arm(isLeft, out int upper, out int lower, out int hand);
                        float fkHand = PositionError(remote, hand), fkGap = Mathf.Abs(Vector3.Distance(remote.Bones[upper].position, remote.Bones[hand].position) - Vector3.Distance(remote.Bones[upper].position, remote.Sender[hand].position));
                        worstFk = Mathf.Max(worstFk, fkHand);
                        Assert.That(SolveArm(remote, isLeft), Is.True);
                        ArmError e = Error(remote, upper, lower, hand);
                        Worst(ref worst, e);
                        string label = $"{spec.Name} {pose} {(isLeft ? "L" : "R")} trial{trial}";
                        Assert.That(RayDistance(remote, upper, hand), Is.LessThan(5e-5f), $"{label}: hand sits on the ray to the sent target");
                        Assert.That(e.Hand, Is.LessThan(Mathf.Max(Shortfall(remote, upper, lower, hand), fkGap) + 2e-4f), $"{label}: hand lands on target, short only by the reach the noisy FK disagrees on");
                        Assert.That(e.Hand, Is.LessThanOrEqualTo(fkHand + 1e-4f), $"{label}: IK never leaves the hand further off than FK");
                        Assert.That(e.HandRot, Is.LessThan(0.05f), $"{label}: hand rotation is the sent rotation");
                        Assert.That(e.Elbow, Is.LessThan(0.012f * spec.EyeY / 1.638f), $"{label}: elbow stays with the sender's elbow");
                        Assert.That(e.Upper, Is.LessThan(3f), $"{label}: upper arm stays with the sender's");
                        Assert.That(e.Lower, Is.LessThan(3f), $"{label}: forearm stays with the sender's");
                    }
                }
            }
            Report($"{spec.Name} noisy FK (worst FK hand {worstFk * 1000f:F2} mm)", worst);
        }
        [Test]
        public void StraightArmsHoldTheirElbowThroughTargetQuantization([Values(0, 1, 2)] int avatar)
        {
            BasisFitBodySpec spec = Avatars[avatar];
            using var sim = BasisFitSimulation.Create(Scenario(spec, BasisFitTrackers.None));
            using var remote = CreateRemote(sim, 29, false);
            var random = new System.Random(31);
            float step = 2f * BasisAvatarEndEffectorsRange / 4095f;
            foreach (BasisFitPose pose in new[] { BasisFitPose.TPose, BasisFitPose.ReachForward })
            {
                sim.Solve(pose);
                ArmError worst = default;
                float worstMiss = 0f;
                for (int trial = 0; trial < 40; trial++)
                {
                    CopyPose(remote);
                    for (int side = 0; side < 2; side++)
                    {
                        bool isLeft = side == 0;
                        Arm(isLeft, out int upper, out int lower, out int hand);
                        Vector3 noise = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * step, target = remote.Sender[hand].position + noise;
                        BasisRemoteLimbIK.SolveArm(Limb(remote, upper, lower, hand), isLeft, target, TargetRotation(remote, hand), ArmFrame(remote), out Quaternion a, out Quaternion b, out Quaternion c);
                        remote.Bones[upper].localRotation = a;
                        remote.Bones[lower].localRotation = b;
                        remote.Bones[hand].localRotation = c;
                        Worst(ref worst, Error(remote, upper, lower, hand));
                        worstMiss = Mathf.Max(worstMiss, Vector3.Distance(remote.Bones[hand].position, target));
                    }
                }
                Report($"{spec.Name} {pose} quantized target (miss {worstMiss * 1000f:F3} mm)", worst);
                Assert.That(worst.Elbow, Is.LessThan(1.5e-3f), $"{spec.Name} {pose}: a millimetre of target quantization must not pump a straight elbow");
                Assert.That(worst.Upper, Is.LessThan(0.5f), $"{spec.Name} {pose}: straight upper arm holds");
                Assert.That(worst.Lower, Is.LessThan(0.5f), $"{spec.Name} {pose}: straight forearm holds");
                Assert.That(worstMiss, Is.LessThan(step * 1.5f), $"{spec.Name} {pose}: the hand still lands within the quantization step of the target");
            }
        }
        const float BasisAvatarEndEffectorsRange = Basis.Scripts.Networking.NetworkedAvatar.BasisAvatarEndEffectors.PosRange;
        [Test]
        public void StraightArmsDoNotRollUnderWireNoise()
        {
            using var sim = BasisFitSimulation.Create(Scenario(BasisFitBodies.Realistic, BasisFitTrackers.None));
            using var remote = CreateRemote(sim, 19, false);
            var random = new System.Random(23);
            foreach (BasisFitPose pose in new[] { BasisFitPose.TPose, BasisFitPose.ReachForward })
            {
                sim.Solve(pose);
                float worstUpper = 0f, worstLower = 0f, worstElbow = 0f;
                for (int trial = 0; trial < 25; trial++)
                {
                    CopyPose(remote);
                    AddNoise(remote, random, 0.1f);
                    for (int side = 0; side < 2; side++)
                    {
                        bool isLeft = side == 0;
                        Arm(isLeft, out int upper, out int lower, out int hand);
                        Assert.That(SolveArm(remote, isLeft), Is.True);
                        ArmError e = Error(remote, upper, lower, hand);
                        worstUpper = Mathf.Max(worstUpper, e.Upper);
                        worstLower = Mathf.Max(worstLower, e.Lower);
                        worstElbow = Mathf.Max(worstElbow, e.Elbow);
                    }
                }
                TestContext.Out.WriteLine($"{pose}: worst upper {worstUpper:F3} deg, lower {worstLower:F3} deg, elbow {worstElbow * 1000f:F2} mm");
                Assert.That(worstUpper, Is.LessThan(1f), $"{pose}: a straight arm's roll follows the sender, it never flips");
                Assert.That(worstLower, Is.LessThan(1f), $"{pose}: a straight forearm's roll follows the sender, it never flips");
                Assert.That(worstElbow, Is.LessThan(0.004f), $"{pose}: straight elbow stays put");
            }
        }
        [Test]
        public void RemoteLegsRebuildTheSendersLegs([Values(0, 1, 2)] int avatar, [Values] bool kneeTrackers, [Values(0, 5)] int axisSeed)
        {
            BasisFitBodySpec spec = Avatars[avatar];
            BasisFitTrackers trackers = BasisFitTrackers.Hips | BasisFitTrackers.Feet | (kneeTrackers ? BasisFitTrackers.Knees : BasisFitTrackers.None);
            using var sim = BasisFitSimulation.Create(Scenario(spec, trackers));
            using var remote = CreateRemote(sim, axisSeed, false);
            foreach (BasisFitPose pose in new[] { BasisFitPose.Crouch, BasisFitPose.StepUp, BasisFitPose.Idle })
            {
                sim.Solve(pose);
                CopyPose(remote);
                for (int side = 0; side < 2; side++)
                {
                    bool isLeft = side == 0;
                    Leg(isLeft, out int upper, out int lower, out int foot);
                    float ratio = BasisRemoteLimbIK.LegReachRatio(Limb(remote, upper, lower, foot), remote.Sender[foot].position);
                    Assert.That(SolveLeg(remote, isLeft), Is.True);
                    ArmError e = Error(remote, upper, lower, foot);
                    string label = $"{spec.Name} {pose} {(isLeft ? "L" : "R")} seed{axisSeed} reach{ratio:F3}";
                    Assert.That(e.Hand, Is.LessThan(2e-4f), $"{label}: remote foot lands on the sender's foot");
                    Assert.That(e.HandRot, Is.LessThan(0.1f), $"{label}: remote foot rotation matches the sender");
                    if (ratio < BasisRemoteNetworkDriver.LegAnchorFadeStraightReach)
                    {
                        Assert.That(e.Elbow, Is.LessThan(2e-3f), $"{label}: remote knee lands where the sender's knee is");
                        Assert.That(e.Upper, Is.LessThan(2f), $"{label}: remote thigh matches the sender");
                        Assert.That(e.Lower, Is.LessThan(2f), $"{label}: remote shin matches the sender");
                    }
                }
            }
        }
        static readonly int[] wireSlotBone = { 2, 3, 4, 5, 6, 8, 12, 15, 19, 9, 13, 16, 20, 7, 11, 10, 14, 17, 21, 18, 22 };
        static T DriverArray<T>(string field) => (T)typeof(BasisRemoteNetworkDriver).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
        [Test]
        public void TheRemoteJobSolvesExactlyTheAnchoredLimbs([Values(15, 5, 2)] int mask)
        {
            using var sim = BasisFitSimulation.Create(Scenario(BasisFitBodies.Realistic, BasisFitTrackers.Hips | BasisFitTrackers.Feet));
            using var remote = CreateRemote(sim, 41, false);
            bool owned = !BasisRemoteNetworkDriver.EffectorMaskArray.IsCreated;
            BasisRemoteNetworkDriver.Initialize();
            const int key = 3, boneCount = 51;
            var keys = new NativeArray<int>(1, Allocator.TempJob);
            var hipsPos = new NativeArray<Unity.Mathematics.float3>(1, Allocator.TempJob);
            var hipsRot = new NativeArray<Unity.Mathematics.quaternion>(1, Allocator.TempJob);
            var readPos = new NativeArray<Unity.Mathematics.float3>(boneCount, Allocator.TempJob);
            var readWorld = new NativeArray<Unity.Mathematics.quaternion>(boneCount, Allocator.TempJob);
            var readLocal = new NativeArray<Unity.Mathematics.quaternion>(boneCount, Allocator.TempJob);
            var decodePre = new NativeArray<Unity.Mathematics.quaternion>(boneCount, Allocator.TempJob);
            var decodePost = new NativeArray<Unity.Mathematics.quaternion>(boneCount, Allocator.TempJob);
            var valid = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var overrideRot = new NativeArray<Unity.Mathematics.quaternion>(boneCount, Allocator.TempJob);
            var overrideMask = new NativeArray<byte>(boneCount, Allocator.TempJob);
            NativeArray<byte> effMask = BasisRemoteNetworkDriver.EffectorMaskArray;
            NativeArray<Unity.Mathematics.float3> effOffset = DriverArray<NativeArray<Unity.Mathematics.float3>>("_effOffset");
            NativeArray<Unity.Mathematics.quaternion> effTipRot = DriverArray<NativeArray<Unity.Mathematics.quaternion>>("_effTipRot");
            var random = new System.Random(mask);
            int[] roots = { leftUpperArm, rightUpperArm, leftUpperLeg, rightUpperLeg }, joints = { leftLowerArm, rightLowerArm, leftLowerLeg, rightLowerLeg }, tips = { leftHand, rightHand, leftFoot, rightFoot };
            int[] rootSlots = { 5, 6, 7, 8 }, jointSlots = { 9, 10, 11, 12 }, tipSlots = { 15, 16, 17, 18 };
            try
            {
                keys[0] = key;
                foreach (BasisFitPose pose in new[] { BasisFitPose.ReachUp, BasisFitPose.Crouch, BasisFitPose.StepUp, BasisFitPose.Idle })
                {
                    sim.Solve(pose);
                    CopyPose(remote);
                    AddNoise(remote, random, 0.3f);
                    for (int s = 0; s < boneCount; s++)
                    {
                        Transform bone = s < wireSlotBone.Length ? remote.Bones[wireSlotBone[s]] : null;
                        valid[s] = (byte)(bone != null ? 1 : 0);
                        overrideMask[s] = 0;
                        if (bone == null) continue;
                        readPos[s] = bone.position;
                        readWorld[s] = bone.rotation;
                        readLocal[s] = bone.localRotation;
                        decodePre[s] = remote.Rest[wireSlotBone[s]];
                        decodePost[s] = Unity.Mathematics.quaternion.identity;
                    }
                    Transform hipsBone = remote.Bones[hips];
                    hipsPos[0] = hipsBone.position;
                    hipsRot[0] = hipsBone.rotation;
                    effMask[key] = (byte)mask;
                    for (int e = 0; e < 4; e++)
                    {
                        effOffset[key * 4 + e] = Quaternion.Inverse(hipsBone.rotation) * (remote.Sender[tips[e]].position - hipsBone.position);
                        effTipRot[key * 4 + e] = TargetRotation(remote, tips[e]);
                    }
                    BasisRemoteNetworkDriver.ScheduleComputeEffectorIK(keys, 1, boneCount, hipsPos, hipsRot, readPos, readWorld, readLocal, decodePre, decodePost, valid, overrideRot, overrideMask, 1).Complete();
                    for (int e = 0; e < 4; e++)
                    {
                        string label = $"{pose} mask{mask} effector{e}";
                        BasisRemoteLimbPose limb = Limb(remote, roots[e], joints[e], tips[e]);
                        Vector3 target = hipsBone.position + hipsBone.rotation * (Vector3)effOffset[key * 4 + e];
                        float weight = e < 2 ? 1f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(BasisRemoteNetworkDriver.LegAnchorFadeBentReach, BasisRemoteNetworkDriver.LegAnchorFadeStraightReach, BasisRemoteLimbIK.LegReachRatio(limb, target)));
                        bool anchored = (mask & (1 << e)) != 0 && weight > 0f;
                        Assert.That(overrideMask[rootSlots[e]], Is.EqualTo((byte)(anchored ? 1 : 0)), $"{label}: only anchored limbs are overridden");
                        Assert.That(overrideMask[jointSlots[e]], Is.EqualTo(overrideMask[rootSlots[e]]), label);
                        Assert.That(overrideMask[tipSlots[e]], Is.EqualTo(overrideMask[rootSlots[e]]), label);
                        if (!anchored) continue;
                        Quaternion a, b, c;
                        bool solved = e < 2 ? BasisRemoteLimbIK.SolveArm(limb, e == 0, target, TargetRotation(remote, tips[e]), ArmFrame(remote), out a, out b, out c) : BasisRemoteLimbIK.SolveLeg(limb, target, TargetRotation(remote, tips[e]), LegRight(remote), out a, out b, out c);
                        Assert.That(solved, Is.True, label);
                        Assert.That(AngleDeg(Quaternion.Slerp(limb.RootLocal, a, weight), overrideRot[rootSlots[e]]), Is.LessThan(0.01f), $"{label}: job upper == solver");
                        Assert.That(AngleDeg(Quaternion.Slerp(limb.JointLocal, b, weight), overrideRot[jointSlots[e]]), Is.LessThan(0.01f), $"{label}: job lower == solver");
                        Assert.That(AngleDeg(Quaternion.Slerp(limb.TipLocal, c, weight), overrideRot[tipSlots[e]]), Is.LessThan(0.01f), $"{label}: job tip == solver");
                    }
                    for (int s = 0; s < boneCount; s++)
                    {
                        if (overrideMask[s] != 0) remote.Bones[wireSlotBone[s]].localRotation = overrideRot[s];
                    }
                    for (int e = 0; e < 2; e++)
                    {
                        if ((mask & (1 << e)) == 0) continue;
                        Assert.That(RayDistance(remote, roots[e], tips[e]), Is.LessThan(5e-5f), $"{pose} mask{mask}: written hand sits on the ray to the sent target");
                        Assert.That(PositionError(remote, tips[e]), Is.LessThan(0.006f), $"{pose} mask{mask}: written hand lands on the sent target");
                        Assert.That(RotationError(remote, tips[e]), Is.LessThan(0.05f), $"{pose} mask{mask}: written hand takes the sent rotation");
                    }
                }
            }
            finally
            {
                if (effMask.IsCreated) effMask[key] = 0;
                keys.Dispose();
                hipsPos.Dispose();
                hipsRot.Dispose();
                readPos.Dispose();
                readWorld.Dispose();
                readLocal.Dispose();
                decodePre.Dispose();
                decodePost.Dispose();
                valid.Dispose();
                overrideRot.Dispose();
                overrideMask.Dispose();
                if (owned) BasisRemoteNetworkDriver.Shutdown();
            }
        }
        [BurstCompile(CompileSynchronously = true)]
        struct RemoteSolveJob : IJob
        {
            [ReadOnly] public NativeArray<BasisRemoteLimbPose> Limbs;
            [ReadOnly] public NativeArray<Vector3> Targets;
            [ReadOnly] public NativeArray<Quaternion> TargetRotations;
            public BasisSwivelFrame Frame;
            public Vector3 LegRight;
            public NativeArray<Quaternion> Out;
            public NativeArray<int> Managed;
            public void Execute()
            {
                MarkManaged();
                for (int e = 0; e < 4; e++)
                {
                    Quaternion a, b, c;
                    if (e < 2) BasisRemoteLimbIK.SolveArm(Limbs[e], e == 0, Targets[e], TargetRotations[e], Frame, out a, out b, out c);
                    else BasisRemoteLimbIK.SolveLeg(Limbs[e], Targets[e], TargetRotations[e], LegRight, out a, out b, out c);
                    Out[e * 3] = a;
                    Out[e * 3 + 1] = b;
                    Out[e * 3 + 2] = c;
                }
            }
            [BurstDiscard]
            void MarkManaged() => Managed[0] = 1;
        }
        [Test]
        public void BurstMatchesManaged()
        {
            if (!BurstCompiler.IsEnabled)
            {
                Assert.Ignore("Burst is disabled in this editor");
            }
            using var sim = BasisFitSimulation.Create(Scenario(BasisFitBodies.Realistic, BasisFitTrackers.Hips | BasisFitTrackers.Feet));
            using var remote = CreateRemote(sim, 13, true);
            var random = new System.Random(3);
            foreach (BasisFitPose pose in new[] { BasisFitPose.ReachUp, BasisFitPose.Crouch, BasisFitPose.StepUp })
            {
                sim.Solve(pose);
                CopyPose(remote);
                AddNoise(remote, random, 0.3f);
                var limbs = new NativeArray<BasisRemoteLimbPose>(4, Allocator.TempJob);
                var targets = new NativeArray<Vector3>(4, Allocator.TempJob);
                var rotations = new NativeArray<Quaternion>(4, Allocator.TempJob);
                var output = new NativeArray<Quaternion>(12, Allocator.TempJob);
                var managed = new NativeArray<int>(1, Allocator.TempJob);
                try
                {
                    int[] tips = { leftHand, rightHand, leftFoot, rightFoot };
                    for (int e = 0; e < 4; e++)
                    {
                        int upper = e < 2 ? (e == 0 ? leftUpperArm : rightUpperArm) : (e == 2 ? leftUpperLeg : rightUpperLeg);
                        int lower = e < 2 ? (e == 0 ? leftLowerArm : rightLowerArm) : (e == 2 ? leftLowerLeg : rightLowerLeg);
                        limbs[e] = Limb(remote, upper, lower, tips[e]);
                        targets[e] = remote.Sender[tips[e]].position;
                        rotations[e] = TargetRotation(remote, tips[e]);
                    }
                    var job = new RemoteSolveJob { Limbs = limbs, Targets = targets, TargetRotations = rotations, Frame = ArmFrame(remote), LegRight = LegRight(remote), Out = output, Managed = managed };
                    job.Run();
                    Assert.That(managed[0], Is.EqualTo(0), "the job ran Burst-compiled");
                    for (int e = 0; e < 4; e++)
                    {
                        Quaternion a, b, c;
                        if (e < 2) BasisRemoteLimbIK.SolveArm(limbs[e], e == 0, targets[e], rotations[e], job.Frame, out a, out b, out c);
                        else BasisRemoteLimbIK.SolveLeg(limbs[e], targets[e], rotations[e], job.LegRight, out a, out b, out c);
                        Assert.That(AngleDeg(a, output[e * 3]), Is.LessThan(0.01f), $"{pose} effector {e} upper: Burst == managed");
                        Assert.That(AngleDeg(b, output[e * 3 + 1]), Is.LessThan(0.01f), $"{pose} effector {e} lower: Burst == managed");
                        Assert.That(AngleDeg(c, output[e * 3 + 2]), Is.LessThan(0.01f), $"{pose} effector {e} tip: Burst == managed");
                    }
                }
                finally
                {
                    limbs.Dispose();
                    targets.Dispose();
                    rotations.Dispose();
                    output.Dispose();
                    managed.Dispose();
                }
            }
        }
    }
}
