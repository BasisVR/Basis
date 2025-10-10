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

        private const int CounterCount = 18;
        private static readonly long[] counters = new long[CounterCount];

        public static void Update()
        {
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
        }
        private static void SampleAndReset(ProfilerCounter<long> counter, BasisNetworkProfilerCounter index)
        {
            long value = Interlocked.Exchange(ref counters[(int)index], 0);
            counter.Sample(value);
        }

        // prefer passing long to avoid truncation of small floats
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
