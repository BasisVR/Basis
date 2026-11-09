using Basis.Scripts.BasisSdk.Contacts;
using Basis.Scripts.Contacts;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using static Basis.Scripts.Contacts.BasisContactSystem;

namespace Basis.Tests.Contacts
{
    public class BasisContactSystemTests
    {
        private const float Tolerance = 1e-4f;
        private const int LocalIndex = 0, RemoteIndex = 1, LocalId = 7, RemoteId = 42;
        private const byte AllowAll = 1 | 2 | 4, OnAvatar = 8, HapticZone = 16;
        private const byte SideLeft = 1, SideRight = 2;

        private readonly System.Collections.Generic.List<System.IDisposable> tracked = new System.Collections.Generic.List<System.IDisposable>();

        private T Track<T>(T value) where T : System.IDisposable
        {
            tracked.Add(value);
            return value;
        }

        [TearDown]
        public void DisposeTracked()
        {
            for (int i = 0; i < tracked.Count; i++) tracked[i].Dispose();
            tracked.Clear();
        }

        private struct SenderSpec
        {
            public float3 A, B;
            public float Radius;
            public int Owner;
            public int[] Tags;
            public byte Side, Flags;
        }

        private Result EvaluateOne(Shape receiver, byte flags, int[] receiverTags, SenderSpec[] senders, byte allowedRemote = 1, byte contactsFromOthers = 1, byte hapticsFromOthers = 1, byte hapticsFromSelf = 1, byte side = 0)
        {
            int shapeCount = senders.Length + 1;
            var shapes = Track(new NativeArray<Shape>(shapeCount, Allocator.TempJob));
            var senderMetas = Track(new NativeArray<SenderMeta>(Mathf.Max(1, senders.Length), Allocator.TempJob));
            var players = Track(new NativeArray<PlayerState>(2, Allocator.TempJob));
            var idToSlot = Track(new NativeArray<short>(MaxPlayerId, Allocator.TempJob));
            for (int i = 0; i < MaxPlayerId; i++) idToSlot[i] = -1;
            idToSlot[LocalId] = LocalIndex;
            idToSlot[RemoteId] = RemoteIndex;
            players[LocalIndex] = new PlayerState { Id = LocalId, IsLocal = 1, Allowed = 1 };
            players[RemoteIndex] = new PlayerState { Id = RemoteId, IsLocal = 0, Allowed = allowedRemote };
            var tagList = new System.Collections.Generic.List<int>();
            int receiverTagOffset = tagList.Count;
            if (receiverTags != null) tagList.AddRange(receiverTags);
            shapes[0] = receiver;
            var groupList = new System.Collections.Generic.List<Group>();
            int previousOwner = int.MinValue;
            for (int i = 0; i < senders.Length; i++)
            {
                shapes[i + 1] = new Shape { A = senders[i].A, B = senders[i].B, Radius = senders[i].Radius };
                int offset = tagList.Count;
                if (senders[i].Tags != null) tagList.AddRange(senders[i].Tags);
                senderMetas[i] = new SenderMeta { Shape = i + 1, TagOffset = offset, TagCount = senders[i].Tags?.Length ?? 0, Side = senders[i].Side, Flags = senders[i].Flags, Source = SourceBody };
                if (senders[i].Owner != previousOwner)
                {
                    groupList.Add(new Group { Start = i, Count = 0, OwnerId = senders[i].Owner == LocalIndex ? LocalId : senders[i].Owner == RemoteIndex ? RemoteId : WorldOwner });
                    previousOwner = senders[i].Owner;
                }
                Group last = groupList[groupList.Count - 1];
                last.Count++;
                groupList[groupList.Count - 1] = last;
            }
            var tags = Track(new NativeArray<int>(Mathf.Max(1, tagList.Count), Allocator.TempJob));
            for (int i = 0; i < tagList.Count; i++) tags[i] = tagList[i];
            var groups = Track(new NativeArray<Group>(Mathf.Max(1, groupList.Count), Allocator.TempJob));
            for (int i = 0; i < groupList.Count; i++) groups[i] = groupList[i];
            var bounds = Track(new NativeArray<GroupBounds>(Mathf.Max(1, groupList.Count), Allocator.TempJob));
            var receivers = Track(new NativeArray<ReceiverMeta>(1, Allocator.TempJob));
            receivers[0] = new ReceiverMeta { Shape = 0, Source = SourceBody, TagOffset = receiverTagOffset, TagCount = receiverTags?.Length ?? 0, Flags = flags, Side = side };
            var results = Track(new NativeArray<Result>(1, Allocator.TempJob));
            new GroupBoundsJob { Groups = groups, Senders = senderMetas, BodyShapes = shapes, AuthoredShapes = shapes, Bounds = bounds }.Run(groupList.Count);
            new EvaluateJob
            {
                Receivers = receivers,
                Senders = senderMetas,
                Groups = groups,
                Bounds = bounds,
                BodyShapes = shapes,
                AuthoredShapes = shapes,
                Tags = tags,
                IdToSlot = idToSlot,
                SlotStates = players,
                Settings = new Settings { ContactsFromOthers = contactsFromOthers, HapticsFromOthers = hapticsFromOthers, HapticsFromSelf = hapticsFromSelf },
                GroupCount = groupList.Count,
                Results = results,
            }.Run(1);
            return results[0];
        }

        private static Shape Sphere(float3 p, float r) => new Shape { A = p, B = p, Radius = r };

        private static SenderSpec Sender(float3 p, float r, int owner, int[] tags = null, byte side = 0, byte flags = 0) => new SenderSpec { A = p, B = p, Radius = r, Owner = owner, Tags = tags, Side = side, Flags = flags };

        [Test]
        public void SegmentDistance_MatchesSdkMath()
        {
            float3[] points = { new float3(0, 0, 0), new float3(1, 0, 0), new float3(0.5f, 1, 0), new float3(0.5f, -1, 0.3f), new float3(2, 2, 2), new float3(2, 2, 2) };
            for (int i = 0; i + 3 < points.Length; i++)
            {
                float burst = SegmentDistanceSquared(points[i], points[i + 1], points[i + 2], points[i + 3]);
                float sdk = BasisContacts.SegmentDistanceSquared(points[i], points[i + 1], points[i + 2], points[i + 3]);
                Assert.AreEqual(sdk, burst, Tolerance);
            }
            Assert.AreEqual(0.09f, SegmentDistanceSquared(new float3(0, 0, 0), new float3(1, 0, 0), new float3(0.5f, 0.3f, -1), new float3(0.5f, 0.3f, 1)), Tolerance);
            Assert.AreEqual(4f, SegmentDistanceSquared(new float3(0, 0, 0), new float3(0, 0, 0), new float3(2, 0, 0), new float3(2, 0, 0)), Tolerance);
        }

        [Test]
        public void Proximity_IsOneAtCentreAndZeroAtEdge()
        {
            Assert.AreEqual(1f, BasisContacts.Proximity(0f, 0.1f, 0.05f), Tolerance);
            Assert.AreEqual(0f, BasisContacts.Proximity(0.15f, 0.1f, 0.05f), Tolerance);
            Assert.AreEqual(0.5f, BasisContacts.Proximity(0.075f, 0.1f, 0.05f), Tolerance);
        }

        [Test]
        public void RemoteSender_InReach_TouchesWithProximity()
        {
            Result r = EvaluateOne(Sphere(0, 0.1f), AllowAll | OnAvatar, null, new[] { Sender(new float3(0.05f, 0, 0), 0.05f, RemoteIndex) });
            Assert.AreEqual(1, r.Touched);
            Assert.AreEqual(RemoteId, r.Player);
            Assert.AreEqual(1f - 0.05f / 0.15f, r.Value, Tolerance);
        }

        [Test]
        public void RemoteSender_OutOfReach_DoesNotTouch()
        {
            Result r = EvaluateOne(Sphere(0, 0.1f), AllowAll | OnAvatar, null, new[] { Sender(new float3(0.2f, 0, 0), 0.05f, RemoteIndex) });
            Assert.AreEqual(0, r.Touched);
            Assert.AreEqual(BasisContacts.NoPlayer, r.Player);
        }

        [Test]
        public void BlockedOrInteractionDisabledPlayer_NeverTouches()
        {
            Result r = EvaluateOne(Sphere(0, 0.1f), AllowAll | OnAvatar, null, new[] { Sender(0, 0.05f, RemoteIndex) }, allowedRemote: 0);
            Assert.AreEqual(0, r.Touched);
        }

        [Test]
        public void ContactsFromOthersOff_GatesAvatarReceiversOnly()
        {
            SenderSpec[] senders = { Sender(0, 0.05f, RemoteIndex) };
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.1f), AllowAll | OnAvatar, null, senders, contactsFromOthers: 0).Touched);
            Assert.AreEqual(1, EvaluateOne(Sphere(0, 0.1f), AllowAll, null, senders, contactsFromOthers: 0).Touched);
        }

        [Test]
        public void AllowFlags_FilterSelfOthersAndWorld()
        {
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.1f), 2 | 4, null, new[] { Sender(0, 0.05f, LocalIndex) }).Touched);
            Assert.AreEqual(1, EvaluateOne(Sphere(0, 0.1f), 1, null, new[] { Sender(0, 0.05f, LocalIndex) }).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.1f), 1 | 4, null, new[] { Sender(0, 0.05f, RemoteIndex) }).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.1f), 1 | 2, null, new[] { Sender(0, 0.05f, -1) }).Touched);
            Result world = EvaluateOne(Sphere(0, 0.1f), 4, null, new[] { Sender(0, 0.05f, -1) });
            Assert.AreEqual(1, world.Touched);
            Assert.AreEqual(BasisContacts.NoPlayer, world.Player);
        }

        [Test]
        public void Tags_MustOverlapWhenReceiverHasAny()
        {
            SenderSpec[] hand = { Sender(0, 0.05f, RemoteIndex, new[] { 3, 4 }) };
            Assert.AreEqual(1, EvaluateOne(Sphere(0, 0.1f), AllowAll, new[] { 9, 4 }, hand).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.1f), AllowAll, new[] { 9 }, hand).Touched);
            Assert.AreEqual(1, EvaluateOne(Sphere(0, 0.1f), AllowAll, null, hand).Touched);
        }

        [Test]
        public void BestPlayer_IsTheDeepestContact()
        {
            SenderSpec[] senders = { Sender(new float3(0.12f, 0, 0), 0.05f, LocalIndex), Sender(new float3(0.01f, 0, 0), 0.05f, RemoteIndex) };
            Result r = EvaluateOne(Sphere(0, 0.1f), AllowAll, null, senders);
            Assert.AreEqual(RemoteId, r.Player);
            Assert.AreEqual(1f - 0.01f / 0.15f, r.Value, Tolerance);
        }

        [Test]
        public void HapticZone_OwnHandOnSameArm_IsIgnored()
        {
            const byte bodyHand = 1 | 2;
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, new[] { Sender(0, 0.045f, LocalIndex, null, SideLeft, bodyHand) }, side: SideLeft).Touched);
            Assert.AreEqual(1, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, new[] { Sender(0, 0.045f, LocalIndex, null, SideRight, bodyHand) }, side: SideLeft).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, new[] { Sender(0, 0.045f, LocalIndex, null, SideRight, bodyHand) }, hapticsFromSelf: 0, side: SideLeft).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, new[] { Sender(0, 0.1f, LocalIndex, null, 0, 1) }).Touched);
        }

        [Test]
        public void HapticZone_OthersFollowTheirOwnSwitch()
        {
            SenderSpec[] remote = { Sender(0, 0.05f, RemoteIndex) };
            Assert.AreEqual(1, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, remote, contactsFromOthers: 0).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, remote, hapticsFromOthers: 0).Touched);
            Assert.AreEqual(0, EvaluateOne(Sphere(0, 0.05f), OnAvatar | HapticZone, null, remote, allowedRemote: 0).Touched);
        }

        [Test]
        public void NearJob_GapIsDistanceBeyondTheClosestProbe()
        {
            var positions = Track(new NativeArray<float3>(3, Allocator.TempJob));
            var probes = Track(new NativeArray<Probe>(2, Allocator.TempJob));
            var gaps = Track(new NativeArray<float>(3, Allocator.TempJob));
            positions[0] = new float3(1, 0, 0);
            positions[1] = new float3(10, 0, 0);
            positions[2] = new float3(100, 0, 0);
            probes[0] = new Probe { Center = float3.zero, Radius = 4f };
            probes[1] = new Probe { Center = new float3(12, 0, 0), Radius = 1f };
            new NearJob { Positions = positions, Probes = probes, ProbeCount = 2, Gap = gaps }.Run(3);
            Assert.AreEqual(-3f, gaps[0], Tolerance);
            Assert.AreEqual(1f, gaps[1], Tolerance);
            Assert.AreEqual(87f, gaps[2], Tolerance);
        }

        [Test]
        public void UnslottedPlayer_IsSkippedEvenWhenTouching()
        {
            var shapes = Track(new NativeArray<Shape>(2, Allocator.TempJob));
            shapes[0] = Sphere(0, 0.1f);
            shapes[1] = Sphere(0, 0.1f);
            var senders = Track(new NativeArray<SenderMeta>(1, Allocator.TempJob));
            senders[0] = new SenderMeta { Shape = 1, Source = SourceBody };
            var groups = Track(new NativeArray<Group>(1, Allocator.TempJob));
            groups[0] = new Group { Start = 0, Count = 1, OwnerId = 999 };
            var bounds = Track(new NativeArray<GroupBounds>(1, Allocator.TempJob));
            var receivers = Track(new NativeArray<ReceiverMeta>(1, Allocator.TempJob));
            receivers[0] = new ReceiverMeta { Shape = 0, Source = SourceBody, Flags = AllowAll };
            var idToSlot = Track(new NativeArray<short>(MaxPlayerId, Allocator.TempJob));
            for (int i = 0; i < MaxPlayerId; i++) idToSlot[i] = -1;
            var states = Track(new NativeArray<PlayerState>(1, Allocator.TempJob));
            var tags = Track(new NativeArray<int>(1, Allocator.TempJob));
            var results = Track(new NativeArray<Result>(1, Allocator.TempJob));
            new GroupBoundsJob { Groups = groups, Senders = senders, BodyShapes = shapes, AuthoredShapes = shapes, Bounds = bounds }.Run(1);
            new EvaluateJob { Receivers = receivers, Senders = senders, Groups = groups, Bounds = bounds, BodyShapes = shapes, AuthoredShapes = shapes, Tags = tags, IdToSlot = idToSlot, SlotStates = states, Settings = new Settings { ContactsFromOthers = 1, HapticsFromOthers = 1, HapticsFromSelf = 1 }, GroupCount = 1, Results = results }.Run(1);
            Assert.AreEqual(0, results[0].Touched);
        }

        [Test]
        public void Scale_ThousandPlayersStaysWithinBudget()
        {
            const int players = 1000, worldProbes = 100, slotsUsed = 64, receiverCount = 217;
            var positions = Track(new NativeArray<float3>(players, Allocator.TempJob));
            var probes = Track(new NativeArray<Probe>(worldProbes + 1, Allocator.TempJob));
            var gaps = Track(new NativeArray<float>(players, Allocator.TempJob));
            var random = new Unity.Mathematics.Random(1234);
            for (int i = 0; i < players; i++) positions[i] = random.NextFloat3(new float3(-60, 0, -60), new float3(60, 2, 60));
            for (int i = 0; i <= worldProbes; i++) probes[i] = new Probe { Center = random.NextFloat3(new float3(-60, 0, -60), new float3(60, 2, 60)), Radius = 4.5f };
            new NearJob { Positions = positions, Probes = probes, ProbeCount = worldProbes + 1, Gap = gaps }.Schedule(players, 64).Complete();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            new NearJob { Positions = positions, Probes = probes, ProbeCount = worldProbes + 1, Gap = gaps }.Schedule(players, 64).Complete();
            double nearMs = watch.Elapsed.TotalMilliseconds;

            int senderCount = slotsUsed * ShapesPerSlot;
            var shapes = Track(new NativeArray<Shape>(senderCount + receiverCount, Allocator.TempJob));
            var senderMetas = Track(new NativeArray<SenderMeta>(senderCount, Allocator.TempJob));
            var groups = Track(new NativeArray<Group>(slotsUsed, Allocator.TempJob));
            var bounds = Track(new NativeArray<GroupBounds>(slotsUsed, Allocator.TempJob));
            var states = Track(new NativeArray<PlayerState>(slotsUsed, Allocator.TempJob));
            var idToSlot = Track(new NativeArray<short>(MaxPlayerId, Allocator.TempJob));
            for (int i = 0; i < MaxPlayerId; i++) idToSlot[i] = -1;
            for (int s = 0; s < slotsUsed; s++)
            {
                float3 root = random.NextFloat3(new float3(-3, 0, -3), new float3(3, 0, 3));
                for (int k = 0; k < ShapesPerSlot; k++)
                {
                    float3 p = root + random.NextFloat3(new float3(-0.4f, 0, -0.4f), new float3(0.4f, 1.8f, 0.4f));
                    shapes[s * ShapesPerSlot + k] = new Shape { A = p, B = p + new float3(0, 0.1f, 0), Radius = 0.05f };
                    senderMetas[s * ShapesPerSlot + k] = new SenderMeta { Shape = s * ShapesPerSlot + k, Source = SourceBody, Flags = SenderBody };
                }
                groups[s] = new Group { Start = s * ShapesPerSlot, Count = ShapesPerSlot, OwnerId = 100 + s };
                idToSlot[100 + s] = (short)s;
                states[s] = new PlayerState { Id = 100 + s, IsLocal = (byte)(s == 0 ? 1 : 0), Allowed = 1 };
            }
            var receivers = Track(new NativeArray<ReceiverMeta>(receiverCount, Allocator.TempJob));
            for (int r = 0; r < receiverCount; r++)
            {
                float3 p = random.NextFloat3(new float3(-3, 0, -3), new float3(3, 2, 3));
                shapes[senderCount + r] = new Shape { A = p, B = p, Radius = 0.1f };
                receivers[r] = new ReceiverMeta { Shape = senderCount + r, Source = SourceBody, Flags = AllowAll };
            }
            var tags = Track(new NativeArray<int>(1, Allocator.TempJob));
            var results = Track(new NativeArray<Result>(receiverCount, Allocator.TempJob));
            JobHandle Run()
            {
                JobHandle b = new GroupBoundsJob { Groups = groups, Senders = senderMetas, BodyShapes = shapes, AuthoredShapes = shapes, Bounds = bounds }.Schedule(slotsUsed, 8);
                return new EvaluateJob { Receivers = receivers, Senders = senderMetas, Groups = groups, Bounds = bounds, BodyShapes = shapes, AuthoredShapes = shapes, Tags = tags, IdToSlot = idToSlot, SlotStates = states, Settings = new Settings { ContactsFromOthers = 1, HapticsFromOthers = 1, HapticsFromSelf = 1 }, GroupCount = slotsUsed, Results = results }.Schedule(receiverCount, 4, b);
            }
            Run().Complete();
            watch.Restart();
            Run().Complete();
            double evaluateMs = watch.Elapsed.TotalMilliseconds;
            Debug.Log($"BasisContacts scale: near {nearMs:F3} ms for {players} players x {worldProbes + 1} probes, evaluate {evaluateMs:F3} ms for {receiverCount} receivers x {slotsUsed} players");
            Assert.Less(nearMs, 20.0);
            Assert.Less(evaluateMs, 20.0);
        }

        [Test]
        public void BuildShapes_AuthoredCapsuleScalesWithTransform()
        {
            var descs = Track(new NativeArray<ShapeDesc>(1, Allocator.TempJob));
            var matrices = Track(new NativeArray<float4x4>(1, Allocator.TempJob));
            var valid = Track(new NativeArray<byte>(1, Allocator.TempJob));
            var shapes = Track(new NativeArray<Shape>(1, Allocator.TempJob));
            matrices[0] = float4x4.TRS(new float3(1, 2, 3), quaternion.identity, new float3(2f));
            valid[0] = 1;
            descs[0] = new ShapeDesc { Kind = 1, T0 = 0, T1 = -1, T2 = -1, T3 = -1, T4 = -1, ScaleHead = -1, ScaleHips = -1, Center = new float3(0, 0.5f, 0), Radius = 0.1f, Height = 0.6f };
            new BuildShapesJob { Descs = descs, Matrices = matrices, Valid = valid, Assigned = valid, Shapes = shapes }.Run(1);
            Assert.AreEqual(0.2f, shapes[0].Radius, Tolerance);
            Assert.AreEqual(3f - 0.4f, shapes[0].A.y, Tolerance);
            Assert.AreEqual(3f + 0.4f, shapes[0].B.y, Tolerance);
        }

        [Test]
        public void BuildShapes_ChestLeftZoneSitsFrontLeft()
        {
            var descs = Track(new NativeArray<ShapeDesc>(1, Allocator.TempJob));
            var matrices = Track(new NativeArray<float4x4>(6, Allocator.TempJob));
            var valid = Track(new NativeArray<byte>(6, Allocator.TempJob));
            var shapes = Track(new NativeArray<Shape>(1, Allocator.TempJob));
            float3[] bones = { new float3(0, 1, 0), new float3(0, 1.4f, 0), new float3(0, 1.55f, 0), new float3(-0.2f, 1.45f, 0), new float3(0.2f, 1.45f, 0), new float3(0, 1.55f, 0) };
            for (int i = 0; i < bones.Length; i++)
            {
                matrices[i] = float4x4.Translate(bones[i]);
                valid[i] = 1;
            }
            descs[0] = new ShapeDesc { Kind = 5, T0 = 0, T1 = 1, T2 = 2, T3 = 3, T4 = 4, ScaleHead = 5, ScaleHips = 0, Radius = 0.08f, K0 = 1f, K1 = 0.9f, K2 = -0.45f };
            new BuildShapesJob { Descs = descs, Matrices = matrices, Valid = valid, Assigned = valid, Shapes = shapes }.Run(1);
            Assert.Less(shapes[0].A.x, 0f);
            Assert.Greater(shapes[0].A.z, 0f);
            Assert.AreEqual(1.4f, shapes[0].A.y, Tolerance);
            Assert.AreEqual(-0.09f, shapes[0].A.x, Tolerance);
            Assert.AreEqual(0.08f, shapes[0].Radius, Tolerance);
        }

        [Test]
        public void BuildShapes_InvalidTransform_DisablesShape()
        {
            var descs = Track(new NativeArray<ShapeDesc>(1, Allocator.TempJob));
            var matrices = Track(new NativeArray<float4x4>(1, Allocator.TempJob));
            var valid = Track(new NativeArray<byte>(1, Allocator.TempJob));
            var shapes = Track(new NativeArray<Shape>(1, Allocator.TempJob));
            descs[0] = new ShapeDesc { Kind = 2, T0 = 0, T1 = -1, T2 = -1, T3 = -1, T4 = -1, ScaleHead = -1, ScaleHips = -1, Radius = 0.1f };
            new BuildShapesJob { Descs = descs, Matrices = matrices, Valid = valid, Assigned = valid, Shapes = shapes }.Run(1);
            Assert.Less(shapes[0].Radius, 0f);
        }

        [Test]
        public void ReadBodyTransforms_SkipsUnassignedAndUnwantedSides()
        {
            int count = BonesPerSlot * 2;
            GameObject root = new GameObject("ContactsReadTest");
            try
            {
                Transform[] bones = new Transform[count];
                for (int i = 0; i < count; i++)
                {
                    bones[i] = new GameObject("Bone" + i).transform;
                    bones[i].SetParent(root.transform);
                    bones[i].position = new Vector3(i, 0, 0);
                }
                var access = Track(new TransformAccessArray(bones));
                var assigned = Track(new NativeArray<byte>(count, Allocator.TempJob));
                var matrices = Track(new NativeArray<float4x4>(count, Allocator.TempJob));
                var valid = Track(new NativeArray<byte>(count, Allocator.TempJob));
                for (int i = 0; i < count; i++) assigned[i] = i == 1 ? (byte)0 : (byte)1;
                new ReadBodyTransformsJob { Assigned = assigned, ReadLocal = 1, ReadRemote = 0, Matrices = matrices, Valid = valid }.ScheduleReadOnly(access, 8).Complete();
                Assert.AreEqual(1, valid[0]);
                Assert.AreEqual(0, valid[1]);
                Assert.AreEqual(0, valid[BonesPerSlot]);
                Assert.AreEqual(0f, matrices[0].c3.x, Tolerance);
                new ReadBodyTransformsJob { Assigned = assigned, ReadLocal = 0, ReadRemote = 1, Matrices = matrices, Valid = valid }.ScheduleReadOnly(access, 8).Complete();
                Assert.AreEqual(0, valid[0]);
                Assert.AreEqual(1, valid[BonesPerSlot]);
                Assert.AreEqual(BonesPerSlot, matrices[BonesPerSlot].c3.x, Tolerance);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BodyHaptics_SubmitAppliesCapAndNotifies()
        {
            var output = new RecordingOutput();
            bool enabled = BasisBodyHaptics.Enabled;
            float cap = BasisBodyHaptics.IntensityCap;
            BasisBodyHaptics.RegisterOutput(output);
            try
            {
                BasisBodyHaptics.IntensityCap = 0.5f;
                float[] values = new float[BasisBodyHaptics.ZoneCount];
                values[(int)BasisBodyHapticZone.ChestLeft] = 1f;
                BasisBodyHaptics.Submit(values);
                Assert.AreEqual(1, output.Calls);
                Assert.AreEqual(0.5f, output.Last[(int)BasisBodyHapticZone.ChestLeft], Tolerance);
                BasisBodyHaptics.Submit(values);
                Assert.AreEqual(1, output.Calls);
                BasisBodyHaptics.ClearAll();
                Assert.AreEqual(2, output.Calls);
                Assert.AreEqual(0f, output.Last[(int)BasisBodyHapticZone.ChestLeft]);
            }
            finally
            {
                BasisBodyHaptics.UnregisterOutput(output);
                BasisBodyHaptics.IntensityCap = cap;
                BasisBodyHaptics.Enabled = enabled;
                BasisBodyHaptics.ClearAll();
            }
        }

        private sealed class RecordingOutput : IBasisBodyHapticsOutput
        {
            public int Calls;
            public float[] Last = new float[BasisBodyHaptics.ZoneCount];

            public void OnBodyHapticsChanged(float[] intensities)
            {
                Calls++;
                System.Array.Copy(intensities, Last, intensities.Length);
            }
        }
    }
}
