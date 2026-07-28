using BasisNetworkServer;
using Xunit;
using Xunit.Abstractions;

namespace BasisServerTests;

/// <summary>
/// Exercises <see cref="BasisServerP2PBroker"/>'s offload lifecycle — the server-side gate that
/// makes the server STOP relaying voice + avatar between a direct-connected (P2P) pair once both
/// report LinkUp. This is the one voice suppressor with no client-side self-heal, so if it fails
/// to clear when a peer disconnects, the pair goes silent until the server restarts — the
/// reported "direct connect works, then can't hear them after a rejoin" symptom.
///
/// Peer ids are reused by LiteNetLib on reconnect (GetNextPeerId dequeues freed ids), so a
/// rejoiner usually returns with the SAME id. These tests pin that a stale offload entry never
/// survives to collide with the reused id.
///
/// The broker's real entry points take LiteNetLib NetPeers (internal ctors, need a live
/// NetManager) so they can't be driven from a unit test; the offload logic only ever uses
/// peer ids, exposed via the ApplyLinkUp / RegisterSessionForTests / ResetForTests seams.
/// </summary>
[Collection("BasisServer shared network statics")] // shares BasisServerP2PBroker statics (ResetForTests) with the P2P lifecycle suite
public class P2PBrokerOffloadTests
{
    private readonly ITestOutputHelper _out;

    public P2PBrokerOffloadTests(ITestOutputHelper output)
    {
        _out = output;
        // Broker state is static; start each test clean.
        BasisServerP2PBroker.ResetForTests();
    }

    /// <summary>Establish a fully-offloaded pair, as the broker would after Request/Accept and
    /// both sides reporting LinkUp.</summary>
    private static string Offload(int a, int b, string? token = null)
    {
        token ??= $"tok-{a}-{b}";
        BasisServerP2PBroker.RegisterSessionForTests(token, a, b);
        BasisServerP2PBroker.ApplyLinkUp(a, token);
        BasisServerP2PBroker.ApplyLinkUp(b, token);
        return token;
    }

    [Fact]
    public void OffloadRequiresBothSides()
    {
        const string tok = "t";
        BasisServerP2PBroker.RegisterSessionForTests(tok, 3, 5);
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));

        BasisServerP2PBroker.ApplyLinkUp(3, tok);
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5)); // only one side up

        BasisServerP2PBroker.ApplyLinkUp(5, tok);
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(3, 5)); // both up -> offloaded
    }

    [Fact]
    public void OffloadIsSymmetric()
    {
        Offload(3, 5);
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(3, 5));
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(5, 3));
    }

    [Fact]
    public void SamePeerId_IsNeverOffloaded()
    {
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(5, 5));
    }

    // --- The core rejoin path ---------------------------------------------------------------

    [Fact]
    public void PeerDisconnect_ClearsOffloadAndSession()
    {
        Offload(3, 5);
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(3, 5));

        // Peer 5 leaves the server (BasisServerHandleEvents.CleanupPeerSubsystems -> RemovePeer).
        BasisServerP2PBroker.RemovePeer(5);

        // If this stays true the server keeps skipping the voice relay between the pair.
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
        Assert.False(BasisServerP2PBroker.HasSessionForTests("tok-3-5"));
    }

    [Fact]
    public void InitiatorDisconnect_ClearsOffload()
    {
        Offload(3, 5);
        BasisServerP2PBroker.RemovePeer(3); // the side that sent the original Request leaves
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
    }

    [Fact]
    public void RejoinWithReusedId_IsNotStillOffloaded()
    {
        // A=3, B=5 direct-connected.
        Offload(3, 5);
        // B leaves; LiteNetLib recycles peer id 5.
        BasisServerP2PBroker.RemovePeer(5);
        // B rejoins and gets the same id 5 back. Direct connect does NOT auto re-establish, so
        // there is no session for the pair — voice must relay through the server.
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
    }

    [Fact]
    public void ReEstablishAfterRejoin_OffloadsAgain()
    {
        Offload(3, 5);
        BasisServerP2PBroker.RemovePeer(5);
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));

        // They manually re-establish the direct connection after the rejoin (fresh token).
        Offload(3, 5, "t2");
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(3, 5));
    }

    [Fact]
    public void ReusedId_WithDifferentPartner_HasNoStaleSuppression()
    {
        Offload(3, 5);
        BasisServerP2PBroker.RemovePeer(5);

        // A different player joins, reuses id 5, and direct-connects to peer 7.
        Offload(5, 7, "t2");

        Assert.True(BasisServerP2PBroker.IsP2POffloaded(5, 7));
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5)); // old pair not resurrected
    }

    // --- Ordering / race edges --------------------------------------------------------------

    [Fact]
    public void LinkUpAfterDisconnect_DoesNotReoffload()
    {
        // A LinkUp that was already in flight when the peer dropped must not re-create the
        // offload after the session was torn down.
        const string tok = "t";
        BasisServerP2PBroker.RegisterSessionForTests(tok, 3, 5);
        BasisServerP2PBroker.ApplyLinkUp(3, tok);   // first side up
        BasisServerP2PBroker.RemovePeer(5);         // peer 5 drops mid-handshake
        BasisServerP2PBroker.ApplyLinkUp(5, tok);   // stale, late LinkUp

        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
    }

    [Fact]
    public void LinkLost_ClearsOffload_ButReArmsSession()
    {
        string tok = Offload(3, 5);
        // The starved side reports the link died (client watchdog / OnP2PPeerDisconnected).
        BasisServerP2PBroker.ApplyLinkLost(3, tok, otherPlayerId: 5);

        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5)); // relay resumes immediately
        Assert.True(BasisServerP2PBroker.HasSessionForTests(tok)); // session kept, re-armed
    }

    [Fact]
    public void DoubleDisconnect_IsIdempotent()
    {
        Offload(3, 5);
        BasisServerP2PBroker.RemovePeer(5);
        BasisServerP2PBroker.RemovePeer(5); // second disconnect for the same id
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
    }

    [Fact]
    public void RemoveUnknownPeer_IsNoOp()
    {
        BasisServerP2PBroker.RemovePeer(99); // no sessions at all
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 99));
    }

    // --- Isolation between pairs ------------------------------------------------------------

    [Fact]
    public void DisconnectingOnePair_LeavesOtherPairsOffloaded()
    {
        Offload(3, 5, "a");
        Offload(10, 11, "b");

        BasisServerP2PBroker.RemovePeer(5);

        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(10, 11)); // unrelated pair untouched
    }

    [Fact]
    public void PeerInTwoDirectConnections_DisconnectClearsBoth()
    {
        // Peer 5 is direct-connected to both 3 and 7 at once.
        Offload(3, 5, "a");
        Offload(5, 7, "b");
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(3, 5));
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(5, 7));

        BasisServerP2PBroker.RemovePeer(5);

        Assert.False(BasisServerP2PBroker.IsP2POffloaded(3, 5));
        Assert.False(BasisServerP2PBroker.IsP2POffloaded(5, 7));
    }
}
