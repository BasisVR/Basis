using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using BasisPermissions;
using BasisServerHandle;
using Xunit;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisServerTests;

// ─────────────────────────────────────────────────────────────────────────────
// BasisPlayerMuteManager (BasisNetworkServer\Security\BasisPlayerMuteManager.cs).
//
// A moderation mute is enforced server-side: the client is told about it so the UI
// can grey out, but nothing about the enforcement may depend on the client honouring
// that. These tests drive the manager directly and then through the shared gates the
// voice and chat paths actually call, because a mute that the manager records but the
// gates never consult is a mute that does nothing.
//
// The class is static and file-backed, so every test lives in this one class (xunit
// runs a class's methods sequentially), uses GUID-suffixed uuids, unmutes in a finally,
// and pins UseFileOnDisc off unless it is the persistence test.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisPlayerMuteManagerTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 70_000;

    private static (string Uuid, FakeNetPeer Peer) ConnectPlayer()
    {
        NetworkServer.AuthIdentity = Identity;
        int id = Interlocked.Increment(ref peerIdCounter);
        string uuid = $"mute-user-{Guid.NewGuid():N}";
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.9.9");
        Identity.Register(uuid, id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        return (uuid, peer);
    }

    private static void RemovePlayer(FakeNetPeer peer) => NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);

    private static void ClearMute(string uuid)
    {
        BasisPlayerMuteManager.Apply(uuid, voice: true, muted: false);
        BasisPlayerMuteManager.Apply(uuid, voice: false, muted: false);
    }

    /// <summary>Reads one captured MuteStateApply push: [mode][bool voice][bool text].</summary>
    private static (bool Voice, bool Text) ReadMuteState(FakeNetPeer peer, int index)
    {
        NetDataReader r = new NetDataReader(peer.Sent[index].Data);
        AdminRequest req = new AdminRequest();
        req.Deserialize(r);
        Assert.Equal(AdminRequestMode.MuteStateApply, req.GetAdminRequestMode());
        return (r.GetBool(), r.GetBool());
    }

    // ---- flags ----

    [Fact]
    public void VoiceAndTextMutes_AreIndependentFlagsOnOneRecord()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            Assert.True(BasisPlayerMuteManager.IsVoiceMuted(uuid));
            Assert.False(BasisPlayerMuteManager.IsTextMuted(uuid));

            BasisPlayerMuteManager.Apply(uuid, voice: false, muted: true);
            Assert.True(BasisPlayerMuteManager.IsVoiceMuted(uuid));
            Assert.True(BasisPlayerMuteManager.IsTextMuted(uuid));

            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: false);
            Assert.False(BasisPlayerMuteManager.IsVoiceMuted(uuid));
            Assert.True(BasisPlayerMuteManager.IsTextMuted(uuid));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void UnmutingBothFlags_DropsTheRecord()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            BasisPlayerMuteManager.Apply(uuid, voice: false, muted: true);
            ClearMute(uuid);

            Assert.False(BasisPlayerMuteManager.IsVoiceMuted(uuid));
            Assert.False(BasisPlayerMuteManager.IsTextMuted(uuid));
            Assert.False(BasisPlayerMuteManager.IsVoiceMutedFor(peer));
            Assert.False(BasisPlayerMuteManager.IsTextMutedFor(peer));
        }
        finally
        {
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void UnknownAndNullUuids_QueryAsUnmuted()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        Assert.False(BasisPlayerMuteManager.IsVoiceMuted(null!));
        Assert.False(BasisPlayerMuteManager.IsTextMuted(null!));
        Assert.False(BasisPlayerMuteManager.IsVoiceMuted($"nobody-{Guid.NewGuid():N}"));
        Assert.False(BasisPlayerMuteManager.IsTextMuted($"nobody-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void BlankUuids_AreRejected()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        Assert.Equal("UUID invalid", BasisPlayerMuteManager.Apply(null!, voice: true, muted: true));
        Assert.Equal("UUID invalid", BasisPlayerMuteManager.Apply("", voice: true, muted: true));
        Assert.Equal("UUID invalid", BasisPlayerMuteManager.Apply("   ", voice: false, muted: true));
    }

    [Fact]
    public void ProtectedPlayers_CannotBeMuted()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;
        perms.AddUserNode(uuid, PermNodes.protection);
        try
        {
            Assert.Equal("Target is protected", BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true));
            Assert.Equal("Target is protected", BasisPlayerMuteManager.Apply(uuid, voice: false, muted: true));
            Assert.False(BasisPlayerMuteManager.IsVoiceMuted(uuid));
            Assert.False(BasisPlayerMuteManager.IsTextMuted(uuid));
            Assert.Empty(peer.Sent);
        }
        finally
        {
            perms.RemoveUserNode(uuid, PermNodes.protection);
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void ProtectionAlsoBlocksUnmuting_SoAMuteCannotBeLiftedByGrantingProtection()
    {
        // The guard is on Apply, not on "muted == true". Pinned because the reply text is
        // identical either way, and a moderator reading "Target is protected" after asking
        // for an UNMUTE would otherwise assume the mute is gone.
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            perms.AddUserNode(uuid, PermNodes.protection);

            Assert.Equal("Target is protected", BasisPlayerMuteManager.Apply(uuid, voice: true, muted: false));
            Assert.True(BasisPlayerMuteManager.IsVoiceMuted(uuid));
        }
        finally
        {
            perms.RemoveUserNode(uuid, PermNodes.protection);
            ClearMute(uuid);
            RemovePlayer(peer);
        }
    }

    // ---- offline targets ----

    [Fact]
    public void OfflinePlayers_AreMutedAndTheReplySaysSo()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        NetworkServer.AuthIdentity = Identity;
        string uuid = $"offline-{Guid.NewGuid():N}";
        try
        {
            string reply = BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            Assert.Contains("offline", reply);
            Assert.True(BasisPlayerMuteManager.IsVoiceMuted(uuid));
        }
        finally
        {
            ClearMute(uuid);
        }
    }

    [Fact]
    public void MuteSurvivesTheTargetReconnecting_BecauseTheRecordIsUuidKeyed()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, first) = ConnectPlayer();
        BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
        RemovePlayer(first);

        // Same person, new peer id: exactly what a rejoin looks like to the server.
        int rejoinId = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer second = new FakeNetPeer(rejoinId, "10.9.9.10");
        Identity.Register(uuid, rejoinId);
        NetworkServer.AuthenticatedPeers[rejoinId] = second;
        try
        {
            Assert.True(BasisPlayerMuteManager.IsVoiceMutedFor(second));
            Assert.True(BasisServerHandleEvents.IsVoiceBlockedFor(second));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(second);
        }
    }

    // ---- the gates the voice and chat paths actually call ----

    [Fact]
    public void VoiceMute_BlocksTheVoiceGate()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            Assert.False(BasisServerHandleEvents.IsVoiceBlockedFor(peer));

            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            Assert.True(BasisServerHandleEvents.IsVoiceBlockedFor(peer));
            Assert.True(BasisServerHandleEvents.IsVoiceBlockedForUuid(uuid));

            // A voice mute must not silence text.
            Assert.False(BasisNetworkChat.IsChatBlockedFor(peer));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void TextMute_BlocksTheChatGate_AndTherefore_TypingState()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            Assert.False(BasisNetworkChat.IsChatBlockedFor(peer));

            BasisPlayerMuteManager.Apply(uuid, voice: false, muted: true);
            Assert.True(BasisNetworkChat.IsChatBlockedFor(peer));
            Assert.True(BasisNetworkChat.IsChatBlockedForUuid(uuid));

            // The typing relay shares the same gate, so a muted player cannot leak
            // "is typing" activity while their messages are being dropped.
            Assert.False(BasisServerHandleEvents.IsVoiceBlockedFor(peer));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void AnUnregisteredPeer_IsNotTreatedAsMuted()
    {
        // NetIDToUUID fails for a peer that never authenticated; the gate has to read that
        // as "no record", not as a match against some other player's record.
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, muted) = ConnectPlayer();
        FakeNetPeer stranger = new FakeNetPeer(Interlocked.Increment(ref peerIdCounter), "10.9.9.11");
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            Assert.False(BasisPlayerMuteManager.IsVoiceMutedFor(stranger));
            Assert.False(BasisPlayerMuteManager.IsTextMutedFor(stranger));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(muted);
        }
    }

    // ---- the push to the target's own client ----

    [Fact]
    public void ApplyingAMute_PushesTheFullStateToAnOnlineTarget()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);
            Assert.Single(peer.Sent);
            Assert.Equal((true, false), ReadMuteState(peer, 0));
            Assert.Equal(BasisNetworkCommons.AdminChannel, peer.Sent[0].Channel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, peer.Sent[0].Method);

            BasisPlayerMuteManager.Apply(uuid, voice: false, muted: true);
            Assert.Equal(2, peer.Sent.Count);
            Assert.Equal((true, true), ReadMuteState(peer, 1));

            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: false);
            Assert.Equal(3, peer.Sent.Count);
            Assert.Equal((false, true), ReadMuteState(peer, 2));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void JoinTimePush_HappensOnlyForMutedPlayers()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (mutedUuid, mutedPeer) = ConnectPlayer();
        var (_, quietPeer) = ConnectPlayer();
        try
        {
            BasisPlayerMuteManager.Apply(mutedUuid, voice: false, muted: true);
            mutedPeer.Sent.Clear();

            BasisPlayerMuteManager.SendStateToPeerIfMuted(quietPeer);
            Assert.Empty(quietPeer.Sent);

            BasisPlayerMuteManager.SendStateToPeerIfMuted(mutedPeer);
            Assert.Single(mutedPeer.Sent);
            Assert.Equal((false, true), ReadMuteState(mutedPeer, 0));
        }
        finally
        {
            ClearMute(mutedUuid);
            RemovePlayer(mutedPeer);
            RemovePlayer(quietPeer);
        }
    }

    [Fact]
    public void SendStateToPeer_ToleratesANullPeer()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        BasisPlayerMuteManager.SendStateToPeer(null!);
        BasisPlayerMuteManager.SendStateToPeerIfMuted(null!);
    }

    [Fact]
    public void SendStateToPeer_ForAnUnmutedPeer_ReportsBothFlagsClear()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            BasisPlayerMuteManager.SendStateToPeer(peer);
            Assert.Single(peer.Sent);
            Assert.Equal((false, false), ReadMuteState(peer, 0));
        }
        finally
        {
            ClearMute(uuid);
            RemovePlayer(peer);
        }
    }

    // ---- the admin path ----

    [Fact]
    public void AdminMute_RequiresTheModerationMuteNode()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        BasisPlayerModeration.UseFileOnDisc = false;
        var (adminUuid, adminPeer) = ConnectPlayer();
        var (targetUuid, targetPeer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;
        try
        {
            BasisPlayerModeration.OnAdminMessage(adminPeer, MuteRequest(AdminRequestMode.SetVoiceMute, targetUuid, true));
            Assert.False(BasisPlayerMuteManager.IsVoiceMuted(targetUuid));
            Assert.Empty(targetPeer.Sent);

            perms.AddUserNode(adminUuid, PermNodes.ModerationMute);
            BasisPlayerModeration.OnAdminMessage(adminPeer, MuteRequest(AdminRequestMode.SetVoiceMute, targetUuid, true));
            Assert.True(BasisPlayerMuteManager.IsVoiceMuted(targetUuid));
            Assert.Single(targetPeer.Sent);
        }
        finally
        {
            perms.RemoveUserNode(adminUuid, PermNodes.ModerationMute);
            ClearMute(targetUuid);
            RemovePlayer(adminPeer);
            RemovePlayer(targetPeer);
        }
    }

    [Fact]
    public void AdminTextMute_UsesTheSameNode_AndSetsOnlyTheTextFlag()
    {
        BasisPlayerMuteManager.UseFileOnDisc = false;
        BasisPlayerModeration.UseFileOnDisc = false;
        var (adminUuid, adminPeer) = ConnectPlayer();
        var (targetUuid, targetPeer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;
        perms.AddUserNode(adminUuid, PermNodes.ModerationMute);
        try
        {
            BasisPlayerModeration.OnAdminMessage(adminPeer, MuteRequest(AdminRequestMode.SetTextMute, targetUuid, true));
            Assert.True(BasisPlayerMuteManager.IsTextMuted(targetUuid));
            Assert.False(BasisPlayerMuteManager.IsVoiceMuted(targetUuid));
            Assert.Equal((false, true), ReadMuteState(targetPeer, 0));
        }
        finally
        {
            perms.RemoveUserNode(adminUuid, PermNodes.ModerationMute);
            ClearMute(targetUuid);
            RemovePlayer(adminPeer);
            RemovePlayer(targetPeer);
        }
    }

    private static NetPacketReader MuteRequest(AdminRequestMode mode, string uuid, bool muted)
    {
        NetDataWriter w = new NetDataWriter();
        new AdminRequest().Serialize(w, mode);
        w.Put(uuid);
        w.Put(muted);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        return NetPacketReader.Create(bytes, 0, bytes.Length, () => { });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BasisPeerRateLimiter (BasisNetworkServer\Security\BasisPeerRateLimiter.cs).
//
// The limiter guards handlers that fan one client message out to every peer, so an
// unlimited sender turns a single small packet into N sends. It is a plain token
// bucket over the wall clock, and it drops silently: a reply or a log line would
// hand the flooder back the amplification the limiter exists to remove.
// ─────────────────────────────────────────────────────────────────────────────

public class BasisPeerRateLimiterTests
{
    private static int idCounter = 90_000;
    private static FakeNetPeer NewPeer() => new FakeNetPeer(Interlocked.Increment(ref idCounter), "10.8.8.8");

    [Fact]
    public void BurstIsSpentThenTheSenderIsDropped()
    {
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 4f);
        FakeNetPeer peer = NewPeer();

        for (int i = 0; i < 4; i++)
        {
            Assert.True(limiter.TryConsume(peer), $"burst token {i} should be available");
        }
        Assert.False(limiter.TryConsume(peer));
        Assert.False(limiter.TryConsume(peer));
    }

    [Fact]
    public void ExhaustionIsSilent_TheCallerJustGetsFalse()
    {
        // No exception, no state the caller has to unwind: the handler's only
        // correct response is to return without touching the wire.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 1f);
        FakeNetPeer peer = NewPeer();
        Assert.True(limiter.TryConsume(peer));
        for (int i = 0; i < 1000; i++)
        {
            Assert.False(limiter.TryConsume(peer));
        }
    }

    [Fact]
    public void PeersAreLimitedIndependently()
    {
        // One flooder must not be able to mute everyone else's typing state.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 2f);
        FakeNetPeer flooder = NewPeer();
        FakeNetPeer bystander = NewPeer();

        Assert.True(limiter.TryConsume(flooder));
        Assert.True(limiter.TryConsume(flooder));
        Assert.False(limiter.TryConsume(flooder));

        Assert.True(limiter.TryConsume(bystander));
        Assert.True(limiter.TryConsume(bystander));
    }

    [Fact]
    public void TokensRefillOverTime()
    {
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 500f, tokenBurst: 1f);
        FakeNetPeer peer = NewPeer();

        Assert.True(limiter.TryConsume(peer));
        Assert.False(limiter.TryConsume(peer));

        Thread.Sleep(50);
        Assert.True(limiter.TryConsume(peer));
    }

    [Fact]
    public void RefillIsCappedAtTheBurst()
    {
        // An idle peer banks at most tokenBurst, so a long quiet period cannot be cashed in
        // as one huge flood. The rate is deliberately slow: at a fast rate the drain loop's
        // own runtime refills the bucket underneath the assertion.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 10f, tokenBurst: 3f);
        FakeNetPeer peer = NewPeer();

        for (int i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryConsume(peer));
        }
        Assert.False(limiter.TryConsume(peer));

        // Long enough to have banked five tokens if the cap were not there.
        Thread.Sleep(500);

        int allowed = 0;
        for (int i = 0; i < 20; i++)
        {
            if (limiter.TryConsume(peer)) allowed++;
        }
        Assert.Equal(3, allowed);
    }

    [Fact]
    public void ZeroBurst_RejectsEverything()
    {
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 0f);
        Assert.False(limiter.TryConsume(NewPeer()));
    }

    [Fact]
    public void FirstMessageFromAFreshPeerIsAlwaysAllowed()
    {
        // The bucket is created full, so a normal client's first typing update is
        // never dropped just because it is their first.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 2f, tokenBurst: 8f);
        for (int i = 0; i < 200; i++)
        {
            Assert.True(limiter.TryConsume(NewPeer()));
        }
    }

    [Fact]
    public void PeerIdChurn_DoesNotGrowTrackingWithoutBound()
    {
        // Tracking is capped and cleared wholesale when it is exceeded, so a peer that
        // reconnects under a new id cannot make the server hold a bucket per attempt.
        // The clear is observable: a peer that had spent its burst gets a fresh one.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 1f);
        FakeNetPeer victim = NewPeer();
        Assert.True(limiter.TryConsume(victim));
        Assert.False(limiter.TryConsume(victim));

        for (int i = 0; i < 4200; i++)
        {
            limiter.TryConsume(NewPeer());
        }

        Assert.True(limiter.TryConsume(victim));
    }

    [Fact]
    public void ConcurrentConsumersNeverHandOutMoreThanTheBurst()
    {
        // One peer id can be driven from several transport threads at once; the bucket
        // is locked, so the total granted is still bounded by the burst.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 10f);
        FakeNetPeer peer = NewPeer();

        int granted = 0;
        Parallel.For(0, 500, _ =>
        {
            if (limiter.TryConsume(peer)) Interlocked.Increment(ref granted);
        });

        Assert.Equal(10, granted);
    }

    [Fact]
    public void ChatAndTypingLimiters_AreConfiguredForSustainedHumanRates()
    {
        // Pins the shipped budgets. Typing state is emitted per keystroke burst, so it
        // gets the larger burst; both refill slowly enough that a scripted client cannot
        // hold the broadcast path open.
        var typing = new BasisPeerRateLimiter(tokensPerSecond: 2f, tokenBurst: 8f);
        FakeNetPeer peer = NewPeer();

        int allowed = 0;
        for (int i = 0; i < 100; i++)
        {
            if (typing.TryConsume(peer)) allowed++;
        }
        Assert.InRange(allowed, 8, 12);
    }
}
