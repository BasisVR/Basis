using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Contacts;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Scripts.Contacts
{
    [NoAutoStaticsCleanup]
    public static class BasisContactSystem
    {
        public const float BodyReferenceTorso = 0.55f;
        public const float NearRefreshSeconds = 0.1f;
        public const float NearRadius = 4f;
        public const float NearExitMargin = 1f;
        public const float AuthoredRetrySeconds = 0.5f;
        public const int BonesPerSlot = 24;
        public const int ShapesPerSlot = 8;
        public const int InitialSlots = 16;
        public const int MaxSlots = 128;
        public const int MaxAuthoredTransforms = 16384;
        public const int MaxPlayerId = 65536;
        public const int BuiltinTagCount = 14;
        public static int MaxNearPlayers = 64;
        public static bool ContactsFromOthers = true;
        public const byte SourceBody = 0, SourceAuthored = 1;
        public const byte KindAuthoredSphere = 0, KindAuthoredCapsule = 1, KindBoneSphere = 2, KindBoneCapsule = 3, KindPalm = 4, KindTorsoZone = 5;
        public const byte SenderBody = 1, SenderSelfHapticOk = 2;
        public const byte ReceiverAllowSelf = 1, ReceiverAllowOthers = 2, ReceiverAllowWorld = 4, ReceiverOnAvatar = 8, ReceiverHapticZone = 16;
        public const byte SideNone = 0, SideLeft = 1, SideRight = 2;
        public const int WorldOwner = -1;
        private const int BHips = 0, BHead = 1, BNeck = 2, BChest = 3, BLUpperArm = 4, BLLowerArm = 5, BLHand = 6, BLKnuckle = 7, BLTip = 8, BLTipParent = 9;
        private const int BRUpperArm = 10, BRLowerArm = 11, BRHand = 12, BRKnuckle = 13, BRTip = 14, BRTipParent = 15;
        private const int BLUpperLeg = 16, BLLowerLeg = 17, BLFoot = 18, BLToe = 19, BRUpperLeg = 20, BRLowerLeg = 21, BRFoot = 22, BRToe = 23;
        private static readonly ProfilerMarker tickMarker = new ProfilerMarker("BasisContacts.Tick");
        private static readonly ProfilerMarker nearMarker = new ProfilerMarker("BasisContacts.NearRefresh");
        private static readonly ProfilerMarker rebuildMarker = new ProfilerMarker("BasisContacts.RebuildAuthored");
        private static readonly ProfilerMarker applyMarker = new ProfilerMarker("BasisContacts.Apply");

        public struct ShapeDesc
        {
            public byte Kind;
            public int T0, T1, T2, T3, T4, ScaleHead, ScaleHips;
            public float3 Center;
            public float Radius, Height, K0, K1, K2;
        }

        public struct Shape
        {
            public float3 A, B;
            public float Radius;
        }

        public struct SenderMeta
        {
            public int Shape, TagOffset, TagCount;
            public byte Source, Side, Flags;
        }

        public struct Group
        {
            public int Start, Count, OwnerId;
        }

        public struct GroupBounds
        {
            public float3 Center;
            public float Radius;
        }

        public struct ReceiverMeta
        {
            public int Shape, TagOffset, TagCount;
            public byte Source, Side, Flags;
        }

        public struct PlayerState
        {
            public int Id;
            public byte IsLocal, Allowed;
        }

        public struct Result
        {
            public float Value;
            public int Player;
            public byte Touched;
        }

        public struct Settings
        {
            public byte ContactsFromOthers, HapticsFromOthers, HapticsFromSelf;
        }

        public struct Probe
        {
            public float3 Center;
            public float Radius;
        }

        [BurstCompile]
        public struct ReadTransformsJob : IJobParallelForTransform
        {
            [WriteOnly] public NativeArray<float4x4> Matrices;
            [WriteOnly] public NativeArray<byte> Valid;

            public void Execute(int index, TransformAccess transform)
            {
                if (!transform.isValid)
                {
                    Valid[index] = 0;
                    Matrices[index] = float4x4.identity;
                    return;
                }
                Valid[index] = 1;
                Matrices[index] = transform.localToWorldMatrix;
            }
        }

        [BurstCompile]
        public struct ReadBodyTransformsJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<byte> Assigned;
            public byte ReadLocal, ReadRemote;
            [WriteOnly] public NativeArray<float4x4> Matrices;
            [WriteOnly] public NativeArray<byte> Valid;

            public void Execute(int index, TransformAccess transform)
            {
                if (Assigned[index] == 0 || (index < BonesPerSlot ? ReadLocal : ReadRemote) == 0 || !transform.isValid)
                {
                    Valid[index] = 0;
                    return;
                }
                Valid[index] = 1;
                Matrices[index] = transform.localToWorldMatrix;
            }
        }

        [BurstCompile]
        public struct BuildShapesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ShapeDesc> Descs;
            [ReadOnly] public NativeArray<float4x4> Matrices;
            [ReadOnly] public NativeArray<byte> Valid;
            [ReadOnly] public NativeArray<byte> Assigned;
            [WriteOnly] public NativeArray<Shape> Shapes;

            private bool Has(int index) => index >= 0 && Valid[index] != 0 && Assigned[index] != 0;
            private float3 Pos(int index) => Matrices[index].c3.xyz;

            public void Execute(int index)
            {
                ShapeDesc desc = Descs[index];
                Shape shape = new Shape { Radius = -1f };
                if (!Has(desc.T0))
                {
                    Shapes[index] = shape;
                    return;
                }
                float scale = 1f;
                if (Has(desc.ScaleHead) && Has(desc.ScaleHips))
                {
                    scale = math.clamp(math.distance(Pos(desc.ScaleHead), Pos(desc.ScaleHips)) / BodyReferenceTorso, 0.1f, 10f);
                }
                switch (desc.Kind)
                {
                    case KindAuthoredSphere:
                    case KindAuthoredCapsule:
                        {
                            float4x4 m = Matrices[desc.T0];
                            float uniform = math.max(math.length(m.c0.xyz), math.max(math.length(m.c1.xyz), math.length(m.c2.xyz)));
                            float radius = math.clamp(desc.Radius, 0f, BasisContacts.MaxRadius);
                            float3 center = math.mul(m, new float4(desc.Center, 1f)).xyz;
                            shape.Radius = radius * uniform;
                            shape.A = center;
                            shape.B = center;
                            if (desc.Kind == KindAuthoredCapsule)
                            {
                                float half = math.max(0f, math.clamp(desc.Height, 0f, BasisContacts.MaxHeight) * 0.5f - radius) * uniform;
                                float3 up = math.normalizesafe(m.c1.xyz) * half;
                                shape.A = center - up;
                                shape.B = center + up;
                            }
                            break;
                        }
                    case KindBoneSphere:
                        {
                            float3 p = Pos(desc.T0);
                            if (Has(desc.T1))
                            {
                                p += math.normalizesafe(p - Pos(desc.T1)) * desc.K0 * scale;
                            }
                            shape.A = p;
                            shape.B = p;
                            shape.Radius = desc.Radius * scale;
                            break;
                        }
                    case KindBoneCapsule:
                        {
                            shape.A = Pos(desc.T0);
                            shape.B = Has(desc.T1) ? Pos(desc.T1) : shape.A;
                            shape.Radius = desc.Radius * scale;
                            break;
                        }
                    case KindPalm:
                        {
                            float3 p = Has(desc.T1) ? math.lerp(Pos(desc.T0), Pos(desc.T1), 0.5f) : Pos(desc.T0);
                            shape.A = p;
                            shape.B = p;
                            shape.Radius = desc.Radius * scale;
                            break;
                        }
                    case KindTorsoZone:
                        {
                            if (!Has(desc.T1) || !Has(desc.T2) || !Has(desc.T3) || !Has(desc.T4))
                            {
                                break;
                            }
                            float3 hips = Pos(desc.T0), left = Pos(desc.T3), right = Pos(desc.T4);
                            float3 across = math.normalizesafe(right - left);
                            float3 up = Pos(desc.T2) - hips;
                            up = math.normalizesafe(up - math.dot(up, across) * across);
                            float3 forward = math.cross(across, up);
                            float3 center = math.lerp(hips, Pos(desc.T1), desc.K0) + forward * (desc.K1 * 0.1f * scale) + across * (desc.K2 * 0.5f * math.distance(left, right));
                            shape.A = center;
                            shape.B = center;
                            shape.Radius = desc.Radius * scale;
                            break;
                        }
                }
                Shapes[index] = shape;
            }
        }

        [BurstCompile]
        public struct GroupBoundsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Group> Groups;
            [ReadOnly] public NativeArray<SenderMeta> Senders;
            [ReadOnly] public NativeArray<Shape> BodyShapes;
            [ReadOnly] public NativeArray<Shape> AuthoredShapes;
            [WriteOnly] public NativeArray<GroupBounds> Bounds;

            public void Execute(int index)
            {
                Group group = Groups[index];
                float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
                float radius = 0f;
                bool any = false;
                for (int k = group.Start; k < group.Start + group.Count; k++)
                {
                    SenderMeta sender = Senders[k];
                    Shape shape = sender.Source == SourceBody ? BodyShapes[sender.Shape] : AuthoredShapes[sender.Shape];
                    if (shape.Radius < 0f)
                    {
                        continue;
                    }
                    any = true;
                    min = math.min(min, math.min(shape.A, shape.B));
                    max = math.max(max, math.max(shape.A, shape.B));
                    radius = math.max(radius, shape.Radius);
                }
                Bounds[index] = any ? new GroupBounds { Center = (min + max) * 0.5f, Radius = math.length(max - min) * 0.5f + radius } : new GroupBounds { Radius = -1f };
            }
        }

        [BurstCompile]
        public struct EvaluateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ReceiverMeta> Receivers;
            [ReadOnly] public NativeArray<SenderMeta> Senders;
            [ReadOnly] public NativeArray<Group> Groups;
            [ReadOnly] public NativeArray<GroupBounds> Bounds;
            [ReadOnly] public NativeArray<Shape> BodyShapes;
            [ReadOnly] public NativeArray<Shape> AuthoredShapes;
            [ReadOnly] public NativeArray<int> Tags;
            [ReadOnly] public NativeArray<short> IdToSlot;
            [ReadOnly] public NativeArray<PlayerState> SlotStates;
            public Settings Settings;
            public int GroupCount;
            [WriteOnly] public NativeArray<Result> Results;

            public void Execute(int index)
            {
                ReceiverMeta receiver = Receivers[index];
                Shape receiverShape = receiver.Source == SourceBody ? BodyShapes[receiver.Shape] : AuthoredShapes[receiver.Shape];
                Result result = new Result { Player = BasisContacts.NoPlayer };
                if (receiverShape.Radius < 0f)
                {
                    Results[index] = result;
                    return;
                }
                bool haptic = (receiver.Flags & ReceiverHapticZone) != 0, onAvatar = (receiver.Flags & ReceiverOnAvatar) != 0;
                bool touched = false;
                float best = 0f;
                int bestPlayer = BasisContacts.NoPlayer;
                for (int g = 0; g < GroupCount; g++)
                {
                    Group group = Groups[g];
                    if (group.Count == 0)
                    {
                        continue;
                    }
                    bool local = false;
                    int playerId = BasisContacts.NoPlayer;
                    if (group.OwnerId < 0)
                    {
                        if (haptic ? Settings.HapticsFromOthers == 0 : (receiver.Flags & ReceiverAllowWorld) == 0)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        int slot = group.OwnerId < IdToSlot.Length ? IdToSlot[group.OwnerId] : -1;
                        if (slot < 0)
                        {
                            continue;
                        }
                        PlayerState player = SlotStates[slot];
                        playerId = player.Id;
                        local = player.IsLocal != 0;
                        if (local)
                        {
                            if (haptic ? Settings.HapticsFromSelf == 0 : (receiver.Flags & ReceiverAllowSelf) == 0)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            if (player.Allowed == 0)
                            {
                                continue;
                            }
                            if (haptic ? Settings.HapticsFromOthers == 0 : ((receiver.Flags & ReceiverAllowOthers) == 0 || (onAvatar && Settings.ContactsFromOthers == 0)))
                            {
                                continue;
                            }
                        }
                    }
                    GroupBounds groupBounds = Bounds[g];
                    if (groupBounds.Radius < 0f)
                    {
                        continue;
                    }
                    float reachBounds = groupBounds.Radius + receiverShape.Radius;
                    if (PointSegmentDistanceSquared(groupBounds.Center, receiverShape.A, receiverShape.B) > reachBounds * reachBounds)
                    {
                        continue;
                    }
                    for (int k = group.Start; k < group.Start + group.Count; k++)
                    {
                        SenderMeta sender = Senders[k];
                        Shape senderShape = sender.Source == SourceBody ? BodyShapes[sender.Shape] : AuthoredShapes[sender.Shape];
                        if (senderShape.Radius < 0f)
                        {
                            continue;
                        }
                        if (haptic && local && (sender.Flags & SenderBody) != 0)
                        {
                            if ((sender.Flags & SenderSelfHapticOk) == 0 || (receiver.Side != SideNone && sender.Side == receiver.Side))
                            {
                                continue;
                            }
                        }
                        if (!TagsMatch(receiver, sender))
                        {
                            continue;
                        }
                        float reach = receiverShape.Radius + senderShape.Radius;
                        float distanceSquared = SegmentDistanceSquared(receiverShape.A, receiverShape.B, senderShape.A, senderShape.B);
                        if (distanceSquared > reach * reach)
                        {
                            continue;
                        }
                        float proximity = reach > 0f ? math.saturate(1f - math.sqrt(distanceSquared) / reach) : 1f;
                        if (!touched || proximity > best)
                        {
                            best = proximity;
                            bestPlayer = playerId;
                        }
                        touched = true;
                    }
                }
                result.Value = best;
                result.Player = bestPlayer;
                result.Touched = touched ? (byte)1 : (byte)0;
                Results[index] = result;
            }

            private bool TagsMatch(ReceiverMeta receiver, SenderMeta sender)
            {
                if (receiver.TagCount == 0)
                {
                    return true;
                }
                for (int a = receiver.TagOffset; a < receiver.TagOffset + receiver.TagCount; a++)
                {
                    int tag = Tags[a];
                    for (int b = sender.TagOffset; b < sender.TagOffset + sender.TagCount; b++)
                    {
                        if (Tags[b] == tag)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        [BurstCompile]
        public struct NearJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<Probe> Probes;
            public int ProbeCount;
            [WriteOnly] public NativeArray<float> Gap;

            public void Execute(int index)
            {
                float3 position = Positions[index];
                float gap = float.MaxValue;
                for (int p = 0; p < ProbeCount; p++)
                {
                    Probe probe = Probes[p];
                    gap = math.min(gap, math.distance(position, probe.Center) - probe.Radius);
                }
                Gap[index] = gap;
            }
        }

        public static float PointSegmentDistanceSquared(float3 point, float3 a, float3 b)
        {
            float3 ab = b - a;
            float lengthSquared = math.lengthsq(ab);
            float t = lengthSquared > 1e-8f ? math.saturate(math.dot(point - a, ab) / lengthSquared) : 0f;
            return math.distancesq(point, a + ab * t);
        }

        public static float SegmentDistanceSquared(float3 p1, float3 q1, float3 p2, float3 q2)
        {
            float3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = math.dot(d1, d1), e = math.dot(d2, d2), f = math.dot(d2, r);
            float s, t;
            const float epsilon = 1e-8f;
            if (a <= epsilon && e <= epsilon)
            {
                return math.lengthsq(r);
            }
            if (a <= epsilon)
            {
                s = 0f;
                t = math.saturate(f / e);
            }
            else
            {
                float c = math.dot(d1, r);
                if (e <= epsilon)
                {
                    t = 0f;
                    s = math.saturate(-c / a);
                }
                else
                {
                    float b = math.dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > epsilon ? math.saturate((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = math.saturate(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = math.saturate((b - c) / a);
                    }
                }
            }
            return math.distancesq(p1 + d1 * s, p2 + d2 * t);
        }

        public static int BodyShapeIndex(int slot, int shape) => BasisBodyHaptics.ZoneCount + slot * ShapesPerSlot + shape;

        private sealed class Slot
        {
            public int Id = -1;
            public BasisRemotePlayer Remote;
            public BasisAvatar Avatar;
            public Transform Hips, Head;
            public bool IsLocal, Used;
        }

        private struct Candidate
        {
            public int Id;
            public float Gap;
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            public int Compare(Candidate a, Candidate b) => a.Gap.CompareTo(b.Gap);
        }

        private struct AuthoredGroup
        {
            public int OwnerId, Start, Count;
        }

        private static readonly CandidateComparer candidateComparer = new CandidateComparer();
        private static readonly List<Slot> slots = new List<Slot>();
        private static readonly Dictionary<int, int> slotById = new Dictionary<int, int>();
        private static readonly List<int> freeSlots = new List<int>();
        private static readonly Transform[] boneScratch = new Transform[BonesPerSlot];
        private static readonly List<Transform> authoredTransforms = new List<Transform>();
        private static readonly Dictionary<Transform, int> authoredTransformIndex = new Dictionary<Transform, int>();
        private static readonly List<ShapeDesc> authoredDescs = new List<ShapeDesc>();
        private static readonly List<SenderMeta> authoredSenders = new List<SenderMeta>();
        private static readonly List<AuthoredGroup> authoredGroups = new List<AuthoredGroup>();
        private static readonly List<int> authoredTags = new List<int>();
        private static readonly List<ReceiverMeta> receiverList = new List<ReceiverMeta>();
        private static readonly List<SenderMeta> senderList = new List<SenderMeta>();
        private static readonly List<Group> groupList = new List<Group>();
        private static readonly List<int> tagPool = new List<int>();
        private static readonly List<BasisContactReceiver> receiverSnapshot = new List<BasisContactReceiver>();
        private static readonly List<BasisContactReceiver> previousSnapshot = new List<BasisContactReceiver>();
        private static readonly HashSet<BasisContactReceiver> snapshotSet = new HashSet<BasisContactReceiver>();
        private static readonly Dictionary<int, List<BasisContactSender>> sendersByOwner = new Dictionary<int, List<BasisContactSender>>();
        private static readonly Stack<List<BasisContactSender>> senderListPool = new Stack<List<BasisContactSender>>();
        private static readonly Dictionary<string, int> tagIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<BasisContactReceiver, float> pulseUntil = new Dictionary<BasisContactReceiver, float>();
        private static readonly List<Probe> probeList = new List<Probe>();
        private static readonly List<Candidate> candidates = new List<Candidate>();
        private static readonly HashSet<int> keepIds = new HashSet<int>();
        private static readonly List<int> releaseScratch = new List<int>();
        private static readonly int[] zoneReceiver = new int[BasisBodyHaptics.ZoneCount];
        private static readonly float[] zoneScratch = new float[BasisBodyHaptics.ZoneCount];
        private static int[] nearIds = Array.Empty<int>();
        private static TransformAccessArray bodyAccess, authoredAccess;
        private static NativeArray<float4x4> bodyMatrices, authoredMatrices;
        private static NativeArray<byte> bodyValid, bodyAssigned, authoredValid;
        private static NativeArray<ShapeDesc> bodyDescs, nativeAuthoredDescs;
        private static NativeArray<Shape> bodyShapes, authoredShapes;
        private static NativeArray<SenderMeta> nativeSenders;
        private static NativeArray<Group> nativeGroups;
        private static NativeArray<GroupBounds> bounds;
        private static NativeArray<ReceiverMeta> nativeReceivers;
        private static NativeArray<int> nativeTags;
        private static NativeArray<short> idToSlot;
        private static NativeArray<PlayerState> slotStates;
        private static NativeArray<Result> results;
        private static NativeArray<float3> nearPositions;
        private static NativeArray<Probe> nearProbes;
        private static NativeArray<float> nearGaps;
        private static JobHandle readHandle, evaluateHandle, nearHandle;
        private static readonly HashSet<string> builtinTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BasisContactTags.Head, BasisContactTags.Torso, BasisContactTags.Hand, BasisContactTags.HandLeft, BasisContactTags.HandRight, BasisContactTags.Finger,
            BasisContactTags.FingerLeft, BasisContactTags.FingerRight, BasisContactTags.Foot, BasisContactTags.FootLeft, BasisContactTags.FootRight,
        };
        private static readonly List<BasisContactReceiver> worldProbeReceivers = new List<BasisContactReceiver>();
        private static bool scheduled, readPending, nearPending, active, layoutDirty, authoredDirty, builtHaptics;
        private static bool wantsSelfBody, wantsWorldOthersBody, wantsAvatarOthersBody, wantsAvatarOthers, hasRemoteAuthored, bodySelf, bodyOthers, relevantCached;
        private static int builtVersion = -1, relevantVersion = -1;
        private static BasisAvatar relevantAvatar;
        private static int nearCount, slotCapacity, authoredReceiverCount, hapticCount, unresolvedOwners;
        private static float nearRefreshAt, authoredRetryAt;
        private static GameObject dummyObject;
        private static Transform dummy;

        public static bool IsActive => active;
        public static int ActiveSlotCount => slotById.Count;
        public static int SlotCapacity => slotCapacity;
        public static int AuthoredTransformCount => authoredTransforms.Count;
        public static int ReceiverCount => receiverList.Count;

        public static void FrameTick()
        {
            using (tickMarker.Auto())
            {
                if (scheduled)
                {
                    evaluateHandle.Complete();
                    readPending = false;
                    scheduled = false;
                    using (applyMarker.Auto())
                    {
                        ApplyResults();
                    }
                }
                bool haptics = BasisBodyHaptics.Enabled;
                if (!haptics && !HasRelevantReceivers())
                {
                    if (active)
                    {
                        Deactivate();
                    }
                    return;
                }
                if (!active)
                {
                    Activate();
                }
                float now = Time.unscaledTime;
                if (nearPending)
                {
                    nearHandle.Complete();
                    nearPending = false;
                    using (nearMarker.Auto())
                    {
                        ApplyNear();
                    }
                }
                if (builtVersion != BasisContacts.Version || builtHaptics != haptics || (unresolvedOwners > 0 && now >= authoredRetryAt))
                {
                    using (rebuildMarker.Auto())
                    {
                        RebuildAuthored(haptics);
                    }
                    builtVersion = BasisContacts.Version;
                    builtHaptics = haptics;
                    authoredRetryAt = now + AuthoredRetrySeconds;
                    layoutDirty = true;
                }
                bool hapticOthers = haptics && BasisBodyHaptics.FromOthers;
                bodySelf = (haptics && BasisBodyHaptics.FromSelf) || wantsSelfBody;
                bodyOthers = hapticOthers || wantsWorldOthersBody || (wantsAvatarOthersBody && ContactsFromOthers);
                if (now >= nearRefreshAt)
                {
                    nearRefreshAt = now + NearRefreshSeconds;
                    using (nearMarker.Auto())
                    {
                        RefreshLocalSlot();
                        RefreshSlotBindings();
                        ScheduleNear(hapticOthers);
                    }
                }
                if (layoutDirty)
                {
                    RebuildLayout();
                    layoutDirty = false;
                }
                if (receiverList.Count == 0)
                {
                    return;
                }
                SyncSlots();
                Schedule();
            }
        }

        public static void CompleteTransformRead()
        {
            if (readPending)
            {
                readHandle.Complete();
                readPending = false;
            }
        }

        private static void Activate()
        {
            active = true;
            if (dummy == null)
            {
                dummyObject = new GameObject("BasisContactsUnused") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying)
                {
                    UnityEngine.Object.DontDestroyOnLoad(dummyObject);
                }
                dummy = dummyObject.transform;
            }
            if (!idToSlot.IsCreated)
            {
                idToSlot = new NativeArray<short>(MaxPlayerId, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int index = 0; index < MaxPlayerId; index++)
                {
                    idToSlot[index] = -1;
                }
            }
            EnsureBuiltinTags();
            EnsureSlotCapacity(InitialSlots);
            builtVersion = -1;
            nearRefreshAt = 0f;
            layoutDirty = true;
        }

        private static void Schedule()
        {
            Settings settings = new Settings
            {
                ContactsFromOthers = ContactsFromOthers ? (byte)1 : (byte)0,
                HapticsFromOthers = BasisBodyHaptics.FromOthers ? (byte)1 : (byte)0,
                HapticsFromSelf = BasisBodyHaptics.FromSelf ? (byte)1 : (byte)0,
            };
            bool readLocal = hapticCount > 0 || bodySelf, readRemote = bodyOthers && slotById.Count > (slots[0].Used ? 1 : 0);
            JobHandle read = default, build = default;
            if (readLocal || readRemote)
            {
                int descCount = readRemote ? bodyDescs.Length : BodyShapeIndex(1, 0);
                read = new ReadBodyTransformsJob { Assigned = bodyAssigned, ReadLocal = readLocal ? (byte)1 : (byte)0, ReadRemote = readRemote ? (byte)1 : (byte)0, Matrices = bodyMatrices, Valid = bodyValid }.ScheduleReadOnly(bodyAccess, 32);
                build = new BuildShapesJob { Descs = bodyDescs, Matrices = bodyMatrices, Valid = bodyValid, Assigned = bodyAssigned, Shapes = bodyShapes }.Schedule(descCount, 16, read);
            }
            if (authoredTransforms.Count > 0)
            {
                JobHandle authoredRead = new ReadTransformsJob { Matrices = authoredMatrices, Valid = authoredValid }.ScheduleReadOnly(authoredAccess, 32);
                JobHandle authoredBuild = new BuildShapesJob { Descs = nativeAuthoredDescs, Matrices = authoredMatrices, Valid = authoredValid, Assigned = authoredValid, Shapes = authoredShapes }.Schedule(authoredDescs.Count, 16, authoredRead);
                read = JobHandle.CombineDependencies(read, authoredRead);
                build = JobHandle.CombineDependencies(build, authoredBuild);
            }
            JobHandle bound = new GroupBoundsJob { Groups = nativeGroups, Senders = nativeSenders, BodyShapes = bodyShapes, AuthoredShapes = authoredShapes, Bounds = bounds }.Schedule(groupList.Count, 8, build);
            evaluateHandle = new EvaluateJob
            {
                Receivers = nativeReceivers,
                Senders = nativeSenders,
                Groups = nativeGroups,
                Bounds = bounds,
                BodyShapes = bodyShapes,
                AuthoredShapes = authoredShapes,
                Tags = nativeTags,
                IdToSlot = idToSlot,
                SlotStates = slotStates,
                Settings = settings,
                GroupCount = groupList.Count,
                Results = results,
            }.Schedule(receiverList.Count, 4, bound);
            readHandle = read;
            JobHandle.ScheduleBatchedJobs();
            scheduled = true;
            readPending = true;
        }

        private static void ApplyResults()
        {
            float now = Time.unscaledTime;
            int count = Mathf.Min(authoredReceiverCount, receiverSnapshot.Count);
            for (int index = 0; index < count; index++)
            {
                BasisContactReceiver receiver = receiverSnapshot[index];
                if (receiver == null || !receiver.isActiveAndEnabled)
                {
                    continue;
                }
                Result result = results[index];
                bool touched = result.Touched != 0;
                float value;
                switch (receiver.mode)
                {
                    case BasisContactReceiverMode.Proximity:
                        value = result.Value;
                        break;
                    case BasisContactReceiverMode.OnEnter:
                        if (touched && !receiver.IsTouched)
                        {
                            pulseUntil[receiver] = now + Mathf.Clamp(receiver.pulseSeconds, 0.02f, 5f);
                        }
                        value = pulseUntil.TryGetValue(receiver, out float until) && now < until ? 1f : 0f;
                        if (value == 0f && !touched)
                        {
                            pulseUntil.Remove(receiver);
                        }
                        break;
                    default:
                        value = touched ? 1f : 0f;
                        break;
                }
                receiver.ApplyContactState(value, touched, result.Player);
            }
            if (hapticCount > 0)
            {
                for (int zone = 0; zone < BasisBodyHaptics.ZoneCount; zone++)
                {
                    int slot = zoneReceiver[zone];
                    zoneScratch[zone] = slot >= 0 && results[slot].Touched != 0 ? results[slot].Value : 0f;
                }
                BasisBodyHaptics.Submit(zoneScratch);
            }
        }

        private static int LocalId()
        {
            return BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) ? localId : 0;
        }

        private static void RefreshLocalSlot()
        {
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            int id = LocalId();
            Slot slot = slots[0];
            if (local == null || local.IsDestroyed)
            {
                ReleaseSlot(0);
                return;
            }
            BasisTransformMapping mapping = local.LocalRigDriver != null ? local.LocalRigDriver.basisTransformMapping : null;
            if (slot.Used && slot.Id == id && slot.Avatar == local.BasisAvatar && mapping != null && slot.Hips == mapping.Hips && slot.Head == mapping.head)
            {
                return;
            }
            ReleaseSlot(0);
            AssignSlot(0, id, true, null, local.BasisAvatar, mapping);
        }

        private static void RefreshSlotBindings()
        {
            for (int index = 1; index < slots.Count; index++)
            {
                Slot slot = slots[index];
                if (!slot.Used)
                {
                    continue;
                }
                BasisRemotePlayer remote = slot.Remote;
                if (remote == null || remote.IsDestroyed || remote.IsEffectivelyBlocked)
                {
                    ReleaseSlot(index);
                    continue;
                }
                BasisTransformMapping mapping = remote.RemoteAvatarDriver != null ? remote.RemoteAvatarDriver.References : null;
                if (mapping == null || mapping.Hips == null)
                {
                    ReleaseSlot(index);
                    continue;
                }
                if (slot.Avatar != remote.BasisAvatar || slot.Hips != mapping.Hips || slot.Head != mapping.head)
                {
                    int id = slot.Id;
                    ReleaseSlot(index);
                    freeSlots.Remove(index);
                    AssignSlot(index, id, false, remote, remote.BasisAvatar, mapping);
                }
            }
        }

        private static void ScheduleNear(bool hapticOthers)
        {
            probeList.Clear();
            Slot local = slots[0];
            if (local.Used && local.Hips != null && (hapticOthers || (ContactsFromOthers && (wantsAvatarOthersBody || (wantsAvatarOthers && hasRemoteAuthored)))))
            {
                probeList.Add(new Probe { Center = local.Hips.position, Radius = BasisPlayerInteract.AvatarScaledRange(NearRadius) });
            }
            for (int index = 0; index < worldProbeReceivers.Count; index++)
            {
                BasisContactReceiver receiver = worldProbeReceivers[index];
                if (receiver == null || !receiver.isActiveAndEnabled)
                {
                    continue;
                }
                probeList.Add(new Probe { Center = receiver.transform.position, Radius = NearRadius + Mathf.Clamp(receiver.radius, 0f, BasisContacts.MaxRadius) + Mathf.Clamp(receiver.height, 0f, BasisContacts.MaxHeight) });
            }
            BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            int count = Mathf.Min(BasisNetworkPlayers.ReceiverCount, snapshot.Length);
            if (probeList.Count == 0 || count == 0)
            {
                nearCount = 0;
                for (int index = 1; index < slots.Count; index++)
                {
                    ReleaseSlot(index);
                }
                return;
            }
            if (!nearPositions.IsCreated || nearPositions.Length < count)
            {
                int capacity = Mathf.NextPowerOfTwo(Mathf.Max(64, count));
                if (nearPositions.IsCreated) nearPositions.Dispose();
                if (nearGaps.IsCreated) nearGaps.Dispose();
                nearPositions = new NativeArray<float3>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                nearGaps = new NativeArray<float>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                nearIds = new int[capacity];
            }
            if (!nearProbes.IsCreated || nearProbes.Length < probeList.Count)
            {
                if (nearProbes.IsCreated) nearProbes.Dispose();
                nearProbes = new NativeArray<Probe>(Mathf.NextPowerOfTwo(Mathf.Max(16, probeList.Count)), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            for (int index = 0; index < probeList.Count; index++)
            {
                nearProbes[index] = probeList[index];
            }
            float3 farAway = new float3(1e9f);
            for (int index = 0; index < count; index++)
            {
                BasisNetworkReceiver receiver = snapshot[index];
                if (receiver == null)
                {
                    nearIds[index] = -1;
                    nearPositions[index] = farAway;
                    continue;
                }
                int id = receiver.playerId;
                nearIds[index] = id;
                nearPositions[index] = RemoteBoneJobSystem.GetOutGoingMouth(id, out float3 mouth) ? mouth : farAway;
            }
            nearCount = count;
            nearHandle = new NearJob { Positions = nearPositions, Probes = nearProbes, ProbeCount = probeList.Count, Gap = nearGaps }.Schedule(count, 64);
            nearPending = true;
        }

        private static bool HasRelevantReceivers()
        {
            List<BasisContactReceiver> receivers = BasisContacts.Receivers;
            if (receivers.Count == 0)
            {
                return false;
            }
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            BasisAvatar avatar = local != null && !local.IsDestroyed ? local.BasisAvatar : null;
            if (relevantVersion == BasisContacts.Version && ReferenceEquals(relevantAvatar, avatar))
            {
                return relevantCached;
            }
            relevantVersion = BasisContacts.Version;
            relevantAvatar = avatar;
            relevantCached = false;
            for (int index = 0; index < receivers.Count; index++)
            {
                BasisContactReceiver receiver = receivers[index];
                if (receiver != null && (receiver.OwnerAvatar == null || receiver.OwnerAvatar == avatar))
                {
                    relevantCached = true;
                    break;
                }
            }
            return relevantCached;
        }

        private static bool AcceptsBody(string[] tags)
        {
            if (tags == null)
            {
                return true;
            }
            int count = 0;
            for (int index = 0; index < tags.Length && count < BasisContacts.MaxTagsPerComponent; index++)
            {
                string tag = tags[index];
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }
                if (builtinTags.Contains(tag.Trim()))
                {
                    return true;
                }
                count++;
            }
            return count == 0;
        }

        private static void ApplyNear()
        {
            candidates.Clear();
            for (int index = 0; index < nearCount; index++)
            {
                int id = nearIds[index];
                if (id < 0)
                {
                    continue;
                }
                float gap = nearGaps[index];
                if (gap <= (slotById.ContainsKey(id) ? NearExitMargin : 0f))
                {
                    candidates.Add(new Candidate { Id = id, Gap = gap });
                }
            }
            int cap = Mathf.Clamp(MaxNearPlayers, 0, MaxSlots - 1);
            if (candidates.Count > cap)
            {
                candidates.Sort(candidateComparer);
                candidates.RemoveRange(cap, candidates.Count - cap);
            }
            keepIds.Clear();
            for (int index = 0; index < candidates.Count; index++)
            {
                keepIds.Add(candidates[index].Id);
            }
            releaseScratch.Clear();
            for (int index = 1; index < slots.Count; index++)
            {
                if (slots[index].Used && !keepIds.Contains(slots[index].Id))
                {
                    releaseScratch.Add(index);
                }
            }
            for (int index = 0; index < releaseScratch.Count; index++)
            {
                ReleaseSlot(releaseScratch[index]);
            }
            for (int index = 0; index < candidates.Count; index++)
            {
                int id = candidates[index].Id;
                if (slotById.ContainsKey(id) || !BasisNetworkPlayers.RemotePlayers.TryGetValue((ushort)id, out BasisRemotePlayer remote))
                {
                    continue;
                }
                if (remote == null || remote.IsDestroyed || remote.IsEffectivelyBlocked)
                {
                    continue;
                }
                BasisTransformMapping mapping = remote.RemoteAvatarDriver != null ? remote.RemoteAvatarDriver.References : null;
                if (mapping == null || mapping.Hips == null)
                {
                    continue;
                }
                int slot = TakeFreeSlot();
                if (slot < 0)
                {
                    break;
                }
                AssignSlot(slot, id, false, remote, remote.BasisAvatar, mapping);
            }
        }

        private static int TakeFreeSlot()
        {
            if (freeSlots.Count == 0)
            {
                if (slotCapacity >= MaxSlots)
                {
                    return -1;
                }
                EnsureSlotCapacity(Mathf.Min(MaxSlots, slotCapacity * 2));
            }
            int last = freeSlots.Count - 1;
            int slot = freeSlots[last];
            freeSlots.RemoveAt(last);
            return slot;
        }

        private static void FillBones(BasisTransformMapping mapping)
        {
            Array.Clear(boneScratch, 0, BonesPerSlot);
            boneScratch[BHips] = mapping.Hips;
            boneScratch[BHead] = mapping.head;
            boneScratch[BNeck] = mapping.neck != null ? mapping.neck : mapping.head;
            boneScratch[BChest] = mapping.Upperchest != null ? mapping.Upperchest : mapping.chest != null ? mapping.chest : mapping.spine;
            boneScratch[BLUpperArm] = mapping.leftUpperArm;
            boneScratch[BLLowerArm] = mapping.leftLowerArm;
            boneScratch[BLHand] = mapping.leftHand;
            boneScratch[BLKnuckle] = mapping.LeftMiddle != null && mapping.LeftMiddle.Length > 0 ? mapping.LeftMiddle[0] : null;
            boneScratch[BLTip] = LastBone(mapping.LeftIndex, out Transform leftPrevious);
            boneScratch[BLTipParent] = leftPrevious != null ? leftPrevious : mapping.leftHand;
            boneScratch[BRUpperArm] = mapping.RightUpperArm;
            boneScratch[BRLowerArm] = mapping.RightLowerArm;
            boneScratch[BRHand] = mapping.rightHand;
            boneScratch[BRKnuckle] = mapping.RightMiddle != null && mapping.RightMiddle.Length > 0 ? mapping.RightMiddle[0] : null;
            boneScratch[BRTip] = LastBone(mapping.RightIndex, out Transform rightPrevious);
            boneScratch[BRTipParent] = rightPrevious != null ? rightPrevious : mapping.rightHand;
            boneScratch[BLUpperLeg] = mapping.LeftUpperLeg;
            boneScratch[BLLowerLeg] = mapping.LeftLowerLeg;
            boneScratch[BLFoot] = mapping.leftFoot;
            boneScratch[BLToe] = mapping.leftToe;
            boneScratch[BRUpperLeg] = mapping.RightUpperLeg;
            boneScratch[BRLowerLeg] = mapping.RightLowerLeg;
            boneScratch[BRFoot] = mapping.rightFoot;
            boneScratch[BRToe] = mapping.rightToe;
        }

        private static Transform LastBone(Transform[] chain, out Transform previous)
        {
            previous = null;
            if (chain == null)
            {
                return null;
            }
            Transform last = null;
            for (int index = 0; index < chain.Length; index++)
            {
                if (chain[index] != null)
                {
                    previous = last;
                    last = chain[index];
                }
            }
            return last;
        }

        private static bool IsRemoteBone(int bone)
        {
            switch (bone)
            {
                case BLUpperArm:
                case BLLowerArm:
                case BRUpperArm:
                case BRLowerArm:
                case BLUpperLeg:
                case BLLowerLeg:
                case BRUpperLeg:
                case BRLowerLeg:
                    return false;
                default:
                    return true;
            }
        }

        private static void AssignSlot(int slotIndex, int id, bool isLocal, BasisRemotePlayer remote, BasisAvatar avatar, BasisTransformMapping mapping)
        {
            if (mapping == null || mapping.Hips == null || (uint)id >= MaxPlayerId)
            {
                if (slotIndex > 0 && !freeSlots.Contains(slotIndex))
                {
                    freeSlots.Add(slotIndex);
                }
                return;
            }
            FillBones(mapping);
            int baseIndex = slotIndex * BonesPerSlot;
            for (int bone = 0; bone < BonesPerSlot; bone++)
            {
                Transform transform = boneScratch[bone];
                bool assigned = (isLocal || IsRemoteBone(bone)) && transform != null;
                bodyAccess[baseIndex + bone] = assigned ? transform : dummy;
                bodyAssigned[baseIndex + bone] = assigned ? (byte)1 : (byte)0;
            }
            Slot slot = slots[slotIndex];
            slot.Id = id;
            slot.IsLocal = isLocal;
            slot.Remote = remote;
            slot.Avatar = avatar;
            slot.Hips = mapping.Hips;
            slot.Head = mapping.head;
            slot.Used = true;
            slotById[id] = slotIndex;
            idToSlot[id] = (short)slotIndex;
        }

        private static void ReleaseSlot(int slotIndex)
        {
            Slot slot = slots[slotIndex];
            if (!slot.Used)
            {
                return;
            }
            if (slotById.TryGetValue(slot.Id, out int mapped) && mapped == slotIndex)
            {
                slotById.Remove(slot.Id);
                idToSlot[slot.Id] = -1;
            }
            int baseIndex = slotIndex * BonesPerSlot;
            for (int bone = 0; bone < BonesPerSlot; bone++)
            {
                bodyAccess[baseIndex + bone] = dummy;
                bodyAssigned[baseIndex + bone] = 0;
            }
            slot.Used = false;
            slot.Remote = null;
            slot.Avatar = null;
            slot.Hips = null;
            slot.Head = null;
            slot.Id = -1;
            if (slotIndex > 0)
            {
                freeSlots.Add(slotIndex);
            }
        }

        private static void SyncSlots()
        {
            for (int index = 0; index < slotCapacity; index++)
            {
                Slot slot = slots[index];
                Group group = nativeGroups[index];
                group.Count = slot.Used && (index == 0 ? bodySelf : bodyOthers) ? ShapesPerSlot : 0;
                group.OwnerId = slot.Used ? slot.Id : WorldOwner;
                nativeGroups[index] = group;
                if (!slot.Used)
                {
                    continue;
                }
                BasisRemotePlayer remote = slot.Remote;
                bool allowed = slot.IsLocal || (remote != null && !remote.IsDestroyed && !remote.IsEffectivelyBlocked && remote.AvatarInteractionAllowed);
                slotStates[index] = new PlayerState { Id = slot.Id, IsLocal = slot.IsLocal ? (byte)1 : (byte)0, Allowed = allowed ? (byte)1 : (byte)0 };
            }
        }

        private static void EnsureBuiltinTags()
        {
            TagId(BasisContactTags.Head);
            TagId(BasisContactTags.Torso);
            TagId(BasisContactTags.Hand);
            TagId(BasisContactTags.HandLeft);
            TagId(BasisContactTags.HandRight);
            TagId(BasisContactTags.Finger);
            TagId(BasisContactTags.FingerLeft);
            TagId(BasisContactTags.FingerRight);
            TagId(BasisContactTags.Foot);
            TagId(BasisContactTags.FootLeft);
            TagId(BasisContactTags.FootRight);
        }

        private static int TagId(string tag)
        {
            if (!tagIds.TryGetValue(tag, out int id))
            {
                id = tagIds.Count + 1;
                tagIds.Add(tag, id);
            }
            return id;
        }

        private static void EnsureSlotCapacity(int capacity)
        {
            capacity = Mathf.Clamp(capacity, 1, MaxSlots);
            if (capacity <= slotCapacity)
            {
                return;
            }
            CompleteAll();
            int oldCapacity = slotCapacity;
            Transform[] bones = new Transform[capacity * BonesPerSlot];
            for (int index = 0; index < bones.Length; index++)
            {
                bones[index] = index < oldCapacity * BonesPerSlot && bodyAccess.isCreated ? bodyAccess[index] : dummy;
            }
            NativeArray<byte> assigned = new NativeArray<byte>(bones.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            NativeArray<PlayerState> states = new NativeArray<PlayerState>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (bodyAssigned.IsCreated)
            {
                NativeArray<byte>.Copy(bodyAssigned, assigned, bodyAssigned.Length);
                bodyAssigned.Dispose();
            }
            if (slotStates.IsCreated)
            {
                NativeArray<PlayerState>.Copy(slotStates, states, slotStates.Length);
                slotStates.Dispose();
            }
            bodyAssigned = assigned;
            slotStates = states;
            if (bodyAccess.isCreated) bodyAccess.Dispose();
            bodyAccess = new TransformAccessArray(bones, 64);
            if (bodyMatrices.IsCreated) bodyMatrices.Dispose();
            if (bodyValid.IsCreated) bodyValid.Dispose();
            bodyMatrices = new NativeArray<float4x4>(bones.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            bodyValid = new NativeArray<byte>(bones.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int index = oldCapacity; index < capacity; index++)
            {
                slots.Add(new Slot());
                if (index > 0)
                {
                    freeSlots.Add(index);
                }
            }
            slotCapacity = capacity;
            BuildBodyDescs();
            layoutDirty = true;
        }

        private static void BuildBodyDescs()
        {
            if (bodyDescs.IsCreated) bodyDescs.Dispose();
            if (bodyShapes.IsCreated) bodyShapes.Dispose();
            int count = BasisBodyHaptics.ZoneCount + slotCapacity * ShapesPerSlot;
            bodyDescs = new NativeArray<ShapeDesc>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            bodyShapes = new NativeArray<Shape>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            bodyDescs[(int)BasisBodyHapticZone.Head] = Bone(KindBoneSphere, BHead, BNeck, 0.11f, 0.07f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ChestLeft] = Torso(1f, 0.9f, -0.45f);
            bodyDescs[(int)BasisBodyHapticZone.ChestRight] = Torso(1f, 0.9f, 0.45f);
            bodyDescs[(int)BasisBodyHapticZone.StomachLeft] = Torso(0.35f, 0.9f, -0.4f);
            bodyDescs[(int)BasisBodyHapticZone.StomachRight] = Torso(0.35f, 0.9f, 0.4f);
            bodyDescs[(int)BasisBodyHapticZone.UpperBackLeft] = Torso(1f, -0.9f, -0.45f);
            bodyDescs[(int)BasisBodyHapticZone.UpperBackRight] = Torso(1f, -0.9f, 0.45f);
            bodyDescs[(int)BasisBodyHapticZone.LowerBackLeft] = Torso(0.35f, -0.9f, -0.4f);
            bodyDescs[(int)BasisBodyHapticZone.LowerBackRight] = Torso(0.35f, -0.9f, 0.4f);
            bodyDescs[(int)BasisBodyHapticZone.UpperArmLeft] = Bone(KindBoneCapsule, BLUpperArm, BLLowerArm, 0.05f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.UpperArmRight] = Bone(KindBoneCapsule, BRUpperArm, BRLowerArm, 0.05f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ForearmLeft] = Bone(KindBoneCapsule, BLLowerArm, BLHand, 0.045f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ForearmRight] = Bone(KindBoneCapsule, BRLowerArm, BRHand, 0.045f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ThighLeft] = Bone(KindBoneCapsule, BLUpperLeg, BLLowerLeg, 0.07f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ThighRight] = Bone(KindBoneCapsule, BRUpperLeg, BRLowerLeg, 0.07f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ShinLeft] = Bone(KindBoneCapsule, BLLowerLeg, BLFoot, 0.055f, 0f, BHead, BHips);
            bodyDescs[(int)BasisBodyHapticZone.ShinRight] = Bone(KindBoneCapsule, BRLowerLeg, BRFoot, 0.055f, 0f, BHead, BHips);
            for (int slot = 0; slot < slotCapacity; slot++)
            {
                int t = slot * BonesPerSlot, d = BodyShapeIndex(slot, 0), head = t + BHead, hips = t + BHips;
                bodyDescs[d + 0] = Bone(KindBoneSphere, t + BHead, t + BNeck, 0.1f, 0.07f, head, hips);
                bodyDescs[d + 1] = Bone(KindBoneCapsule, t + BHips, t + BChest, 0.12f, 0f, head, hips);
                bodyDescs[d + 2] = Bone(KindPalm, t + BLHand, t + BLKnuckle, 0.045f, 0f, head, hips);
                bodyDescs[d + 3] = Bone(KindBoneSphere, t + BLTip, t + BLTipParent, 0.012f, 0.01f, head, hips);
                bodyDescs[d + 4] = Bone(KindPalm, t + BRHand, t + BRKnuckle, 0.045f, 0f, head, hips);
                bodyDescs[d + 5] = Bone(KindBoneSphere, t + BRTip, t + BRTipParent, 0.012f, 0.01f, head, hips);
                bodyDescs[d + 6] = Bone(KindBoneCapsule, t + BLFoot, t + BLToe, 0.045f, 0f, head, hips);
                bodyDescs[d + 7] = Bone(KindBoneCapsule, t + BRFoot, t + BRToe, 0.045f, 0f, head, hips);
            }
        }

        private static ShapeDesc Bone(byte kind, int t0, int t1, float radius, float k0, int head, int hips)
        {
            return new ShapeDesc { Kind = kind, T0 = t0, T1 = t1, T2 = -1, T3 = -1, T4 = -1, ScaleHead = head, ScaleHips = hips, Radius = radius, K0 = k0 };
        }

        private static ShapeDesc Torso(float height, float depth, float across)
        {
            return new ShapeDesc { Kind = KindTorsoZone, T0 = BHips, T1 = BChest, T2 = BNeck, T3 = BLUpperArm, T4 = BRUpperArm, ScaleHead = BHead, ScaleHips = BHips, Radius = 0.08f, K0 = height, K1 = depth, K2 = across };
        }

        private static byte ZoneSide(BasisBodyHapticZone zone)
        {
            switch (zone)
            {
                case BasisBodyHapticZone.Head:
                    return SideNone;
                case BasisBodyHapticZone.ChestLeft:
                case BasisBodyHapticZone.StomachLeft:
                case BasisBodyHapticZone.UpperBackLeft:
                case BasisBodyHapticZone.LowerBackLeft:
                case BasisBodyHapticZone.UpperArmLeft:
                case BasisBodyHapticZone.ForearmLeft:
                case BasisBodyHapticZone.ThighLeft:
                case BasisBodyHapticZone.ShinLeft:
                    return SideLeft;
                default:
                    return SideRight;
            }
        }

        private static bool TryResolveOwner(BasisAvatar avatar, BasisAvatar localAvatar, int localId, out int owner)
        {
            owner = WorldOwner;
            if (avatar == null)
            {
                return true;
            }
            if (avatar == localAvatar)
            {
                owner = localId;
                return true;
            }
            if (avatar.TryGetLinkedPlayer(out ushort id))
            {
                owner = id;
                return true;
            }
            return false;
        }

        private static void RebuildAuthored(bool haptics)
        {
            CompleteAll();
            authoredTransforms.Clear();
            authoredTransformIndex.Clear();
            authoredDescs.Clear();
            authoredSenders.Clear();
            authoredGroups.Clear();
            authoredTags.Clear();
            receiverList.Clear();
            previousSnapshot.Clear();
            previousSnapshot.AddRange(receiverSnapshot);
            receiverSnapshot.Clear();
            foreach (KeyValuePair<int, List<BasisContactSender>> pair in sendersByOwner)
            {
                pair.Value.Clear();
                senderListPool.Push(pair.Value);
            }
            sendersByOwner.Clear();
            unresolvedOwners = 0;
            worldProbeReceivers.Clear();
            wantsSelfBody = wantsWorldOthersBody = wantsAvatarOthersBody = wantsAvatarOthers = hasRemoteAuthored = false;
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            BasisAvatar localAvatar = local != null && !local.IsDestroyed ? local.BasisAvatar : null;
            int localId = LocalId();

            List<BasisContactSender> senders = BasisContacts.Senders;
            for (int index = 0; index < senders.Count; index++)
            {
                BasisContactSender sender = senders[index];
                if (sender == null || !sender.isActiveAndEnabled)
                {
                    continue;
                }
                if (!TryResolveOwner(sender.OwnerAvatar, localAvatar, localId, out int owner))
                {
                    unresolvedOwners++;
                    continue;
                }
                if (!sendersByOwner.TryGetValue(owner, out List<BasisContactSender> list))
                {
                    list = senderListPool.Count > 0 ? senderListPool.Pop() : new List<BasisContactSender>();
                    sendersByOwner.Add(owner, list);
                }
                list.Add(sender);
            }
            foreach (KeyValuePair<int, List<BasisContactSender>> pair in sendersByOwner)
            {
                int start = authoredSenders.Count;
                List<BasisContactSender> list = pair.Value;
                for (int index = 0; index < list.Count; index++)
                {
                    BasisContactSender sender = list[index];
                    int shape = AddAuthoredShape(sender.transform, sender.shape, sender.center, sender.radius, sender.height);
                    if (shape < 0)
                    {
                        break;
                    }
                    int tagOffset = authoredTags.Count;
                    int tagCount = AddTags(sender.tags);
                    authoredSenders.Add(new SenderMeta { Shape = shape, TagOffset = tagOffset, TagCount = tagCount, Source = SourceAuthored });
                }
                if (authoredSenders.Count > start)
                {
                    authoredGroups.Add(new AuthoredGroup { OwnerId = pair.Key, Start = start, Count = authoredSenders.Count - start });
                    hasRemoteAuthored |= pair.Key != WorldOwner && pair.Key != localId;
                }
            }

            List<BasisContactReceiver> receivers = BasisContacts.Receivers;
            for (int index = 0; index < receivers.Count; index++)
            {
                BasisContactReceiver receiver = receivers[index];
                if (receiver == null || !receiver.isActiveAndEnabled)
                {
                    continue;
                }
                byte flags = 0;
                if (receiver.OwnerAvatar != null)
                {
                    if (receiver.OwnerAvatar != localAvatar)
                    {
                        continue;
                    }
                    flags |= ReceiverOnAvatar;
                }
                int shape = AddAuthoredShape(receiver.transform, receiver.shape, receiver.center, receiver.radius, receiver.height);
                if (shape < 0)
                {
                    break;
                }
                if (receiver.allowSelf) flags |= ReceiverAllowSelf;
                if (receiver.allowOthers) flags |= ReceiverAllowOthers;
                if (receiver.allowWorld) flags |= ReceiverAllowWorld;
                bool body = AcceptsBody(receiver.tags), onAvatar = (flags & ReceiverOnAvatar) != 0;
                wantsSelfBody |= receiver.allowSelf && body;
                if (receiver.allowOthers)
                {
                    if (onAvatar)
                    {
                        wantsAvatarOthers = true;
                        wantsAvatarOthersBody |= body;
                    }
                    else
                    {
                        wantsWorldOthersBody |= body;
                        if (body || hasRemoteAuthored)
                        {
                            worldProbeReceivers.Add(receiver);
                        }
                    }
                }
                int tagOffset = authoredTags.Count;
                int tagCount = AddTags(receiver.tags);
                receiverList.Add(new ReceiverMeta { Shape = shape, Source = SourceAuthored, Flags = flags, TagOffset = tagOffset, TagCount = tagCount });
                receiverSnapshot.Add(receiver);
            }
            authoredReceiverCount = receiverList.Count;

            for (int zone = 0; zone < zoneReceiver.Length; zone++)
            {
                zoneReceiver[zone] = -1;
            }
            if (haptics)
            {
                for (int zone = 0; zone < BasisBodyHaptics.ZoneCount; zone++)
                {
                    zoneReceiver[zone] = receiverList.Count;
                    receiverList.Add(new ReceiverMeta { Shape = zone, Source = SourceBody, Side = ZoneSide((BasisBodyHapticZone)zone), Flags = (byte)(ReceiverOnAvatar | ReceiverHapticZone) });
                }
            }
            hapticCount = receiverList.Count - authoredReceiverCount;

            snapshotSet.Clear();
            snapshotSet.UnionWith(receiverSnapshot);
            for (int index = 0; index < previousSnapshot.Count; index++)
            {
                BasisContactReceiver dropped = previousSnapshot[index];
                if (dropped != null && !snapshotSet.Contains(dropped) && dropped.IsTouched)
                {
                    dropped.ApplyContactState(0f, false, BasisContacts.NoPlayer);
                    pulseUntil.Remove(dropped);
                }
            }
            previousSnapshot.Clear();
            snapshotSet.Clear();

            if (authoredAccess.isCreated) authoredAccess.Dispose();
            if (authoredMatrices.IsCreated) authoredMatrices.Dispose();
            if (authoredValid.IsCreated) authoredValid.Dispose();
            if (nativeAuthoredDescs.IsCreated) nativeAuthoredDescs.Dispose();
            if (authoredShapes.IsCreated) authoredShapes.Dispose();
            int transformCount = Mathf.Max(1, authoredTransforms.Count);
            authoredAccess = new TransformAccessArray(authoredTransforms.Count > 0 ? authoredTransforms.ToArray() : new Transform[] { dummy }, 64);
            authoredMatrices = new NativeArray<float4x4>(transformCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            authoredValid = new NativeArray<byte>(transformCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            nativeAuthoredDescs = ToNative(authoredDescs);
            authoredShapes = new NativeArray<Shape>(Mathf.Max(1, authoredDescs.Count), Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        private static void RebuildLayout()
        {
            CompleteAll();
            senderList.Clear();
            groupList.Clear();
            tagPool.Clear();
            tagPool.Add(TagId(BasisContactTags.Head));
            tagPool.Add(TagId(BasisContactTags.Torso));
            tagPool.Add(TagId(BasisContactTags.Hand)); tagPool.Add(TagId(BasisContactTags.HandLeft));
            tagPool.Add(TagId(BasisContactTags.Finger)); tagPool.Add(TagId(BasisContactTags.FingerLeft));
            tagPool.Add(TagId(BasisContactTags.Hand)); tagPool.Add(TagId(BasisContactTags.HandRight));
            tagPool.Add(TagId(BasisContactTags.Finger)); tagPool.Add(TagId(BasisContactTags.FingerRight));
            tagPool.Add(TagId(BasisContactTags.Foot)); tagPool.Add(TagId(BasisContactTags.FootLeft));
            tagPool.Add(TagId(BasisContactTags.Foot)); tagPool.Add(TagId(BasisContactTags.FootRight));
            tagPool.AddRange(authoredTags);
            for (int slot = 0; slot < slotCapacity; slot++)
            {
                int d = BodyShapeIndex(slot, 0);
                senderList.Add(new SenderMeta { Shape = d + 0, TagOffset = 0, TagCount = 1, Source = SourceBody, Side = SideNone, Flags = SenderBody });
                senderList.Add(new SenderMeta { Shape = d + 1, TagOffset = 1, TagCount = 1, Source = SourceBody, Side = SideNone, Flags = SenderBody });
                senderList.Add(new SenderMeta { Shape = d + 2, TagOffset = 2, TagCount = 2, Source = SourceBody, Side = SideLeft, Flags = SenderBody | SenderSelfHapticOk });
                senderList.Add(new SenderMeta { Shape = d + 3, TagOffset = 4, TagCount = 2, Source = SourceBody, Side = SideLeft, Flags = SenderBody | SenderSelfHapticOk });
                senderList.Add(new SenderMeta { Shape = d + 4, TagOffset = 6, TagCount = 2, Source = SourceBody, Side = SideRight, Flags = SenderBody | SenderSelfHapticOk });
                senderList.Add(new SenderMeta { Shape = d + 5, TagOffset = 8, TagCount = 2, Source = SourceBody, Side = SideRight, Flags = SenderBody | SenderSelfHapticOk });
                senderList.Add(new SenderMeta { Shape = d + 6, TagOffset = 10, TagCount = 2, Source = SourceBody, Side = SideLeft, Flags = SenderBody });
                senderList.Add(new SenderMeta { Shape = d + 7, TagOffset = 12, TagCount = 2, Source = SourceBody, Side = SideRight, Flags = SenderBody });
                groupList.Add(new Group { Start = slot * ShapesPerSlot, Count = 0, OwnerId = WorldOwner });
            }
            int senderBase = senderList.Count;
            for (int index = 0; index < authoredSenders.Count; index++)
            {
                SenderMeta meta = authoredSenders[index];
                meta.TagOffset += BuiltinTagCount;
                senderList.Add(meta);
            }
            for (int index = 0; index < authoredGroups.Count; index++)
            {
                AuthoredGroup group = authoredGroups[index];
                groupList.Add(new Group { Start = senderBase + group.Start, Count = group.Count, OwnerId = group.OwnerId });
            }
            ReplaceNative(ref nativeSenders, senderList);
            ReplaceNative(ref nativeGroups, groupList);
            ReplaceNative(ref nativeTags, tagPool);
            if (nativeReceivers.IsCreated) nativeReceivers.Dispose();
            nativeReceivers = new NativeArray<ReceiverMeta>(Mathf.Max(1, receiverList.Count), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int index = 0; index < receiverList.Count; index++)
            {
                ReceiverMeta meta = receiverList[index];
                if (meta.Source == SourceAuthored)
                {
                    meta.TagOffset += BuiltinTagCount;
                }
                nativeReceivers[index] = meta;
            }
            if (bounds.IsCreated) bounds.Dispose();
            bounds = new NativeArray<GroupBounds>(Mathf.Max(1, groupList.Count), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (results.IsCreated) results.Dispose();
            results = new NativeArray<Result>(Mathf.Max(1, receiverList.Count), Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        private static int AddAuthoredTransform(Transform transform)
        {
            if (transform == null)
            {
                return -1;
            }
            if (authoredTransformIndex.TryGetValue(transform, out int index))
            {
                return index;
            }
            if (authoredTransforms.Count >= MaxAuthoredTransforms)
            {
                return -1;
            }
            index = authoredTransforms.Count;
            authoredTransforms.Add(transform);
            authoredTransformIndex.Add(transform, index);
            return index;
        }

        private static int AddAuthoredShape(Transform transform, BasisContactShape shape, Vector3 center, float radius, float height)
        {
            int t0 = AddAuthoredTransform(transform);
            if (t0 < 0)
            {
                return -1;
            }
            authoredDescs.Add(new ShapeDesc
            {
                Kind = shape == BasisContactShape.Capsule ? KindAuthoredCapsule : KindAuthoredSphere,
                T0 = t0, T1 = -1, T2 = -1, T3 = -1, T4 = -1, ScaleHead = -1, ScaleHips = -1,
                Center = center,
                Radius = radius,
                Height = height,
            });
            return authoredDescs.Count - 1;
        }

        private static int AddTags(string[] tags)
        {
            if (tags == null)
            {
                return 0;
            }
            int count = 0;
            for (int index = 0; index < tags.Length && count < BasisContacts.MaxTagsPerComponent; index++)
            {
                string tag = tags[index];
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }
                authoredTags.Add(TagId(tag.Trim()));
                count++;
            }
            return count;
        }

        private static NativeArray<T> ToNative<T>(List<T> list) where T : struct
        {
            NativeArray<T> array = new NativeArray<T>(Mathf.Max(1, list.Count), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int index = 0; index < list.Count; index++)
            {
                array[index] = list[index];
            }
            return array;
        }

        private static void ReplaceNative<T>(ref NativeArray<T> array, List<T> list) where T : struct
        {
            if (array.IsCreated) array.Dispose();
            array = ToNative(list);
        }

        private static void CompleteAll()
        {
            if (scheduled || readPending)
            {
                evaluateHandle.Complete();
                readHandle.Complete();
                scheduled = false;
                readPending = false;
            }
            if (nearPending)
            {
                nearHandle.Complete();
                nearPending = false;
            }
        }

        private static void Deactivate()
        {
            for (int index = 0; index < receiverSnapshot.Count; index++)
            {
                BasisContactReceiver receiver = receiverSnapshot[index];
                if (receiver != null)
                {
                    receiver.ApplyContactState(0f, false, BasisContacts.NoPlayer);
                }
            }
            BasisBodyHaptics.ClearAll();
            Dispose();
        }

        public static void Dispose()
        {
            CompleteAll();
            if (bodyAccess.isCreated) bodyAccess.Dispose();
            if (authoredAccess.isCreated) authoredAccess.Dispose();
            if (bodyMatrices.IsCreated) bodyMatrices.Dispose();
            if (authoredMatrices.IsCreated) authoredMatrices.Dispose();
            if (bodyValid.IsCreated) bodyValid.Dispose();
            if (bodyAssigned.IsCreated) bodyAssigned.Dispose();
            if (authoredValid.IsCreated) authoredValid.Dispose();
            if (bodyDescs.IsCreated) bodyDescs.Dispose();
            if (nativeAuthoredDescs.IsCreated) nativeAuthoredDescs.Dispose();
            if (bodyShapes.IsCreated) bodyShapes.Dispose();
            if (authoredShapes.IsCreated) authoredShapes.Dispose();
            if (nativeSenders.IsCreated) nativeSenders.Dispose();
            if (nativeGroups.IsCreated) nativeGroups.Dispose();
            if (bounds.IsCreated) bounds.Dispose();
            if (nativeReceivers.IsCreated) nativeReceivers.Dispose();
            if (nativeTags.IsCreated) nativeTags.Dispose();
            if (idToSlot.IsCreated) idToSlot.Dispose();
            if (slotStates.IsCreated) slotStates.Dispose();
            if (results.IsCreated) results.Dispose();
            if (nearPositions.IsCreated) nearPositions.Dispose();
            if (nearProbes.IsCreated) nearProbes.Dispose();
            if (nearGaps.IsCreated) nearGaps.Dispose();
            if (dummyObject != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(dummyObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(dummyObject);
                }
            }
            dummyObject = null;
            dummy = null;
            active = false;
            builtVersion = -1;
            slotCapacity = 0;
            nearCount = 0;
            authoredReceiverCount = 0;
            hapticCount = 0;
            unresolvedOwners = 0;
            layoutDirty = false;
            nearIds = Array.Empty<int>();
            slots.Clear();
            slotById.Clear();
            freeSlots.Clear();
            authoredTransforms.Clear();
            authoredTransformIndex.Clear();
            authoredDescs.Clear();
            authoredSenders.Clear();
            authoredGroups.Clear();
            authoredTags.Clear();
            senderList.Clear();
            groupList.Clear();
            receiverList.Clear();
            tagPool.Clear();
            receiverSnapshot.Clear();
            previousSnapshot.Clear();
            foreach (KeyValuePair<int, List<BasisContactSender>> pair in sendersByOwner)
            {
                pair.Value.Clear();
                senderListPool.Push(pair.Value);
            }
            sendersByOwner.Clear();
            pulseUntil.Clear();
            candidates.Clear();
            probeList.Clear();
            worldProbeReceivers.Clear();
            wantsSelfBody = wantsWorldOthersBody = wantsAvatarOthersBody = wantsAvatarOthers = hasRemoteAuthored = bodySelf = bodyOthers = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Dispose();
            tagIds.Clear();
            senderListPool.Clear();
            readHandle = default;
            evaluateHandle = default;
            nearHandle = default;
            nearRefreshAt = 0f;
            authoredRetryAt = 0f;
            relevantVersion = -1;
            relevantAvatar = null;
            relevantCached = false;
        }
    }
}
