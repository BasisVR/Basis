using System;
using System.Collections.Concurrent;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace Basis.Scripts.Profiler
{
    public static class BasisNetworkProfiler
    {
        public static readonly ProfilerCategory Category = ProfilerCategory.Network;

        // Labels
        public const string AudioSegmentDataMessageText = "Audio Segment Data Message";
        public const string AuthenticationMessageText = "Authentication Message";
        public const string AvatarDataMessageText = "Avatar Data Message";
        public const string CreateAllRemoteMessageText = "Create All Remote Message";
        public const string CreateSingleRemoteMessageText = "Create Single Remote Message";
        public const string LocalAvatarSyncMessageText = "Local Avatar Sync Message";
        public const string OwnershipTransferMessageText = "Ownership Transfer Message";
        public const string RequestOwnershipTransferMessageText = "Request Ownership Transfer Message";
        public const string PlayerIdMessageText = "Player ID Message";
        public const string PlayerMetaDataMessageText = "Player Metadata Message";
        public const string ReadyMessageText = "Ready Message";
        public const string SceneDataMessageText = "Scene Data Message";
        public const string ServerAudioSegmentMessageText = "Server Audio Segment Message";
        public const string ServerAvatarChangeMessageText = "Server Avatar Change Message";
        public const string ServerSideSyncPlayerMessageText = "Server Side Sync Player Message";
        public const string AudioRecipientsMessageText = "Audio Recipients Message";
        public const string AvatarChangeMessageText = "Avatar Change Message";
        public const string ServerAvatarDataMessageText = "Server Avatar Data Message";
        public const string DisconnectionMessageText = "Disconnection Message";
        public const string ShoutVoiceMessageText = "Shout Voice Message";
        public const string GetOwnershipMessageText = "Get Ownership Message";
        public const string ChangeOwnershipMessageText = "Change Ownership Message";
        public const string RemoveOwnershipMessageText = "Remove Ownership Message";
        public const string PlayerAvatarMessageText = "Player Avatar Message";
        public const string NetIDAssignMessageText = "Net ID Assign Message";
        public const string NetIDAssignsMessageText = "Net ID Assigns Message";
        public const string LoadResourceMessageText = "Load Resource Message";
        public const string UnloadResourceMessageText = "Unload Resource Message";
        public const string AdminMessageText = "Admin Message";
        public const string ContentShareMessageText = "Content Share Message";
        public const string ContentShareCleanupMessageText = "Content Share Cleanup Message";
        public const string ChatMessageText = "Chat Message";
        public const string StoreDatabaseMessageText = "Store Database Message";
        public const string ServerStatisticsMessageText = "Server Statistics Message";
        public const string CameraPIPStateMessageText = "Camera PIP State Message";
        public const string CameraPIPPositionMessageText = "Camera PIP Position Message";
        public const string SpawnPreloadedMessageText = "Spawn Preloaded Message";
        public const string EventsMessageText = "Events Message";

        public const string OutboundAvatarP2PText = "Outbound Avatar P2P";
        public const string OutboundAvatarServerText = "Outbound Avatar Server";
        public const string InboundAvatarP2PText = "Inbound Avatar P2P";
        public const string P2PConnectedSessionsText = "P2P Connected Sessions";

        // Profiler counters (per-type; sampled via Update())
        private static readonly ProfilerCounter<long> AudioSegmentDataMessageCounter = new(Category, AudioSegmentDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> AuthenticationMessageCounter = new(Category, AuthenticationMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> AvatarDataMessageCounter = new(Category, AvatarDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> CreateAllRemoteMessageCounter = new(Category, CreateAllRemoteMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> CreateSingleRemoteMessageCounter = new(Category, CreateSingleRemoteMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> LocalAvatarSyncMessageCounter = new(Category, LocalAvatarSyncMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> OwnershipTransferMessageCounter = new(Category, OwnershipTransferMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> RequestOwnershipTransferMessageCounter = new(Category, RequestOwnershipTransferMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> PlayerIdMessageCounter = new(Category, PlayerIdMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> PlayerMetaDataMessageCounter = new(Category, PlayerMetaDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ReadyMessageCounter = new(Category, ReadyMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> SceneDataMessageCounter = new(Category, SceneDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ServerAudioSegmentMessageCounter = new(Category, ServerAudioSegmentMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ServerAvatarChangeMessageCounter = new(Category, ServerAvatarChangeMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ServerSideSyncPlayerMessageCounter = new(Category, ServerSideSyncPlayerMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> AudioRecipientsMessageCounter = new(Category, AudioRecipientsMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> AvatarChangeMessageCounter = new(Category, AvatarChangeMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ServerAvatarDataMessageCounter = new(Category, ServerAvatarDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> DisconnectionMessageCounter = new(Category, DisconnectionMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ShoutVoiceMessageCounter = new(Category, ShoutVoiceMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> GetOwnershipMessageCounter = new(Category, GetOwnershipMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ChangeOwnershipMessageCounter = new(Category, ChangeOwnershipMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> RemoveOwnershipMessageCounter = new(Category, RemoveOwnershipMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> PlayerAvatarMessageCounter = new(Category, PlayerAvatarMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> NetIDAssignMessageCounter = new(Category, NetIDAssignMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> NetIDAssignsMessageCounter = new(Category, NetIDAssignsMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> LoadResourceMessageCounter = new(Category, LoadResourceMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> UnloadResourceMessageCounter = new(Category, UnloadResourceMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> AdminMessageCounter = new(Category, AdminMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ContentShareMessageCounter = new(Category, ContentShareMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ContentShareCleanupMessageCounter = new(Category, ContentShareCleanupMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ChatMessageCounter = new(Category, ChatMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> StoreDatabaseMessageCounter = new(Category, StoreDatabaseMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> ServerStatisticsMessageCounter = new(Category, ServerStatisticsMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> CameraPIPStateMessageCounter = new(Category, CameraPIPStateMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> CameraPIPPositionMessageCounter = new(Category, CameraPIPPositionMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> SpawnPreloadedMessageCounter = new(Category, SpawnPreloadedMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> EventsMessageCounter = new(Category, EventsMessageText, ProfilerMarkerDataUnit.Bytes);

        private static readonly ProfilerCounter<long> OutboundAvatarP2PCounter = new(Category, OutboundAvatarP2PText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> OutboundAvatarServerCounter = new(Category, OutboundAvatarServerText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> InboundAvatarP2PCounter = new(Category, InboundAvatarP2PText, ProfilerMarkerDataUnit.Bytes);
        // Gauge — sampled live in Update() from ConnectedSessionsProvider, not accumulated.
        private static readonly ProfilerCounter<long> P2PConnectedSessionsCounter = new(Category, P2PConnectedSessionsText, ProfilerMarkerDataUnit.Count);

        public static System.Func<int> ConnectedSessionsProvider;

        private const int CounterCount = 42;
        private static readonly long[] counters = new long[CounterCount];

        public static void Update()
        {
            // Skip the per-frame sampling unless the profiler is actually recording.
            if (!UnityEngine.Profiling.Profiler.enabled) return;

            SampleAndReset(AudioSegmentDataMessageCounter, BasisNetworkProfilerCounter.AudioSegmentData);
            SampleAndReset(AuthenticationMessageCounter, BasisNetworkProfilerCounter.Authentication);
            SampleAndReset(AvatarDataMessageCounter, BasisNetworkProfilerCounter.AvatarDataMessage);
            SampleAndReset(CreateAllRemoteMessageCounter, BasisNetworkProfilerCounter.CreateAllRemote);
            SampleAndReset(CreateSingleRemoteMessageCounter, BasisNetworkProfilerCounter.CreateSingleRemote);
            SampleAndReset(LocalAvatarSyncMessageCounter, BasisNetworkProfilerCounter.LocalAvatarSync);
            SampleAndReset(OwnershipTransferMessageCounter, BasisNetworkProfilerCounter.OwnershipTransfer);
            SampleAndReset(RequestOwnershipTransferMessageCounter, BasisNetworkProfilerCounter.RequestOwnershipTransfer);
            SampleAndReset(PlayerIdMessageCounter, BasisNetworkProfilerCounter.PlayerId);
            SampleAndReset(PlayerMetaDataMessageCounter, BasisNetworkProfilerCounter.PlayerMetaData);
            SampleAndReset(ReadyMessageCounter, BasisNetworkProfilerCounter.Ready);
            SampleAndReset(SceneDataMessageCounter, BasisNetworkProfilerCounter.SceneData);
            SampleAndReset(ServerAudioSegmentMessageCounter, BasisNetworkProfilerCounter.ServerAudioSegment);
            SampleAndReset(ServerAvatarChangeMessageCounter, BasisNetworkProfilerCounter.ServerAvatarChange);
            SampleAndReset(ServerSideSyncPlayerMessageCounter, BasisNetworkProfilerCounter.ServerSideSyncPlayer);
            SampleAndReset(AudioRecipientsMessageCounter, BasisNetworkProfilerCounter.AudioRecipients);
            SampleAndReset(AvatarChangeMessageCounter, BasisNetworkProfilerCounter.AvatarChange);
            SampleAndReset(ServerAvatarDataMessageCounter, BasisNetworkProfilerCounter.ServerAvatarData);
            SampleAndReset(DisconnectionMessageCounter, BasisNetworkProfilerCounter.Disconnection);
            SampleAndReset(ShoutVoiceMessageCounter, BasisNetworkProfilerCounter.ShoutVoice);
            SampleAndReset(GetOwnershipMessageCounter, BasisNetworkProfilerCounter.GetOwnership);
            SampleAndReset(ChangeOwnershipMessageCounter, BasisNetworkProfilerCounter.ChangeOwnership);
            SampleAndReset(RemoveOwnershipMessageCounter, BasisNetworkProfilerCounter.RemoveOwnership);
            SampleAndReset(PlayerAvatarMessageCounter, BasisNetworkProfilerCounter.PlayerAvatar);
            SampleAndReset(NetIDAssignMessageCounter, BasisNetworkProfilerCounter.NetIDAssign);
            SampleAndReset(NetIDAssignsMessageCounter, BasisNetworkProfilerCounter.NetIDAssigns);
            SampleAndReset(LoadResourceMessageCounter, BasisNetworkProfilerCounter.LoadResource);
            SampleAndReset(UnloadResourceMessageCounter, BasisNetworkProfilerCounter.UnloadResource);
            SampleAndReset(AdminMessageCounter, BasisNetworkProfilerCounter.Admin);
            SampleAndReset(ContentShareMessageCounter, BasisNetworkProfilerCounter.ContentShare);
            SampleAndReset(ContentShareCleanupMessageCounter, BasisNetworkProfilerCounter.ContentShareCleanup);
            SampleAndReset(ChatMessageCounter, BasisNetworkProfilerCounter.Chat);
            SampleAndReset(StoreDatabaseMessageCounter, BasisNetworkProfilerCounter.StoreDatabase);
            SampleAndReset(ServerStatisticsMessageCounter, BasisNetworkProfilerCounter.ServerStatistics);
            SampleAndReset(CameraPIPStateMessageCounter, BasisNetworkProfilerCounter.CameraPIPState);
            SampleAndReset(CameraPIPPositionMessageCounter, BasisNetworkProfilerCounter.CameraPIPPosition);
            SampleAndReset(SpawnPreloadedMessageCounter, BasisNetworkProfilerCounter.SpawnPreloaded);
            SampleAndReset(EventsMessageCounter, BasisNetworkProfilerCounter.Events);

            SampleAndReset(OutboundAvatarP2PCounter, BasisNetworkProfilerCounter.OutboundAvatarP2P);
            SampleAndReset(OutboundAvatarServerCounter, BasisNetworkProfilerCounter.OutboundAvatarServer);
            SampleAndReset(InboundAvatarP2PCounter, BasisNetworkProfilerCounter.InboundAvatarP2P);
            P2PConnectedSessionsCounter.Sample(ConnectedSessionsProvider?.Invoke() ?? 0);
        }
        private static void SampleAndReset(ProfilerCounter<long> counter, BasisNetworkProfilerCounter index)
        {
            long value = Interlocked.Exchange(ref counters[(int)index], 0);
            counter.Sample(value);
        }

        // prefer passing long to avoid truncation of small floats
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void AddToCounter(BasisNetworkProfilerCounter counter, long value)
        {
            Interlocked.Add(ref counters[(int)counter], value);
        }

        // ---------- Per-index inbound/outbound pairs using ProfilerCounterValue<T> ----------

        // Using class (not struct) to avoid copies.
        public sealed class CounterPair
        {
            public ProfilerCounterValue<long> Bytes;
            public ProfilerCounterValue<long> Count;
        }

        // Inbound / Outbound: per-index counter pairs
        private static readonly ConcurrentDictionary<int, CounterPair> InPerIndex = new();
        private static readonly ConcurrentDictionary<int, CounterPair> OutPerIndex = new();
        // Resolve a friendly name for each index/key.
        // Replace with your own mapping if "index" is not a channelId.
        public static Func<int, string> ResolveName = (index) => $"Index {index}";

        public static CounterPair GetOrCreate(ConcurrentDictionary<int, CounterPair> dict, int index, string direction, string friendlyName)
        {
            return dict.GetOrAdd(index, _ =>
            {
                // Example names: "Inbound/Audio Segment Data Message Bytes", "Outbound/Scene Data Message Count"
                var bytesName = $"{direction}/{friendlyName} Bytes";
                var countName = $"{direction}/{friendlyName} Count";

                var options = ProfilerCounterOptions.FlushOnEndOfFrame | ProfilerCounterOptions.ResetToZeroOnFlush;

                return new CounterPair
                {
                    Bytes = new ProfilerCounterValue<long>(Category, bytesName, ProfilerMarkerDataUnit.Bytes, options),
                    Count = new ProfilerCounterValue<long>(Category, countName, ProfilerMarkerDataUnit.Count, options)
                };
            });
        }

        public static void SampleInbound(int index, ulong bytes, ulong count)
        {
            var name = ResolveName(index);
            CounterPair pair = GetOrCreate(InPerIndex, index, "Inbound", name);

            long bytesToSample = (long)bytes;
            long countToSample = (long)count;

            // These are per-frame deltas; options ensure reset at end-of-frame.
            pair.Bytes.Value = bytesToSample;
            pair.Count.Value = countToSample;
        }

        public static void SampleOutbound(int index, ulong bytes, ulong count)
        {
            var name = ResolveName(index);
            CounterPair pair = GetOrCreate(OutPerIndex, index, "Outbound", name);

            long bytesToSample = (long)bytes;
            long countToSample = (long)count;

            pair.Bytes.Value = bytesToSample;
            pair.Count.Value = countToSample;
        }
    }
}
