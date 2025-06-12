using System.Threading;
using Unity.Profiling;

namespace Basis.Scripts.Profiler
{
    public static class BasisNetworkProfiler
    {
        public static readonly ProfilerCategory Category = ProfilerCategory.Network;

        // Profiler counters
        private static readonly ProfilerCounter<int> AudioSegmentDataMessageCounter = new ProfilerCounter<int>(Category, AudioSegmentDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> AuthenticationMessageCounter = new ProfilerCounter<int>(Category, AuthenticationMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> AvatarDataMessageCounter = new ProfilerCounter<int>(Category, AvatarDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> CreateAllRemoteMessageCounter = new ProfilerCounter<int>(Category, CreateAllRemoteMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> CreateSingleRemoteMessageCounter = new ProfilerCounter<int>(Category, CreateSingleRemoteMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> LocalAvatarSyncMessageCounter = new ProfilerCounter<int>(Category, LocalAvatarSyncMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> OwnershipTransferMessageCounter = new ProfilerCounter<int>(Category, OwnershipTransferMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> RequestOwnershipTransferMessageCounter = new ProfilerCounter<int>(Category, RequestOwnershipTransferMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> PlayerIdMessageCounter = new ProfilerCounter<int>(Category, PlayerIdMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> PlayerMetaDataMessageCounter = new ProfilerCounter<int>(Category, PlayerMetaDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> ReadyMessageCounter = new ProfilerCounter<int>(Category, ReadyMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> SceneDataMessageCounter = new ProfilerCounter<int>(Category, SceneDataMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> ServerAudioSegmentMessageCounter = new ProfilerCounter<int>(Category, ServerAudioSegmentMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> ServerAvatarChangeMessageCounter = new ProfilerCounter<int>(Category, ServerAvatarChangeMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> ServerSideSyncPlayerMessageCounter = new ProfilerCounter<int>(Category, ServerSideSyncPlayerMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> AudioRecipientsMessageCounter = new ProfilerCounter<int>(Category, AudioRecipientsMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> AvatarChangeMessageCounter = new ProfilerCounter<int>(Category, AvatarChangeMessageText, ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> ServerAvatarDataMessageCounter = new ProfilerCounter<int>(Category, ServerAvatarDataMessageText, ProfilerMarkerDataUnit.Bytes);
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

        private static void SampleAndReset(ProfilerCounter<int> counter, BasisNetworkProfilerCounter index)
        {
            long value = Interlocked.Exchange(ref counters[(int)index], 0);
            counter.Sample((int)value);
        }
        public static void AddToCounter(BasisNetworkProfilerCounter counter, float value)
        {
            Interlocked.Add(ref counters[(int)counter], (long)value);
        }
    }
}
