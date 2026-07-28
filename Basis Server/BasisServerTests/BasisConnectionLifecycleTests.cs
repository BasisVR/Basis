using Basis.Network.Core;
using Basis.Network.Server.Auth;
using BasisNetworkCore;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System.Net;
using System.Net.Sockets;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;

namespace BasisServerTests;

// ─────────────────────────────────────────────────────────────────────────────
// Client↔server "direct connect" lifecycle: the full up-and-down through the real
// BasisServerHandleEvents state machine — HandleConnectionRequest (accept/deny),
// OnNetworkAccepted (admission gates + membership) and HandlePeerDisconnected
// (teardown + broadcast) — plus the reconnect/collision races.
//
// NetManager, NetPeer and ConnectionRequest are Basis.Network.Core interfaces, so
// the whole sequence is driven synchronously with no UDP socket. Every test snapshots
// and restores the NetworkServer statics it touches and runs under the shared
// "BasisServer shared network statics" collection so it never races the other
// stateful suites.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Transport shell whose only job is to report a controllable ConnectedPeersCount.</summary>
internal sealed class FakeNetManager : NetManager
{
    public int ConnectedPeersCount { get; set; }
    public NetStatistics Statistics { get; } = new NetStatistics();

    public void Start(IPAddress iPv4Address, IPAddress iPv6Address, int setPort) { }
    public void Stop() { }
    public NetPeer Connect(string sIP, int port, NetDataWriter writer) => null!;
    public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint) => true;
}

/// <summary>
/// Pending connection stand-in. Records the reject payload the server wrote and hands back a
/// preconfigured peer from Accept(), so both the deny and the accept branches are observable.
/// </summary>
internal sealed class RecordingConnectionRequest : ConnectionRequest
{
    public required NetDataReader Data { get; init; }
    public required IPEndPoint RemoteEndPoint { get; init; }
    public NetPeer PeerToReturn { get; init; } = null!;

    public bool WasAccepted { get; private set; }
    public bool WasRejected { get; private set; }
    public byte[] RejectPayload { get; private set; } = System.Array.Empty<byte>();

    public NetPeer Accept()
    {
        WasAccepted = true;
        return PeerToReturn;
    }

    public void Reject(NetDataWriter w)
    {
        WasRejected = true;
        RejectPayload = w.CopyData();
    }
}

/// <summary>Password auth stub with a flippable verdict.</summary>
internal sealed class FakeAuth : IAuth
{
    public bool Result { get; set; } = true;
    public bool IsAuthenticated(byte[] bytesMsg) => Result;
}

/// <summary>
/// Snapshots the NetworkServer statics a lifecycle test mutates and restores them on dispose,
/// removing only the peers the test itself added so a leaked entry never bleeds into the next test.
/// </summary>
internal sealed class ServerStaticsScope : System.IDisposable
{
    private readonly NetManager _server = NetworkServer.Server;
    private readonly Configuration _config = NetworkServer.Configuration;
    private readonly IAuth _auth = NetworkServer.Auth;
    private readonly IAuthIdentity _identity = NetworkServer.AuthIdentity;
    private readonly BasisAllowList _allow = NetworkServer.AllowList;
    private readonly BasisBanList _ban = NetworkServer.BanList;
    private readonly int _highQualityLength = NetworkServer.HighQualityLength;
    private readonly HashSet<int> _baselineKeys = new(NetworkServer.AuthenticatedPeers.Keys);

    public void Dispose()
    {
        foreach (int id in NetworkServer.AuthenticatedPeers.Keys.ToArray())
        {
            if (!_baselineKeys.Contains(id))
            {
                NetworkServer.AuthenticatedPeers.TryRemove(id, out _);
            }
        }
        NetworkServer.Server = _server;
        NetworkServer.Configuration = _config;
        NetworkServer.Auth = _auth;
        NetworkServer.AuthIdentity = _identity;
        NetworkServer.AllowList = _allow;
        NetworkServer.BanList = _ban;
        NetworkServer.HighQualityLength = _highQualityLength;
        NetworkServer.RebuildPeerSnapshot();
    }
}

/// <summary>Shared builders for connect payloads, peers and reject-payload parsing.</summary>
internal static class LifecycleSupport
{
    private static int _peerIdCounter = 30_000;
    public static int NextPeerId() => Interlocked.Increment(ref _peerIdCounter);
    public static string NewUuid() => $"conn-user-{Guid.NewGuid():N}";

    public static FakeNetPeer Peer(int id, string ip = "203.0.113.9") => new(id, ip);

    /// <summary>A ReadyMessage that WasDeserializedCorrectly (non-null avatar-change and sync arrays).</summary>
    public static ReadyMessage MakeReady(string uuid, string displayName, string platform = "test-platform")
    {
        int payload = ConvertToSize(BitQuality.Low);
        return new ReadyMessage
        {
            playerMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = uuid,
                playerDisplayName = displayName,
                playerPlatform = platform,
            },
            clientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                loadMode = 0,
                byteArray = new byte[] { 1 },
                LocalAvatarIndex = 0,
            },
            localAvatarSyncMessage = new LocalAvatarSyncMessage
            {
                DataQualityLevel = (byte)BitQuality.Low,
                array = new byte[payload],
            },
        };
    }

    /// <summary>The exact wire order the real client writes: [version][BytesMessage auth][ReadyMessage].</summary>
    public static byte[] ConnectPayload(ushort version, byte[]? auth, ReadyMessage? ready)
    {
        NetDataWriter w = new NetDataWriter(true, 64);
        w.Put(version);
        if (auth != null)
        {
            new BytesMessage().Serialize(w, auth);
        }
        if (ready.HasValue)
        {
            ready.Value.Serialize(w);
        }
        return w.CopyData();
    }

    public static RecordingConnectionRequest Request(byte[] data, FakeNetPeer? accepted = null, string ip = "203.0.113.9")
        => new()
        {
            Data = new NetDataReader(data),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), 6006),
            PeerToReturn = accepted!,
        };

    public static string RejectReason(byte[] payload) => new NetDataReader(payload).GetString();

    public static (uint Magic, byte Kind, ushort Aux0, ushort Aux1, string Message) RejectStructured(byte[] payload)
    {
        NetDataReader r = new NetDataReader(payload);
        return (r.GetUInt(), r.GetByte(), r.GetUShort(), r.GetUShort(), r.GetString());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HandleConnectionRequest: the pre-accept deny gate (banned IP, full, bad version,
// auth) and the happy accept that admits a peer.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisConnectionRequestGateTests
{
    private static Configuration OpenConfig() => new()
    {
        PeerLimit = 100,
        UseAuth = false,
        UseAuthIdentity = false,
        BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
    };

    private static void InstallOpenServer(ServerStaticsScope _)
    {
        NetworkServer.Configuration = OpenConfig();
        NetworkServer.Server = new FakeNetManager { ConnectedPeersCount = 0 };
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AuthIdentity = new MapAuthIdentity();
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
    }

    [Fact]
    public void BannedIp_IsRejectedBeforeAnyDataIsRead()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);
        BasisPlayerModeration.UseFileOnDisc = false;

        // Seed an IP ban the only way the server can: ban a connected player's address.
        string bannedIp = "198.51.100.23";
        int victimId = LifecycleSupport.NextPeerId();
        string victimUuid = LifecycleSupport.NewUuid();
        MapAuthIdentity identity = (MapAuthIdentity)NetworkServer.AuthIdentity;
        identity.Register(victimUuid, victimId);
        FakeNetPeer victim = LifecycleSupport.Peer(victimId, bannedIp);
        NetworkServer.AuthenticatedPeers[victimId] = victim;
        try
        {
            BasisPlayerModeration.IpBan(victimUuid, "seed");
            Assert.True(BasisPlayerModeration.IsIpBanned(bannedIp));

            RecordingConnectionRequest req = LifecycleSupport.Request(System.Array.Empty<byte>(), ip: bannedIp);
            BasisServerHandleEvents.HandleConnectionRequest(req);

            Assert.True(req.WasRejected);
            Assert.False(req.WasAccepted);
            Assert.Equal("Banned IP", LifecycleSupport.RejectReason(req.RejectPayload));
        }
        finally
        {
            BasisPlayerModeration.UnbanIp(bannedIp);
            NetworkServer.AuthenticatedPeers.TryRemove(victimId, out _);
        }
    }

    [Fact]
    public void ServerFull_IsRejectedWithStructuredServerFullKind()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);
        NetworkServer.Configuration.PeerLimit = 4;
        ((FakeNetManager)NetworkServer.Server).ConnectedPeersCount = 4;

        RecordingConnectionRequest req = LifecycleSupport.Request(System.Array.Empty<byte>());
        BasisServerHandleEvents.HandleConnectionRequest(req);

        Assert.True(req.WasRejected);
        Assert.False(req.WasAccepted);
        var reject = LifecycleSupport.RejectStructured(req.RejectPayload);
        Assert.Equal(BasisNetworkCommons.RejectMagic, reject.Magic);
        Assert.Equal(BasisNetworkCommons.RejectKind_ServerFull, reject.Kind);
    }

    [Fact]
    public void MissingVersionUShort_IsRejectedAsInvalidClientData()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);

        RecordingConnectionRequest req = LifecycleSupport.Request(System.Array.Empty<byte>());
        BasisServerHandleEvents.HandleConnectionRequest(req);

        Assert.True(req.WasRejected);
        Assert.False(req.WasAccepted);
        Assert.Equal("Invalid client data.", LifecycleSupport.RejectReason(req.RejectPayload));
    }

    [Fact]
    public void VersionMismatch_IsRejectedWithStructuredVersionMismatchKind()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);

        ushort wrong = (ushort)(BasisNetworkVersion.ServerVersion + 1);
        RecordingConnectionRequest req = LifecycleSupport.Request(LifecycleSupport.ConnectPayload(wrong, null, null));
        BasisServerHandleEvents.HandleConnectionRequest(req);

        Assert.True(req.WasRejected);
        Assert.False(req.WasAccepted);
        var reject = LifecycleSupport.RejectStructured(req.RejectPayload);
        Assert.Equal(BasisNetworkCommons.RejectMagic, reject.Magic);
        Assert.Equal(BasisNetworkCommons.RejectKind_VersionMismatch, reject.Kind);
        Assert.Equal(BasisNetworkVersion.ServerVersion, reject.Aux0);
        Assert.Equal(wrong, reject.Aux1);
    }

    [Fact]
    public void MalformedAuthPayload_IsRejected_WhenAuthEnabled()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);
        NetworkServer.Configuration.UseAuth = true;

        // Correct version, then a BytesMessage length that overruns the buffer → Deserialize fails.
        NetDataWriter w = new NetDataWriter(true, 8);
        w.Put(BasisNetworkVersion.ServerVersion);
        w.Put((ushort)500); // claims 500 bytes, none follow
        RecordingConnectionRequest req = LifecycleSupport.Request(w.CopyData());
        BasisServerHandleEvents.HandleConnectionRequest(req);

        Assert.True(req.WasRejected);
        Assert.False(req.WasAccepted);
        Assert.Equal("Malformed auth payload", LifecycleSupport.RejectReason(req.RejectPayload));
    }

    [Fact]
    public void WrongPassword_IsRejected_WhenAuthEnabled()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);
        NetworkServer.Configuration.UseAuth = true;
        ((FakeAuth)NetworkServer.Auth).Result = false;

        byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1, 2, 3 }, null);
        RecordingConnectionRequest req = LifecycleSupport.Request(data);
        BasisServerHandleEvents.HandleConnectionRequest(req);

        Assert.True(req.WasRejected);
        Assert.False(req.WasAccepted);
        Assert.Equal("Authentication failed, Auth rejected", LifecycleSupport.RejectReason(req.RejectPayload));
    }

    [Fact]
    public void ValidReadyMessage_IsAccepted_AndRegistersThePeer()
    {
        using var scope = new ServerStaticsScope();
        InstallOpenServer(scope);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        FakeNetPeer peer = LifecycleSupport.Peer(id);
        ReadyMessage ready = LifecycleSupport.MakeReady(uuid, "Connie");
        byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1 }, ready);
        RecordingConnectionRequest req = LifecycleSupport.Request(data, accepted: peer);

        BasisServerHandleEvents.HandleConnectionRequest(req);

        Assert.True(req.WasAccepted);
        Assert.False(req.WasRejected);
        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer stored));
        Assert.Same(peer, stored);
        Assert.Same(NetworkServer.AuthenticatedPeerTag, peer.Tag);
        // The peer must have received its ServerMetaData on the metadata channel.
        Assert.Contains(peer.Sent, s => s.Channel == BasisNetworkCommons.metaDataChannel);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// OnNetworkAccepted: the post-accept admission gates and the membership bookkeeping,
// driven directly with an in-memory ReadyMessage.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisAdmissionGateTests
{
    private static void Install(ServerStaticsScope _, BasisUserRestrictionMode mode)
    {
        NetworkServer.Configuration = new Configuration
        {
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = false,
            BasisUserRestrictionMode = mode,
        };
        NetworkServer.Server = new FakeNetManager();
        NetworkServer.AuthIdentity = new MapAuthIdentity();
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
    }

    [Fact]
    public void AllowListMode_UnlistedUuid_IsRejected_AndNotRegistered()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.AllowList);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        FakeNetPeer peer = LifecycleSupport.Peer(id);

        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, "NotAllowed"), uuid);

        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
        Assert.Equal(1, peer.DisconnectCalls);
        Assert.Equal("You are not on the allowlist.",
            new NetDataReader(peer.DisconnectData[0]).GetString());
    }

    [Fact]
    public async Task AllowListMode_ListedUuid_IsRegistered()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.AllowList);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        await NetworkServer.AllowList.AddToAllowlistAsync(uuid);
        FakeNetPeer peer = LifecycleSupport.Peer(id);

        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, "Allowed"), uuid);

        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer stored));
        Assert.Same(peer, stored);
        Assert.Equal(0, peer.DisconnectCalls);
    }

    [Fact]
    public async Task BanListMode_BannedUuid_IsRejected()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.BanList);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        await NetworkServer.BanList.AddToBanListAsync(uuid);
        FakeNetPeer peer = LifecycleSupport.Peer(id);

        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, "Banned"), uuid);

        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
        Assert.Equal(1, peer.DisconnectCalls);
        Assert.Equal("You are not permitted on this server.",
            new NetDataReader(peer.DisconnectData[0]).GetString());
    }

    [Fact]
    public void RejoinOnlyMode_UncapturedUuid_IsRejected()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.RejoinOnly);
        BasisRejoinLockManager.Clear(); // nobody captured → nobody may join

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        FakeNetPeer peer = LifecycleSupport.Peer(id);

        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, "Stranger"), uuid);

        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
        Assert.Equal(1, peer.DisconnectCalls);
        Assert.Equal("The server is locked — only players already here may rejoin.",
            new NetDataReader(peer.DisconnectData[0]).GetString());
    }

    [Fact]
    public void EmptyDisplayName_IsRejected()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.Normal);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        FakeNetPeer peer = LifecycleSupport.Peer(id);

        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, ""), uuid);

        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
        Assert.Equal(1, peer.DisconnectCalls);
        Assert.Equal("Choose a non-empty username.",
            new NetDataReader(peer.DisconnectData[0]).GetString());
    }

    [Fact]
    public void NormalMode_ValidPeer_IsRegisteredExactlyOnce()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.Normal);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        FakeNetPeer peer = LifecycleSupport.Peer(id);

        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, "Fine"), uuid);

        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer stored));
        Assert.Same(peer, stored);
        Assert.Contains(NetworkServer.PeerSnapshot, p => ReferenceEquals(p, peer));
        Assert.Equal(0, peer.DisconnectCalls);
    }

    [Fact]
    public void ReconnectCollision_EvictsStalePeer_AndTheNewPeerWinsTheSlot()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.Normal);

        int id = LifecycleSupport.NextPeerId();
        string staleUuid = LifecycleSupport.NewUuid();
        string freshUuid = LifecycleSupport.NewUuid();
        FakeNetPeer stale = LifecycleSupport.Peer(id);
        FakeNetPeer fresh = LifecycleSupport.Peer(id); // same id, different object (recycled slot)

        // Stale peer already occupies the slot when the reconnection is accepted.
        NetworkServer.AuthenticatedPeers[id] = stale;
        NetworkServer.RebuildPeerSnapshot();

        BasisServerHandleEvents.OnNetworkAccepted(fresh, LifecycleSupport.MakeReady(freshUuid, "Fresh"), freshUuid);

        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer stored));
        Assert.Same(fresh, stored);
        Assert.Single(NetworkServer.AuthenticatedPeers.Keys, k => k == id);
        _ = staleUuid;
    }

    [Fact]
    public void ReAcceptingTheSamePeerObject_IsRejectedAsAlreadyExists()
    {
        using var scope = new ServerStaticsScope();
        Install(scope, BasisUserRestrictionMode.Normal);

        int id = LifecycleSupport.NextPeerId();
        string uuid = LifecycleSupport.NewUuid();
        FakeNetPeer peer = LifecycleSupport.Peer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();

        // Re-admitting the identical object cannot collision-evict itself, so it is refused.
        BasisServerHandleEvents.OnNetworkAccepted(peer, LifecycleSupport.MakeReady(uuid, "Twice"), uuid);

        Assert.Equal(1, peer.DisconnectCalls);
        Assert.Equal("Peer already exists.", new NetDataReader(peer.DisconnectData[0]).GetString());
        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HandlePeerDisconnected: teardown, the disconnect broadcast, and the graceful
// handling of null / never-authenticated peers.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisDisconnectLifecycleTests
{
    private static DisconnectInfo Info(DisconnectReason reason = DisconnectReason.RemoteConnectionClose)
        => new() { Reason = reason, SocketErrorCode = SocketError.Success };

    private static (MapAuthIdentity Identity, FakeNetPeer Peer) Connected(int id, string ip = "203.0.113.9")
    {
        string uuid = LifecycleSupport.NewUuid();
        MapAuthIdentity identity = (MapAuthIdentity)NetworkServer.AuthIdentity;
        identity.Register(uuid, id);
        FakeNetPeer peer = LifecycleSupport.Peer(id, ip);
        NetworkServer.AuthenticatedPeers[id] = peer;
        return (identity, peer);
    }

    private static void InstallServer()
    {
        NetworkServer.Configuration = new Configuration { PeerLimit = 100, BasisUserRestrictionMode = BasisUserRestrictionMode.Normal };
        NetworkServer.AuthIdentity = new MapAuthIdentity();
    }

    [Fact]
    public void DisconnectingAuthenticatedPeer_RemovesItAndRebuildsTheSnapshot()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();

        int id = LifecycleSupport.NextPeerId();
        (_, FakeNetPeer peer) = Connected(id);
        NetworkServer.RebuildPeerSnapshot();

        BasisServerHandleEvents.HandlePeerDisconnected(peer, Info());

        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
        Assert.DoesNotContain(NetworkServer.PeerSnapshot, p => ReferenceEquals(p, peer));
    }

    [Fact]
    public void DisconnectBroadcast_NotifiesEveryOtherPeer_WithTheLeaverId()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();

        int leavingId = LifecycleSupport.NextPeerId();
        int witnessA = LifecycleSupport.NextPeerId();
        int witnessB = LifecycleSupport.NextPeerId();
        (_, FakeNetPeer leaving) = Connected(leavingId);
        (_, FakeNetPeer a) = Connected(witnessA);
        (_, FakeNetPeer b) = Connected(witnessB);
        NetworkServer.RebuildPeerSnapshot();

        // The broadcaster's queues are process-wide, so drop anything another test left pending;
        // otherwise a stale id rides along in this test's packet and is read as the leaver.
        BasisServerHandleEvents.JoinBroadcast.Stop();

        BasisServerHandleEvents.HandlePeerDisconnected(leaving, Info());
        // Departures are coalesced now, so the notice goes out on the next flush rather than inline.
        // The invariant below is unchanged — only when it is observable moved.
        BasisServerHandleEvents.JoinBroadcast.Flush();

        // Both remaining peers get one disconnect notice carrying the leaver's ushort id.
        foreach (FakeNetPeer witness in new[] { a, b })
        {
            var notice = Assert.Single(witness.Sent);
            Assert.Equal(BasisNetworkCommons.DisconnectionChannel, notice.Channel);
            Assert.Equal((ushort)leavingId, new NetDataReader(notice.Data).GetUShort());
        }
        // The peer that left is never sent its own removal.
        Assert.Empty(leaving.Sent);
    }

    [Fact]
    public void DisconnectingNeverAuthenticatedPeer_IsAGracefulNoOp()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();

        int id = LifecycleSupport.NextPeerId();
        FakeNetPeer stranger = LifecycleSupport.Peer(id); // never inserted into AuthenticatedPeers

        BasisServerHandleEvents.HandlePeerDisconnected(stranger, Info(DisconnectReason.ConnectionFailed));

        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));
    }

    [Fact]
    public void DisconnectingNullPeer_DoesNotThrow()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();

        BasisServerHandleEvents.HandlePeerDisconnected(null!, Info());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The reconnect races: a clean up→down→up round trip, and the ordering hazard where
// a stale peer's delayed disconnect must not evict the live peer that took its slot.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisReconnectStateTests
{
    private static DisconnectInfo Info() => new() { Reason = DisconnectReason.RemoteConnectionClose };

    private static void InstallServer()
    {
        NetworkServer.Configuration = new Configuration { PeerLimit = 100, BasisUserRestrictionMode = BasisUserRestrictionMode.Normal };
        NetworkServer.Server = new FakeNetManager();
        NetworkServer.AuthIdentity = new MapAuthIdentity();
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
    }

    [Fact]
    public void ConnectDisconnectReconnect_SameId_LeavesTheNewPeerRegistered()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();
        MapAuthIdentity identity = (MapAuthIdentity)NetworkServer.AuthIdentity;

        int id = LifecycleSupport.NextPeerId();

        // Up.
        string firstUuid = LifecycleSupport.NewUuid();
        FakeNetPeer first = LifecycleSupport.Peer(id);
        identity.Register(firstUuid, id);
        BasisServerHandleEvents.OnNetworkAccepted(first, LifecycleSupport.MakeReady(firstUuid, "First"), firstUuid);
        Assert.Same(first, NetworkServer.AuthenticatedPeers[id]);

        // Down.
        BasisServerHandleEvents.HandlePeerDisconnected(first, Info());
        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(id));

        // Up again on the recycled id with a fresh peer object.
        string secondUuid = LifecycleSupport.NewUuid();
        FakeNetPeer second = LifecycleSupport.Peer(id);
        identity.Register(secondUuid, id);
        BasisServerHandleEvents.OnNetworkAccepted(second, LifecycleSupport.MakeReady(secondUuid, "Second"), secondUuid);

        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer stored));
        Assert.Same(second, stored);
    }

    [Fact]
    public void StaleDisconnectAfterReconnectCollision_DoesNotEvictTheLivePeer()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();

        int id = LifecycleSupport.NextPeerId();
        FakeNetPeer stale = LifecycleSupport.Peer(id);
        FakeNetPeer live = LifecycleSupport.Peer(id); // reconnection that already won the slot

        // Post-collision state: the live peer holds the slot; the stale peer's disconnect
        // event is still in flight and now fires with the same id.
        NetworkServer.AuthenticatedPeers[id] = live;
        NetworkServer.RebuildPeerSnapshot();

        BasisServerHandleEvents.HandlePeerDisconnected(stale, Info());

        // Invariant: a stale peer's teardown must only remove itself, never the live peer
        // that owns the id now — mirroring the value-matched remove in RejectWithReason(NetPeer).
        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer stored),
            "the live peer was evicted by a stale peer's disconnect (key-only TryRemove)");
        Assert.Same(live, stored);
    }
}
