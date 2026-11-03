using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Xunit;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public sealed class AvatarHostRelayWireTests
{
    private static ushort PluginId
    {
        get
        {
            BasisServerMessageRegistry.EnsureInitialized();
            Assert.True(BasisServerMessageRegistry.TryGetPluginId(BasisAvatarHostProtocol.MessageName, out ushort id));
            return id;
        }
    }
    private static void Dispatch(NetPeer from, System.Action<NetDataWriter> write)
    {
        NetDataWriter writer = new NetDataWriter();
        writer.Put(PluginId);
        write(writer);
        byte[] payload = writer.CopyData();
        Assert.True(BasisServerMessageRegistry.DispatchPlugin(from, NetPacketReader.Create(payload, 0, payload.Length, static () => { }), BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered));
    }
    private static List<NetDataReader> Replies(FakeNetPeer peer, byte op)
    {
        List<NetDataReader> replies = new();
        lock (peer.Sent)
        {
            foreach ((byte[] data, byte channel, DeliveryMethod _) in peer.Sent)
            {
                if (channel != BasisNetworkCommons.PluginReliableChannel) continue;
                NetDataReader reader = new NetDataReader(data);
                if (reader.GetUShort() != PluginId || reader.GetByte() != op) continue;
                replies.Add(reader);
            }
        }
        return replies;
    }
    private static byte[] Assemble(FakeNetPeer peer, uint requestId)
    {
        List<byte> bytes = new();
        foreach (NetDataReader reader in Replies(peer, BasisAvatarHostProtocol.OpChunk))
        {
            if (reader.GetUInt() != requestId) continue;
            int offset = reader.GetInt(), count = reader.GetInt();
            Assert.Equal(bytes.Count, offset);
            byte[] chunk = new byte[count];
            reader.GetBytes(chunk, 0, count);
            bytes.AddRange(chunk);
        }
        return bytes.ToArray();
    }
    [Fact]
    public void OfferRequestServeAndCacheRoundTripOverTheWire()
    {
        using ServerStaticsScope scope = new();
        NetworkServer.Configuration = new Configuration { AvatarHostMaxMegabytes = 8, AvatarHostCacheMegabytes = 16 };
        FakeNetPeer owner = LifecycleSupport.Peer(LifecycleSupport.NextPeerId()), alice = LifecycleSupport.Peer(LifecycleSupport.NextPeerId()), bob = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        foreach (FakeNetPeer peer in new[] { owner, alice, bob }) NetworkServer.AuthenticatedPeers[peer.Id] = peer;
        byte[] id = Enumerable.Range(40, BasisAvatarHostProtocol.IdBytes).Select(i => (byte)i).ToArray();
        byte[] section = Enumerable.Range(0, 40_000).Select(i => (byte)(i * 31)).ToArray();
        const long start = 1234;
        try
        {
            Dispatch(owner, w => { w.Put(BasisAvatarHostProtocol.OpOffer); BasisAvatarHostProtocol.WriteId(w, id); w.Put(100_000L); });
            NetDataReader offer = Assert.Single(Replies(owner, BasisAvatarHostProtocol.OpOfferResult));
            Assert.True(BasisAvatarHostProtocol.TryReadId(offer, out byte[] echoed));
            Assert.Equal(id, echoed);
            Assert.True(offer.GetBool());
            Dispatch(alice, w => { w.Put(BasisAvatarHostProtocol.OpRequest); w.Put(7u); BasisAvatarHostProtocol.WriteId(w, id); w.Put(start); w.Put(section.Length); });
            NetDataReader fetch = Assert.Single(Replies(owner, BasisAvatarHostProtocol.OpFetch));
            uint fetchId = fetch.GetUInt();
            Assert.True(BasisAvatarHostProtocol.TryReadId(fetch, out _));
            Assert.Equal(start, fetch.GetLong());
            Assert.Equal(section.Length, fetch.GetInt());
            for (int offset = 0; offset < section.Length; offset += BasisAvatarHostProtocol.ChunkBytes)
            {
                int count = System.Math.Min(BasisAvatarHostProtocol.ChunkBytes, section.Length - offset), at = offset;
                Dispatch(owner, w => { w.Put(BasisAvatarHostProtocol.OpOwnerChunk); w.Put(fetchId); w.Put(at); w.Put(count); w.Put(section, at, count); });
            }
            Assert.Equal(section, Assemble(alice, 7));
            Dispatch(bob, w => { w.Put(BasisAvatarHostProtocol.OpRequest); w.Put(9u); BasisAvatarHostProtocol.WriteId(w, id); w.Put(start); w.Put(section.Length); });
            Assert.Single(Replies(owner, BasisAvatarHostProtocol.OpFetch));
            Assert.Equal(section, Assemble(bob, 9));
            Assert.Equal(0, BasisAvatarHostRelay.QueuedPieces);
            BasisAvatarHostRelay.RemovePeer(owner.Id);
            Dispatch(bob, w => { w.Put(BasisAvatarHostProtocol.OpRequest); w.Put(10u); BasisAvatarHostProtocol.WriteId(w, id); w.Put(start); w.Put(section.Length); });
            NetDataReader failed = Assert.Single(Replies(bob, BasisAvatarHostProtocol.OpFailed));
            Assert.Equal(10u, failed.GetUInt());
            Assert.Equal(BasisAvatarHostProtocol.ReasonUnknownId, failed.GetByte());
        }
        finally
        {
            foreach (FakeNetPeer peer in new[] { owner, alice, bob }) BasisAvatarHostRelay.RemovePeer(peer.Id);
        }
    }
    [Fact]
    public void LockWithdrawsAndRefusesOffersFromPlayersWithoutBypass()
    {
        using ServerStaticsScope scope = new();
        NetworkServer.Configuration = new Configuration { AvatarHostMaxMegabytes = 8, AvatarHostCacheMegabytes = 16 };
        FakeNetPeer owner = LifecycleSupport.Peer(LifecycleSupport.NextPeerId()), viewer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        foreach (FakeNetPeer peer in new[] { owner, viewer }) NetworkServer.AuthenticatedPeers[peer.Id] = peer;
        byte[] id = Enumerable.Repeat((byte)9, BasisAvatarHostProtocol.IdBytes).ToArray(), second = Enumerable.Repeat((byte)10, BasisAvatarHostProtocol.IdBytes).ToArray();
        Configuration saved = new Configuration();
        BasisNetworkServer.Security.BasisGlobalLockManager.WriteToConfig(saved);
        void SetLocked(bool locked)
        {
            Configuration state = new Configuration();
            BasisNetworkServer.Security.BasisGlobalLockManager.WriteToConfig(state);
            state.AvatarHostingLocked = locked;
            BasisNetworkServer.Security.BasisGlobalLockManager.InitializeFromConfig(state);
        }
        try
        {
            Dispatch(owner, w => { w.Put(BasisAvatarHostProtocol.OpOffer); BasisAvatarHostProtocol.WriteId(w, id); w.Put(1000L); });
            Assert.True(BasisAvatarHostRelay.Ledger.IsOffered(id));
            SetLocked(true);
            BasisAvatarHostRelay.ApplyConfiguration();
            Assert.False(BasisAvatarHostRelay.Ledger.IsOffered(id));
            Dispatch(viewer, w => { w.Put(BasisAvatarHostProtocol.OpRequest); w.Put(3u); BasisAvatarHostProtocol.WriteId(w, id); w.Put(0L); w.Put(8); });
            NetDataReader failed = Assert.Single(Replies(viewer, BasisAvatarHostProtocol.OpFailed));
            Assert.Equal(3u, failed.GetUInt());
            Assert.Equal(BasisAvatarHostProtocol.ReasonUnknownId, failed.GetByte());
            Dispatch(owner, w => { w.Put(BasisAvatarHostProtocol.OpOffer); BasisAvatarHostProtocol.WriteId(w, second); w.Put(1000L); });
            NetDataReader refused = Replies(owner, BasisAvatarHostProtocol.OpOfferResult).Last();
            Assert.True(BasisAvatarHostProtocol.TryReadId(refused, out byte[] echoed));
            Assert.Equal(second, echoed);
            Assert.False(refused.GetBool());
            Assert.Equal(BasisAvatarHostProtocol.ReasonDisabled, refused.GetByte());
            SetLocked(false);
            Dispatch(owner, w => { w.Put(BasisAvatarHostProtocol.OpOffer); BasisAvatarHostProtocol.WriteId(w, second); w.Put(1000L); });
            Assert.True(BasisAvatarHostRelay.Ledger.IsOffered(second));
        }
        finally
        {
            BasisNetworkServer.Security.BasisGlobalLockManager.InitializeFromConfig(saved);
            foreach (FakeNetPeer peer in new[] { owner, viewer }) BasisAvatarHostRelay.RemovePeer(peer.Id);
        }
    }
    [Fact]
    public void ZeroSizeLimitRefusesOffers()
    {
        using ServerStaticsScope scope = new();
        NetworkServer.Configuration = new Configuration { AvatarHostMaxMegabytes = 0 };
        FakeNetPeer owner = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        NetworkServer.AuthenticatedPeers[owner.Id] = owner;
        byte[] id = Enumerable.Repeat((byte)5, BasisAvatarHostProtocol.IdBytes).ToArray();
        try
        {
            Dispatch(owner, w => { w.Put(BasisAvatarHostProtocol.OpOffer); BasisAvatarHostProtocol.WriteId(w, id); w.Put(1000L); });
            NetDataReader offer = Assert.Single(Replies(owner, BasisAvatarHostProtocol.OpOfferResult));
            Assert.True(BasisAvatarHostProtocol.TryReadId(offer, out _));
            Assert.False(offer.GetBool());
            Assert.Equal(BasisAvatarHostProtocol.ReasonDisabled, offer.GetByte());
        }
        finally
        {
            BasisAvatarHostRelay.RemovePeer(owner.Id);
        }
    }
    [Fact]
    public void PluginIsAdvertisedInTheSupplyManifest()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        Assert.Contains(BasisServerMessageRegistry.BuildSupply(), d => d.Name == BasisAvatarHostProtocol.MessageName && d.Channel == BasisNetworkCommons.PluginReliableChannel);
    }
}
