using System.Buffers;
using System.Net;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using Xunit;
using BasisNetworkIDDatabase = BasisNetworkCore.BasisNetworkIDDatabase;
using ClientAvatarChangeMessage = global::SerializableBasis.ClientAvatarChangeMessage;
using ClientBodyFitMessage = global::SerializableBasis.ClientBodyFitMessage;
using ClientMetaDataMessage = global::SerializableBasis.ClientMetaDataMessage;
using OwnershipTransferMessage = DarkRift.Basis_Common.Serializable.SerializableBasis.OwnershipTransferMessage;
using PlayerIdMessage = global::SerializableBasis.PlayerIdMessage;
using ReadyMessage = global::SerializableBasis.ReadyMessage;
using VoiceReceiversMessage = global::SerializableBasis.VoiceReceiversMessage;

namespace BasisServerTests;

// Basis.Network.Core.NetPeer is the transport abstraction *interface*, so the ownership /
// saved-state / net-ID layers can be exercised with an offline stand-in peer: sends are
// no-ops, and every server broadcast in these paths iterates NetworkServer.PeerSnapshot,
// which stays Array.Empty because no test ever starts a server or rebuilds the snapshot.
// All three fixtures share one xunit collection (sequential) because they poke shared
// process statics (writer pool, BNL delegates, NetworkServer.AuthenticatedPeers).
internal sealed class OwnershipFakeNetPeer : NetPeer
{
    private int _sendCount;

    public OwnershipFakeNetPeer(int id) => Id = id;

    public int Id { get; }
    public int SendCount => _sendCount;
    public IPAddress Address => IPAddress.Loopback;
    public int RemoteId => Id;
    public int RoundTripTime => 0;
    public float TimeSinceLastPacket => 0f;
    public long RemoteTimeDelta => 0;
    public int Mtu => 1200;
    public object? Tag { get; set; }

    public void Disconnect() { }
    public void Disconnect(byte[] b) { }
    public void DisconnectForce() { }
    public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod) => Interlocked.Increment(ref _sendCount);
    public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod) => Interlocked.Increment(ref _sendCount);
    public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0) { }
    public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod) => 0;
}

internal static class BnlSilencer
{
    // Without registered sinks BNL falls back to Console color writes; the storm tests
    // below would emit tens of thousands of log lines. Idempotent, safe to set repeatedly.
    internal static void Silence()
    {
        BNL.LogOutput = static _ => { };
        BNL.LogWarningOutput = static _ => { };
        BNL.LogErrorOutput = static _ => { };
    }
}

[Collection("BasisServer shared network statics")]
public class BasisNetworkOwnershipTests
{
    static BasisNetworkOwnershipTests() => BnlSilencer.Silence();

    // Object keys are unique per test; there is no public clear API for
    // BasisNetworkOwnership.ownershipByObjectId, so tests never share keys.
    private static NetPacketReader BuildReader(ushort playerId, string ownershipId)
    {
        var message = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = playerId },
            ownershipID = ownershipId,
        };
        var writer = new NetDataWriter(true, 64);
        message.Serialize(writer);
        return NetPacketReader.Create(writer.Data, 0, writer.Length, static () => { });
    }

    [Fact]
    public void NetworkRequestNewOrExisting_FirstRequesterWins_SecondSeesExistingOwner()
    {
        const string key = "own:first-wins";
        var message = new OwnershipTransferMessage { ownershipID = key };

        Assert.True(BasisNetworkOwnership.NetworkRequestNewOrExisting(message, 42, out ushort owner));
        Assert.Equal((ushort)42, owner);

        Assert.False(BasisNetworkOwnership.NetworkRequestNewOrExisting(message, 43, out owner));
        Assert.Equal((ushort)42, owner);
    }

    [Fact]
    public void GetOwnershipInformation_UnknownObject_ReturnsFalseWithZeroOwner()
    {
        Assert.False(BasisNetworkOwnership.GetOwnershipInformation("own:unknown", out ushort owner));
        Assert.Equal((ushort)0, owner);
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase("own:unknown"));
    }

    [Fact]
    public void AddOwnership_SecondAddForSameObject_IsRejectedAndKeepsFirstOwner()
    {
        const string key = "own:add-twice";
        Assert.True(BasisNetworkOwnership.AddOwnership(key, 1));
        Assert.False(BasisNetworkOwnership.AddOwnership(key, 2));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort owner));
        Assert.Equal((ushort)1, owner);
    }

    [Fact]
    public void SwitchOwnership_TransfersExistingObjectToNewOwner()
    {
        const string key = "own:switch";
        Assert.True(BasisNetworkOwnership.AddOwnership(key, 10));
        Assert.True(BasisNetworkOwnership.SwitchOwnership(key, 20));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort owner));
        Assert.Equal((ushort)20, owner);
    }

    [Fact]
    public void SwitchOwnership_UnknownObject_ImplicitlyAcquiresIt()
    {
        const string key = "own:switch-unknown";
        Assert.True(BasisNetworkOwnership.SwitchOwnership(key, 33));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort owner));
        Assert.Equal((ushort)33, owner);
    }

    [Fact]
    public void RemoveObject_RemovesExisting_ReportsFalseForUnknown()
    {
        const string key = "own:remove";
        Assert.False(BasisNetworkOwnership.RemoveObject(key));
        Assert.True(BasisNetworkOwnership.AddOwnership(key, 3));
        Assert.True(BasisNetworkOwnership.RemoveObject(key));
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase(key));
        Assert.False(BasisNetworkOwnership.RemoveObject(key));
    }

    [Fact]
    public void OwnerIds_UshortEdgeValues_RoundTrip()
    {
        Assert.True(BasisNetworkOwnership.AddOwnership("own:edge:zero", 0));
        Assert.True(BasisNetworkOwnership.AddOwnership("own:edge:max", ushort.MaxValue));

        Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:edge:zero", out ushort zero));
        Assert.Equal((ushort)0, zero);
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:edge:max", out ushort max));
        Assert.Equal(ushort.MaxValue, max);

        Assert.True(BasisNetworkOwnership.SwitchOwnership("own:edge:max", 0));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:edge:max", out ushort switched));
        Assert.Equal((ushort)0, switched);
    }

    [Fact]
    public void RemovePlayerOwnership_StripsOnlyThatPlayersObjects()
    {
        const ushort victim = 41001;
        const ushort bystander = 41002;
        Assert.True(BasisNetworkOwnership.AddOwnership("own:rpo:a", victim));
        Assert.True(BasisNetworkOwnership.AddOwnership("own:rpo:b", victim));
        Assert.True(BasisNetworkOwnership.AddOwnership("own:rpo:c", bystander));

        BasisNetworkOwnership.RemovePlayerOwnership(victim);

        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase("own:rpo:a"));
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase("own:rpo:b"));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:rpo:c", out ushort owner));
        Assert.Equal(bystander, owner);
    }

    [Fact]
    public void RemovePlayerOwnership_PlayerWithoutObjects_IsANoOp()
    {
        BasisNetworkOwnership.RemovePlayerOwnership(64000);
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase("own:rpo:none"));
    }

    [Fact]
    public void OwnershipResponse_WirePath_FirstRequesterWins_SecondGetsExistingOwner()
    {
        const string key = "own:wire:response";

        var first = new OwnershipFakeNetPeer(7);
        BasisNetworkOwnership.OwnershipResponse(BuildReader(0, key), first);
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort owner));
        Assert.Equal((ushort)7, owner);
        Assert.Equal(1, first.SendCount);

        var second = new OwnershipFakeNetPeer(9);
        BasisNetworkOwnership.OwnershipResponse(BuildReader(0, key), second);
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out owner));
        Assert.Equal((ushort)7, owner);
        Assert.Equal(1, second.SendCount);
    }

    [Fact]
    public void OwnershipTransfer_WirePath_SwitchesExistingAndImplicitlyAcquiresUnknown()
    {
        const string existing = "own:wire:transfer-existing";
        Assert.True(BasisNetworkOwnership.AddOwnership(existing, 5));

        var peer = new OwnershipFakeNetPeer(8);
        BasisNetworkOwnership.OwnershipTransfer(BuildReader(5, existing), peer);
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(existing, out ushort owner));
        Assert.Equal((ushort)8, owner);

        const string unknown = "own:wire:transfer-unknown";
        BasisNetworkOwnership.OwnershipTransfer(BuildReader(0, unknown), peer);
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(unknown, out owner));
        Assert.Equal((ushort)8, owner);
    }

    [Fact]
    public void RemoveOwnership_WirePath_OnlyCurrentOwnerCanRemove()
    {
        const string key = "own:wire:remove";
        Assert.True(BasisNetworkOwnership.AddOwnership(key, 11));

        // A non-owner cannot release the object even by naming the real owner in the packet:
        // authorization comes from the sending peer, not the client-supplied player id.
        BasisNetworkOwnership.RemoveOwnership(BuildReader(11, key), new OwnershipFakeNetPeer(12));
        Assert.True(BasisNetworkOwnership.DoesObjectExistInDatabase(key));

        var peer = new OwnershipFakeNetPeer(11);

        BasisNetworkOwnership.RemoveOwnership(BuildReader(11, "own:wire:remove-unknown"), peer);
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase("own:wire:remove-unknown"));

        // The owner's own request succeeds; the redundant player id field is ignored.
        BasisNetworkOwnership.RemoveOwnership(BuildReader(12, key), peer);
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase(key));
    }

    [Fact]
    public void ConcurrentRequestStorm_SameObject_ProducesExactlyOneOwner()
    {
        const string key = "own:storm:same-object";
        const int Threads = 64;
        int winners = 0;
        int winnerRequester = 0;

        Parallel.For(0, Threads, i =>
        {
            var message = new OwnershipTransferMessage { ownershipID = key };
            if (BasisNetworkOwnership.NetworkRequestNewOrExisting(message, (ushort)(i + 1), out ushort assigned))
            {
                Interlocked.Increment(ref winners);
                Interlocked.Exchange(ref winnerRequester, i + 1);
                Assert.Equal((ushort)(i + 1), assigned);
            }
        });

        Assert.Equal(1, winners);
        Assert.True(BasisNetworkOwnership.DoesObjectExistInDatabase(key));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort finalOwner));
        Assert.Equal(winnerRequester, finalOwner);
    }

    [Fact]
    public void ConcurrentSwitchStorm_AllSucceed_FinalOwnerIsOneOfTheRequesters()
    {
        const string key = "own:storm:switch";
        Assert.True(BasisNetworkOwnership.AddOwnership(key, 500));

        const int Threads = 32;
        bool[] results = new bool[Threads];
        Parallel.For(0, Threads, i => results[i] = BasisNetworkOwnership.SwitchOwnership(key, (ushort)(i + 1)));

        Assert.All(results, r => Assert.True(r));
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort finalOwner));
        Assert.InRange(finalOwner, (ushort)1, (ushort)Threads);
    }
}

[Collection("BasisServer shared network statics")]
public class BasisSavedStateTests
{
    static BasisSavedStateTests() => BnlSilencer.Silence();

    // Player ids are unique per test (51xxx / 52xxx range) and every test removes the
    // players it created, since the state dictionaries have no public clear API.

    [Fact]
    public void ReadyMessage_StoresAvatarChangeAndMetaData()
    {
        var peer = new OwnershipFakeNetPeer(51000);
        try
        {
            var ready = new ReadyMessage
            {
                playerMetaDataMessage = new ClientMetaDataMessage
                {
                    playerUUID = "uuid-51000",
                    playerDisplayName = "Tester",
                    playerPlatform = "xunit",
                },
                clientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    loadMode = 2,
                    byteArray = new byte[] { 1, 2, 3 },
                    LocalAvatarIndex = 9,
                },
            };
            BasisSavedState.AddLastData(peer, ready);

            Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
            Assert.Equal((byte)2, avatar.loadMode);
            Assert.Equal(new byte[] { 1, 2, 3 }, avatar.byteArray);
            Assert.Equal((byte)9, avatar.LocalAvatarIndex);

            Assert.True(BasisSavedState.GetLastPlayerMetaData(peer, out var meta));
            Assert.Equal("uuid-51000", meta.playerUUID);
            Assert.Equal("Tester", meta.playerDisplayName);
            Assert.Equal("xunit", meta.playerPlatform);
        }
        finally
        {
            BasisSavedState.RemovePlayer(peer.Id);
        }
    }

    [Fact]
    public void AvatarChange_LatestWriteWins()
    {
        var peer = new OwnershipFakeNetPeer(51001);
        try
        {
            BasisSavedState.AddLastData(peer, new ClientAvatarChangeMessage { loadMode = 0, byteArray = new byte[] { 1 }, LocalAvatarIndex = 1 });
            BasisSavedState.AddLastData(peer, new ClientAvatarChangeMessage { loadMode = 1, byteArray = new byte[] { 7, 7 }, LocalAvatarIndex = 2 });

            Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
            Assert.Equal((byte)1, avatar.loadMode);
            Assert.Equal(new byte[] { 7, 7 }, avatar.byteArray);
            Assert.Equal((byte)2, avatar.LocalAvatarIndex);
        }
        finally
        {
            BasisSavedState.RemovePlayer(peer.Id);
        }
    }

    // ── Body fit ────────────────────────────────────────────────────────────
    // The fit is stored on the avatar record so the late-join replay carries it, but it arrives on its
    // own message. These pin the merge: a fit update must never disturb which avatar is worn, and an
    // avatar change must never silently revert the wearer's proportions.

    [Fact]
    public void BodyFit_MergesIntoTheAvatarRecord_WithoutDisturbingTheAvatar()
    {
        var peer = new OwnershipFakeNetPeer(51010);
        try
        {
            BasisSavedState.AddLastData(peer, new ClientAvatarChangeMessage
            {
                loadMode = 1,
                byteArray = new byte[] { 4, 5, 6 },
                LocalAvatarIndex = 12,
                ArmScale = 1f,
                LegScale = 1f,
                TorsoScale = 1f,
            });

            BasisSavedState.UpdateBodyFit(peer, new ClientBodyFitMessage
            {
                ArmScale = 1.0625f,
                LegScale = 0.9375f,
                TorsoScale = 1.125f,
            });

            Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
            Assert.Equal(1.0625f, avatar.ArmScale);
            Assert.Equal(0.9375f, avatar.LegScale);
            Assert.Equal(1.125f, avatar.TorsoScale);
            // The avatar itself must be untouched — a recalibration is not an avatar swap.
            Assert.Equal((byte)1, avatar.loadMode);
            Assert.Equal(new byte[] { 4, 5, 6 }, avatar.byteArray);
            Assert.Equal((byte)12, avatar.LocalAvatarIndex);
        }
        finally
        {
            BasisSavedState.RemovePlayer(peer.Id);
        }
    }

    /// <summary>
    /// A fit can land before any avatar change (recalibrating while the avatar is still loading).
    /// It has to be held, not dropped, or that player renders unfitted until they recalibrate again.
    /// </summary>
    [Fact]
    public void BodyFit_ArrivingBeforeAnyAvatar_IsHeldOnAPlaceholder()
    {
        var peer = new OwnershipFakeNetPeer(51011);
        try
        {
            BasisSavedState.UpdateBodyFit(peer, new ClientBodyFitMessage
            {
                ArmScale = 1.05f,
                LegScale = 0.95f,
                TorsoScale = 1.02f,
            });

            Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
            Assert.Null(avatar.byteArray);
            Assert.Equal(1.05f, avatar.ArmScale);
            Assert.Equal(0.95f, avatar.LegScale);
            Assert.Equal(1.02f, avatar.TorsoScale);
        }
        finally
        {
            BasisSavedState.RemovePlayer(peer.Id);
        }
    }

    [Fact]
    public void BodyFit_LatestWriteWins()
    {
        var peer = new OwnershipFakeNetPeer(51012);
        try
        {
            BasisSavedState.UpdateBodyFit(peer, new ClientBodyFitMessage { ArmScale = 1.1f, LegScale = 1.1f, TorsoScale = 1.1f });
            BasisSavedState.UpdateBodyFit(peer, new ClientBodyFitMessage { ArmScale = 0.9f, LegScale = 0.8f, TorsoScale = 1.2f });

            Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
            Assert.Equal(0.9f, avatar.ArmScale);
            Assert.Equal(0.8f, avatar.LegScale);
            Assert.Equal(1.2f, avatar.TorsoScale);
        }
        finally
        {
            BasisSavedState.RemovePlayer(peer.Id);
        }
    }

    /// <summary>
    /// An avatar change carries its own fit, so it legitimately replaces the stored one — the client
    /// stamps its current fit into that message and corrects it once the new avatar has re-solved.
    /// </summary>
    [Fact]
    public void AvatarChange_AfterBodyFit_CarriesItsOwnFit()
    {
        var peer = new OwnershipFakeNetPeer(51013);
        try
        {
            BasisSavedState.UpdateBodyFit(peer, new ClientBodyFitMessage { ArmScale = 1.1f, LegScale = 1.1f, TorsoScale = 1.1f });
            BasisSavedState.AddLastData(peer, new ClientAvatarChangeMessage
            {
                loadMode = 0,
                byteArray = new byte[] { 9 },
                LocalAvatarIndex = 3,
                ArmScale = 1.03f,
                LegScale = 1.04f,
                TorsoScale = 1.05f,
            });

            Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
            Assert.Equal(new byte[] { 9 }, avatar.byteArray);
            Assert.Equal(1.03f, avatar.ArmScale);
            Assert.Equal(1.04f, avatar.LegScale);
            Assert.Equal(1.05f, avatar.TorsoScale);
        }
        finally
        {
            BasisSavedState.RemovePlayer(peer.Id);
        }
    }

    [Fact]
    public void BodyFit_IsClearedWithThePlayer()
    {
        var peer = new OwnershipFakeNetPeer(51014);
        BasisSavedState.UpdateBodyFit(peer, new ClientBodyFitMessage { ArmScale = 1.1f, LegScale = 1.1f, TorsoScale = 1.1f });
        BasisSavedState.RemovePlayer(peer.Id);

        Assert.False(BasisSavedState.GetLastAvatarChangeState(peer, out _));
    }

    [Fact]
    public void UnknownPlayer_EveryGetterReturnsSafeDefault()
    {
        var stranger = new OwnershipFakeNetPeer(51999);
        Assert.False(BasisSavedState.GetLastAvatarChangeState(stranger, out _));
        Assert.False(BasisSavedState.GetLastPlayerMetaData(stranger, out _));
        Assert.False(BasisSavedState.GetResolvedVoicePeers(stranger, out _));
        Assert.False(BasisSavedState.IsInShoutMode(stranger.Id));
        Assert.DoesNotContain(stranger.Id, BasisSavedState.GetAllShoutModePlayers());
    }

    [Fact]
    public void RemovePlayer_ClearsEveryStoredStateForThatPlayer()
    {
        var peer = new OwnershipFakeNetPeer(51002);
        var ready = new ReadyMessage
        {
            playerMetaDataMessage = new ClientMetaDataMessage { playerUUID = "u", playerDisplayName = "n", playerPlatform = "p" },
            clientAvatarChangeMessage = new ClientAvatarChangeMessage { loadMode = 1, byteArray = new byte[] { 5 }, LocalAvatarIndex = 1 },
        };
        BasisSavedState.AddLastData(peer, ready);
        BasisSavedState.GetOrCreateResolvedList(peer.Id);
        BasisSavedState.SetShoutMode(peer.Id, true);

        BasisSavedState.RemovePlayer(peer.Id);

        Assert.False(BasisSavedState.GetLastAvatarChangeState(peer, out _));
        Assert.False(BasisSavedState.GetLastPlayerMetaData(peer, out _));
        Assert.False(BasisSavedState.GetResolvedVoicePeers(peer, out _));
        Assert.False(BasisSavedState.IsInShoutMode(peer.Id));
    }

    [Fact]
    public void RemovePlayer_PurgesTheDisconnectedPeerFromOtherPlayersVoiceLists()
    {
        var host = new OwnershipFakeNetPeer(51003);
        var leaving = new OwnershipFakeNetPeer(51004);
        var staying = new OwnershipFakeNetPeer(51005);
        try
        {
            var list = BasisSavedState.GetOrCreateResolvedList(host.Id);
            lock (list)
            {
                list.Add(leaving);
                list.Add(staying);
                list.Add(null!);
            }

            BasisSavedState.RemovePlayer(leaving.Id);

            Assert.True(BasisSavedState.GetResolvedVoicePeers(host, out var after));
            Assert.Same(list, after);
            Assert.DoesNotContain(leaving, after);
            Assert.Contains(staying, after);
            Assert.Contains(after, p => p is null);
            Assert.Equal(2, after.Count);
        }
        finally
        {
            BasisSavedState.RemovePlayer(host.Id);
            BasisSavedState.RemovePlayer(staying.Id);
        }
    }

    [Fact]
    public void VoiceReceivers_ResolveAgainstAuthenticatedPeers_EmptyClears_NullKeeps()
    {
        var host = new OwnershipFakeNetPeer(51010);
        var target = new OwnershipFakeNetPeer(51011);
        NetworkServer.AuthenticatedPeers[target.Id] = target;
        try
        {
            var users = ArrayPool<ushort>.Shared.Rent(2);
            users[0] = (ushort)target.Id;
            users[1] = 51012;
            BasisSavedState.AddLastData(host, new VoiceReceiversMessage { Users = users, UsersLength = 2 });

            Assert.True(BasisSavedState.GetResolvedVoicePeers(host, out var resolved));
            NetPeer single = Assert.Single(resolved);
            Assert.Same(target, single);

            BasisSavedState.AddLastData(host, new VoiceReceiversMessage { Users = null!, UsersLength = 0 });
            Assert.True(BasisSavedState.GetResolvedVoicePeers(host, out var afterNull));
            Assert.Single(afterNull);

            BasisSavedState.AddLastData(host, new VoiceReceiversMessage { Users = Array.Empty<ushort>(), UsersLength = 0 });
            Assert.True(BasisSavedState.GetResolvedVoicePeers(host, out var afterEmpty));
            Assert.Empty(afterEmpty);
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(target.Id, out _);
            BasisSavedState.RemovePlayer(host.Id);
        }
    }

    [Fact]
    public void GetOrCreateResolvedList_IsStablePerPlayer_UntilRemovePlayer()
    {
        const int id = 51007;
        try
        {
            var first = BasisSavedState.GetOrCreateResolvedList(id);
            var second = BasisSavedState.GetOrCreateResolvedList(id);
            Assert.Same(first, second);

            BasisSavedState.RemovePlayer(id);
            var third = BasisSavedState.GetOrCreateResolvedList(id);
            Assert.NotSame(first, third);
        }
        finally
        {
            BasisSavedState.RemovePlayer(id);
        }
    }

    [Fact]
    public void ShoutMode_SetQueryAndEnumerate()
    {
        const int id = 51008;
        try
        {
            Assert.False(BasisSavedState.IsInShoutMode(id));

            BasisSavedState.SetShoutMode(id, true);
            Assert.True(BasisSavedState.IsInShoutMode(id));
            Assert.Contains(id, BasisSavedState.GetAllShoutModePlayers());

            BasisSavedState.SetShoutMode(id, true);
            Assert.True(BasisSavedState.IsInShoutMode(id));

            BasisSavedState.SetShoutMode(id, false);
            Assert.False(BasisSavedState.IsInShoutMode(id));
            Assert.DoesNotContain(id, BasisSavedState.GetAllShoutModePlayers());
        }
        finally
        {
            BasisSavedState.SetShoutMode(id, false);
        }
    }

    [Fact]
    public void ConcurrentStoreAndRead_KeepsEveryPlayersStateIntact()
    {
        const int BaseId = 52000;
        const int Players = 128;
        try
        {
            Parallel.For(0, Players, i =>
            {
                int id = BaseId + i;
                var peer = new OwnershipFakeNetPeer(id);
                BasisSavedState.AddLastData(peer, new ClientAvatarChangeMessage { loadMode = 1, byteArray = new[] { (byte)i }, LocalAvatarIndex = (byte)i });
                BasisSavedState.SetShoutMode(id, (i & 1) == 0);
                Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
                Assert.Equal((byte)i, avatar.LocalAvatarIndex);
            });

            for (int i = 0; i < Players; i++)
            {
                var peer = new OwnershipFakeNetPeer(BaseId + i);
                Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
                Assert.Equal((byte)i, avatar.byteArray[0]);
                Assert.Equal((i & 1) == 0, BasisSavedState.IsInShoutMode(BaseId + i));
            }
        }
        finally
        {
            for (int i = 0; i < Players; i++)
            {
                BasisSavedState.RemovePlayer(BaseId + i);
            }
        }
    }
}

[Collection("BasisServer shared network statics")]
public class BasisNetworkIDDatabaseTests
{
    static BasisNetworkIDDatabaseTests() => BnlSilencer.Silence();

    // Every test starts with Reset() so counter assertions are deterministic; the fixture
    // runs sequentially within its collection, so tests cannot clear each other mid-flight.

    [Fact]
    public void AddOrFind_AssignsSequentialIdsStartingAtZero()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(2);

        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:a");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:b");

        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:a", out ushort a));
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:b", out ushort b));
        Assert.Equal((ushort)0, a);
        Assert.Equal((ushort)1, b);
        Assert.Equal(0, peer.SendCount);
    }

    [Fact]
    public void AddOrFind_DuplicateStringId_KeepsMappingAndDoesNotBurnAnId()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(3);

        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:dup");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:dup");

        Assert.Equal(1, peer.SendCount);
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:dup", out ushort id));
        Assert.Equal((ushort)0, id);
        Assert.Equal(1, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);

        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:after-dup");
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:after-dup", out ushort next));
        Assert.Equal((ushort)1, next);
    }

    [Fact]
    public void UnknownId_IsAbsent_AndGetAllOnEmptyReturnsFalse()
    {
        BasisNetworkIDDatabase.Reset();
        Assert.False(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:never-added", out _));
        Assert.False(BasisNetworkIDDatabase.GetAllNetworkID(out var messages));
        Assert.NotNull(messages);
        Assert.Empty(messages);
    }

    [Fact]
    public void GetAllNetworkID_ReturnsEveryStoredMapping()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(4);
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:all:x");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:all:y");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:all:z");

        Assert.True(BasisNetworkIDDatabase.GetAllNetworkID(out var messages));
        Assert.Equal(3, messages.Count);
        foreach (var message in messages)
        {
            Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(message.NetIDMessage.playerID, out ushort stored));
            Assert.Equal(stored, message.UshortUniqueIDMessage.UniqueIDUshort);
        }
    }

    [Fact]
    public void RemoveUshortNetworkID_RemovesOnlyThatMapping_AndNeverReusesIds()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(5);
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:rm:a");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:rm:b");

        BasisNetworkIDDatabase.RemoveUshortNetworkID(0);
        Assert.False(BasisNetworkIDDatabase.UshortNetworkDatabase.ContainsKey("net:rm:a"));
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:rm:b", out ushort b));
        Assert.Equal((ushort)1, b);

        BasisNetworkIDDatabase.RemoveUshortNetworkID(12345);
        Assert.Equal(1, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);

        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:rm:a");
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:rm:a", out ushort readded));
        Assert.Equal((ushort)2, readded);
    }

    [Fact]
    public void ManySequentialAdds_ProduceUniqueContiguousIds()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(6);
        const int Count = 500;
        for (int i = 0; i < Count; i++)
        {
            BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:many:" + i);
        }

        Assert.Equal(Count, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);
        var ids = BasisNetworkIDDatabase.UshortNetworkDatabase.Values.OrderBy(v => v).ToArray();
        Assert.Equal(Count, ids.Distinct().Count());
        Assert.Equal((ushort)0, ids[0]);
        Assert.Equal((ushort)(Count - 1), ids[^1]);
    }

    [Fact]
    public void ConcurrentAddStorm_DistinctStringIds_NoIdCollisions()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(7);
        const int Count = 256;
        Parallel.For(0, Count, i => BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:storm:" + i));

        Assert.Equal(Count, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);
        var ids = BasisNetworkIDDatabase.UshortNetworkDatabase.Values.OrderBy(v => v).ToArray();
        for (int i = 0; i < Count; i++)
        {
            Assert.Equal((ushort)i, ids[i]);
        }
        Assert.Equal(0, peer.SendCount);
    }

    [Fact]
    public void Reset_ClearsMappingsAndRestartsCounter()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(8);
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:reset:a");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:reset:b");

        BasisNetworkIDDatabase.Reset();

        Assert.Empty(BasisNetworkIDDatabase.UshortNetworkDatabase);
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:reset:c");
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:reset:c", out ushort id));
        Assert.Equal((ushort)0, id);
    }

    [Fact]
    public void CounterExhaustion_DropsAtUshortLimit_AndStaysFullUntilReset()
    {
        BasisNetworkIDDatabase.Reset();
        var peer = new OwnershipFakeNetPeer(9);
        for (int i = 0; i <= ushort.MaxValue; i++)
        {
            BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:cap:" + i);
        }
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:cap:" + ushort.MaxValue, out ushort last));
        Assert.Equal(ushort.MaxValue, last);

        // At the ceiling requests are dropped, never thrown: ids arrive per client message, and a
        // throw per message was an exception storm through the message processor.
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:cap:overflow");
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:cap:overflow-2");
        Assert.False(BasisNetworkIDDatabase.UshortNetworkDatabase.ContainsKey("net:cap:overflow"));
        Assert.Equal(ushort.MaxValue + 1, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);

        BasisNetworkIDDatabase.Reset();
        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:cap:post-reset");
        Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue("net:cap:post-reset", out ushort fresh));
        Assert.Equal((ushort)0, fresh);
    }
}
