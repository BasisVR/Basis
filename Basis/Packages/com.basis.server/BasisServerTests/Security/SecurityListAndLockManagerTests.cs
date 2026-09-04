using Basis.Network.Core;
using Basis.Network.Server.Auth;
using BasisNetworkCore.Security;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using BasisPermissions;
using System.Net;
using Xunit;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Offline stand-in for a connected client: implements the Basis.Network.Core.NetPeer
/// shell interface and records every payload the server "sends" to it, so the
/// Send*ToPeer paths can be exercised without a socket.
/// </summary>
internal sealed class SecurityTestPeer : NetPeer
{
    public SecurityTestPeer(int id) => Id = id;

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
/// File-backed allow list: ordinal case-sensitive membership, one id per line,
/// appended on add and rewritten on remove.
/// </summary>
public class BasisAllowListTests
{
    private static string NewPath() => $"BasisAllowListTests-{Guid.NewGuid():N}.txt";
    private static string NewId(string prefix = "Player") => $"{prefix}-{Guid.NewGuid():N}";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingFile_NothingAllowed_AndNoFileCreated()
    {
        string path = NewPath();
        var list = new BasisAllowList(path);
        Assert.False(list.IsAllowed(NewId()));
        Assert.False(list.IsAllowed(string.Empty));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Add_Allows_CaseSensitively_AndAppendsToFile()
    {
        string path = NewPath();
        try
        {
            var list = new BasisAllowList(path);
            string id = NewId("Player-MiXeD");
            await list.AddToAllowlistAsync(id);

            Assert.True(list.IsAllowed(id));
            Assert.False(list.IsAllowed(id.ToLowerInvariant()));
            Assert.False(list.IsAllowed(id.ToUpperInvariant()));
            Assert.Contains(id, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Add_SameIdTwice_AppendsOnlyOnce()
    {
        string path = NewPath();
        try
        {
            var list = new BasisAllowList(path);
            string id = NewId();
            await list.AddToAllowlistAsync(id);
            await list.AddToAllowlistAsync(id);

            Assert.Equal(new[] { id }, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Remove_Disallows_AndRewritesFileWithoutTheId()
    {
        string path = NewPath();
        try
        {
            var list = new BasisAllowList(path);
            string keep = NewId("keep");
            string drop = NewId("drop");
            await list.AddToAllowlistAsync(keep);
            await list.AddToAllowlistAsync(drop);

            await list.RemoveFromAllowlistAsync(drop);

            Assert.False(list.IsAllowed(drop));
            Assert.True(list.IsAllowed(keep));
            Assert.Equal(new[] { keep }, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Remove_UnknownId_IsANoOp()
    {
        string path = NewPath();
        var list = new BasisAllowList(path);
        await list.RemoveFromAllowlistAsync(NewId());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PersistedList_IsVisibleToASecondInstanceAfterReload()
    {
        string path = NewPath();
        try
        {
            var writerInstance = new BasisAllowList(path);
            string idA = NewId("a");
            string idB = NewId("b");
            await writerInstance.AddToAllowlistAsync(idA);
            await writerInstance.AddToAllowlistAsync(idB);

            var readerInstance = new BasisAllowList(path);
            await readerInstance.ReloadAllowlistAsync();

            Assert.True(readerInstance.IsAllowed(idA));
            Assert.True(readerInstance.IsAllowed(idB));
            Assert.False(readerInstance.IsAllowed(NewId("absent")));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Load_TrimsWhitespace_AndSkipsBlankLines()
    {
        string path = NewPath();
        try
        {
            string id = NewId();
            await File.WriteAllTextAsync(path, $"  {id}  {Environment.NewLine}{Environment.NewLine}   {Environment.NewLine}");

            var list = new BasisAllowList(path);
            await list.ReloadAllowlistAsync();

            Assert.True(list.IsAllowed(id));
            Assert.False(list.IsAllowed($"  {id}  "));
            Assert.False(list.IsAllowed(string.Empty));
        }
        finally
        {
            TryDelete(path);
        }
    }
}

/// <summary>
/// File-backed ban list: same persistence contract as the allow list, but the
/// constructor loads the file synchronously.
/// </summary>
public class BasisBanListTests
{
    private static string NewPath() => $"BasisBanListTests-{Guid.NewGuid():N}.txt";
    private static string NewId(string prefix = "Player") => $"{prefix}-{Guid.NewGuid():N}";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingFile_NobodyBanned_AndNoFileCreated()
    {
        string path = NewPath();
        var list = new BasisBanList(path);
        Assert.False(list.IsBanned(NewId()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Add_Bans_CaseSensitively_AndAppendsToFile()
    {
        string path = NewPath();
        try
        {
            var list = new BasisBanList(path);
            string id = NewId("Griefer-MiXeD");
            await list.AddToBanListAsync(id);

            Assert.True(list.IsBanned(id));
            Assert.False(list.IsBanned(id.ToLowerInvariant()));
            Assert.False(list.IsBanned(id.ToUpperInvariant()));
            Assert.Contains(id, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Add_SameIdTwice_AppendsOnlyOnce()
    {
        string path = NewPath();
        try
        {
            var list = new BasisBanList(path);
            string id = NewId();
            await list.AddToBanListAsync(id);
            await list.AddToBanListAsync(id);

            Assert.Equal(new[] { id }, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Remove_Unbans_AndRewritesFileWithoutTheId()
    {
        string path = NewPath();
        try
        {
            var list = new BasisBanList(path);
            string keep = NewId("keep");
            string drop = NewId("drop");
            await list.AddToBanListAsync(keep);
            await list.AddToBanListAsync(drop);

            await list.RemoveFromBanListAsync(drop);

            Assert.False(list.IsBanned(drop));
            Assert.True(list.IsBanned(keep));
            Assert.Equal(new[] { keep }, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Constructor_LoadsExistingFileSynchronously_TrimmingAndSkippingBlanks()
    {
        string path = NewPath();
        try
        {
            string id = NewId();
            File.WriteAllText(path, $"  {id}  {Environment.NewLine}{Environment.NewLine}   {Environment.NewLine}");

            var list = new BasisBanList(path);

            Assert.True(list.IsBanned(id));
            Assert.False(list.IsBanned($"  {id}  "));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Reload_PicksUpExternalFileEdits()
    {
        string path = NewPath();
        try
        {
            var list = new BasisBanList(path);
            string id = NewId();
            Assert.False(list.IsBanned(id));

            await File.WriteAllTextAsync(path, id + Environment.NewLine);
            await list.ReloadBanListAsync();

            Assert.True(list.IsBanned(id));
        }
        finally
        {
            TryDelete(path);
        }
    }
}

/// <summary>
/// Runtime-only rejoin lockdown: the set is only populated by capturing the currently
/// authenticated peers and never grows afterwards.
/// </summary>
[Collection("BasisServer shared network statics")]
public class BasisRejoinLockManagerTests
{
    private sealed class FixedUuidAuthIdentity : IAuthIdentity
    {
        private readonly string _uuid;
        public FixedUuidAuthIdentity(string uuid) => _uuid = uuid;

        public void ProcessConnection(Configuration configuration, ConnectionRequest connectionRequest, NetPeer netPeer) { }
        public void DeInitialize() { }
        public void RemoveConnection(int netPeer) { }
        public bool RemoveConnection(int netPeer, NetPeer expected) => false;
        public bool NetIDToUUID(NetPeer peer, out string uuid)
        {
            uuid = _uuid;
            return true;
        }
        public bool UUIDToNetID(string uuid, out int peer)
        {
            peer = 0;
            return false;
        }
    }

    [Fact]
    public void FreshOrCleared_NothingIsAllowed()
    {
        BasisRejoinLockManager.Clear();
        Assert.Equal(0, BasisRejoinLockManager.Count);
        Assert.False(BasisRejoinLockManager.IsAllowed("nobody"));
    }

    [Fact]
    public void IsAllowed_NullOrEmpty_IsFalse()
    {
        BasisRejoinLockManager.Clear();
        Assert.False(BasisRejoinLockManager.IsAllowed(null));
        Assert.False(BasisRejoinLockManager.IsAllowed(string.Empty));
    }

    [Fact]
    public void Capture_WithoutAuthIdentity_LeavesTheSetEmpty()
    {
        IAuthIdentity previous = NetworkServer.AuthIdentity;
        try
        {
            NetworkServer.AuthIdentity = null;
            BasisRejoinLockManager.CaptureCurrentPopulation();
            Assert.Equal(0, BasisRejoinLockManager.Count);
        }
        finally
        {
            NetworkServer.AuthIdentity = previous;
            BasisRejoinLockManager.Clear();
        }
    }

    [Fact]
    public void Capture_SnapshotsConnectedUuids_SoTheyMayRejoin()
    {
        const int peerId = 910001;
        string uuid = $"rejoin-{Guid.NewGuid():N}";
        IAuthIdentity previous = NetworkServer.AuthIdentity;
        try
        {
            NetworkServer.AuthIdentity = new FixedUuidAuthIdentity(uuid);
            Assert.True(NetworkServer.AuthenticatedPeers.TryAdd(peerId, new SecurityTestPeer(peerId)));

            BasisRejoinLockManager.CaptureCurrentPopulation();

            Assert.Equal(1, BasisRejoinLockManager.Count);
            Assert.True(BasisRejoinLockManager.IsAllowed(uuid));
            Assert.False(BasisRejoinLockManager.IsAllowed($"stranger-{uuid}"));
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peerId, out _);
            NetworkServer.AuthIdentity = previous;
            BasisRejoinLockManager.Clear();
        }
    }

    [Fact]
    public void Capture_ReplacesThePreviousSnapshot()
    {
        const int peerId = 910002;
        string firstUuid = $"first-{Guid.NewGuid():N}";
        string secondUuid = $"second-{Guid.NewGuid():N}";
        IAuthIdentity previous = NetworkServer.AuthIdentity;
        try
        {
            Assert.True(NetworkServer.AuthenticatedPeers.TryAdd(peerId, new SecurityTestPeer(peerId)));

            NetworkServer.AuthIdentity = new FixedUuidAuthIdentity(firstUuid);
            BasisRejoinLockManager.CaptureCurrentPopulation();
            Assert.True(BasisRejoinLockManager.IsAllowed(firstUuid));

            NetworkServer.AuthIdentity = new FixedUuidAuthIdentity(secondUuid);
            BasisRejoinLockManager.CaptureCurrentPopulation();

            Assert.False(BasisRejoinLockManager.IsAllowed(firstUuid));
            Assert.True(BasisRejoinLockManager.IsAllowed(secondUuid));
            Assert.Equal(1, BasisRejoinLockManager.Count);
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peerId, out _);
            NetworkServer.AuthIdentity = previous;
            BasisRejoinLockManager.Clear();
        }
    }

    [Fact]
    public void Clear_RevokesEveryCapturedUuid()
    {
        const int peerId = 910003;
        string uuid = $"revoked-{Guid.NewGuid():N}";
        IAuthIdentity previous = NetworkServer.AuthIdentity;
        try
        {
            NetworkServer.AuthIdentity = new FixedUuidAuthIdentity(uuid);
            Assert.True(NetworkServer.AuthenticatedPeers.TryAdd(peerId, new SecurityTestPeer(peerId)));
            BasisRejoinLockManager.CaptureCurrentPopulation();
            Assert.True(BasisRejoinLockManager.IsAllowed(uuid));

            BasisRejoinLockManager.Clear();

            Assert.False(BasisRejoinLockManager.IsAllowed(uuid));
            Assert.Equal(0, BasisRejoinLockManager.Count);
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peerId, out _);
            NetworkServer.AuthIdentity = previous;
            BasisRejoinLockManager.Clear();
        }
    }
}

/// <summary>
/// Server-wide interlocked lock toggles seeded from Configuration, plus the
/// append-only GlobalGetLockState wire layout.
/// </summary>
[Collection("BasisServer shared network statics")]
public class BasisGlobalLockManagerTests
{
    private static Configuration AllUnlocked() => new()
    {
        AvatarsLocked = false,
        PropsLocked = false,
        WorldsLocked = false,
        ServersLocked = false,
        ThirdPersonDisabled = false,
        AdditionalAvatarDataLock = false,
        CameraMetadataDisallowMask = 0,
        PlayspaceMoverLocked = false,
        DirectConnectLocked = false,
        CilboxLocked = false,
        ImagesLocked = false,
        EndEffectorIKDisabled = false,
        TextChatLocked = false,
        VoiceChatLocked = false,
        MediaPlayerLocked = false,
        CameraCaptureLocked = false,
        PropGrabbingLocked = false,
    };

    private static Configuration AllLocked() => new()
    {
        AvatarsLocked = true,
        PropsLocked = true,
        WorldsLocked = true,
        ServersLocked = true,
        ThirdPersonDisabled = true,
        AdditionalAvatarDataLock = true,
        CameraMetadataDisallowMask = 0xAB,
        PlayspaceMoverLocked = true,
        DirectConnectLocked = true,
        CilboxLocked = true,
        ImagesLocked = true,
        EndEffectorIKDisabled = true,
        TextChatLocked = true,
        VoiceChatLocked = true,
        MediaPlayerLocked = true,
        CameraCaptureLocked = true,
        PropGrabbingLocked = true,
    };

    private static void AssertAllFlags(bool expected)
    {
        Assert.Equal(expected, BasisGlobalLockManager.AvatarsLocked);
        Assert.Equal(expected, BasisGlobalLockManager.PropsLocked);
        Assert.Equal(expected, BasisGlobalLockManager.WorldsLocked);
        Assert.Equal(expected, BasisGlobalLockManager.ServersLocked);
        Assert.Equal(expected, BasisGlobalLockManager.ThirdPersonDisabled);
        Assert.Equal(expected, BasisGlobalLockManager.AdditionalAvatarDataLock);
        Assert.Equal(expected, BasisGlobalLockManager.PlayspaceMoverLocked);
        Assert.Equal(expected, BasisGlobalLockManager.DirectConnectLocked);
        Assert.Equal(expected, BasisGlobalLockManager.CilboxLocked);
        Assert.Equal(expected, BasisGlobalLockManager.ImagesLocked);
        Assert.Equal(expected, BasisGlobalLockManager.EndEffectorIKDisabled);
        Assert.Equal(expected, BasisGlobalLockManager.TextChatLocked);
        Assert.Equal(expected, BasisGlobalLockManager.VoiceChatLocked);
        Assert.Equal(expected, BasisGlobalLockManager.MediaPlayerLocked);
        Assert.Equal(expected, BasisGlobalLockManager.CameraCaptureLocked);
        Assert.Equal(expected, BasisGlobalLockManager.PropGrabbingLocked);
    }

    [Fact]
    public void InitializeFromConfig_SeedsEveryFlagAndTheMask()
    {
        try
        {
            BasisGlobalLockManager.InitializeFromConfig(AllLocked());
            AssertAllFlags(true);
            Assert.Equal(0xAB, BasisGlobalLockManager.CameraMetadataDisallowMask);
        }
        finally
        {
            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        }
        AssertAllFlags(false);
        Assert.Equal(0, BasisGlobalLockManager.CameraMetadataDisallowMask);
    }

    [Fact]
    public void DefaultConfiguration_BootsWithOnlyWorldsLocked()
    {
        try
        {
            BasisGlobalLockManager.InitializeFromConfig(new Configuration());
            Assert.True(BasisGlobalLockManager.WorldsLocked);
            Assert.False(BasisGlobalLockManager.AvatarsLocked);
            Assert.False(BasisGlobalLockManager.PropsLocked);
            Assert.False(BasisGlobalLockManager.ServersLocked);
            Assert.False(BasisGlobalLockManager.ThirdPersonDisabled);
            Assert.False(BasisGlobalLockManager.AdditionalAvatarDataLock);
            Assert.False(BasisGlobalLockManager.PlayspaceMoverLocked);
            Assert.False(BasisGlobalLockManager.DirectConnectLocked);
            Assert.False(BasisGlobalLockManager.CilboxLocked);
            Assert.False(BasisGlobalLockManager.ImagesLocked);
            Assert.False(BasisGlobalLockManager.EndEffectorIKDisabled);
            Assert.False(BasisGlobalLockManager.TextChatLocked);
            Assert.False(BasisGlobalLockManager.VoiceChatLocked);
            Assert.False(BasisGlobalLockManager.MediaPlayerLocked);
            Assert.False(BasisGlobalLockManager.CameraCaptureLocked);
            Assert.False(BasisGlobalLockManager.PropGrabbingLocked);
            Assert.Equal(0, BasisGlobalLockManager.CameraMetadataDisallowMask);
        }
        finally
        {
            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        }
    }

    [Fact]
    public void EveryToggle_FlipsItsFlag_AndReturnsTheNewState()
    {
        BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        var toggles = new (Func<bool> Toggle, Func<bool> State)[]
        {
            (BasisGlobalLockManager.ToggleAvatars, () => BasisGlobalLockManager.AvatarsLocked),
            (BasisGlobalLockManager.ToggleProps, () => BasisGlobalLockManager.PropsLocked),
            (BasisGlobalLockManager.ToggleWorlds, () => BasisGlobalLockManager.WorldsLocked),
            (BasisGlobalLockManager.ToggleServers, () => BasisGlobalLockManager.ServersLocked),
            (BasisGlobalLockManager.ToggleThirdPerson, () => BasisGlobalLockManager.ThirdPersonDisabled),
            (BasisGlobalLockManager.ToggleAdditionalAvatarDataLock, () => BasisGlobalLockManager.AdditionalAvatarDataLock),
            (BasisGlobalLockManager.TogglePlayspaceMover, () => BasisGlobalLockManager.PlayspaceMoverLocked),
            (BasisGlobalLockManager.ToggleDirectConnect, () => BasisGlobalLockManager.DirectConnectLocked),
            (BasisGlobalLockManager.ToggleCilbox, () => BasisGlobalLockManager.CilboxLocked),
            (BasisGlobalLockManager.ToggleImages, () => BasisGlobalLockManager.ImagesLocked),
            (BasisGlobalLockManager.ToggleEndEffectorIK, () => BasisGlobalLockManager.EndEffectorIKDisabled),
            (BasisGlobalLockManager.ToggleTextChat, () => BasisGlobalLockManager.TextChatLocked),
            (BasisGlobalLockManager.ToggleVoiceChat, () => BasisGlobalLockManager.VoiceChatLocked),
            (BasisGlobalLockManager.ToggleMediaPlayer, () => BasisGlobalLockManager.MediaPlayerLocked),
            (BasisGlobalLockManager.ToggleCameraCapture, () => BasisGlobalLockManager.CameraCaptureLocked),
            (BasisGlobalLockManager.TogglePropGrabbing, () => BasisGlobalLockManager.PropGrabbingLocked),
            (BasisGlobalLockManager.ToggleSafeDisplayNames, () => BasisGlobalLockManager.SafeDisplayNamesForced),
        };

        foreach ((Func<bool> toggle, Func<bool> state) in toggles)
        {
            Assert.False(state());
            Assert.True(toggle());
            Assert.True(state());
            Assert.False(toggle());
            Assert.False(state());
        }
    }

    [Fact]
    public void CameraMetadataMask_RoundTripsTheWholeByte()
    {
        try
        {
            BasisGlobalLockManager.SetCameraMetadataDisallowMask(0x5A);
            Assert.Equal(0x5A, BasisGlobalLockManager.CameraMetadataDisallowMask);
            BasisGlobalLockManager.SetCameraMetadataDisallowMask(byte.MaxValue);
            Assert.Equal(byte.MaxValue, BasisGlobalLockManager.CameraMetadataDisallowMask);
        }
        finally
        {
            BasisGlobalLockManager.SetCameraMetadataDisallowMask(0);
        }
        Assert.Equal(0, BasisGlobalLockManager.CameraMetadataDisallowMask);
    }

    [Fact]
    public void ParallelToggles_AlternateAtomically()
    {
        BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        int lockedResults = 0;
        Parallel.For(0, 100, _ =>
        {
            if (BasisGlobalLockManager.ToggleImages())
            {
                Interlocked.Increment(ref lockedResults);
            }
        });
        // 100 atomic flips from unlocked: the state alternates strictly, so exactly half
        // of the calls observe the locked state and the final state is unlocked again.
        Assert.Equal(50, lockedResults);
        Assert.False(BasisGlobalLockManager.ImagesLocked);
    }

    [Fact]
    public void SendLockStateToPeer_WritesTheAppendOnlyWireLayout()
    {
        Configuration previousConfiguration = NetworkServer.Configuration;
        try
        {
            NetworkServer.Configuration = new Configuration
            {
                BasisUserRestrictionMode = BasisUserRestrictionMode.AllowList,
            };
            BasisGlobalLockManager.InitializeFromConfig(new Configuration
            {
                AvatarsLocked = true,
                PropsLocked = false,
                WorldsLocked = true,
                ServersLocked = false,
                ThirdPersonDisabled = true,
                AdditionalAvatarDataLock = false,
                CameraMetadataDisallowMask = 0x5A,
                PlayspaceMoverLocked = true,
                DirectConnectLocked = false,
                CilboxLocked = true,
                ImagesLocked = false,
                EndEffectorIKDisabled = true,
                TextChatLocked = true,
                VoiceChatLocked = false,
                MediaPlayerLocked = true,
                CameraCaptureLocked = false,
                PropGrabbingLocked = true,
                SafeDisplayNamesForced = true,
            });

            var peer = new SecurityTestPeer(1);
            BasisGlobalLockManager.SendLockStateToPeer(peer);

            byte[] payload = Assert.Single(peer.Sent);
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, peer.LastDelivery);
            Assert.Equal(new byte[]
            {
                (byte)AdminRequestMode.GlobalGetLockState,
                1, 0, 1, 0,                               // avatars, props, worlds, servers
                1, 0,                                     // third person, additional avatar data
                0x5A,                                     // camera metadata disallow mask
                (byte)BasisUserRestrictionMode.AllowList, // restriction mode
                1, 0,                                     // playspace mover, direct connect
                1, 0,                                     // cilbox, images
                1,                                        // end-effector IK disabled
                1,                                        // text chat locked
                0, 1, 0, 1,                               // voice, media player, camera capture, prop grabbing
                1,                                        // safe display names forced
            }, payload);

            BasisGlobalLockManager.BroadcastLockState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            NetworkServer.Configuration = previousConfiguration;
            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        }
    }

    /// <summary>
    /// The text-chat lock is enforced server-side, so the gate itself is the security boundary —
    /// it must block only while the lock is on, and must let basis.chat.lockbypass holders through.
    /// Uses the UUID overload deliberately: the NetPeer form mutates the shared
    /// NetworkServer.AuthIdentity, which perturbs the connection-lifecycle suite.
    /// </summary>
    [Fact]
    public void IsChatBlockedForUuid_BlocksOnlyLockedUsersWithoutTheBypassNode()
    {
        PermissionManager manager = PermissionManager.PermissionIntegration.Manager;
        string plainUuid = $"chat-plain-{Guid.NewGuid():N}";
        string bypassUuid = $"chat-bypass-{Guid.NewGuid():N}";

        try
        {
            manager.AddUserNode(bypassUuid, PermNodes.ChatLockBypass);

            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
            Assert.False(BasisNetworkChat.IsChatBlockedForUuid(plainUuid));
            Assert.False(BasisNetworkChat.IsChatBlockedForUuid(bypassUuid));

            Assert.True(BasisGlobalLockManager.ToggleTextChat());
            Assert.True(BasisNetworkChat.IsChatBlockedForUuid(plainUuid));
            Assert.False(BasisNetworkChat.IsChatBlockedForUuid(bypassUuid));
        }
        finally
        {
            manager.RemoveUserNode(bypassUuid, PermNodes.ChatLockBypass);
            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        }
    }

    /// <summary>
    /// Voice is the other server-enforced lock, and it gates BOTH the normal and announce paths —
    /// an announce-mode user must not keep broadcasting through a voice lock they can't bypass.
    /// </summary>
    [Fact]
    public void IsVoiceBlockedForUuid_BlocksOnlyLockedUsersWithoutTheBypassNode()
    {
        PermissionManager manager = PermissionManager.PermissionIntegration.Manager;
        string plainUuid = $"voice-plain-{Guid.NewGuid():N}";
        string bypassUuid = $"voice-bypass-{Guid.NewGuid():N}";

        try
        {
            manager.AddUserNode(bypassUuid, PermNodes.VoiceLockBypass);

            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
            Assert.False(BasisServerHandle.BasisServerHandleEvents.IsVoiceBlockedForUuid(plainUuid));
            Assert.False(BasisServerHandle.BasisServerHandleEvents.IsVoiceBlockedForUuid(bypassUuid));

            Assert.True(BasisGlobalLockManager.ToggleVoiceChat());
            Assert.True(BasisServerHandle.BasisServerHandleEvents.IsVoiceBlockedForUuid(plainUuid));
            Assert.False(BasisServerHandle.BasisServerHandleEvents.IsVoiceBlockedForUuid(bypassUuid));
        }
        finally
        {
            manager.RemoveUserNode(bypassUuid, PermNodes.VoiceLockBypass);
            BasisGlobalLockManager.InitializeFromConfig(AllUnlocked());
        }
    }
}

/// <summary>
/// Content-share DoS caps: sanitization to defaults and absolute maxima,
/// change reporting, and the GlobalGetResourceLimits payload.
/// </summary>
public class BasisResourceLimitManagerTests
{
    private static void RestoreDefaults() => BasisResourceLimitManager.SetLimits(32);

    [Theory]
    [InlineData(40, 40)]
    [InlineData(1, 1)]
    [InlineData(0, 32)]
    [InlineData(-7, 32)]
    [InlineData(4096, 4096)]
    [InlineData(int.MaxValue, 4096)]
    public void SetLimits_SanitizesTheCap(int spheres, int expectedSpheres)
    {
        try
        {
            BasisResourceLimitManager.SetLimits(spheres);
            Assert.Equal(expectedSpheres, BasisResourceLimitManager.MaxContentSpheresPerPlayer);
        }
        finally
        {
            RestoreDefaults();
        }
    }

    [Fact]
    public void SetLimits_ReportsWhetherAnythingActuallyChanged()
    {
        RestoreDefaults();
        Assert.False(BasisResourceLimitManager.SetLimits(32));
        Assert.False(BasisResourceLimitManager.SetLimits(-1)); // sanitized straight back to the default
        Assert.True(BasisResourceLimitManager.SetLimits(33));
        Assert.True(BasisResourceLimitManager.SetLimits(32));
        Assert.False(BasisResourceLimitManager.SetLimits(32));
    }

    [Fact]
    public void InitializeFromConfig_AppliesTheConfiguredCaps()
    {
        try
        {
            BasisResourceLimitManager.InitializeFromConfig(new Configuration
            {
                MaxContentSpheresPerPlayer = 64,
            });
            Assert.Equal(64, BasisResourceLimitManager.MaxContentSpheresPerPlayer);
        }
        finally
        {
            RestoreDefaults();
        }
    }

    [Fact]
    public void SendStateToPeer_WritesModeByteThenTheCap()
    {
        try
        {
            BasisResourceLimitManager.SetLimits(44);
            var peer = new SecurityTestPeer(2);
            BasisResourceLimitManager.SendStateToPeer(peer);

            var reader = new NetDataReader(Assert.Single(peer.Sent));
            Assert.Equal((byte)AdminRequestMode.GlobalGetResourceLimits, reader.GetByte());
            Assert.Equal(44, reader.GetInt());
            Assert.Equal(0, reader.AvailableBytes);
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.LastChannel);

            BasisResourceLimitManager.BroadcastState(); // zero connected peers: must be a safe no-op
        }
        finally
        {
            RestoreDefaults();
        }
    }
}
