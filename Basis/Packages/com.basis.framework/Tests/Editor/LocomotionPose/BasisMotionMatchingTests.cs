using System;
using System.Collections.Generic;
using System.Diagnostics;
using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Basis.Framework.IK.Tests
{
    /// <summary>
    /// Runs the real database build and runtime job. Most tests use a small procedural rig whose stance feet travel
    /// backward at about the blend velocity and lift during the swing. The stock-clip test bakes the shipped clips.
    /// </summary>
    public class BasisMotionMatchingTests
    {
        const int RootNode = 0, HipsNode = 1, LeftUpper = 2, LeftLower = 3, LeftFootNode = 4, RightUpper = 5, RightLower = 6, RightFootNode = 7, SamplesPerClip = 31;
        const float Frame = 1f / 90f;

        sealed class Rig : IDisposable, IPoseDriver
        {
            public NativeArray<float3> Positions => OutPosition;
            public NativeArray<quaternion> Rotations => OutRotation;
            public void Reset()
            {
                State[0] = BasisMotionMatchingState.Default;
            }
            void IPoseDriver.Step(in BasisLocoParams input, float deltaTime) => Step(in input, deltaTime);
            public readonly int NodeCount;
            public readonly BasisLocomotionPoseBake Bake;
            public readonly BasisMotionMatchingDatabase Database;
            public NativeArray<BasisMotionMatchingState> State;
            public NativeArray<byte> Latch;
            public NativeArray<BasisLocoContribution> Contributions;
            public NativeArray<float3> ScratchPosition, PreviousAngularVelocity, OffsetAngularVelocity, OutPosition, OutScale;
            public NativeArray<quaternion> ScratchRotation, PreviousRotation, OffsetRotation, FinalRotation, OutRotation;
            public NativeArray<int> Parents;
            public NativeArray<float3> Rest;
            public BasisMotionMatchingLegNodes LeftLeg, RightLeg;
            public bool FootLock = true, Inertialization = true;
            public BasisMotionMatchingTuning Tuning = BasisMotionMatchingTuning.Default;
            public float3 RootWorld;
            public float4x4 FrameToWorld = float4x4.identity;
            public float UnitsPerMeter = 1f;
            readonly bool ownsBake;
            NativeArray<float2> childPositions;
            NativeArray<float> childTimeScales;

            public Rig(int nodeCount = 8, float worldScale = 1f)
            {
                NodeCount = nodeCount;
                FrameToWorld = float4x4.Scale(worldScale);
                UnitsPerMeter = 1f / worldScale;
                ownsBake = true;
                Bake = new BasisLocomotionPoseBake { NodeCount = nodeCount, HipsNode = HipsNode };
                Parents = new NativeArray<int>(nodeCount, Allocator.Persistent);
                Rest = new NativeArray<float3>(nodeCount, Allocator.Persistent);
                SetNode(RootNode, -1, float3.zero);
                SetNode(HipsNode, RootNode, new float3(0f, 1f, 0f));
                SetNode(LeftUpper, HipsNode, new float3(-0.1f, -0.05f, 0f));
                SetNode(LeftLower, LeftUpper, new float3(0f, -0.45f, 0f));
                SetNode(LeftFootNode, LeftLower, new float3(0f, -0.45f, 0f));
                SetNode(RightUpper, HipsNode, new float3(0.1f, -0.05f, 0f));
                SetNode(RightLower, RightUpper, new float3(0f, -0.45f, 0f));
                SetNode(RightFootNode, RightLower, new float3(0f, -0.45f, 0f));
                for (int node = RightFootNode + 1; node < nodeCount; node++)
                {
                    SetNode(node, node == RightFootNode + 1 ? HipsNode : node - 1, new float3(0f, 0.1f, 0f));
                }
                LeftLeg = new BasisMotionMatchingLegNodes { Upper = LeftUpper, Lower = LeftLower, Foot = LeftFootNode };
                RightLeg = new BasisMotionMatchingLegNodes { Upper = RightUpper, Lower = RightLower, Foot = RightFootNode };
                CreateChildTables();
                FillBake();
                Database = BasisMotionMatchingDatabase.ScheduleBuild(Bake, Parents, Rest, LeftFootNode, RightFootNode, new float3(0f, 1f, 0f), UnitsPerMeter, childPositions, childTimeScales);
                Database.Complete();
                CreateRuntimeArrays();
            }
            public Rig(BasisLocomotionPoseBake bake, NativeArray<int> parents, NativeArray<float3> rest, BasisMotionMatchingLegNodes leftLeg, BasisMotionMatchingLegNodes rightLeg, float3 up, float4x4 frameToWorld, float unitsPerMeter)
            {
                NodeCount = bake.NodeCount;
                Bake = bake;
                Parents = new NativeArray<int>(parents, Allocator.Persistent);
                Rest = new NativeArray<float3>(rest, Allocator.Persistent);
                LeftLeg = leftLeg;
                RightLeg = rightLeg;
                FrameToWorld = frameToWorld;
                UnitsPerMeter = unitsPerMeter;
                CreateChildTables();
                Database = BasisMotionMatchingDatabase.ScheduleBuild(Bake, Parents, Rest, leftLeg.Foot, rightLeg.Foot, up, unitsPerMeter, childPositions, childTimeScales);
                Database.Complete();
                CreateRuntimeArrays();
            }
            void SetNode(int node, int parent, float3 rest)
            {
                Parents[node] = parent;
                Rest[node] = rest;
            }
            void CreateChildTables()
            {
                int children = BasisLocomotionGraph.ProneClipStart + BasisLocomotionGraph.ProneChildCount;
                childPositions = new NativeArray<float2>(children, Allocator.Persistent);
                childTimeScales = new NativeArray<float>(children, Allocator.Persistent);
                CopyTree(BasisLocomotionGraph.WalkingClipStart, BasisLocomotionGraph.WalkingChildPositions, BasisLocomotionGraph.WalkingChildTimeScales);
                CopyTree(BasisLocomotionGraph.CrouchingClipStart, BasisLocomotionGraph.CrouchingChildPositions, BasisLocomotionGraph.CrouchingChildTimeScales);
                CopyTree(BasisLocomotionGraph.ProneClipStart, BasisLocomotionGraph.ProneChildPositions, BasisLocomotionGraph.ProneChildTimeScales);
            }
            void CreateRuntimeArrays()
            {
                State = new NativeArray<BasisMotionMatchingState>(new[] { BasisMotionMatchingState.Default }, Allocator.Persistent);
                Latch = new NativeArray<byte>(1, Allocator.Persistent);
                Contributions = new NativeArray<BasisLocoContribution>(BasisLocomotionGraph.MaxContributions, Allocator.Persistent);
                ScratchPosition = new NativeArray<float3>(NodeCount, Allocator.Persistent);
                PreviousAngularVelocity = new NativeArray<float3>(NodeCount, Allocator.Persistent);
                OffsetAngularVelocity = new NativeArray<float3>(NodeCount, Allocator.Persistent);
                OutPosition = new NativeArray<float3>(NodeCount, Allocator.Persistent);
                OutScale = new NativeArray<float3>(NodeCount, Allocator.Persistent);
                ScratchRotation = new NativeArray<quaternion>(NodeCount, Allocator.Persistent);
                PreviousRotation = new NativeArray<quaternion>(NodeCount, Allocator.Persistent);
                OffsetRotation = new NativeArray<quaternion>(NodeCount, Allocator.Persistent);
                FinalRotation = new NativeArray<quaternion>(NodeCount, Allocator.Persistent);
                OutRotation = new NativeArray<quaternion>(NodeCount, Allocator.Persistent);
            }
            void CopyTree(int start, float2[] positions, float[] scales)
            {
                for (int i = 0; i < positions.Length; i++)
                {
                    childPositions[start + i] = positions[i];
                    childTimeScales[start + i] = scales[i];
                }
            }
            void FillBake()
            {
                int clips = BasisLocomotionGraph.ClipCount;
                Bake.ClipRotationOffset = new NativeArray<int>(clips, Allocator.Persistent);
                Bake.ClipHipsOffset = new NativeArray<int>(clips, Allocator.Persistent);
                Bake.ClipSampleCount = new NativeArray<int>(clips, Allocator.Persistent);
                Bake.ClipLength = new NativeArray<float>(clips, Allocator.Persistent);
                Bake.ClipLooping = new NativeArray<byte>(clips, Allocator.Persistent);
                Bake.Rotations = new NativeArray<quaternion>(clips * SamplesPerClip * NodeCount, Allocator.Persistent);
                Bake.HipsPositions = new NativeArray<float3>(clips * SamplesPerClip, Allocator.Persistent);
                Bake.SnapshotScales = new NativeArray<float3>(NodeCount, Allocator.Persistent);
                for (int node = 0; node < NodeCount; node++)
                {
                    Bake.SnapshotScales[node] = new float3(1f);
                }
                for (int clip = 0; clip < clips; clip++)
                {
                    Bake.ClipRotationOffset[clip] = clip * SamplesPerClip * NodeCount;
                    Bake.ClipHipsOffset[clip] = clip * SamplesPerClip;
                    Bake.ClipSampleCount[clip] = SamplesPerClip;
                    Bake.ClipLength[clip] = 1f;
                    Bake.ClipLooping[clip] = (byte)(clip < BasisLocomotionGraph.JumpClip ? 1 : 0);
                    bool cycle = clip < BasisLocomotionGraph.JumpClip;
                    float2 velocity = cycle ? childPositions[clip] : float2.zero;
                    float speed = math.length(velocity);
                    float duration = cycle ? 1f / childTimeScales[clip] : 1f;
                    float3 axis = speed > 0.01f ? new float3(velocity.y, 0f, -velocity.x) / speed : new float3(1f, 0f, 0f);
                    // Peak backward foot speed of a sinusoidal swing equals the blend velocity at this amplitude.
                    float amplitude = cycle ? math.min(speed * duration / (2f * math.PI * 0.9f), 0.7f) : 0f;
                    float flexScale = cycle && speed > 0.01f ? 0.25f + amplitude * 1.2f : 0f;
                    for (int sample = 0; sample < SamplesPerClip; sample++)
                    {
                        float theta = 2f * math.PI * sample / (SamplesPerClip - 1);
                        int rotationBase = Bake.ClipRotationOffset[clip] + sample * NodeCount;
                        for (int node = 0; node < NodeCount; node++)
                        {
                            Bake.Rotations[rotationBase + node] = quaternion.AxisAngle(axis, amplitude * 0.2f * math.sin(theta));
                        }
                        Bake.Rotations[rotationBase + RootNode] = quaternion.identity;
                        // One-shots use a pose nothing in the cycles reaches, so a state change is a large jump.
                        Bake.Rotations[rotationBase + HipsNode] = cycle ? quaternion.identity : quaternion.AxisAngle(new float3(1f, 0f, 0f), 0.6f);
                        Bake.Rotations[rotationBase + LeftUpper] = quaternion.AxisAngle(axis, -0.05f - amplitude * math.sin(theta));
                        Bake.Rotations[rotationBase + LeftLower] = quaternion.AxisAngle(axis, 0.1f + flexScale * math.max(0f, math.cos(theta)));
                        Bake.Rotations[rotationBase + LeftFootNode] = quaternion.identity;
                        Bake.Rotations[rotationBase + RightUpper] = quaternion.AxisAngle(axis, -0.05f + amplitude * math.sin(theta));
                        Bake.Rotations[rotationBase + RightLower] = quaternion.AxisAngle(axis, 0.1f + flexScale * math.max(0f, -math.cos(theta)));
                        Bake.Rotations[rotationBase + RightFootNode] = quaternion.identity;
                        Bake.HipsPositions[Bake.ClipHipsOffset[clip] + sample] = new float3(0f, 1f + 0.02f * speed * math.cos(2f * theta), 0f);
                    }
                }
                Bake.Ready = true;
            }
            public BasisMotionMatchingJob CreateJob(in BasisLocoParams parameters, float deltaTime)
            {
                float4x4 toWorld = math.mul(float4x4.Translate(RootWorld), FrameToWorld);
                return new BasisMotionMatchingJob
                {
                    Params = parameters,
                    DeltaTime = deltaTime,
                    Sampler = Bake.CreateSampler(Rest),
                    SkeletonUnitsPerMeter = UnitsPerMeter,
                    FootLockEnabled = FootLock,
                    InertializationEnabled = Inertialization,
                    FacingToSkeleton = quaternion.identity,
                    Tuning = Tuning,
                    LeftLeg = LeftLeg,
                    RightLeg = RightLeg,
                    SkeletonToWorld = toWorld,
                    WorldToSkeleton = math.inverse(toWorld),
                    Parents = Parents,
                    ContactFlags = Database.ContactFlags,
                    GridStanceSpeed = Database.GridStanceSpeed,
                    ClipPhaseOffset = Database.ClipPhaseOffset,
                    ClipLooping = Bake.ClipLooping,
                    SnapshotScales = Bake.SnapshotScales,
                    ClipGroundSpeed = Database.ClipGroundSpeed,
                    ChildTimeScales = childTimeScales,
                    FeatureScale = Database.FeatureScale,
                    Features = Database.Features,
                    State = State,
                    LandingLatch = Latch,
                    Contributions = Contributions,
                    ScratchPosition = ScratchPosition,
                    ScratchRotation = ScratchRotation,
                    PreviousRotation = PreviousRotation,
                    OffsetRotation = OffsetRotation,
                    FinalRotation = FinalRotation,
                    PreviousAngularVelocity = PreviousAngularVelocity,
                    OffsetAngularVelocity = OffsetAngularVelocity,
                    OutLocalPosition = OutPosition,
                    OutLocalRotation = OutRotation,
                    OutLocalScale = OutScale,
                };
            }
            public void Step(in BasisLocoParams parameters, float deltaTime = Frame)
            {
                RootWorld += new float3(parameters.VelocityX, 0f, parameters.VelocityZ) * deltaTime;
                CreateJob(in parameters, deltaTime).Run();
            }
            public void Run(in BasisLocoParams parameters, float seconds)
            {
                int frames = (int)math.ceil(seconds / Frame);
                for (int i = 0; i < frames; i++)
                {
                    Step(in parameters);
                }
            }
            public void FootWorldPositions(out float3 left, out float3 right)
            {
                FootWorldPositions(OutPosition, OutRotation, out left, out right);
            }
            public void FootWorldPositions(NativeArray<float3> position, NativeArray<quaternion> rotation, out float3 left, out float3 right)
            {
                using var worldPosition = new NativeArray<float3>(NodeCount, Allocator.Temp);
                using var worldRotation = new NativeArray<quaternion>(NodeCount, Allocator.Temp);
                using var worldScale = new NativeArray<float3>(NodeCount, Allocator.Temp);
                using var valid = new NativeArray<bool>(NodeCount, Allocator.Temp);
                BasisMotionMatchingKinematics.Forward(Bake.HipsNode, NodeCount, Parents, position, rotation, Bake.SnapshotScales, worldPosition, worldRotation, worldScale, valid);
                float4x4 toWorld = math.mul(float4x4.Translate(RootWorld), FrameToWorld);
                left = math.transform(toWorld, worldPosition[LeftLeg.Foot]);
                right = math.transform(toWorld, worldPosition[RightLeg.Foot]);
            }
            public float2 CurrentGridVelocity => Database.GridVelocity[State[0].Grid];
            public void Dispose()
            {
                Database.Dispose();
                if (ownsBake)
                {
                    Bake.Dispose();
                }
                Parents.Dispose();
                Rest.Dispose();
                childPositions.Dispose();
                childTimeScales.Dispose();
                State.Dispose();
                Latch.Dispose();
                Contributions.Dispose();
                ScratchPosition.Dispose();
                PreviousAngularVelocity.Dispose();
                OffsetAngularVelocity.Dispose();
                OutPosition.Dispose();
                OutScale.Dispose();
                ScratchRotation.Dispose();
                PreviousRotation.Dispose();
                OffsetRotation.Dispose();
                FinalRotation.Dispose();
                OutRotation.Dispose();
            }
        }

        static BasisLocoParams Moving(float x, float z) => new BasisLocoParams
        {
            VelocityX = x,
            VelocityZ = z,
            DesiredVelocityX = x,
            DesiredVelocityZ = z,
            CurrentSpeed = 1f,
        };
        static float AngleDegrees(quaternion a, quaternion b)
        {
            return math.degrees(2f * math.acos(math.min(math.abs(math.dot(a.value, b.value)), 1f)));
        }
        /// <summary>Mean world speed of whichever foot is slowest each frame: a planted foot should score near zero.</summary>
        static float MeasurePlantedFootSpeed(Rig rig, in BasisLocoParams parameters, float seconds)
        {
            rig.Step(in parameters);
            rig.FootWorldPositions(out float3 lastLeft, out float3 lastRight);
            int frames = (int)math.ceil(seconds / Frame);
            float total = 0f;
            for (int i = 0; i < frames; i++)
            {
                rig.Step(in parameters);
                rig.FootWorldPositions(out float3 left, out float3 right);
                total += math.min(math.distance(left, lastLeft), math.distance(right, lastRight)) / Frame;
                lastLeft = left;
                lastRight = right;
            }
            return total / frames;
        }

        [Test]
        public void DatabaseBuild_ProducesFiniteFeaturesAndNormalizedBlends()
        {
            using var rig = new Rig();
            Assert.IsTrue(rig.Database.Ready);
            for (int group = 0; group < rig.Database.FeatureScale.Length; group++)
            {
                Assert.Greater(rig.Database.FeatureScale[group], 0f, $"feature group {group}");
            }
            for (int i = 0; i < rig.Database.Features.Length; i++)
            {
                Assert.IsTrue(math.all(math.isfinite(rig.Database.Features[i])), $"feature vector {i}");
            }
            for (int grid = 0; grid < BasisMotionMatchingDatabase.GridCount; grid++)
            {
                int count = rig.Database.GridContributionCount[grid];
                Assert.Greater(count, 0, $"grid {grid}");
                float total = 0f;
                for (int c = 0; c < count; c++)
                {
                    total += rig.Database.GridContributions[rig.Database.GridContributionOffset[grid] + c].Weight;
                }
                Assert.AreEqual(1f, total, 1e-4f, $"grid {grid}");
                Assert.Greater(rig.Database.GridDuration[grid], 0f, $"grid {grid}");
            }
            Assert.AreEqual(float2.zero, rig.Database.GridVelocity[BasisMotionMatchingDatabase.WalkingGridStart]);
            Assert.AreEqual(float2.zero, rig.Database.GridVelocity[BasisMotionMatchingDatabase.CrouchingGridStart]);
            Assert.AreEqual(float2.zero, rig.Database.GridVelocity[BasisMotionMatchingDatabase.ProneGridStart]);
        }

        [Test]
        public void ContactBake_FindsAlternatingStanceAndARealStrideSpeed()
        {
            using var rig = new Rig();
            int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
            int ring = 3;
            int grid = BasisMotionMatchingDatabase.WalkingGridStart + 1 + (ring - 1) * BasisMotionMatchingDatabase.WalkingDirections;
            Assert.AreEqual(1.8f, rig.Database.GridVelocity[grid].y, 1e-3f);
            int leftOnly = 0, rightOnly = 0, left = 0, right = 0;
            for (int phase = 0; phase < phases; phase++)
            {
                byte flags = rig.Database.ContactFlags[grid * phases + phase];
                bool l = (flags & BasisMotionMatchingDatabase.LeftContactBit) != 0, r = (flags & BasisMotionMatchingDatabase.RightContactBit) != 0;
                left += l ? 1 : 0;
                right += r ? 1 : 0;
                leftOnly += l && !r ? 1 : 0;
                rightOnly += r && !l ? 1 : 0;
            }
            Assert.Greater(left, phases / 10);
            Assert.Greater(right, phases / 10);
            Assert.Less(left, phases * 3 / 4);
            Assert.Less(right, phases * 3 / 4);
            Assert.Greater(leftOnly, 0, "the feet should take turns");
            Assert.Greater(rightOnly, 0, "the feet should take turns");
            Assert.Greater(rig.Database.GridStanceSpeed[grid], 0.5f);
            Assert.Less(rig.Database.GridStanceSpeed[grid], 2.7f);
        }

        [Test]
        public void SolveLeg_ReachesTheTargetAndKeepsTheKneeOnItsSide()
        {
            float3 hip = new float3(0f, 1f, 0f);
            quaternion upperLocal = quaternion.AxisAngle(new float3(1f, 0f, 0f), -0.3f);
            quaternion lowerLocal = quaternion.AxisAngle(new float3(1f, 0f, 0f), 0.6f);
            float3 down = new float3(0f, -0.45f, 0f);
            float3 Knee(quaternion upper) => hip + math.mul(upper, down);
            float3 Ankle(quaternion upper, quaternion lower) => Knee(upper) + math.mul(math.mul(upper, lower), down);
            float3 restKnee = Knee(upperLocal);
            float3 restAnkle = Ankle(upperLocal, lowerLocal);
            float restForward = math.dot(restKnee - (hip + restAnkle) * 0.5f, new float3(0f, 0f, 1f));
            float3 target = restAnkle + new float3(0.1f, 0.15f, -0.2f);

            float3 axis = default;
            bool hasAxis = false;
            quaternion upper = upperLocal, lower = lowerLocal;
            BasisMotionMatchingJob.SolveLeg(hip, restKnee, restAnkle, target, upperLocal, math.mul(upperLocal, lowerLocal), ref upper, ref lower, ref axis, ref hasAxis);
            Assert.Less(math.distance(Ankle(upper, lower), target), 1e-3f);
            float solvedForward = math.dot(Knee(upper) - (hip + target) * 0.5f, new float3(0f, 0f, 1f));
            Assert.AreEqual(math.sign(restForward), math.sign(solvedForward), "the knee should not flip to the other side");

            float3 outOfReach = hip + new float3(0f, -3f, 0f);
            upper = upperLocal;
            lower = lowerLocal;
            BasisMotionMatchingJob.SolveLeg(hip, restKnee, restAnkle, outOfReach, upperLocal, math.mul(upperLocal, lowerLocal), ref upper, ref lower, ref axis, ref hasAxis);
            Assert.Less(math.distance(Ankle(upper, lower), hip + new float3(0f, -0.9f * BasisMotionMatchingJob.MaxLegExtension, 0f)), 2e-3f, "clamps just short of full extension along the target line");
            Assert.IsTrue(math.all(math.isfinite(upper.value)) && math.all(math.isfinite(lower.value)));
        }

        [Test]
        public void FootLock_HoldsTheFootStillOnceItIsPlanted()
        {
            using var rig = new Rig();
            BasisLocoParams walk = Moving(0f, 1.8f);
            rig.Run(in walk, 1f);
            rig.FootWorldPositions(out float3 lastLeft, out float3 lastRight);
            bool wasLeft = rig.State[0].LeftFoot.Locked, wasRight = rig.State[0].RightFoot.Locked;
            int lockedFrames = 0, unlocks = 0;
            float travel = 0f;
            const int Frames = 360;
            for (int i = 0; i < Frames; i++)
            {
                rig.Step(in walk);
                rig.FootWorldPositions(out float3 left, out float3 right);
                BasisMotionMatchingState state = rig.State[0];
                if (state.LeftFoot.Locked && wasLeft)
                {
                    travel += math.distance(left, lastLeft);
                    lockedFrames++;
                }
                if (state.RightFoot.Locked && wasRight)
                {
                    travel += math.distance(right, lastRight);
                    lockedFrames++;
                }
                unlocks += (wasLeft && !state.LeftFoot.Locked ? 1 : 0) + (wasRight && !state.RightFoot.Locked ? 1 : 0);
                wasLeft = state.LeftFoot.Locked;
                wasRight = state.RightFoot.Locked;
                lastLeft = left;
                lastRight = right;
            }
            float speedWhileLocked = travel / math.max(lockedFrames, 1) / Frame;
            TestContext.WriteLine($"Locked {lockedFrames} foot-frames of {Frames * 2}, {unlocks} unlocks, foot speed while locked {speedWhileLocked:F3} m/s");
            Assert.Greater(lockedFrames, Frames / 4, "the feet should spend real time planted");
            Assert.Less(speedWhileLocked, walk.VelocityZ * 0.5f, "a planted foot should move far slower than the body");
            Assert.Less(unlocks, Frames / 6, "the lock must not thrash");
        }

        [Test]
        public void ClipPhaseBake_FindsWhereEachCyclePlantsTheFootAndSkipsTheIdle()
        {
            using var rig = new Rig();
            // Every cycle on this rig plants at the same point, so alignment must not push them apart.
            float apart = math.abs(rig.Database.ClipPhaseOffset[0] - rig.Database.ClipPhaseOffset[8]);
            Assert.Less(math.min(apart, 1f - apart), 0.2f, "walking and running plant together here");
            Assert.AreEqual(0f, rig.Database.ClipPhaseOffset[BasisLocomotionGraph.JumpClip], "one-shots are never offset");
        }

        [Test]
        public void JointRateCap_BoundsAOneFramePopWithoutTouchingNormalMotion()
        {
            using var rig = new Rig();
            rig.Tuning.MaxJointDegreesPerSecond = 600f;
            rig.Run(Moving(0f, 1.8f), 0.5f);
            quaternion[] previous = rig.OutRotation.ToArray();
            BasisLocoParams jump = Moving(0f, 0f);
            jump.IsJumping = true;
            for (int frame = 0; frame < 30; frame++)
            {
                rig.Step(in jump);
                for (int node = 0; node < rig.NodeCount; node++)
                {
                    Assert.LessOrEqual(AngleDegrees(previous[node], rig.OutRotation[node]), 600f * Frame + 0.01f, $"node {node} frame {frame}");
                    previous[node] = rig.OutRotation[node];
                }
            }
        }

        [Test]
        public void HalfSizeAvatar_HalvesItsStridesAndStillPlantsItsFeet()
        {
            using var full = new Rig();
            using var half = new Rig(8, 0.5f);
            for (int clip = 0; clip < BasisLocomotionGraph.JumpClip; clip++)
            {
                if (clip == 9 || clip == 25 || clip == 34)
                {
                    continue;
                }
                float ratio = half.Database.ClipGroundSpeed[clip] / full.Database.ClipGroundSpeed[clip];
                Assert.AreEqual(0.5f, ratio, 0.1f, $"clip {clip}");
            }
            BasisLocoParams walk = Moving(0f, 0.6f);
            half.Run(in walk, 1f);
            int locked = 0;
            for (int i = 0; i < 180; i++)
            {
                half.Step(in walk);
                locked += half.State[0].LeftFoot.Locked || half.State[0].RightFoot.Locked ? 1 : 0;
            }
            Assert.Greater(locked, 30, "feet should plant at this avatar's walking speed");
        }

        [TestCase(45f)]
        [TestCase(72f)]
        [TestCase(144f)]
        public void AnyFrameRate_ReachesTheSameBlendAndStaysFinite(float rate)
        {
            using var rig = new Rig();
            BasisLocoParams walk = Moving(0f, 1.8f);
            float deltaTime = 1f / rate;
            int frames = (int)(2f * rate);
            for (int i = 0; i < frames; i++)
            {
                rig.Step(in walk, deltaTime);
                for (int node = 0; node < rig.NodeCount; node++)
                {
                    Assert.IsTrue(math.all(math.isfinite(rig.OutRotation[node].value)), $"node {node} frame {i}");
                }
            }
            Assert.Less(math.distance(rig.CurrentGridVelocity, new float2(0f, 1.8f)), BasisMotionMatchingDatabase.WalkingRingStep);
        }

        [Test]
        public void SolveLeg_NearlyStraightLegNeverReversesTheKnee()
        {
            float3 axis = new float3(1f, 0f, 0f);
            bool hasAxis = true;
            float3 hip = new float3(0f, 1f, 0f);
            // The knee sits a hair behind the hip-ankle line, so its raw plane points the opposite way.
            float3 knee = new float3(0f, 0.55f, -0.002f);
            float3 ankle = new float3(0f, 0.1f, 0f);
            quaternion upper = quaternion.identity, lower = quaternion.identity;
            BasisMotionMatchingJob.SolveLeg(hip, knee, ankle, ankle + new float3(0f, 0.05f, 0f), quaternion.identity, quaternion.identity, ref upper, ref lower, ref axis, ref hasAxis);
            Assert.AreEqual(1f, axis.x, 1e-5f, "noise in a straight leg must not replace the hinge axis");

            knee = new float3(0f, 0.55f, -0.2f);
            BasisMotionMatchingJob.SolveLeg(hip, knee, ankle, ankle + new float3(0f, 0.05f, 0f), quaternion.identity, quaternion.identity, ref upper, ref lower, ref axis, ref hasAxis);
            Assert.AreEqual(1f, axis.x, 1e-5f, "a hinge does not reverse, even when a bent reading disagrees");
        }

        [Test]
        public void FootLock_IsReleasedWhenStillOrProneOrAirborne()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 1.8f), 1f);
            rig.Run(Moving(0f, 0f), 1f);
            Assert.IsFalse(rig.State[0].LeftFoot.Locked || rig.State[0].RightFoot.Locked, "standing still belongs to the foot driver");

            BasisLocoParams prone = Moving(0f, 0.4f);
            prone.CrouchedState = true;
            prone.ProneState = true;
            rig.Run(in prone, 1f);
            Assert.IsFalse(rig.State[0].LeftFoot.Locked || rig.State[0].RightFoot.Locked, "prone");

            BasisLocoParams crouched = Moving(0f, 1.2f);
            crouched.CrouchedState = true;
            rig.Run(in crouched, 1f);
            rig.Run(Moving(0f, 1.8f), 1f);
            BasisLocoParams fall = Moving(0f, 1.8f);
            fall.IsFalling = true;
            rig.Run(in fall, 0.6f);
            Assert.AreEqual(BasisLocoState.Falling, rig.State[0].LocoState);
            Assert.IsFalse(rig.State[0].LeftFoot.Locked || rig.State[0].RightFoot.Locked, "airborne");
        }

        [Test]
        public void FootLock_ATeleportDoesNotDragTheFootAcrossTheWorld()
        {
            using var rig = new Rig();
            BasisLocoParams walk = Moving(0f, 1.8f);
            rig.Run(in walk, 1f);
            rig.RootWorld += new float3(50f, 0f, 0f);
            rig.Step(in walk);
            rig.FootWorldPositions(out float3 left, out float3 right);
            Assert.Less(math.distance(left, rig.RootWorld), 2f);
            Assert.Less(math.distance(right, rig.RootWorld), 2f);
            Assert.Less(math.length(rig.State[0].LeftFoot.OffsetWorld), 1f);
            Assert.Less(math.length(rig.State[0].RightFoot.OffsetWorld), 1f);
        }

        [Test]
        public void PredictVelocity_StartsAtCurrentAndSettlesOnDesired()
        {
            float2 start = new float2(3f, 0f), goal = new float2(0f, 2f);
            Assert.Less(math.distance(BasisMotionMatchingJob.PredictVelocity(start, goal, 0f), start), 1e-4f);
            Assert.Less(math.distance(BasisMotionMatchingJob.PredictVelocity(start, goal, 3f), goal), 1e-3f);
            Assert.Less(math.distance(BasisMotionMatchingJob.PredictVelocity(goal, goal, 0.4f), goal), 1e-4f);
        }

        [Test]
        public void DampVelocity_IsFrameRateIndependent()
        {
            float2 slow = float2.zero, slowAcceleration = float2.zero, fast = float2.zero, fastAcceleration = float2.zero, target = new float2(0f, 3f);
            for (int i = 0; i < 6; i++) BasisMotionMatchingJob.DampVelocity(ref slow, ref slowAcceleration, target, 0.1f, 1f / 30f);
            for (int i = 0; i < 24; i++) BasisMotionMatchingJob.DampVelocity(ref fast, ref fastAcceleration, target, 0.1f, 1f / 120f);
            Assert.Less(math.distance(slow, fast), 1e-3f);
        }

        [Test]
        public void NearestGrid_FindsTheEntryThatHoldsThatVelocity()
        {
            using var rig = new Rig();
            float[] angles = { 0f, 0.4f, 1.7f, 3.1f, 4.4f, 6.1f };
            float[] speeds = { 0f, 0.3f, 1.2f, 2.5f, 3.6f, 9f };
            foreach (float angle in angles)
            {
                foreach (float speed in speeds)
                {
                    float2 velocity = new float2(math.sin(angle), math.cos(angle)) * speed;
                    int found = BasisMotionMatchingDatabase.NearestGrid(BasisLocoState.Walking, velocity);
                    Assert.GreaterOrEqual(found, BasisMotionMatchingDatabase.WalkingGridStart);
                    Assert.Less(found, BasisMotionMatchingDatabase.WalkingGridStart + BasisMotionMatchingDatabase.WalkingGridCount);
                    float best = float.MaxValue;
                    for (int grid = 0; grid < BasisMotionMatchingDatabase.WalkingGridCount; grid++)
                    {
                        best = math.min(best, math.distance(rig.Database.GridVelocity[grid], velocity));
                    }
                    Assert.AreEqual(best, math.distance(rig.Database.GridVelocity[found], velocity), 0.35f, $"angle {angle} speed {speed}");
                }
            }
        }

        [Test]
        public void StanceSpeedAt_MatchesTheTableOnItsOwnEntries()
        {
            using var rig = new Rig();
            for (int grid = 0; grid < BasisMotionMatchingDatabase.WalkingGridCount; grid++)
            {
                float value = BasisMotionMatchingDatabase.StanceSpeedAt(BasisLocoState.Walking, rig.Database.GridVelocity[grid], rig.Database.GridStanceSpeed);
                Assert.AreEqual(rig.Database.GridStanceSpeed[grid], value, 1e-3f, $"grid {grid}");
            }
        }

        [TestCase(0f, 2.5f)]
        [TestCase(0f, 1.2f)]
        [TestCase(0f, 3.6f)]
        [TestCase(-1.4f, 0f)]
        [TestCase(1.8f, -1.8f)]
        public void SteadyInput_SettlesNearTheRequestedVelocity(float x, float z)
        {
            using var rig = new Rig();
            rig.Run(Moving(x, z), 2f);
            float error = math.distance(rig.CurrentGridVelocity, new float2(x, z));
            Assert.Less(error, BasisMotionMatchingDatabase.WalkingRingStep, $"settled on {rig.CurrentGridVelocity}");
        }

        [Test]
        public void SteadyWalk_NeverSwitches()
        {
            using var rig = new Rig();
            BasisLocoParams walk = Moving(0f, 2.5f);
            rig.Run(in walk, 2f);
            int transitions = rig.State[0].TransitionCount, searches = rig.State[0].PhaseSearchCount;
            rig.Run(in walk, 5f);
            Assert.AreEqual(transitions, rig.State[0].TransitionCount);
            Assert.AreEqual(searches, rig.State[0].PhaseSearchCount);
        }

        [Test]
        public void StickRamp_FollowsTheVelocityWithoutSteps()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 0f), 0.5f);
            int searches = rig.State[0].PhaseSearchCount;
            float2 last = rig.State[0].BlendVelocity;
            float largestStep = 0f;
            const float Seconds = 6f;
            int frames = (int)(Seconds / Frame);
            for (int frame = 0; frame < frames; frame++)
            {
                float t = frame * Frame / Seconds;
                float speed = 0.3f + 3.3f * math.sin(math.PI * t);
                rig.Step(Moving(math.sin(2f * math.PI * t) * speed, math.cos(2f * math.PI * t) * speed));
                largestStep = math.max(largestStep, math.distance(rig.State[0].BlendVelocity, last));
                last = rig.State[0].BlendVelocity;
            }
            // The old grid search moved 0.6 m/s in one frame at every ring change.
            Assert.Less(largestStep, 0.1f);
            Assert.LessOrEqual(rig.State[0].PhaseSearchCount, searches + 1, "a moving stride keeps its phase");
        }

        [Test]
        public void EveryStart_PicksItsPhaseOnce()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 0f), 0.5f);
            for (int start = 1; start <= 3; start++)
            {
                rig.Run(Moving(0f, 2.5f), 0.8f);
                Assert.AreEqual(start, rig.State[0].PhaseSearchCount);
                rig.Run(Moving(0f, 0f), 0.8f);
            }
        }

        [TestCase(0.25f)]
        [TestCase(3f)]
        public void AnyAvatarSize_StaysFiniteAndPlantsItsFeet(float worldScale)
        {
            using var rig = new Rig(8, worldScale);
            BasisLocoParams walk = Moving(0f, 1.2f * worldScale);
            rig.Run(in walk, 1f);
            int locked = 0;
            for (int i = 0; i < 180; i++)
            {
                rig.Step(in walk);
                locked += rig.State[0].LeftFoot.Locked || rig.State[0].RightFoot.Locked ? 1 : 0;
                for (int node = 0; node < rig.NodeCount; node++)
                {
                    Assert.IsTrue(math.all(math.isfinite(rig.OutRotation[node].value)), $"node {node}");
                }
            }
            Assert.Greater(locked, 30);
        }

        [Test]
        public void Stopping_LetsTheRaisedFootLandBeforeFadingToIdle()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 2.5f), 1.2f);
            float held = 0f;
            for (int i = 0; i < 90; i++)
            {
                rig.Step(Moving(0f, 0f));
                held = math.max(held, rig.State[0].StopTimer);
            }
            Assert.Greater(held, 0f, "stopping should have been noticed");
            Assert.AreEqual(BasisMotionMatchingDatabase.WalkingGridStart, rig.State[0].Grid, "the hold must not stop it reaching idle");
        }

        [Test]
        public void FootTargets_FollowTheLockForTheLegSolve()
        {
            using var rig = new Rig();
            BasisLocoParams walk = Moving(0f, 1.8f);
            rig.Run(in walk, 1f);
            int engaged = 0;
            for (int i = 0; i < 180; i++)
            {
                rig.Step(in walk);
                BasisMotionMatchingFootLock foot = rig.State[0].LeftFoot;
                Assert.GreaterOrEqual(foot.TargetWeight, 0f);
                Assert.LessOrEqual(foot.TargetWeight, 1f);
                Assert.IsTrue(math.all(math.isfinite(foot.TargetWorld)));
                if (foot.Locked && foot.TargetWeight >= 1f)
                {
                    engaged++;
                    Assert.Less(math.distance(foot.TargetWorld, foot.LockWorld + foot.OffsetWorld), 1e-4f);
                }
            }
            Assert.Greater(engaged, 20);
            rig.Run(Moving(0f, 0f), 1.5f);
            Assert.AreEqual(0f, rig.State[0].LeftFoot.TargetWeight, "standing still belongs to the foot driver");
        }

        [Test]
        public void InertializationOff_SnapsStateChangesAndLeavesNoOffset()
        {
            float LargestStep(bool enabled)
            {
                using var rig = new Rig();
                rig.Inertialization = enabled;
                rig.Tuning.MaxJointDegreesPerSecond = 0f;
                rig.Run(Moving(0f, 1.8f), 1f);
                quaternion[] previous = rig.OutRotation.ToArray();
                BasisLocoParams jump = Moving(0f, 0f);
                jump.IsJumping = true;
                float largest = 0f;
                for (int frame = 0; frame < 30; frame++)
                {
                    rig.Step(in jump);
                    for (int node = 0; node < rig.NodeCount; node++)
                    {
                        largest = math.max(largest, AngleDegrees(previous[node], rig.OutRotation[node]));
                        previous[node] = rig.OutRotation[node];
                    }
                }
                if (!enabled)
                {
                    Assert.AreEqual(float.MaxValue, rig.State[0].InertializeTime);
                }
                return largest;
            }
            Assert.Greater(LargestStep(false), LargestStep(true) * 2f, "turning it off should be a visible cut");
        }

        [Test]
        public void TheIdleKeepsItsOwnClockWhileWalking()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 0.4f), 1f);
            float before = rig.State[0].IdleTime;
            rig.Run(Moving(0f, 0.4f), 1f);
            Assert.AreEqual(1f, rig.State[0].IdleTime - before, 0.02f);
        }

        [Test]
        public void SuddenDirectionChange_StaysContinuousThenLandsOnTheTargetPose()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 3.6f), 1.5f);
            quaternion[] previous = rig.OutRotation.ToArray();
            BasisLocoParams reversed = Moving(0f, -3.6f);
            float largestStep = 0f;
            for (int frame = 0; frame < 45; frame++)
            {
                rig.Step(in reversed);
                for (int node = 0; node < rig.NodeCount; node++)
                {
                    largestStep = math.max(largestStep, AngleDegrees(previous[node], rig.OutRotation[node]));
                    previous[node] = rig.OutRotation[node];
                }
            }
            Assert.Less(rig.State[0].BlendVelocity.y, 0f, "the blend should have reversed by now");
            // At 90 fps an unblended cut between opposite strides would step far more than this.
            Assert.Less(largestStep, 20f);

            rig.Run(Moving(0f, 0f), 2.5f);
            for (int node = 0; node < rig.NodeCount; node++)
            {
                Assert.Less(AngleDegrees(rig.OffsetRotation[node], quaternion.identity), 1f, $"node {node} offset should decay away");
            }
        }

        [Test]
        public void Stopping_ReturnsToIdle()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 2.5f), 1f);
            rig.Run(Moving(0f, 0f), 1f);
            Assert.AreEqual(BasisMotionMatchingDatabase.WalkingGridStart, rig.State[0].Grid);
        }

        [Test]
        public void PushingIntoAWall_FollowsTheBodyNotTheStick()
        {
            using var rig = new Rig();
            BasisLocoParams wall = Moving(0f, 0f);
            wall.DesiredVelocityZ = 3.6f;
            rig.Run(in wall, 1.5f);
            Assert.AreEqual(BasisMotionMatchingDatabase.WalkingGridStart, rig.State[0].Grid);
        }

        [Test]
        public void StartingToMove_AnticipatesFromDesiredBeforeTheBodyCatchesUp()
        {
            using var rig = new Rig();
            rig.Run(Moving(0f, 0f), 0.5f);
            BasisLocoParams starting = Moving(0f, 0f);
            starting.DesiredVelocityZ = 3f;
            rig.Run(in starting, 0.05f);
            Assert.Greater(rig.State[0].BlendVelocity.y, 0.1f, "the prediction should already pull toward the stick");
        }

        [Test]
        public void CrouchAndProne_OnlySearchTheirOwnStateRange()
        {
            using var rig = new Rig();
            BasisLocoParams crouched = Moving(0f, 1.2f);
            crouched.CrouchedState = true;
            rig.Run(in crouched, 1f);
            Assert.AreEqual(BasisLocoState.Crouching, rig.State[0].LocoState);
            Assert.GreaterOrEqual(rig.State[0].Grid, BasisMotionMatchingDatabase.CrouchingGridStart);
            Assert.Less(rig.State[0].Grid, BasisMotionMatchingDatabase.ProneGridStart);

            BasisLocoParams prone = Moving(0f, 0.4f);
            prone.CrouchedState = true;
            prone.ProneState = true;
            rig.Run(in prone, 1f);
            Assert.AreEqual(BasisLocoState.Prone, rig.State[0].LocoState);
            Assert.GreaterOrEqual(rig.State[0].Grid, BasisMotionMatchingDatabase.ProneGridStart);
        }

        [Test]
        public void JumpFallLand_PlayEachOneShotAndHoldLandingLongEnoughToSee()
        {
            using var rig = new Rig();
            BasisLocoParams jump = Moving(0f, 0f);
            jump.IsJumping = true;
            rig.Step(in jump);
            Assert.AreEqual(BasisLocoState.Jump, rig.State[0].LocoState);

            BasisLocoParams fall = Moving(0f, 0f);
            fall.IsFalling = true;
            rig.Run(in fall, 0.6f);
            Assert.AreEqual(BasisLocoState.Falling, rig.State[0].LocoState);

            BasisLocoParams grounded = Moving(0f, 0f);
            rig.Latch[0] = 1;
            rig.Step(in grounded);
            Assert.AreEqual(BasisLocoState.Landing, rig.State[0].LocoState);
            Assert.AreEqual(0, rig.Latch[0], "the landing trigger is consumed");

            // The graph's crossfade would still be showing the landing clip here.
            rig.Run(in grounded, 0.2f);
            Assert.AreEqual(BasisLocoState.Landing, rig.State[0].LocoState, "landing must hold for its dwell");
            rig.Run(in grounded, 0.1f);
            Assert.AreEqual(BasisLocoState.Walking, rig.State[0].LocoState);
        }

        [Test]
        public void ZeroDeltaTime_FreezesTheCycleWithoutNaNs()
        {
            using var rig = new Rig();
            BasisLocoParams walk = Moving(0f, 2.5f);
            rig.Run(in walk, 0.5f);
            BasisMotionMatchingState before = rig.State[0];
            quaternion[] pose = rig.OutRotation.ToArray();
            for (int i = 0; i < 5; i++)
            {
                rig.Step(in walk, 0f);
            }
            Assert.AreEqual(before.Norm, rig.State[0].Norm);
            Assert.AreEqual(before.Grid, rig.State[0].Grid);
            for (int node = 0; node < rig.NodeCount; node++)
            {
                Assert.IsTrue(math.all(math.isfinite(rig.OutRotation[node].value)), $"node {node}");
                Assert.Less(AngleDegrees(pose[node], rig.OutRotation[node]), 0.5f, $"node {node}");
            }
        }

        [Test]
        public void PoseJob_StillSamplesAndWritesScaleAfterTheSamplerSplit()
        {
            using var rig = new Rig();
            using var contributions = new NativeArray<BasisLocoContribution>(new[] { new BasisLocoContribution { Clip = 0, Weight = 1f, Time = 0.25f } }, Allocator.TempJob);
            using var count = new NativeArray<int>(new[] { 1 }, Allocator.TempJob);
            new BasisLocomotionPoseJob
            {
                Contributions = contributions,
                ContributionCount = count,
                SnapshotScales = rig.Bake.SnapshotScales,
                Sampler = rig.Bake.CreateSampler(rig.Rest),
                OutLocalPosition = rig.OutPosition,
                OutLocalRotation = rig.OutRotation,
                OutLocalScale = rig.OutScale,
            }.Run();
            float x = 0.25f * (SamplesPerClip - 1);
            int a = (int)x;
            int rotationBase = rig.Bake.ClipRotationOffset[0];
            quaternion expected = math.slerp(rig.Bake.Rotations[rotationBase + a * rig.NodeCount + LeftUpper], rig.Bake.Rotations[rotationBase + (a + 1) * rig.NodeCount + LeftUpper], x - a);
            Assert.Less(AngleDegrees(expected, rig.OutRotation[LeftUpper]), 0.01f);
            Assert.AreEqual(new float3(1f), rig.OutScale[LeftUpper]);
            Assert.AreEqual(rig.Rest[LeftUpper], rig.OutPosition[LeftUpper]);
        }

        [Test]
        public void WorstCaseFrame_StaysCheap()
        {
            using var rig = new Rig(60);
            // Falling back to rest and leaving it again each cycle keeps the search and inertialization running.
            BasisLocoParams[] inputs = { Moving(0f, 3.6f), Moving(0f, 0f) };
            rig.Run(inputs[0], 0.2f);
            const int Frames = 2000;
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < Frames; i++)
            {
                rig.CreateJob(in inputs[(i / 20) % inputs.Length], 0.1f).Run();
            }
            timer.Stop();
            double microseconds = timer.Elapsed.TotalMilliseconds * 1000.0 / Frames;
            TestContext.WriteLine($"Motion matching worst-case frame: {microseconds:F1} us ({BasisMotionMatchingDatabase.EntryCount} entries, burst enabled={BurstCompiler.IsEnabled}, {rig.NodeCount} nodes)");
            Assert.Less(microseconds, 2000.0);
        }

        /// <summary>What a fidelity measurement needs from either the graph path or the motion matching path.</summary>
        interface IPoseDriver
        {
            void Step(in BasisLocoParams input, float deltaTime);
            void Reset();
            NativeArray<float3> Positions { get; }
            NativeArray<quaternion> Rotations { get; }
        }

        struct SteadyMetrics
        {
            public float PlantedSpeed, FootAcceleration, ContactSpeed;
        }
        struct ScriptMetrics
        {
            public float WorstStepRate, MeanAngularAcceleration, WorstFootSpeed;
        }

        /// <summary>
        /// Planted speed: mean world speed of the slower foot, which should be near zero. Foot acceleration: RMS change
        /// of foot velocity, which reads as jitter when high.
        /// </summary>
        static SteadyMetrics MeasureSteady(IPoseDriver driver, Rig rig, BasisLocoParams input, float deltaTime, float seconds)
        {
            driver.Reset();
            int settle = (int)math.ceil(1f / deltaTime);
            for (int i = 0; i < settle; i++)
            {
                driver.Step(in input, deltaTime);
            }
            rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 lastLeft, out float3 lastRight);
            float3 lastLeftVelocity = float3.zero, lastRightVelocity = float3.zero;
            int frames = (int)math.ceil(seconds / deltaTime);
            float planted = 0f, acceleration = 0f, contact = 0f;
            int contactFrames = 0;
            for (int i = 0; i < frames; i++)
            {
                driver.Step(in input, deltaTime);
                rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 left, out float3 right);
                float3 leftVelocity = (left - lastLeft) / deltaTime;
                float3 rightVelocity = (right - lastRight) / deltaTime;
                planted += math.min(math.length(leftVelocity), math.length(rightVelocity));
                // A run spends part of its cycle with both feet off the ground, where no foot can be planted at all.
                BasisMotionMatchingState motion = rig.State[0];
                int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
                byte flags = rig.Database.ContactFlags[motion.Grid * phases + (int)(math.frac(motion.Norm) * phases + 0.5f) % phases];
                bool leftContact = (flags & BasisMotionMatchingDatabase.LeftContactBit) != 0, rightContact = (flags & BasisMotionMatchingDatabase.RightContactBit) != 0;
                if (leftContact || rightContact)
                {
                    contact += math.min(leftContact ? math.length(leftVelocity) : float.MaxValue, rightContact ? math.length(rightVelocity) : float.MaxValue);
                    contactFrames++;
                }
                if (i > 0)
                {
                    float3 leftAcceleration = (leftVelocity - lastLeftVelocity) / deltaTime;
                    float3 rightAcceleration = (rightVelocity - lastRightVelocity) / deltaTime;
                    acceleration += math.lengthsq(leftAcceleration) + math.lengthsq(rightAcceleration);
                }
                lastLeft = left;
                lastRight = right;
                lastLeftVelocity = leftVelocity;
                lastRightVelocity = rightVelocity;
            }
            return new SteadyMetrics { PlantedSpeed = planted / frames, FootAcceleration = math.sqrt(acceleration / (2f * (frames - 1))), ContactSpeed = contactFrames > 0 ? contact / contactFrames : 0f };
        }
        /// <summary>Everything a player does in a few seconds, with rates so different frame rates are comparable.</summary>
        static ScriptMetrics MeasureScript(IPoseDriver driver, Rig rig, float deltaTime)
        {
            BasisLocoParams[] script = { Moving(0f, 2.5f), Moving(0f, -2.5f), Moving(2.5f, 0f), Moving(0f, 0f), Moving(-1.8f, 1.8f), Moving(0f, 3.6f), Moving(0f, 0f), Moving(-2.5f, 0f), Moving(0f, 1.2f) };
            driver.Reset();
            driver.Step(script[0], deltaTime);
            int nodes = rig.NodeCount;
            quaternion[] previous = driver.Rotations.ToArray();
            float[] previousRate = new float[nodes];
            rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 lastLeft, out float3 lastRight);
            float worstRate = 0f, worstFoot = 0f, acceleration = 0f;
            int samples = 0;
            int framesPerInput = (int)math.ceil(1f / deltaTime);
            foreach (BasisLocoParams input in script)
            {
                for (int frame = 0; frame < framesPerInput; frame++)
                {
                    driver.Step(in input, deltaTime);
                    for (int node = 0; node < nodes; node++)
                    {
                        float rate = AngleDegrees(previous[node], driver.Rotations[node]) / deltaTime;
                        worstRate = math.max(worstRate, rate);
                        acceleration += math.abs(rate - previousRate[node]) / deltaTime;
                        samples++;
                        previousRate[node] = rate;
                        previous[node] = driver.Rotations[node];
                    }
                    rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 left, out float3 right);
                    worstFoot = math.max(worstFoot, math.max(math.distance(left, lastLeft), math.distance(right, lastRight)) / deltaTime);
                    lastLeft = left;
                    lastRight = right;
                }
            }
            return new ScriptMetrics { WorstStepRate = worstRate, MeanAngularAcceleration = acceleration / samples, WorstFootSpeed = worstFoot };
        }

        /// <summary>An analog stick easing up to a run and around a circle: how motion usually changes in VR.</summary>
        static ScriptMetrics MeasureRamp(IPoseDriver driver, Rig rig, float deltaTime)
        {
            driver.Reset();
            driver.Step(Moving(0f, 0f), deltaTime);
            int nodes = rig.NodeCount;
            quaternion[] previous = driver.Rotations.ToArray();
            float[] previousRate = new float[nodes];
            rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 lastLeft, out float3 lastRight);
            float worstRate = 0f, worstFoot = 0f, acceleration = 0f;
            int samples = 0;
            const float Seconds = 8f;
            int frames = (int)math.ceil(Seconds / deltaTime);
            for (int frame = 0; frame < frames; frame++)
            {
                float t = frame * deltaTime / Seconds;
                float speed = 3.6f * math.sin(math.PI * t);
                float angle = 2f * math.PI * t;
                BasisLocoParams input = Moving(math.sin(angle) * speed, math.cos(angle) * speed);
                driver.Step(in input, deltaTime);
                for (int node = 0; node < nodes; node++)
                {
                    float rate = AngleDegrees(previous[node], driver.Rotations[node]) / deltaTime;
                    worstRate = math.max(worstRate, rate);
                    acceleration += math.abs(rate - previousRate[node]) / deltaTime;
                    samples++;
                    previousRate[node] = rate;
                    previous[node] = driver.Rotations[node];
                }
                rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 left, out float3 right);
                worstFoot = math.max(worstFoot, math.max(math.distance(left, lastLeft), math.distance(right, lastRight)) / deltaTime);
                lastLeft = left;
                lastRight = right;
            }
            return new ScriptMetrics { WorstStepRate = worstRate, MeanAngularAcceleration = acceleration / samples, WorstFootSpeed = worstFoot };
        }

        /// <summary>Bakes the shipped clips through the real baker and compares against the graph path they replace.</summary>
        [Test]
        public void StockClips_BeatTheGraphOnFidelity()
        {
            const string ControllerPath = "Packages/com.basis.sdk/Animator/BasisLocomotion.controller";
            const string CharacterPath = "Packages/com.basis.sdk/Animator/Animations/LocomotionAnimationsCharacter.fbx";
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
            Assert.IsNotNull(controller);
            Assert.IsNotNull(prefab);
            GameObject character = UnityEngine.Object.Instantiate(prefab);
            character.hideFlags = HideFlags.HideAndDontSave;
            var skeleton = new BasisPoseSkeleton();
            var baker = new BasisLocomotionPoseBaker();
            BasisLocomotionPoseBake bake = null;
            try
            {
                Assert.IsTrue(character.TryGetComponent(out Animator animator));
                Assert.IsTrue(animator.isHuman, "the locomotion character should import as humanoid");
                HumanBodyBones[] wanted =
                {
                    HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
                    HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                    HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
                };
                var bones = new List<Transform>();
                foreach (HumanBodyBones bone in wanted)
                {
                    Transform transform = animator.GetBoneTransform(bone);
                    if (transform != null)
                    {
                        bones.Add(transform);
                    }
                }
                skeleton.Build(animator.transform, bones);
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.IsTrue(baker.Start(animator, controller, skeleton.Nodes, hips));
                int guard = 0;
                while (!baker.Done && !baker.Failed && guard++ < 100000)
                {
                    baker.Tick();
                }
                Assert.IsTrue(baker.Done, "the stock clips should bake");
                bake = baker.TakeBake();

                int Node(HumanBodyBones bone) => Array.IndexOf(skeleton.Nodes, animator.GetBoneTransform(bone));
                var leftLeg = new BasisMotionMatchingLegNodes { Upper = Node(HumanBodyBones.LeftUpperLeg), Lower = Node(HumanBodyBones.LeftLowerLeg), Foot = Node(HumanBodyBones.LeftFoot) };
                var rightLeg = new BasisMotionMatchingLegNodes { Upper = Node(HumanBodyBones.RightUpperLeg), Lower = Node(HumanBodyBones.RightLowerLeg), Foot = Node(HumanBodyBones.RightFoot) };
                Transform frame = hips.parent;
                float4x4 frameToWorld = frame.localToWorldMatrix;
                float unitsPerMeter = math.length(((float4x4)frame.worldToLocalMatrix).c0.xyz);
                float3 up = frame.InverseTransformDirection(Vector3.up);

                var buildTimer = Stopwatch.StartNew();
                using var rig = new Rig(bake, skeleton.Stream.Parent, skeleton.Stream.RestLocalPosition, leftLeg, rightLeg, up, frameToWorld, unitsPerMeter);
                buildTimer.Stop();
                using var graph = new StockGraph(rig);
                TestContext.WriteLine($"Database build {buildTimer.Elapsed.TotalMilliseconds:F1} ms including runtime array setup, {rig.NodeCount} nodes");
                ReportStrides(rig);

                float[][] velocities =
                {
                    new[] { 0f, 1.2f }, new[] { 0f, 2.5f }, new[] { 0f, 3.6f }, new[] { 0f, -1.2f }, new[] { 0f, -2.5f },
                    new[] { 2.5f, 0f }, new[] { -2.5f, 0f }, new[] { 1.8f, 1.8f }, new[] { -1.8f, 1.8f }, new[] { 1.8f, -1.8f }, new[] { -1.8f, -1.8f },
                };
                float graphPlanted = 0f, motionPlanted = 0f, graphJitter = 0f, motionJitter = 0f;
                foreach (float[] velocity in velocities)
                {
                    BasisLocoParams input = Moving(velocity[0], velocity[1]);
                    SteadyMetrics g = MeasureSteady(graph, rig, input, Frame, 3f);
                    SteadyMetrics m = MeasureSteady(rig, rig, input, Frame, 3f);
                    TestContext.WriteLine($"({velocity[0],5:F1},{velocity[1],5:F1}): planted graph {g.PlantedSpeed:F3} mm {m.PlantedSpeed:F3} m/s, in contact {m.ContactSpeed:F3} | foot accel graph {g.FootAcceleration:F1} mm {m.FootAcceleration:F1} m/s2");
                    graphPlanted += g.PlantedSpeed;
                    motionPlanted += m.PlantedSpeed;
                    graphJitter += g.FootAcceleration;
                    motionJitter += m.FootAcceleration;
                }
                graphPlanted /= velocities.Length;
                motionPlanted /= velocities.Length;
                graphJitter /= velocities.Length;
                motionJitter /= velocities.Length;
                TestContext.WriteLine($"MEAN planted graph {graphPlanted:F3} mm {motionPlanted:F3} m/s | foot accel graph {graphJitter:F1} mm {motionJitter:F1} m/s2");

                ScriptMetrics graphRamp = MeasureRamp(graph, rig, Frame);
                ScriptMetrics motionRamp = MeasureRamp(rig, rig, Frame);
                TestContext.WriteLine($"Stick ramp: worst joint rate graph {graphRamp.WorstStepRate:F0} mm {motionRamp.WorstStepRate:F0} deg/s | mean joint accel graph {graphRamp.MeanAngularAcceleration:F0} mm {motionRamp.MeanAngularAcceleration:F0} deg/s2 | worst foot speed graph {graphRamp.WorstFootSpeed:F1} mm {motionRamp.WorstFootSpeed:F1} m/s");
                void Compare(string label)
                {
                    float planted = 0f, jitter = 0f;
                    foreach (float[] velocity in velocities)
                    {
                        SteadyMetrics m = MeasureSteady(rig, rig, Moving(velocity[0], velocity[1]), Frame, 3f);
                        planted += m.PlantedSpeed;
                        jitter += m.FootAcceleration;
                    }
                    ScriptMetrics script = MeasureScript(rig, rig, Frame);
                    TestContext.WriteLine($"{label}: planted {planted / velocities.Length:F3} m/s, foot accel {jitter / velocities.Length:F1} m/s2, worst joint rate {script.WorstStepRate:F0} deg/s");
                }
                rig.FootLock = false;
                Compare("Foot lock off");
                rig.FootLock = true;
                rig.Tuning.MaxJointDegreesPerSecond = 0f;
                Compare("Rate cap off");
                rig.Tuning = BasisMotionMatchingTuning.Default;

                float[] rates = { 45f, 72f, 90f, 120f, 144f };
                ScriptMetrics graphAt90 = default, motionAt90 = default;
                foreach (float rate in rates)
                {
                    ScriptMetrics g = MeasureScript(graph, rig, 1f / rate);
                    ScriptMetrics m = MeasureScript(rig, rig, 1f / rate);
                    TestContext.WriteLine($"{rate,4:F0} fps script: worst joint rate graph {g.WorstStepRate:F0} mm {m.WorstStepRate:F0} deg/s | mean joint accel graph {g.MeanAngularAcceleration:F0} mm {m.MeanAngularAcceleration:F0} deg/s2 | worst foot speed graph {g.WorstFootSpeed:F1} mm {m.WorstFootSpeed:F1} m/s");
                    if (rate == 90f)
                    {
                        graphAt90 = g;
                        motionAt90 = m;
                    }
                }

                BasisLocoParams idle = Moving(0f, 0f);
                rig.Reset();
                rig.Run(in idle, 0.5f);
                BasisLocoParams starting = Moving(0f, 2.5f);
                float responseSeconds = 0f;
                while (responseSeconds < 1f && math.length(rig.CurrentGridVelocity) < 1.2f)
                {
                    rig.Step(in starting);
                    responseSeconds += Frame;
                }
                TestContext.WriteLine($"Start response: {responseSeconds * 1000f:F0} ms until the walk blend is reached");
                ReportStop(rig, graph);

                // Calibrated on the shipped clips; see the printed values for current margins.
                Assert.Less(motionPlanted, graphPlanted * 0.75f, "motion matching should plant the feet clearly better than the graph");
                Assert.Less(motionJitter, graphJitter * 1.1f, "foot lock must not add noticeable jitter");
                Assert.Less(motionAt90.WorstStepRate, graphAt90.WorstStepRate * 0.4f, "motion matching should pop far less than the graph");
                Assert.Less(motionRamp.WorstStepRate, rig.Tuning.MaxJointDegreesPerSecond * 1.01f, "the rate cap must hold on a stick ramp");
                Assert.Less(responseSeconds, 0.3f, "the character must react promptly");

                // Misaligned plant phases show up as blended strides far below their speed.
                int directions = BasisMotionMatchingDatabase.WalkingDirections;
                for (int ring = 3; ring <= 5; ring++)
                {
                    int grid = BasisMotionMatchingDatabase.WalkingGridStart + 1 + (ring - 1) * directions;
                    float stride = rig.Database.GridStanceSpeed[grid] / rig.UnitsPerMeter;
                    float nominal = math.length(rig.Database.GridVelocity[grid]);
                    Assert.Greater(stride, nominal * 0.65f, $"forward blend at {nominal} m/s lost its stride");
                }
            }
            finally
            {
                baker.Dispose();
                bake?.Dispose();
                skeleton.Dispose();
                UnityEngine.Object.DestroyImmediate(character);
            }
        }
        /// <summary>Letting go of a run, both with an instant body stop and with the body coasting.</summary>
        static void ReportStop(Rig rig, StockGraph graph)
        {
            float SettledSpeed(float t, float seconds)
            {
                float remaining = math.max(0f, 1f - t / seconds);
                return 3.6f * remaining * remaining;
            }
            void Measure(string label, IPoseDriver driver, float settleSeconds)
            {
                driver.Reset();
                BasisLocoParams run = Moving(0f, 3.6f);
                for (int i = 0; i < 180; i++) driver.Step(in run, Frame);
                quaternion[] previous = driver.Rotations.ToArray();
                float[] previousRate = new float[rig.NodeCount];
                rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 lastLeft, out float3 lastRight);
                float worstRate = 0f, worstFoot = 0f, acceleration = 0f;
                int samples = 0;
                for (int frame = 0; frame < 135; frame++)
                {
                    float speed = settleSeconds > 0f ? SettledSpeed(frame * Frame, settleSeconds) : 0f;
                    BasisLocoParams input = Moving(0f, speed);
                    driver.Step(in input, Frame);
                    for (int node = 0; node < rig.NodeCount; node++)
                    {
                        float rate = AngleDegrees(previous[node], driver.Rotations[node]) / Frame;
                        worstRate = math.max(worstRate, rate);
                        acceleration += math.abs(rate - previousRate[node]) / Frame;
                        samples++;
                        previousRate[node] = rate;
                        previous[node] = driver.Rotations[node];
                    }
                    rig.FootWorldPositions(driver.Positions, driver.Rotations, out float3 left, out float3 right);
                    worstFoot = math.max(worstFoot, math.max(math.distance(left, lastLeft), math.distance(right, lastRight)) / Frame);
                    lastLeft = left;
                    lastRight = right;
                }
                TestContext.WriteLine($"Stop {label}: worst joint rate {worstRate:F0} deg/s, mean joint accel {acceleration / samples:F0} deg/s2, worst foot speed {worstFoot:F1} m/s");
            }
            Measure("instant graph", graph, 0f);
            Measure("instant mm", rig, 0f);
            Measure("settle 0.35s graph", graph, 0.35f);
            Measure("settle 0.35s mm", rig, 0.35f);
        }
        static void ReportStrides(Rig rig)
        {
            var d = rig.Database;
            TestContext.WriteLine($"Clip ground speeds: Walk {d.ClipGroundSpeed[0]:F2} (authored 1.20), WalkRight {d.ClipGroundSpeed[2]:F2} (1.39), Back {d.ClipGroundSpeed[4]:F2} (1.85), WalkLeft {d.ClipGroundSpeed[6]:F2} (1.39), Run {d.ClipGroundSpeed[8]:F2} (3.60), RunRight {d.ClipGroundSpeed[11]:F2} (3.60), RunBack {d.ClipGroundSpeed[13]:F2} (3.60), RunLeft {d.ClipGroundSpeed[15]:F2} (3.60), Crouch {d.ClipGroundSpeed[17]:F2} (1.20)");
            int directions = BasisMotionMatchingDatabase.WalkingDirections;
            for (int ring = 1; ring <= BasisMotionMatchingDatabase.WalkingRings; ring++)
            {
                int grid = BasisMotionMatchingDatabase.WalkingGridStart + 1 + (ring - 1) * directions;
                int back = grid + directions / 2;
                int diagonal = grid + directions * 5 / 8;
                TestContext.WriteLine($"Ring {math.length(rig.Database.GridVelocity[grid]):F1} m/s stride: forward {rig.Database.GridStanceSpeed[grid] / rig.UnitsPerMeter:F2} ({rig.Database.GridContributionCount[grid]} clips), side {rig.Database.GridStanceSpeed[grid + directions / 4] / rig.UnitsPerMeter:F2}, back {rig.Database.GridStanceSpeed[back] / rig.UnitsPerMeter:F2} ({rig.Database.GridContributionCount[back]} clips), back diagonal {rig.Database.GridStanceSpeed[diagonal] / rig.UnitsPerMeter:F2} ({rig.Database.GridContributionCount[diagonal]} clips)");
            }
        }

        /// <summary>Drives the shipped graph and pose jobs over the same bake, as the baseline.</summary>
        sealed class StockGraph : IDisposable, IPoseDriver
        {
            readonly Rig rig;
            NativeArray<BasisLocoContribution> contributions;
            NativeArray<BasisLocoSimState> sim;
            NativeArray<int> count;
            NativeArray<byte> latch;
            NativeArray<float2> positions;
            NativeArray<float> scales;
            NativeArray<float3> position, scale;
            NativeArray<quaternion> rotation;
            public NativeArray<float3> Positions => position;
            public NativeArray<quaternion> Rotations => rotation;
            public StockGraph(Rig rig)
            {
                this.rig = rig;
                contributions = new NativeArray<BasisLocoContribution>(BasisLocomotionGraph.MaxContributions, Allocator.Persistent);
                sim = new NativeArray<BasisLocoSimState>(new[] { BasisLocomotionGraph.DefaultSimState }, Allocator.Persistent);
                count = new NativeArray<int>(1, Allocator.Persistent);
                latch = new NativeArray<byte>(1, Allocator.Persistent);
                int children = BasisLocomotionGraph.ProneClipStart + BasisLocomotionGraph.ProneChildCount;
                positions = new NativeArray<float2>(children, Allocator.Persistent);
                scales = new NativeArray<float>(children, Allocator.Persistent);
                Copy(BasisLocomotionGraph.WalkingClipStart, BasisLocomotionGraph.WalkingChildPositions, BasisLocomotionGraph.WalkingChildTimeScales);
                Copy(BasisLocomotionGraph.CrouchingClipStart, BasisLocomotionGraph.CrouchingChildPositions, BasisLocomotionGraph.CrouchingChildTimeScales);
                Copy(BasisLocomotionGraph.ProneClipStart, BasisLocomotionGraph.ProneChildPositions, BasisLocomotionGraph.ProneChildTimeScales);
                position = new NativeArray<float3>(rig.NodeCount, Allocator.Persistent);
                scale = new NativeArray<float3>(rig.NodeCount, Allocator.Persistent);
                rotation = new NativeArray<quaternion>(rig.NodeCount, Allocator.Persistent);
            }
            void Copy(int start, float2[] sourcePositions, float[] sourceScales)
            {
                for (int i = 0; i < sourcePositions.Length; i++)
                {
                    positions[start + i] = sourcePositions[i];
                    scales[start + i] = sourceScales[i];
                }
            }
            public void Reset()
            {
                sim[0] = BasisLocomotionGraph.DefaultSimState;
            }
            public void Step(in BasisLocoParams input, float deltaTime)
            {
                rig.RootWorld += new float3(input.VelocityX, 0f, input.VelocityZ) * deltaTime;
                new BasisLocomotionGraphJob
                {
                    Params = input,
                    DeltaTime = deltaTime,
                    ClipLengths = rig.Bake.ClipLength,
                    ClipLooping = rig.Bake.ClipLooping,
                    ChildPositions = positions,
                    ChildTimeScales = scales,
                    Sim = sim,
                    Contributions = contributions,
                    ContributionCount = count,
                    LandingLatch = latch,
                }.Run();
                new BasisLocomotionPoseJob
                {
                    Contributions = contributions,
                    ContributionCount = count,
                    SnapshotScales = rig.Bake.SnapshotScales,
                    Sampler = rig.Bake.CreateSampler(rig.Rest),
                    OutLocalPosition = position,
                    OutLocalRotation = rotation,
                    OutLocalScale = scale,
                }.Run();
            }
            public void Dispose()
            {
                contributions.Dispose();
                sim.Dispose();
                count.Dispose();
                latch.Dispose();
                positions.Dispose();
                scales.Dispose();
                position.Dispose();
                scale.Dispose();
                rotation.Dispose();
            }
        }
    }
}
