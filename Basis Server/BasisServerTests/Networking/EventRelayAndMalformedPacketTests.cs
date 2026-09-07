using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using Xunit;

namespace BasisServerTests;

// ─────────────────────────────────────────────────────────────────────────────
// EventsChannel: the relay handlers, and what happens when their input is a lie.
//
// Every event on this channel is a client payload the server rewrites and forwards.
// Two properties matter and neither is visible from a happy-path round trip:
//
//   1. The forwarded sender id is the AUTHENTICATED peer id, never a field the
//      sender chose. A relay that echoed a sender-supplied id would let any peer
//      speak as any other, which for temp-block and voice-record consent means
//      forging a decision on someone else's behalf.
//
//   2. A short payload must not be parsed anyway. The reader throws on an over-read
//      and BasisNetworkMessageProcessor catches, counts and eventually disconnects,
//      so a malformed packet costs the SENDER, not the server.
//
// Everything here drives the real router and the real processor; NetPeer is an
// interface, so no socket is involved.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class EventRelayAndMalformedPacketTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 20_000;

    private static FakeNetPeer NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.7.7.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        Identity.Register($"events-user-{Guid.NewGuid():N}", id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        return peer;
    }

    private static void Remove(params FakeNetPeer[] peers)
    {
        foreach (FakeNetPeer peer in peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private static NetPacketReader Packet(params Action<NetDataWriter>[] writes)
    {
        NetDataWriter w = new NetDataWriter();
        foreach (Action<NetDataWriter> write in writes) write(w);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        return NetPacketReader.Create(bytes, 0, bytes.Length, () => { });
    }

    private static NetPacketReader RawPacket(params byte[] bytes)
        => NetPacketReader.Create(bytes, 0, bytes.Length, () => { });

    // ─────────────────────────────────────────────────────────────────────────
    // Temp block: a targeted relay
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TempBlock_ReachesOnlyTheTarget_AndCarriesTheSendersRealId()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer target = NewAuthenticatedPeer();
        FakeNetPeer bystander = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleTempBlock.HandleEvent(
                Packet(w => w.Put((ushort)target.Id), w => w.Put(true)),
                sender,
                BasisNetworkCommons.EventType_PlayerTempBlock);

            Assert.Single(target.Sent);
            Assert.Empty(bystander.Sent);
            Assert.Empty(sender.Sent);

            NetDataReader r = new NetDataReader(target.Sent[0].Data);
            Assert.Equal(BasisNetworkCommons.EventType_PlayerTempBlock, r.GetByte());
            Assert.Equal((ushort)sender.Id, r.GetUShort());
            Assert.True(r.GetBool());
            Assert.Equal(BasisNetworkCommons.EventsChannel, target.Sent[0].Channel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, target.Sent[0].Method);
        }
        finally
        {
            Remove(sender, target, bystander);
        }
    }

    [Fact]
    public void TempBlock_ForAnUnknownTarget_SendsNothing()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer bystander = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleTempBlock.HandleEvent(
                Packet(w => w.Put((ushort)64_000), w => w.Put(true)),
                sender,
                BasisNetworkCommons.EventType_PlayerTempBlock);

            Assert.Empty(bystander.Sent);
            Assert.Empty(sender.Sent);
        }
        finally
        {
            Remove(sender, bystander);
        }
    }

    [Fact]
    public void TempBlock_CannotBeAddressedToTheSenderToForgeATargetsBlock()
    {
        // Targeting yourself is legal on the wire; the point is that the relayed sender
        // id is still the real one, so the receiver cannot be told "someone else blocked you".
        FakeNetPeer sender = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleTempBlock.HandleEvent(
                Packet(w => w.Put((ushort)sender.Id), w => w.Put(false)),
                sender,
                BasisNetworkCommons.EventType_PlayerTempBlock);

            Assert.Single(sender.Sent);
            NetDataReader r = new NetDataReader(sender.Sent[0].Data);
            r.GetByte();
            Assert.Equal((ushort)sender.Id, r.GetUShort());
        }
        finally
        {
            Remove(sender);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Voice record: request and consent, both targeted
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VoiceRecordRequest_RelaysPurposeToTheTargetWithTheRealSenderId()
    {
        FakeNetPeer recorder = NewAuthenticatedPeer();
        FakeNetPeer recordee = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleVoiceRecord.HandleEvent(
                Packet(w => w.Put((ushort)recordee.Id), w => w.Put((byte)3)),
                recorder,
                BasisNetworkCommons.EventType_VoiceRecordRequest);

            Assert.Single(recordee.Sent);
            NetDataReader r = new NetDataReader(recordee.Sent[0].Data);
            Assert.Equal(BasisNetworkCommons.EventType_VoiceRecordRequest, r.GetByte());
            Assert.Equal((ushort)recorder.Id, r.GetUShort());
            Assert.Equal((byte)3, r.GetByte());
            Assert.True(r.EndOfData);
        }
        finally
        {
            Remove(recorder, recordee);
        }
    }

    [Fact]
    public void VoiceRecordConsent_CarriesTheExtraStateByte()
    {
        FakeNetPeer recordee = NewAuthenticatedPeer();
        FakeNetPeer recorder = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleVoiceRecord.HandleEvent(
                Packet(w => w.Put((ushort)recorder.Id), w => w.Put((byte)1), w => w.Put((byte)9)),
                recordee,
                BasisNetworkCommons.EventType_VoiceRecordConsent);

            Assert.Single(recorder.Sent);
            NetDataReader r = new NetDataReader(recorder.Sent[0].Data);
            Assert.Equal(BasisNetworkCommons.EventType_VoiceRecordConsent, r.GetByte());
            Assert.Equal((ushort)recordee.Id, r.GetUShort());
            Assert.Equal((byte)1, r.GetByte());
            Assert.Equal((byte)9, r.GetByte());
            Assert.True(r.EndOfData);
        }
        finally
        {
            Remove(recordee, recorder);
        }
    }

    [Fact]
    public void VoiceRecord_ShortPayload_IsDroppedWithoutSending()
    {
        // This handler checks AvailableBytes itself, so a truncated consent is a silent
        // drop rather than a throw. Both outcomes are safe; the test pins which one it is
        // so the error-count behaviour of the surrounding processor stays predictable.
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer target = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleVoiceRecord.HandleEvent(
                Packet(w => w.Put((ushort)target.Id)),
                sender,
                BasisNetworkCommons.EventType_VoiceRecordRequest);
            Assert.Empty(target.Sent);

            BasisNetworkHandleVoiceRecord.HandleEvent(
                Packet(w => w.Put((ushort)target.Id), w => w.Put((byte)1)),
                sender,
                BasisNetworkCommons.EventType_VoiceRecordConsent);
            Assert.Empty(target.Sent);
        }
        finally
        {
            Remove(sender, target);
        }
    }

    [Fact]
    public void VoiceRecord_ForAnUnknownTarget_SendsNothing()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleVoiceRecord.HandleEvent(
                Packet(w => w.Put((ushort)63_999), w => w.Put((byte)0)),
                sender,
                BasisNetworkCommons.EventType_VoiceRecordRequest);
            Assert.Empty(sender.Sent);
        }
        finally
        {
            Remove(sender);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chat typing: a broadcast, gated by the mute/lock gate and the rate limiter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChatTyping_BroadcastsToEveryoneButTheSender()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer a = NewAuthenticatedPeer();
        FakeNetPeer b = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleChatTyping.HandleEvent(
                Packet(w => w.Put(true)),
                sender,
                BasisNetworkCommons.EventType_PlayerChatTyping);

            Assert.Empty(sender.Sent);
            Assert.Single(a.Sent);
            Assert.Single(b.Sent);

            NetDataReader r = new NetDataReader(a.Sent[0].Data);
            Assert.Equal(BasisNetworkCommons.EventType_PlayerChatTyping, r.GetByte());
            Assert.Equal((ushort)sender.Id, r.GetUShort());
            Assert.True(r.GetBool());
            Assert.Equal(DeliveryMethod.Sequenced, a.Sent[0].Method);
        }
        finally
        {
            Remove(sender, a, b);
        }
    }

    [Fact]
    public void ChatTyping_IsRateLimited_SoOnePeerCannotHoldTheBroadcastPathOpen()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            for (int i = 0; i < 200; i++)
            {
                BasisNetworkHandleChatTyping.HandleEvent(
                    Packet(w => w.Put(i % 2 == 0)),
                    sender,
                    BasisNetworkCommons.EventType_PlayerChatTyping);
            }

            // The shipped typing budget is a burst of 8 refilling at 2/s; 200 packets in
            // a few milliseconds must not become 200 instance-wide broadcasts.
            Assert.InRange(listener.Sent.Count, 1, 20);
        }
        finally
        {
            Remove(sender, listener);
        }
    }

    [Fact]
    public void ChatTyping_FromATextMutedPeer_IsDropped()
    {
        // The typing relay shares the chat gate on purpose: a muted player must not be
        // able to keep flashing "is typing" at the room while their messages are dropped.
        BasisPlayerMuteManager.UseFileOnDisc = false;
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        Assert.True(NetworkServer.AuthIdentity.NetIDToUUID(sender, out string uuid));
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: false, muted: true);
            listener.Sent.Clear();

            BasisNetworkHandleChatTyping.HandleEvent(
                Packet(w => w.Put(true)),
                sender,
                BasisNetworkCommons.EventType_PlayerChatTyping);

            Assert.Empty(listener.Sent);
        }
        finally
        {
            BasisPlayerMuteManager.Apply(uuid, voice: false, muted: false);
            Remove(sender, listener);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Jiggle grab: sender rewriting plus its own limiter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JiggleGrabStart_ForwardsTheSendersRealIdAndTheWholeGrabRecord()
    {
        FakeNetPeer grabber = NewAuthenticatedPeer();
        FakeNetPeer target = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleJiggleGrab.HandleEvent(
                Packet(
                    w => w.Put(BasisNetworkCommons.JiggleGrabOp_Start),
                    w => w.Put((ushort)target.Id),
                    w => w.Put((byte)2),
                    w => w.Put((ushort)17),
                    w => w.Put((byte)1),
                    w => w.Put(0xDEADBEEFu),
                    w => w.Put((ushort)100),
                    w => w.Put((ushort)200),
                    w => w.Put((ushort)300)),
                grabber,
                BasisNetworkCommons.EventType_JiggleGrab);

            Assert.Single(target.Sent);
            NetDataReader r = new NetDataReader(target.Sent[0].Data);
            Assert.Equal(BasisNetworkCommons.EventType_JiggleGrab, r.GetByte());
            Assert.Equal(BasisNetworkCommons.JiggleGrabOp_Start, r.GetByte());
            Assert.Equal((ushort)grabber.Id, r.GetUShort());
            Assert.Equal((ushort)target.Id, r.GetUShort());
            Assert.Equal((byte)2, r.GetByte());
            Assert.Equal((ushort)17, r.GetUShort());
            Assert.Equal((byte)1, r.GetByte());
            Assert.Equal(0xDEADBEEFu, r.GetUInt());
            Assert.Equal((ushort)100, r.GetUShort());
            Assert.Equal((ushort)200, r.GetUShort());
            Assert.Equal((ushort)300, r.GetUShort());
            Assert.True(r.EndOfData);
        }
        finally
        {
            Remove(grabber, target);
        }
    }

    [Fact]
    public void JiggleGrabStop_BroadcastsSoStateCannotLeak()
    {
        FakeNetPeer grabber = NewAuthenticatedPeer();
        FakeNetPeer target = NewAuthenticatedPeer();
        FakeNetPeer bystander = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleJiggleGrab.HandleEvent(
                Packet(
                    w => w.Put(BasisNetworkCommons.JiggleGrabOp_Stop),
                    w => w.Put((ushort)target.Id),
                    w => w.Put((byte)0),
                    w => w.Put((ushort)5)),
                grabber,
                BasisNetworkCommons.EventType_JiggleGrab);

            Assert.Single(target.Sent);
            Assert.Single(bystander.Sent);
            Assert.Empty(grabber.Sent);
        }
        finally
        {
            Remove(grabber, target, bystander);
        }
    }

    [Fact]
    public void JiggleGrabDeny_CarriesTheDenyingPlayersRealId()
    {
        FakeNetPeer denier = NewAuthenticatedPeer();
        FakeNetPeer grabber = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleJiggleGrab.HandleEvent(
                Packet(w => w.Put(BasisNetworkCommons.JiggleGrabOp_Deny), w => w.Put((ushort)grabber.Id)),
                denier,
                BasisNetworkCommons.EventType_JiggleGrab);

            Assert.Single(grabber.Sent);
            NetDataReader r = new NetDataReader(grabber.Sent[0].Data);
            r.GetByte();
            Assert.Equal(BasisNetworkCommons.JiggleGrabOp_Deny, r.GetByte());
            Assert.Equal((ushort)denier.Id, r.GetUShort());
            Assert.Equal((ushort)grabber.Id, r.GetUShort());
        }
        finally
        {
            Remove(denier, grabber);
        }
    }

    [Fact]
    public void JiggleGrab_UnknownOp_IsIgnored()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleJiggleGrab.HandleEvent(
                Packet(w => w.Put((byte)0xFE), w => w.Put((ushort)1)),
                sender,
                BasisNetworkCommons.EventType_JiggleGrab);

            Assert.Empty(listener.Sent);
        }
        finally
        {
            Remove(sender, listener);
        }
    }

    [Fact]
    public void JiggleGrab_IsRateLimited()
    {
        FakeNetPeer grabber = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            for (int i = 0; i < 300; i++)
            {
                BasisNetworkHandleJiggleGrab.HandleEvent(
                    Packet(
                        w => w.Put(BasisNetworkCommons.JiggleGrabOp_Deny),
                        w => w.Put((ushort)listener.Id)),
                    grabber,
                    BasisNetworkCommons.EventType_JiggleGrab);
            }

            // Burst 16, refilling at 8/s: a tight loop must not fan out 300 times.
            Assert.InRange(listener.Sent.Count, 1, 40);
        }
        finally
        {
            Remove(grabber, listener);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Malformed input through the real router
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BasisNetworkCommons.EventType_PlayerTempBlock)]
    [InlineData(BasisNetworkCommons.EventType_AvatarRateChange)]
    [InlineData(BasisNetworkCommons.EventType_TalkModeChanged)]
    [InlineData(BasisNetworkCommons.EventType_MuteStateChanged)]
    [InlineData(BasisNetworkCommons.EventType_PlayerChatTyping)]
    [InlineData(BasisNetworkCommons.EventType_JiggleGrab)]
    public void EventWithNoBody_IsRejectedRatherThanReadingThePooledBuffer(byte eventType)
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            // A pooled buffer holding a previous packet's bytes, with only the event-type
            // byte declared as this packet's payload.
            byte[] pooled = new byte[64];
            pooled[0] = eventType;
            for (int i = 1; i < pooled.Length; i++) pooled[i] = 0xAB;
            NetPacketReader reader = NetPacketReader.Create(pooled, 0, 1, () => { });

            Assert.ThrowsAny<Exception>(() => BasisServerEventsRouter.HandleEvent(reader, sender));
            Assert.Empty(listener.Sent);
        }
        finally
        {
            Remove(sender, listener);
        }
    }

    [Fact]
    public void UnknownEventType_IsRecycledAndIgnored()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisServerEventsRouter.HandleEvent(RawPacket(0xFF, 1, 2, 3), sender);
            Assert.Empty(listener.Sent);
        }
        finally
        {
            Remove(sender, listener);
        }
    }

    [Fact]
    public void EmptyEventPacket_IsRejected()
    {
        FakeNetPeer sender = NewAuthenticatedPeer();
        try
        {
            Assert.ThrowsAny<Exception>(() => BasisServerEventsRouter.HandleEvent(RawPacket(), sender));
        }
        finally
        {
            Remove(sender);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Malformed input through the processor that wraps the router
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MalformedPacket_DoesNotEscapeProcessMessage()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        FakeNetPeer sender = NewAuthenticatedPeer();
        FakeNetPeer listener = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            byte[] pooled = new byte[32];
            pooled[0] = BasisNetworkCommons.EventType_PlayerTempBlock;
            NetPacketReader reader = NetPacketReader.Create(pooled, 0, 1, () => { });

            BasisNetworkMessageProcessor.ProcessMessage(sender, reader, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);

            Assert.Equal(0, sender.DisconnectCalls);
            Assert.Empty(listener.Sent);
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            Remove(sender, listener);
        }
    }

    [Fact]
    public void SustainedMalformedTraffic_EventuallyDisconnectsTheSender()
    {
        // The escalation is what turns "the server survives one bad packet" into
        // "the server is not a free CPU sink for a peer sending nothing but bad packets".
        BasisServerMessageRegistry.EnsureInitialized();
        FakeNetPeer sender = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            byte[] pooled = new byte[32];
            pooled[0] = BasisNetworkCommons.EventType_PlayerTempBlock;

            for (int i = 0; i < 499; i++)
            {
                BasisNetworkMessageProcessor.ProcessMessage(
                    sender, NetPacketReader.Create(pooled, 0, 1, () => { }),
                    BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            }
            Assert.Equal(0, sender.DisconnectCalls);

            BasisNetworkMessageProcessor.ProcessMessage(
                sender, NetPacketReader.Create(pooled, 0, 1, () => { }),
                BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            Assert.Equal(1, sender.DisconnectCalls);
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            Remove(sender);
        }
    }

    [Fact]
    public void UnauthenticatedPeer_CannotReachAnyHandler()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        FakeNetPeer listener = NewAuthenticatedPeer();
        FakeNetPeer stranger = new FakeNetPeer(Interlocked.Increment(ref peerIdCounter), "10.7.7.8");
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(stranger.Id);
            BasisNetworkMessageProcessor.ProcessMessage(
                stranger,
                Packet(w => w.Put(BasisNetworkCommons.EventType_PlayerChatTyping), w => w.Put(true)),
                BasisNetworkCommons.EventsChannel,
                DeliveryMethod.ReliableOrdered);

            Assert.Empty(listener.Sent);
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(stranger.Id);
            Remove(listener);
        }
    }

    [Fact]
    public void UnknownChannel_IsCountedNotDispatched()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        FakeNetPeer sender = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            for (int i = 0; i < 500; i++)
            {
                BasisNetworkMessageProcessor.ProcessMessage(
                    sender, RawPacket(1, 2, 3), BasisNetworkCommons.TotalChannels - 1, DeliveryMethod.ReliableOrdered);
            }
            Assert.Equal(1, sender.DisconnectCalls);
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            Remove(sender);
        }
    }

    [Fact]
    public void ClearPeerErrors_ResetsTheEscalationCounter()
    {
        // Called on disconnect. Without it a recycled peer id would inherit the previous
        // occupant's error count and be dropped early.
        BasisServerMessageRegistry.EnsureInitialized();
        FakeNetPeer sender = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            byte[] pooled = new byte[32];
            pooled[0] = BasisNetworkCommons.EventType_PlayerTempBlock;
            for (int i = 0; i < 499; i++)
            {
                BasisNetworkMessageProcessor.ProcessMessage(
                    sender, NetPacketReader.Create(pooled, 0, 1, () => { }),
                    BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            }
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);

            for (int i = 0; i < 499; i++)
            {
                BasisNetworkMessageProcessor.ProcessMessage(
                    sender, NetPacketReader.Create(pooled, 0, 1, () => { }),
                    BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            }
            Assert.Equal(0, sender.DisconnectCalls);
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            Remove(sender);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Recycling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecyclingTwice_ReturnsThePacketOnce()
    {
        // The handler recycles on its own path and the processor's catch recycles again.
        // Returning the same pooled buffer twice hands it to two owners at once, which is
        // how one peer's bytes end up inside another peer's packet.
        int returns = 0;
        NetPacketReader reader = NetPacketReader.Create(new byte[] { 1, 2, 3, 4 }, 0, 4, () => returns++);

        reader.Recycle(true);
        reader.Recycle(true);
        reader.Recycle(true);

        Assert.Equal(1, returns);
    }

    [Fact]
    public void MalformedPacketThatAlreadyRecycled_IsStillReturnedOnlyOnce()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        FakeNetPeer sender = NewAuthenticatedPeer();
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            int returns = 0;
            byte[] pooled = new byte[32];
            pooled[0] = BasisNetworkCommons.EventType_PlayerTempBlock;
            NetPacketReader reader = NetPacketReader.Create(pooled, 0, 1, () => returns++);

            BasisNetworkMessageProcessor.ProcessMessage(sender, reader, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);

            Assert.Equal(1, returns);
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(sender.Id);
            Remove(sender);
        }
    }
}
