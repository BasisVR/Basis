using System.Buffers;
using System.Collections.Concurrent;
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
using ServerNetIDMessage = BasisNetworkCore.Serializable.SerializableBasis.ServerNetIDMessage;
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

internal sealed class RecordingNetPeer : NetPeer
{
    public RecordingNetPeer(int id) => Id = id;

    public int Id { get; }
    public ConcurrentQueue<(byte Channel, DeliveryMethod Method, byte[] Data)> Sent { get; } = new();
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
    public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod) => Record(data, data.Length, channelNumber, deliveryMethod);
    public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod) => Record(data.Data, data.Length, channelNumber, deliveryMethod);
    public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0) { }
    public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod) => 0;

    private void Record(byte[] data, int length, byte channel, DeliveryMethod method)
    {
        Sent.Enqueue((channel, method, data.AsSpan(0, length).ToArray()));
        Thread.Yield();
    }
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

        Assert.False(BasisNetworkOwnership.GetOwnershipInformation("own:rpo:a", out ushort a) && a == victim);
        Assert.False(BasisNetworkOwnership.GetOwnershipInformation("own:rpo:b", out ushort b) && b == victim);
        Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:rpo:c", out ushort owner));
        Assert.Equal(bystander, owner);
    }

    [Fact]
    public void RemovePlayerOwnership_PlayerWithoutObjects_IsANoOp()
    {
        BasisNetworkOwnership.RemovePlayerOwnership(64000);
        Assert.False(BasisNetworkOwnership.DoesObjectExistInDatabase("own:rpo:none"));
    }

    private static OwnershipTransferMessage DecodeOwnership(byte[] payload)
    {
        var message = new OwnershipTransferMessage();
        message.Deserialize(NetPacketReader.Create(payload, 0, payload.Length, static () => { }));
        return message;
    }

    [Fact]
    public void RemovePlayerOwnership_WithRemainingPeers_MigratesToLongestConnectedPeerAndBroadcastsTheChange()
    {
        var victim = new FakeNetPeer(41010, "127.0.0.1");
        var oldest = new FakeNetPeer(41011, "127.0.0.1");
        var newer = new FakeNetPeer(41012, "127.0.0.1");
        NetworkServer.AuthenticatedPeers[victim.Id] = victim;
        NetworkServer.AuthenticatedPeers[oldest.Id] = oldest;
        NetworkServer.AuthenticatedPeers[newer.Id] = newer;
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(victim.Id, -3);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(oldest.Id, -2);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(newer.Id, -1);
        try
        {
            Assert.True(BasisNetworkOwnership.AddOwnership("own:migrate:a", (ushort)victim.Id));
            Assert.True(BasisNetworkOwnership.AddOwnership("own:migrate:b", (ushort)victim.Id));
            Assert.True(BasisNetworkOwnership.AddOwnership("own:migrate:c", (ushort)newer.Id));

            BasisNetworkOwnership.RemovePlayerOwnership(victim.Id);

            Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:migrate:a", out ushort a));
            Assert.Equal((ushort)oldest.Id, a);
            Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:migrate:b", out ushort b));
            Assert.Equal((ushort)oldest.Id, b);
            Assert.True(BasisNetworkOwnership.GetOwnershipInformation("own:migrate:c", out ushort c));
            Assert.Equal((ushort)newer.Id, c);

            Assert.Empty(victim.Sent);
            foreach (var remaining in new[] { oldest, newer })
            {
                var changes = remaining.Sent.Where(s => s.Channel == BasisNetworkCommons.ChangeCurrentOwnerRequestChannel).ToList();
                Assert.Equal(2, changes.Count);
                Assert.DoesNotContain(remaining.Sent, s => s.Channel == BasisNetworkCommons.RemoveCurrentOwnerRequestChannel);
                var decoded = changes.Select(s => DecodeOwnership(s.Data)).ToList();
                Assert.All(decoded, m => Assert.Equal((ushort)oldest.Id, m.playerIdMessage.playerID));
                Assert.Equal(new[] { "own:migrate:a", "own:migrate:b" }, decoded.Select(m => m.ownershipID).OrderBy(k => k).ToArray());
            }
        }
        finally
        {
            foreach (var peer in new[] { victim, oldest, newer })
            {
                NetworkServer.AuthenticatedPeers.TryRemove(new KeyValuePair<int, NetPeer>(peer.Id, peer));
                BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(peer.Id);
            }
            BasisNetworkOwnership.RemoveObject("own:migrate:a");
            BasisNetworkOwnership.RemoveObject("own:migrate:b");
            BasisNetworkOwnership.RemoveObject("own:migrate:c");
        }
    }

    [Fact]
    public void TrySelectSuccessor_SkipsTheDepartingPeer_PrefersEarliestJoin_ThenLowestId()
    {
        var departing = new OwnershipFakeNetPeer(41020);
        var earliest = new OwnershipFakeNetPeer(41023);
        var tiedHigh = new OwnershipFakeNetPeer(41022);
        var tiedLow = new OwnershipFakeNetPeer(41021);
        NetworkServer.AuthenticatedPeers[departing.Id] = departing;
        NetworkServer.AuthenticatedPeers[earliest.Id] = earliest;
        NetworkServer.AuthenticatedPeers[tiedHigh.Id] = tiedHigh;
        NetworkServer.AuthenticatedPeers[tiedLow.Id] = tiedLow;
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(departing.Id, -10);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(earliest.Id, -9);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(tiedHigh.Id, -8);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(tiedLow.Id, -8);
        try
        {
            Assert.True(BasisNetworkOwnership.TrySelectSuccessor(departing.Id, out ushort successor, out var recipients));
            Assert.Equal((ushort)earliest.Id, successor);
            Assert.DoesNotContain(departing, recipients);
            Assert.Contains(earliest, recipients);
            Assert.Contains(tiedHigh, recipients);
            Assert.Contains(tiedLow, recipients);

            NetworkServer.AuthenticatedPeers.TryRemove(earliest.Id, out _);
            Assert.True(BasisNetworkOwnership.TrySelectSuccessor(departing.Id, out successor, out _));
            Assert.Equal((ushort)tiedLow.Id, successor);
        }
        finally
        {
            foreach (var peer in new[] { departing, earliest, tiedHigh, tiedLow })
            {
                NetworkServer.AuthenticatedPeers.TryRemove(new KeyValuePair<int, NetPeer>(peer.Id, peer));
                BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(peer.Id);
            }
        }
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

    private static void RunStorm(int threads, Action<int> body)
    {
        using var gate = new ManualResetEventSlim(false);
        var failures = new ConcurrentQueue<Exception>();
        var workers = Enumerable.Range(0, threads).Select(index => new Thread(() =>
        {
            gate.Wait();
            try { body(index); } catch (Exception e) { failures.Enqueue(e); }
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        gate.Set();
        foreach (var worker in workers) worker.Join();
        Assert.Empty(failures);
    }

    private static RecordingNetPeer[] ConnectRecipients(int firstId, int count)
    {
        var peers = Enumerable.Range(0, count).Select(i => new RecordingNetPeer(firstId + i)).ToArray();
        foreach (var peer in peers) NetworkServer.AuthenticatedPeers[peer.Id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return peers;
    }

    private static void DisconnectRecipients(RecordingNetPeer[] peers)
    {
        foreach (var peer in peers) NetworkServer.AuthenticatedPeers.TryRemove(new KeyValuePair<int, NetPeer>(peer.Id, peer));
        NetworkServer.RebuildPeerSnapshot();
    }

    private static IEnumerable<(byte Channel, DeliveryMethod Method, byte[] Data)> OwnershipSent(RecordingNetPeer peer) => peer.Sent.Where(s => s.Channel == BasisNetworkCommons.ChangeCurrentOwnerRequestChannel);

    private static ushort[] OwnersSeenBy(RecordingNetPeer peer) => OwnershipSent(peer).Select(s => DecodeOwnership(s.Data).playerIdMessage.playerID).ToArray();

    [Fact]
    public void ConcurrentTransferStorm_EveryPeerReceivesTheSameOwnerSequence_EndingAtTheServerOwner()
    {
        const string key = "own:storm:fanout-order";
        const int Recipients = 12, Requesters = 8, Rounds = 150;
        var recipients = ConnectRecipients(42100, Recipients);
        try
        {
            Assert.True(BasisNetworkOwnership.AddOwnership(key, 42000));
            RunStorm(Requesters, r =>
            {
                var requester = new OwnershipFakeNetPeer(42001 + r);
                for (int i = 0; i < Rounds; i++)
                {
                    BasisNetworkOwnership.OwnershipTransfer(BuildReader(0, key), requester);
                }
            });

            Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort serverOwner));
            Assert.All(recipients, p =>
            {
                Assert.DoesNotContain(p.Sent, s => s.Channel == BasisNetworkCommons.GetCurrentOwnerRequestChannel);
                Assert.All(OwnershipSent(p), s =>
                {
                    Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method);
                    Assert.Equal(key, DecodeOwnership(s.Data).ownershipID);
                });
            });
            var sequences = recipients.Select(OwnersSeenBy).ToArray();
            Assert.All(sequences, s => Assert.Equal(Requesters * Rounds, s.Length));
            Assert.All(sequences, s => Assert.Equal(serverOwner, s[^1]));
            Assert.All(sequences, s => Assert.Equal(sequences[0], s));
        }
        finally
        {
            DisconnectRecipients(recipients);
            BasisNetworkOwnership.RemoveObject(key);
        }
    }

    [Fact]
    public void ConcurrentGetAndTransferStorm_EveryPeersLastSeenOwnerMatchesTheServer()
    {
        const string key = "own:storm:get-vs-transfer";
        const int Peers = 12, Rounds = 100;
        var peers = ConnectRecipients(42200, Peers);
        try
        {
            RunStorm(Peers, index =>
            {
                var peer = peers[index];
                for (int i = 0; i < Rounds; i++)
                {
                    if ((index & 1) == 0)
                    {
                        BasisNetworkOwnership.OwnershipTransfer(BuildReader(0, key), peer);
                    }
                    else
                    {
                        BasisNetworkOwnership.OwnershipResponse(BuildReader(0, key), peer);
                    }
                }
            });

            Assert.True(BasisNetworkOwnership.GetOwnershipInformation(key, out ushort serverOwner));
            Assert.All(peers, p =>
            {
                Assert.DoesNotContain(p.Sent, s => s.Channel == BasisNetworkCommons.GetCurrentOwnerRequestChannel);
                Assert.Equal(serverOwner, OwnersSeenBy(p)[^1]);
            });
        }
        finally
        {
            DisconnectRecipients(peers);
            BasisNetworkOwnership.RemoveObject(key);
        }
    }

    [Fact]
    public void OwnershipResponse_AndJoinSnapshot_RideTheChangeChannel()
    {
        const string keyA = "own:channel:a", keyB = "own:channel:b";
        var peer = new RecordingNetPeer(42300);
        try
        {
            Assert.True(BasisNetworkOwnership.AddOwnership(keyA, 1));
            Assert.True(BasisNetworkOwnership.AddOwnership(keyB, 2));

            BasisNetworkOwnership.OwnershipResponse(BuildReader(0, keyA), peer);
            var reply = peer.Sent.Single();
            Assert.Equal(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, reply.Channel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, reply.Method);
            Assert.Equal((ushort)1, DecodeOwnership(reply.Data).playerIdMessage.playerID);

            BasisNetworkOwnership.SendOutOwnershipInformation(peer);
            Assert.All(peer.Sent, s => Assert.Equal(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, s.Channel));
            var snapshot = peer.Sent.Select(s => DecodeOwnership(s.Data)).ToList();
            Assert.Contains(snapshot, m => m.ownershipID == keyA && m.playerIdMessage.playerID == 1);
            Assert.Contains(snapshot, m => m.ownershipID == keyB && m.playerIdMessage.playerID == 2);
        }
        finally
        {
            BasisNetworkOwnership.RemoveObject(keyA);
            BasisNetworkOwnership.RemoveObject(keyB);
        }
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
        Assert.False(BasisSavedState.IsInAnnounceMode(stranger.Id));
        Assert.DoesNotContain(stranger.Id, BasisSavedState.GetAllAnnounceModePlayers());
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
        BasisSavedState.SetAnnounceMode(peer.Id, true);

        BasisSavedState.RemovePlayer(peer.Id);

        Assert.False(BasisSavedState.GetLastAvatarChangeState(peer, out _));
        Assert.False(BasisSavedState.GetLastPlayerMetaData(peer, out _));
        Assert.False(BasisSavedState.GetResolvedVoicePeers(peer, out _));
        Assert.False(BasisSavedState.IsInAnnounceMode(peer.Id));
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
    public void AnnounceMode_SetQueryAndEnumerate()
    {
        const int id = 51008;
        try
        {
            Assert.False(BasisSavedState.IsInAnnounceMode(id));

            BasisSavedState.SetAnnounceMode(id, true);
            Assert.True(BasisSavedState.IsInAnnounceMode(id));
            Assert.Contains(id, BasisSavedState.GetAllAnnounceModePlayers());

            BasisSavedState.SetAnnounceMode(id, true);
            Assert.True(BasisSavedState.IsInAnnounceMode(id));

            BasisSavedState.SetAnnounceMode(id, false);
            Assert.False(BasisSavedState.IsInAnnounceMode(id));
            Assert.DoesNotContain(id, BasisSavedState.GetAllAnnounceModePlayers());
        }
        finally
        {
            BasisSavedState.SetAnnounceMode(id, false);
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
                BasisSavedState.SetAnnounceMode(id, (i & 1) == 0);
                Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
                Assert.Equal((byte)i, avatar.LocalAvatarIndex);
            });

            for (int i = 0; i < Players; i++)
            {
                var peer = new OwnershipFakeNetPeer(BaseId + i);
                Assert.True(BasisSavedState.GetLastAvatarChangeState(peer, out var avatar));
                Assert.Equal((byte)i, avatar.byteArray[0]);
                Assert.Equal((i & 1) == 0, BasisSavedState.IsInAnnounceMode(BaseId + i));
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
    public void ConcurrentRequestsForTheSameString_AssignExactlyOneId_AndEveryBroadcastAgrees()
    {
        BasisNetworkIDDatabase.Reset();
        const int Requesters = 16, Rounds = 200;
        var requesters = Enumerable.Range(0, Requesters).Select(i => new RecordingNetPeer(43000 + i)).ToArray();
        foreach (var peer in requesters) NetworkServer.AuthenticatedPeers[peer.Id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            using var barrier = new Barrier(Requesters);
            var failures = new ConcurrentQueue<Exception>();
            var workers = requesters.Select(peer => new Thread(() =>
            {
                try
                {
                    for (int round = 0; round < Rounds; round++)
                    {
                        barrier.SignalAndWait();
                        BasisNetworkIDDatabase.AddOrFindNetworkID(peer, "net:same:" + round);
                    }
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                    barrier.RemoveParticipant();
                }
            })).ToArray();
            foreach (var worker in workers) worker.Start();
            foreach (var worker in workers) worker.Join();
            Assert.Empty(failures);

            Assert.Equal(Rounds, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);
            Assert.Equal((ushort)(Rounds - 1), BasisNetworkIDDatabase.UshortNetworkDatabase.Values.Max());
            Assert.All(requesters, peer => Assert.All(peer.Sent.Where(s => s.Channel == BasisNetworkCommons.netIDAssignChannel), sent =>
            {
                var message = new ServerNetIDMessage();
                message.Deserialize(NetPacketReader.Create(sent.Data, 0, sent.Data.Length, static () => { }));
                Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(message.NetIDMessage.playerID, out ushort stored));
                Assert.Equal(stored, message.UshortUniqueIDMessage.UniqueIDUshort);
            }));
        }
        finally
        {
            foreach (var peer in requesters) NetworkServer.AuthenticatedPeers.TryRemove(new KeyValuePair<int, NetPeer>(peer.Id, peer));
            NetworkServer.RebuildPeerSnapshot();
            BasisNetworkIDDatabase.Reset();
        }
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
        // This test targets the shared ushort-space exhaustion (65,536 ids across the whole instance),
        // which a single peer must be able to drive here. Lift the separate per-peer id cap out of the
        // way for it; PerPeerIdCap_LimitsOneClient below covers that cap on its own. Capture/restore
        // NetworkServer.Configuration so this never leaks a cap into the other shared-static tests.
        Configuration savedConfig = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration { MaxNetworkIdsPerPlayer = ushort.MaxValue + 10 };
        try
        {
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
        finally
        {
            NetworkServer.Configuration = savedConfig;
        }
    }

    [Fact]
    public void PerPeerIdCap_LimitsOneClient_ButNotOthersOrExistingIds()
    {
        BasisNetworkIDDatabase.Reset();
        Configuration savedConfig = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration { MaxNetworkIdsPerPlayer = 4 };
        try
        {
            var greedy = new OwnershipFakeNetPeer(20);
            for (int i = 0; i < 20; i++)
            {
                BasisNetworkIDDatabase.AddOrFindNetworkID(greedy, "net:greedy:" + i);
            }
            // Capped at its allowance no matter how many distinct ids it asks for — one client can no
            // longer consume the shared id space and lock everyone else out.
            Assert.Equal(4, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);

            // A different peer still gets ids; the greedy peer's exhausted allowance is its own.
            var other = new OwnershipFakeNetPeer(21);
            BasisNetworkIDDatabase.AddOrFindNetworkID(other, "net:other:0");
            Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.ContainsKey("net:other:0"));
            Assert.Equal(5, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);

            // Looking up an id it already owns is not a new assignment and is never blocked by the cap.
            BasisNetworkIDDatabase.AddOrFindNetworkID(greedy, "net:greedy:0");
            Assert.Equal(5, BasisNetworkIDDatabase.UshortNetworkDatabase.Count);

            // The count is per session: it clears on disconnect so a rejoin (or a reused id) starts fresh.
            BasisNetworkIDDatabase.RemovePeer(greedy.Id);
            BasisNetworkIDDatabase.AddOrFindNetworkID(greedy, "net:greedy:after-rejoin");
            Assert.True(BasisNetworkIDDatabase.UshortNetworkDatabase.ContainsKey("net:greedy:after-rejoin"));
        }
        finally
        {
            NetworkServer.Configuration = savedConfig;
            BasisNetworkIDDatabase.Reset();
        }
    }
}
