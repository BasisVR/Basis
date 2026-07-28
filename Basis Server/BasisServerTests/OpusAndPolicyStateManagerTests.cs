using Basis.Network.Core;
using BasisNetworkServer.Security;
using System.Net;
using Xunit;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Offline stand-in for a connected client: implements the Basis.Network.Core.NetPeer
/// shell interface and records every payload the server "sends" to it, so the
/// Send*ToPeer paths can be exercised without a socket.
/// </summary>
internal sealed class PolicyTestPeer : NetPeer
{
    public PolicyTestPeer(int id) => Id = id;

    public readonly List<byte[]> Sent = new();
    public byte LastChannel = byte.MaxValue;
    public DeliveryMethod LastDelivery;

    public int Id { get; }
    public IPAddress Address => IPAddress.Loopback;
    public int RemoteId => Id;
    public int RoundTripTime => 0;
    public float TimeSinceLastPacket => 0f;
    public long RemoteTimeDelta => 0;
    public int Mtu => 1200;
    public object Tag { get; set; } = new();

    public void Disconnect() { }
    public void Disconnect(byte[] b) { }
    public void DisconnectForce() { }
    public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod) => 0;

    public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        Sent.Add((byte[])data.Clone());
        LastChannel = channelNumber;
        LastDelivery = deliveryMethod;
    }

    public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        byte[] copy = new byte[data.Length];
        Array.Copy(data.Data, copy, data.Length);
        Sent.Add(copy);
        LastChannel = channelNumber;
        LastDelivery = deliveryMethod;
    }

    public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0) { }
}

/// <summary>
/// Opus frame duration global: only 20 or 40 ms are accepted, anything else
/// falls back to the 20 ms default.
/// </summary>
public class BasisOpusFrameDurationStateManagerTests
{
    [Theory]
    [InlineData(20, true)]
    [InlineData(40, true)]
    [InlineData(0, false)]
    [InlineData(-20, false)]
    [InlineData(10, false)]
    [InlineData(25, false)]
    [InlineData(60, false)]
    public void IsAcceptedDuration_OnlyAccepts20And40(int ms, bool expected)
    {
        Assert.Equal(expected, BasisOpusFrameDurationStateManager.IsAcceptedDuration(ms));
    }

    [Fact]
    public void SetFrameDurationMs_SwitchesBetween20And40_AndReportsChanges()
    {
        try
        {
            BasisOpusFrameDurationStateManager.SetFrameDurationMs(20);
            Assert.True(BasisOpusFrameDurationStateManager.SetFrameDurationMs(40));
            Assert.Equal(40, BasisOpusFrameDurationStateManager.FrameDurationMs);
            Assert.False(BasisOpusFrameDurationStateManager.SetFrameDurationMs(40));
            Assert.True(BasisOpusFrameDurationStateManager.SetFrameDurationMs(20));
            Assert.Equal(20, BasisOpusFrameDurationStateManager.FrameDurationMs);
        }
        finally
        {
            BasisOpusFrameDurationStateManager.SetFrameDurationMs(BasisOpusFrameDurationStateManager.DefaultMs);
        }
    }

    [Fact]
    public void SetFrameDurationMs_RejectedDurations_FallBackToTheDefault()
    {
        try
        {
            BasisOpusFrameDurationStateManager.SetFrameDurationMs(40);
            Assert.True(BasisOpusFrameDurationStateManager.SetFrameDurationMs(60)); // rejected -> DefaultMs, changed from 40
            Assert.Equal(BasisOpusFrameDurationStateManager.DefaultMs, BasisOpusFrameDurationStateManager.FrameDurationMs);
            Assert.False(BasisOpusFrameDurationStateManager.SetFrameDurationMs(10)); // rejected -> DefaultMs, already there
            Assert.Equal(20, BasisOpusFrameDurationStateManager.FrameDurationMs);
        }
        finally
        {
            BasisOpusFrameDurationStateManager.SetFrameDurationMs(BasisOpusFrameDurationStateManager.DefaultMs);
        }
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenDurationByte()
    {
        try
        {
            BasisOpusFrameDurationStateManager.SetFrameDurationMs(40);
            var peer = new PolicyTestPeer(10);
            BasisOpusFrameDurationStateManager.SendStateToPeer(peer);

            Assert.Equal(new[] { (byte)AdminRequestMode.GlobalGetOpusFrameDurationState, (byte)40 }, Assert.Single(peer.Sent));
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, peer.LastDelivery);

            BasisOpusFrameDurationStateManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisOpusFrameDurationStateManager.SetFrameDurationMs(BasisOpusFrameDurationStateManager.DefaultMs);
        }
    }
}

/// <summary>
/// Opus FEC packet-loss percentage global: clamped into 0..100.
/// </summary>
public class BasisOpusPacketLossStateManagerTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void SetPacketLossPercent_ClampsInto0To100(int requested, int expected)
    {
        try
        {
            BasisOpusPacketLossStateManager.SetPacketLossPercent(requested);
            Assert.Equal(expected, BasisOpusPacketLossStateManager.PacketLossPercent);
        }
        finally
        {
            BasisOpusPacketLossStateManager.SetPacketLossPercent(10);
        }
    }

    [Fact]
    public void SetPacketLossPercent_ReportsOnlyRealChanges()
    {
        BasisOpusPacketLossStateManager.SetPacketLossPercent(10);
        Assert.True(BasisOpusPacketLossStateManager.SetPacketLossPercent(33));
        Assert.False(BasisOpusPacketLossStateManager.SetPacketLossPercent(33));
        Assert.True(BasisOpusPacketLossStateManager.SetPacketLossPercent(100));
        Assert.False(BasisOpusPacketLossStateManager.SetPacketLossPercent(133)); // clamps onto the value already stored
        Assert.True(BasisOpusPacketLossStateManager.SetPacketLossPercent(10));
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenPercentByte()
    {
        try
        {
            BasisOpusPacketLossStateManager.SetPacketLossPercent(37);
            var peer = new PolicyTestPeer(11);
            BasisOpusPacketLossStateManager.SendStateToPeer(peer);

            Assert.Equal(new[] { (byte)AdminRequestMode.GlobalGetOpusPacketLossState, (byte)37 }, Assert.Single(peer.Sent));
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, peer.LastDelivery);

            BasisOpusPacketLossStateManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisOpusPacketLossStateManager.SetPacketLossPercent(10);
        }
    }
}

/// <summary>
/// Per-user Opus bitrate overrides keyed by netId plus the session-wide global value;
/// 0 is the "clear / no override" sentinel and the per-user value wins over the global.
/// </summary>
[Collection("BasisServer shared network statics")] // asserts on NetworkServer.PeerSnapshot — must not race tests that populate it
public class BasisUserOpusBitrateStateManagerTests
{
    [Theory]
    [InlineData(BasisUserOpusBitrateStateManager.MinBitrate, BasisUserOpusBitrateStateManager.MinBitrate)]
    [InlineData(1, BasisUserOpusBitrateStateManager.MinBitrate)]
    [InlineData(5999, BasisUserOpusBitrateStateManager.MinBitrate)]
    [InlineData(240000, 240000)]
    [InlineData(BasisUserOpusBitrateStateManager.MaxBitrate, BasisUserOpusBitrateStateManager.MaxBitrate)]
    [InlineData(int.MaxValue, BasisUserOpusBitrateStateManager.MaxBitrate)]
    public void SetBitrate_ClampsIntoTheVoiceRange(int requested, int expected)
    {
        int netId = 701000 + requested % 97;
        try
        {
            Assert.Equal(expected, BasisUserOpusBitrateStateManager.SetBitrate(netId, requested));
            Assert.True(BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out int stored));
            Assert.Equal(expected, stored);
        }
        finally
        {
            BasisUserOpusBitrateStateManager.ClearForPeer(netId);
        }
    }

    [Fact]
    public void SetBitrate_ZeroOrNegative_ClearsTheOverride()
    {
        const int netId = 702001;
        BasisUserOpusBitrateStateManager.SetBitrate(netId, 32000);
        Assert.Equal(0, BasisUserOpusBitrateStateManager.SetBitrate(netId, 0));
        Assert.False(BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out _));

        BasisUserOpusBitrateStateManager.SetBitrate(netId, 32000);
        Assert.Equal(0, BasisUserOpusBitrateStateManager.SetBitrate(netId, -5000));
        Assert.False(BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out _));
    }

    [Fact]
    public void ClearForPeer_RemovesTheOverride()
    {
        const int netId = 702002;
        BasisUserOpusBitrateStateManager.SetBitrate(netId, 48000);
        Assert.True(BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out _));

        BasisUserOpusBitrateStateManager.ClearForPeer(netId);

        Assert.False(BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out _));
    }

    [Fact]
    public void SetGlobalBitrate_ClampsAndZeroClears()
    {
        try
        {
            Assert.Equal(48000, BasisUserOpusBitrateStateManager.SetGlobalBitrate(48000));
            Assert.Equal(48000, BasisUserOpusBitrateStateManager.GlobalBitrate);
            Assert.Equal(BasisUserOpusBitrateStateManager.MinBitrate, BasisUserOpusBitrateStateManager.SetGlobalBitrate(100));
            Assert.Equal(BasisUserOpusBitrateStateManager.MaxBitrate, BasisUserOpusBitrateStateManager.SetGlobalBitrate(int.MaxValue));
            Assert.Equal(0, BasisUserOpusBitrateStateManager.SetGlobalBitrate(-1));
            Assert.Equal(0, BasisUserOpusBitrateStateManager.GlobalBitrate);
        }
        finally
        {
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(0);
        }
    }

    [Fact]
    public void EffectiveBitrate_PerUserOverrideWinsOverTheGlobal()
    {
        const int overridden = 703001;
        const int plain = 703002;
        try
        {
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(0);
            Assert.Equal(0, BasisUserOpusBitrateStateManager.EffectiveBitrateFor(plain));

            BasisUserOpusBitrateStateManager.SetGlobalBitrate(64000);
            Assert.Equal(64000, BasisUserOpusBitrateStateManager.EffectiveBitrateFor(plain));

            BasisUserOpusBitrateStateManager.SetBitrate(overridden, 24000);
            Assert.Equal(24000, BasisUserOpusBitrateStateManager.EffectiveBitrateFor(overridden));
            Assert.Equal(64000, BasisUserOpusBitrateStateManager.EffectiveBitrateFor(plain));

            BasisUserOpusBitrateStateManager.SetBitrate(overridden, 0);
            Assert.Equal(64000, BasisUserOpusBitrateStateManager.EffectiveBitrateFor(overridden));
        }
        finally
        {
            BasisUserOpusBitrateStateManager.ClearForPeer(overridden);
            BasisUserOpusBitrateStateManager.ClearForPeer(plain);
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(0);
        }
    }

    [Fact]
    public void ParallelSetQueryClearStorm_LeavesConsistentState()
    {
        const int baseId = 704000;
        Parallel.For(0, 4096, i =>
        {
            int netId = baseId + (i & 63);
            switch (i % 3)
            {
                case 0:
                    BasisUserOpusBitrateStateManager.SetBitrate(netId, 6000 + i % 1000 * 100);
                    break;
                case 1:
                    if (BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out int seen))
                    {
                        Assert.InRange(seen, BasisUserOpusBitrateStateManager.MinBitrate, BasisUserOpusBitrateStateManager.MaxBitrate);
                    }
                    break;
                default:
                    BasisUserOpusBitrateStateManager.ClearForPeer(netId);
                    break;
            }
        });

        for (int offset = 0; offset < 64; offset++)
        {
            int netId = baseId + offset;
            Assert.Equal(32000, BasisUserOpusBitrateStateManager.SetBitrate(netId, 32000));
            Assert.True(BasisUserOpusBitrateStateManager.TryGetBitrate(netId, out int stored));
            Assert.Equal(32000, stored);
            BasisUserOpusBitrateStateManager.ClearForPeer(netId);
        }
    }

    [Fact]
    public void SendStateToPeer_PushesThatPeersEffectiveBitrate()
    {
        var peer = new PolicyTestPeer(705001);
        try
        {
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(48000);
            BasisUserOpusBitrateStateManager.SetBitrate(peer.Id, 24000);
            BasisUserOpusBitrateStateManager.SendStateToPeer(peer);

            var reader = new NetDataReader(Assert.Single(peer.Sent));
            Assert.Equal((byte)AdminRequestMode.UserOpusBitrateOverride, reader.GetByte());
            Assert.Equal(24000, reader.GetInt());
            Assert.Equal(0, reader.AvailableBytes);
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);

            peer.Sent.Clear();
            BasisUserOpusBitrateStateManager.ClearForPeer(peer.Id);
            BasisUserOpusBitrateStateManager.SendStateToPeer(peer);

            reader = new NetDataReader(Assert.Single(peer.Sent));
            Assert.Equal((byte)AdminRequestMode.UserOpusBitrateOverride, reader.GetByte());
            Assert.Equal(48000, reader.GetInt()); // no per-user override left: falls back to the global
        }
        finally
        {
            BasisUserOpusBitrateStateManager.ClearForPeer(peer.Id);
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(0);
        }
    }

    [Fact]
    public void SendGlobalStateToPeer_WritesModeByteThenGlobalBitrate()
    {
        var peer = new PolicyTestPeer(705002);
        try
        {
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(96000);
            BasisUserOpusBitrateStateManager.SendGlobalStateToPeer(peer);

            var reader = new NetDataReader(Assert.Single(peer.Sent));
            Assert.Equal((byte)AdminRequestMode.GlobalGetOpusBitrateState, reader.GetByte());
            Assert.Equal(96000, reader.GetInt());
            Assert.Equal(0, reader.AvailableBytes);

            BasisUserOpusBitrateStateManager.BroadcastGlobalState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisUserOpusBitrateStateManager.SetGlobalBitrate(0);
        }
    }

    [Fact]
    public void PushEffectiveToAllPeers_WithNoConnectedPeers_DoesNothing()
    {
        Assert.Empty(NetworkServer.PeerSnapshot);
        BasisUserOpusBitrateStateManager.PushEffectiveToAllPeers();
    }
}

/// <summary>
/// Headless connection policy: platform-id classification, the runtime disallow
/// toggle, and its GlobalGetHeadlessDisallowState payload.
/// </summary>
[Collection("BasisServer shared network statics")] // asserts on NetworkServer.PeerSnapshot — must not race tests that populate it
public class BasisHeadlessConnectionPolicyManagerTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Headless", true)]
    [InlineData("headless", true)]
    [InlineData("HEADLESS", true)]
    [InlineData("WindowsServer", true)]
    [InlineData("windowsserver", true)]
    [InlineData("LinuxServer", true)]
    [InlineData("OSXServer", true)]
    [InlineData("Headless ", false)] // exact match, no trimming
    [InlineData("Windows", false)]
    [InlineData("Android", false)]
    [InlineData("Server", false)]
    public void IsHeadlessPlatform_MatchesTheFourServerPlatformIds(string? platform, bool expected)
    {
        Assert.Equal(expected, BasisHeadlessConnectionPolicyManager.IsHeadlessPlatform(platform));
    }

    [Fact]
    public void IsHeadlessClient_ReadsThePlatformFieldOfTheMetaData()
    {
        var headless = new global::SerializableBasis.ClientMetaDataMessage { playerPlatform = "LinuxServer" };
        Assert.True(BasisHeadlessConnectionPolicyManager.IsHeadlessClient(headless));

        var desktop = new global::SerializableBasis.ClientMetaDataMessage { playerPlatform = "Windows" };
        Assert.False(BasisHeadlessConnectionPolicyManager.IsHeadlessClient(desktop));
    }

    [Fact]
    public void InitializeFromConfig_AndSetDisallowHeadless_TrackChanges()
    {
        try
        {
            BasisHeadlessConnectionPolicyManager.InitializeFromConfig(false);
            Assert.False(BasisHeadlessConnectionPolicyManager.HeadlessDisallowed);

            Assert.True(BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(true));
            Assert.True(BasisHeadlessConnectionPolicyManager.HeadlessDisallowed);
            Assert.False(BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(true));
            Assert.True(BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(false));
            Assert.False(BasisHeadlessConnectionPolicyManager.HeadlessDisallowed);

            BasisHeadlessConnectionPolicyManager.InitializeFromConfig(true);
            Assert.True(BasisHeadlessConnectionPolicyManager.HeadlessDisallowed);
        }
        finally
        {
            BasisHeadlessConnectionPolicyManager.InitializeFromConfig(false);
        }
    }

    [Fact]
    public void DisconnectConnectedHeadlessPeers_WithNoPeers_IsANoOp()
    {
        Assert.Empty(NetworkServer.PeerSnapshot);
        BasisHeadlessConnectionPolicyManager.DisconnectConnectedHeadlessPeers();
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenDisallowFlag()
    {
        try
        {
            BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(true);
            var peer = new PolicyTestPeer(12);
            BasisHeadlessConnectionPolicyManager.SendStateToPeer(peer);

            Assert.Equal(new[] { (byte)AdminRequestMode.GlobalGetHeadlessDisallowState, (byte)1 }, Assert.Single(peer.Sent));
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);

            BasisHeadlessConnectionPolicyManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(false);
        }
    }
}

/// <summary>
/// Headless audio playback toggle and its GlobalGetHeadlessAudioState payload.
/// </summary>
public class BasisHeadlessAudioStateManagerTests
{
    [Fact]
    public void SetHeadlessAudio_TogglesAndReportsOnlyRealChanges()
    {
        try
        {
            BasisHeadlessAudioStateManager.SetHeadlessAudio(false);
            Assert.False(BasisHeadlessAudioStateManager.HeadlessAudioOff);

            Assert.True(BasisHeadlessAudioStateManager.SetHeadlessAudio(true));
            Assert.True(BasisHeadlessAudioStateManager.HeadlessAudioOff);
            Assert.False(BasisHeadlessAudioStateManager.SetHeadlessAudio(true));
            Assert.True(BasisHeadlessAudioStateManager.SetHeadlessAudio(false));
            Assert.False(BasisHeadlessAudioStateManager.HeadlessAudioOff);
        }
        finally
        {
            BasisHeadlessAudioStateManager.SetHeadlessAudio(false);
        }
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenOffFlag()
    {
        try
        {
            BasisHeadlessAudioStateManager.SetHeadlessAudio(true);
            var peer = new PolicyTestPeer(13);
            BasisHeadlessAudioStateManager.SendStateToPeer(peer);

            Assert.Equal(new[] { (byte)AdminRequestMode.GlobalGetHeadlessAudioState, (byte)1 }, Assert.Single(peer.Sent));
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, peer.LastDelivery);

            BasisHeadlessAudioStateManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisHeadlessAudioStateManager.SetHeadlessAudio(false);
        }
    }
}

/// <summary>
/// Client crash/error reporting toggle seeded from Configuration.CrashReportingEnabled.
/// </summary>
public class BasisCrashReportStateManagerTests
{
    [Fact]
    public void InitializeFromConfig_SeedsFromCrashReportingEnabled()
    {
        try
        {
            BasisCrashReportStateManager.InitializeFromConfig(new Configuration()); // default config ships with reporting on
            Assert.True(BasisCrashReportStateManager.Enabled);

            BasisCrashReportStateManager.InitializeFromConfig(new Configuration { CrashReportingEnabled = false });
            Assert.False(BasisCrashReportStateManager.Enabled);
        }
        finally
        {
            BasisCrashReportStateManager.SetEnabled(true);
        }
    }

    [Fact]
    public void SetEnabled_ReportsOnlyRealChanges()
    {
        try
        {
            BasisCrashReportStateManager.SetEnabled(true);
            Assert.True(BasisCrashReportStateManager.SetEnabled(false));
            Assert.False(BasisCrashReportStateManager.Enabled);
            Assert.False(BasisCrashReportStateManager.SetEnabled(false));
            Assert.True(BasisCrashReportStateManager.SetEnabled(true));
            Assert.True(BasisCrashReportStateManager.Enabled);
        }
        finally
        {
            BasisCrashReportStateManager.SetEnabled(true);
        }
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenEnabledFlag()
    {
        try
        {
            BasisCrashReportStateManager.SetEnabled(false);
            var peer = new PolicyTestPeer(14);
            BasisCrashReportStateManager.SendStateToPeer(peer);

            Assert.Equal(new[] { (byte)AdminRequestMode.GlobalGetCrashReportState, (byte)0 }, Assert.Single(peer.Sent));
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);

            BasisCrashReportStateManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisCrashReportStateManager.SetEnabled(true);
        }
    }
}

/// <summary>
/// Microphone / hearing range ceilings: non-positive values fall back to the
/// 25 m default, everything else passes through.
/// </summary>
public class BasisAudioRangeLimitManagerTests
{
    [Theory]
    [InlineData(10f, 20f, 10f, 20f)]
    [InlineData(0f, 5f, 25f, 5f)]
    [InlineData(5f, 0f, 5f, 25f)]
    [InlineData(-1f, -2f, 25f, 25f)]
    [InlineData(0.5f, 4000f, 0.5f, 4000f)]
    public void SetLimits_ReplacesNonPositiveValuesWithTheDefault(float mic, float hearing, float expectedMic, float expectedHearing)
    {
        try
        {
            BasisAudioRangeLimitManager.SetLimits(mic, hearing);
            Assert.Equal(expectedMic, BasisAudioRangeLimitManager.MaxMicrophoneRangeMeters);
            Assert.Equal(expectedHearing, BasisAudioRangeLimitManager.MaxHearingRangeMeters);
        }
        finally
        {
            BasisAudioRangeLimitManager.SetLimits(25f, 25f);
        }
    }

    [Fact]
    public void SetLimits_ReportsOnlyRealChanges()
    {
        BasisAudioRangeLimitManager.SetLimits(25f, 25f);
        Assert.False(BasisAudioRangeLimitManager.SetLimits(25f, 25f));
        Assert.False(BasisAudioRangeLimitManager.SetLimits(0f, -1f)); // sanitized straight back to the 25 m default
        Assert.True(BasisAudioRangeLimitManager.SetLimits(12f, 25f));
        Assert.True(BasisAudioRangeLimitManager.SetLimits(25f, 25f));
    }

    [Fact]
    public void InitializeFromConfig_AppliesTheConfiguredCeilings()
    {
        try
        {
            BasisAudioRangeLimitManager.InitializeFromConfig(new Configuration
            {
                MaxMicrophoneRangeMeters = 12f,
                MaxHearingRangeMeters = 34f,
            });
            Assert.Equal(12f, BasisAudioRangeLimitManager.MaxMicrophoneRangeMeters);
            Assert.Equal(34f, BasisAudioRangeLimitManager.MaxHearingRangeMeters);

            BasisAudioRangeLimitManager.InitializeFromConfig(new Configuration());
            Assert.Equal(25f, BasisAudioRangeLimitManager.MaxMicrophoneRangeMeters);
            Assert.Equal(25f, BasisAudioRangeLimitManager.MaxHearingRangeMeters);
        }
        finally
        {
            BasisAudioRangeLimitManager.SetLimits(25f, 25f);
        }
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenBothRanges()
    {
        try
        {
            BasisAudioRangeLimitManager.SetLimits(12.5f, 40.25f);
            var peer = new PolicyTestPeer(15);
            BasisAudioRangeLimitManager.SendStateToPeer(peer);

            var reader = new NetDataReader(Assert.Single(peer.Sent));
            Assert.Equal((byte)AdminRequestMode.GlobalGetAudioRangeLimits, reader.GetByte());
            Assert.Equal(12.5f, reader.GetFloat());
            Assert.Equal(40.25f, reader.GetFloat());
            Assert.Equal(0, reader.AvailableBytes);
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);

            BasisAudioRangeLimitManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisAudioRangeLimitManager.SetLimits(25f, 25f);
        }
    }
}

/// <summary>
/// Avatar eye-height scale limits: NaN/Infinity/non-positive fall back to the
/// defaults, the absolute floor/ceiling clamp, and min is kept at or below max.
/// </summary>
public class BasisAvatarScaleLimitManagerTests
{
    [Theory]
    [InlineData(0.5f, 2f, 0.5f, 2f)]
    [InlineData(float.NaN, 2f, 0.1f, 2f)]
    [InlineData(0.5f, float.NaN, 0.5f, 100f)]
    [InlineData(float.PositiveInfinity, 50f, 0.1f, 50f)]
    [InlineData(0.5f, float.NegativeInfinity, 0.5f, 100f)]
    [InlineData(0f, 0f, 0.1f, 100f)]
    [InlineData(-3f, -3f, 0.1f, 100f)]
    [InlineData(0.005f, 5f, 0.01f, 5f)]
    [InlineData(1f, 5000f, 1f, 1000f)]
    [InlineData(5f, 2f, 5f, 5f)]
    public void SetLimits_SanitizesAndKeepsMinAtOrBelowMax(float min, float max, float expectedMin, float expectedMax)
    {
        try
        {
            BasisAvatarScaleLimitManager.SetLimits(min, max);
            Assert.Equal(expectedMin, BasisAvatarScaleLimitManager.MinMeters);
            Assert.Equal(expectedMax, BasisAvatarScaleLimitManager.MaxMeters);
        }
        finally
        {
            BasisAvatarScaleLimitManager.SetLimits(0.1f, 100f);
        }
    }

    [Fact]
    public void SetLimits_ReportsOnlyRealChanges()
    {
        BasisAvatarScaleLimitManager.SetLimits(0.25f, 3f);
        Assert.False(BasisAvatarScaleLimitManager.SetLimits(0.25f, 3f));
        Assert.True(BasisAvatarScaleLimitManager.SetLimits(0.25f, 4f));
        Assert.True(BasisAvatarScaleLimitManager.SetLimits(0.1f, 100f));
        Assert.False(BasisAvatarScaleLimitManager.SetLimits(float.NaN, float.NaN)); // sanitizes onto the defaults already stored
    }

    [Fact]
    public void InitializeFromConfig_UsesTheConfiguredEyeHeightRange()
    {
        try
        {
            BasisAvatarScaleLimitManager.InitializeFromConfig(new Configuration
            {
                MinAvatarEyeHeightMeters = 0.5f,
                MaxAvatarEyeHeightMeters = 3f,
            });
            Assert.Equal(0.5f, BasisAvatarScaleLimitManager.MinMeters);
            Assert.Equal(3f, BasisAvatarScaleLimitManager.MaxMeters);

            BasisAvatarScaleLimitManager.InitializeFromConfig(new Configuration());
            Assert.Equal(0.1f, BasisAvatarScaleLimitManager.MinMeters);
            Assert.Equal(100f, BasisAvatarScaleLimitManager.MaxMeters);
        }
        finally
        {
            BasisAvatarScaleLimitManager.SetLimits(0.1f, 100f);
        }
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenMinAndMax()
    {
        try
        {
            BasisAvatarScaleLimitManager.SetLimits(0.25f, 8f);
            var peer = new PolicyTestPeer(16);
            BasisAvatarScaleLimitManager.SendStateToPeer(peer);

            var reader = new NetDataReader(Assert.Single(peer.Sent));
            Assert.Equal((byte)AdminRequestMode.GlobalGetAvatarScaleLimits, reader.GetByte());
            Assert.Equal(0.25f, reader.GetFloat());
            Assert.Equal(8f, reader.GetFloat());
            Assert.Equal(0, reader.AvailableBytes);
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);

            BasisAvatarScaleLimitManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            BasisAvatarScaleLimitManager.SetLimits(0.1f, 100f);
        }
    }
}
