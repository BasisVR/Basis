using Basis.Network.Core;
using BasisNetworkCore;
using BasisNetworkServer;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System.Net.Sockets;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

// ─────────────────────────────────────────────────────────────────────────────
// Peer-to-peer "direct connect" signalling lifecycle through the real
// BasisServerP2PBroker.HandleP2PMessage entry point: request → accept → decline/
// cancel → link-up (offload) → link-lost (re-arm) → disconnect teardown, plus the
// deny branches (instance lock, target offline, self-request, pair mismatch).
//
// The existing P2PBrokerOffloadTests drive ApplyLinkUp/RemovePeer by peer-id only and
// never inspect a forwarded signal. These tests build real signal frames, feed them
// through the routing switch, and assert on what each FakeNetPeer receives on the P2P
// channel — closing the request/accept/decline/relay gaps.
//
// Shares the "BasisServer shared network statics" collection so ResetForTests() and
// NetworkServer.AuthenticatedPeers can't race the offload/lifecycle/moderation suites.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisP2PConnectionLifecycleTests
{
    public BasisP2PConnectionLifecycleTests() => BasisServerP2PBroker.ResetForTests();

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    /// <summary>Feed a P2P signal frame (sub-type byte + serialized body) through the real routing switch.</summary>
    private static void Inject(NetPeer from, byte sub, ushort otherPlayerId, string token, byte[] key = null)
    {
        NetDataWriter w = new NetDataWriter(true, 96);
        w.Put(sub);
        new BasisP2PSignalMessage { otherPlayerId = otherPlayerId, sessionToken = token, ephemeralPublicKey = key }.Serialize(w);
        NetPacketReader reader = NetPacketReader.Create(w.CopyData(), 0, w.Length, () => { });
        BasisServerP2PBroker.HandleP2PMessage(reader, from);
    }

    private static (byte Sub, ushort Other, string Token) Parse(byte[] data)
    {
        NetDataReader r = new NetDataReader(data);
        byte sub = r.GetByte();
        BasisP2PSignalMessage m = default;
        m.Deserialize(r);
        return (sub, m.otherPlayerId, m.sessionToken);
    }

    /// <summary>Every P2P-channel signal the peer received, decoded to (sub, other, token).</summary>
    private static List<(byte Sub, ushort Other, string Token)> Signals(FakeNetPeer peer)
        => peer.Sent
            .Where(s => s.Channel == BasisNetworkCommons.P2PChannel)
            .Select(s => Parse(s.Data))
            .ToList();

    private static bool Received(FakeNetPeer peer, byte sub, ushort other, string token)
        => Signals(peer).Any(s => s.Sub == sub && s.Other == other && s.Token == token);

    // ── Request ──────────────────────────────────────────────────────────────

    [Fact]
    public void Request_HappyPath_CreatesSession_ArmsInitiator_AndForwardsToTarget()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);

        Assert.True(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.True(Received(target, BasisNetworkCommons.P2PSub_Request, (ushort)initId, token));
        Assert.True(Received(initiator, BasisNetworkCommons.P2PSub_ServerArmed, (ushort)targetId, token));
    }

    [Fact]
    public void Request_TargetOffline_CancelsInitiator_AndCreatesNoSession()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int missingTarget = LifecycleSupport.NextPeerId(); // never added to AuthenticatedPeers
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)missingTarget, token);

        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.True(Received(initiator, BasisNetworkCommons.P2PSub_Cancel, (ushort)missingTarget, token));
    }

    [Fact]
    public void Request_ToSelf_IsDropped_WithNoReplyAndNoSession()
    {
        using var scope = new ServerStaticsScope();
        int id = LifecycleSupport.NextPeerId();
        FakeNetPeer peer = LifecycleSupport.Peer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        string token = NewToken();

        Inject(peer, BasisNetworkCommons.P2PSub_Request, (ushort)id, token);

        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.Empty(Signals(peer));
    }

    [Fact]
    public void Request_WhenDirectConnectLocked_ForNonAdmin_IsCancelled_AndCreatesNoSession()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        var identity = new MapAuthIdentity();
        identity.Register($"p2p-user-{Guid.NewGuid():N}", initId); // known peer, but granted no permissions
        NetworkServer.AuthIdentity = identity;
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        bool wasLocked = BasisGlobalLockManager.DirectConnectLocked;
        if (!wasLocked) BasisGlobalLockManager.ToggleDirectConnect();
        try
        {
            Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);

            Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
            Assert.True(Received(initiator, BasisNetworkCommons.P2PSub_Cancel, (ushort)targetId, token));
            Assert.Empty(Signals(target)); // target is never told about a locked-out request
        }
        finally
        {
            if (!wasLocked) BasisGlobalLockManager.ToggleDirectConnect();
        }
    }

    // ── Accept ───────────────────────────────────────────────────────────────

    [Fact]
    public void Accept_FromTarget_ForwardsAcceptToInitiator()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);
        Inject(target, BasisNetworkCommons.P2PSub_Accept, (ushort)initId, token);

        Assert.True(Received(initiator, BasisNetworkCommons.P2PSub_Accept, (ushort)targetId, token));
    }

    [Fact]
    public void Accept_ForUnknownToken_IsDropped()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        Inject(target, BasisNetworkCommons.P2PSub_Accept, (ushort)initId, token); // no prior Request

        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.Empty(Signals(initiator));
    }

    [Fact]
    public void Accept_FromNonTargetPeer_IsDropped()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        int strangerId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        FakeNetPeer stranger = LifecycleSupport.Peer(strangerId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        NetworkServer.AuthenticatedPeers[strangerId] = stranger;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);
        initiator.Sent.Clear(); // drop the ServerArmed so only a (wrongly) forwarded Accept would show

        Inject(stranger, BasisNetworkCommons.P2PSub_Accept, (ushort)initId, token);

        Assert.DoesNotContain(Signals(initiator), s => s.Sub == BasisNetworkCommons.P2PSub_Accept);
    }

    // ── Decline / Cancel ──────────────────────────────────────────────────────

    [Fact]
    public void Decline_IsRelayedToInitiator_AndDropsTheSession()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);
        Inject(target, BasisNetworkCommons.P2PSub_Decline, (ushort)initId, token);

        Assert.True(Received(initiator, BasisNetworkCommons.P2PSub_Decline, (ushort)targetId, token));
        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
    }

    [Fact]
    public void Cancel_IsRelayedToTarget_AndDropsTheSession()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);
        Inject(initiator, BasisNetworkCommons.P2PSub_Cancel, (ushort)targetId, token);

        Assert.True(Received(target, BasisNetworkCommons.P2PSub_Cancel, (ushort)initId, token));
        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
    }

    // ── Full round trip: link up (offload) then link lost (re-arm) ─────────────

    [Fact]
    public void RequestAcceptLinkUp_Offloads_ThenLinkLost_ReArmsButKeepsSession()
    {
        using var scope = new ServerStaticsScope();
        int aId = LifecycleSupport.NextPeerId();
        int bId = LifecycleSupport.NextPeerId();
        FakeNetPeer a = LifecycleSupport.Peer(aId);
        FakeNetPeer b = LifecycleSupport.Peer(bId);
        NetworkServer.AuthenticatedPeers[aId] = a;
        NetworkServer.AuthenticatedPeers[bId] = b;
        string token = NewToken();

        Inject(a, BasisNetworkCommons.P2PSub_Request, (ushort)bId, token);
        Inject(b, BasisNetworkCommons.P2PSub_Accept, (ushort)aId, token);

        Inject(a, BasisNetworkCommons.P2PSub_LinkUp, (ushort)bId, token);
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(aId, bId)); // one side up only
        Inject(b, BasisNetworkCommons.P2PSub_LinkUp, (ushort)aId, token);
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(aId, bId));  // both up -> offloaded
        Assert.True(Received(a, BasisNetworkCommons.P2PSub_Offloaded, (ushort)aId, token) ||
                    Received(a, BasisNetworkCommons.P2PSub_Offloaded, (ushort)bId, token));

        // Link drops on one side: relay must resume (offload cleared) but the session survives for re-punch.
        Inject(a, BasisNetworkCommons.P2PSub_LinkLost, (ushort)bId, token);
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(aId, bId));
        Assert.True(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.True(Received(b, BasisNetworkCommons.P2PSub_LinkLost, (ushort)aId, token));
    }

    // ── Disconnect + reconnect ────────────────────────────────────────────────

    [Fact]
    public void PeerDisconnectMidSession_NotifiesSurvivor_AndTearsDownTheSession()
    {
        using var scope = new ServerStaticsScope();
        int initId = LifecycleSupport.NextPeerId();
        int targetId = LifecycleSupport.NextPeerId();
        FakeNetPeer initiator = LifecycleSupport.Peer(initId);
        FakeNetPeer target = LifecycleSupport.Peer(targetId);
        NetworkServer.AuthenticatedPeers[initId] = initiator;
        NetworkServer.AuthenticatedPeers[targetId] = target;
        string token = NewToken();

        Inject(initiator, BasisNetworkCommons.P2PSub_Request, (ushort)targetId, token);
        target.Sent.Clear(); // ignore the earlier Request forward

        // This is exactly what CleanupPeerSubsystems calls on a real disconnect.
        BasisServerP2PBroker.RemovePeer(initId);

        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.True(Received(target, BasisNetworkCommons.P2PSub_Cancel, (ushort)initId, token));
    }

    [Fact]
    public void AfterOffloadedPeerDisconnects_ReconnectOnSameId_IsNotStillOffloaded_AndCanRequestAgain()
    {
        using var scope = new ServerStaticsScope();
        int aId = LifecycleSupport.NextPeerId();
        int bId = LifecycleSupport.NextPeerId();
        FakeNetPeer a = LifecycleSupport.Peer(aId);
        FakeNetPeer b = LifecycleSupport.Peer(bId);
        NetworkServer.AuthenticatedPeers[aId] = a;
        NetworkServer.AuthenticatedPeers[bId] = b;
        string token = NewToken();

        // Full establish + offload.
        Inject(a, BasisNetworkCommons.P2PSub_Request, (ushort)bId, token);
        Inject(b, BasisNetworkCommons.P2PSub_Accept, (ushort)aId, token);
        Inject(a, BasisNetworkCommons.P2PSub_LinkUp, (ushort)bId, token);
        Inject(b, BasisNetworkCommons.P2PSub_LinkUp, (ushort)aId, token);
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(aId, bId));

        // A drops; LiteNetLib later hands the same id back to the rejoiner.
        BasisServerP2PBroker.RemovePeer(aId);
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(aId, bId)); // stale offload must not linger

        FakeNetPeer aReconnect = LifecycleSupport.Peer(aId);
        NetworkServer.AuthenticatedPeers[aId] = aReconnect;
        string token2 = NewToken();
        Inject(aReconnect, BasisNetworkCommons.P2PSub_Request, (ushort)bId, token2);

        Assert.True(BasisServerP2PBroker.HasSessionForTests(token2));
        Assert.True(Received(b, BasisNetworkCommons.P2PSub_Request, (ushort)aId, token2));
    }

    // ── Cross-subsystem blast radius of the stale-disconnect bug ──────────────

    /// <summary>
    /// Real-world consequence of the key-only remove in CleanupPeerSubsystems (see
    /// BasisReconnectStateTests.StaleDisconnectAfterReconnectCollision): CleanupPeerSubsystems
    /// also calls BasisServerP2PBroker.RemovePeer(id), so a stale predecessor's late disconnect
    /// tears down the LIVE peer's active direct-connect session — the "direct connect works, then
    /// dies after a rejoin" symptom this broker's own tests document. Red until the disconnect
    /// handler ignores a disconnect from a peer that no longer owns the id.
    /// </summary>
    [Fact]
    public void StaleDisconnect_DoesNotTearDownTheLivePeersDirectConnectSession()
    {
        using var scope = new ServerStaticsScope();
        NetworkServer.AuthIdentity = new MapAuthIdentity();

        int id = LifecycleSupport.NextPeerId();
        int otherId = LifecycleSupport.NextPeerId();
        FakeNetPeer live = LifecycleSupport.Peer(id);   // reconnected peer that owns the id now
        FakeNetPeer other = LifecycleSupport.Peer(otherId);
        FakeNetPeer stale = LifecycleSupport.Peer(id);  // disconnected predecessor, same id
        NetworkServer.AuthenticatedPeers[id] = live;
        NetworkServer.AuthenticatedPeers[otherId] = other;

        // The live peer has an active direct connection to `other`.
        string token = NewToken();
        Inject(live, BasisNetworkCommons.P2PSub_Request, (ushort)otherId, token);
        Inject(other, BasisNetworkCommons.P2PSub_Accept, (ushort)id, token);
        Assert.True(BasisServerP2PBroker.HasSessionForTests(token));

        // The predecessor's disconnect finally lands.
        BasisServerHandleEvents.HandlePeerDisconnected(stale, new DisconnectInfo { Reason = DisconnectReason.RemoteConnectionClose, SocketErrorCode = SocketError.Success });

        Assert.True(BasisServerP2PBroker.HasSessionForTests(token),
            "a stranger's disconnect tore down the live peer's direct-connect session (CleanupPeerSubsystems.RemovePeer on a key it no longer owns)");
    }

    // ── Routing ───────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownSubType_IsIgnored_WithNoStateChange()
    {
        using var scope = new ServerStaticsScope();
        int id = LifecycleSupport.NextPeerId();
        FakeNetPeer peer = LifecycleSupport.Peer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        string token = NewToken();

        Inject(peer, sub: 99, (ushort)id, token);

        Assert.False(BasisServerP2PBroker.HasSessionForTests(token));
        Assert.Empty(Signals(peer));
    }
}
