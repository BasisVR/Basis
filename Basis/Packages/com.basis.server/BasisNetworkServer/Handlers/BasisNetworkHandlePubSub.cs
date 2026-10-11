using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BasisNetworkServer
{
    public interface IPubSubDataProvider
    {
        /// <summary>
        /// Generates an initial state message for a new subscriber.
        /// If it returns an empty list, no initial state will be sent.
        /// </summary>
        List<byte[]> GetInitialState();
    }

    /// <summary>
    /// Provides a PubSub service typically for use by props, so that they may arbitrary live information from modified servers.
    /// </summary>
    public static class BasisNetworkHandlePubSub
    {
        private sealed class ChannelState
        {
            public readonly string Name;
            public readonly IPubSubDataProvider Provider;
            
            public readonly ConcurrentDictionary<int, HashSet<Guid>> PeerToSubscriptionsDict = new();
            public readonly object Lock = new object();

            public ChannelState(string name, IPubSubDataProvider provider)
            {
                Name = name;
                Provider = provider;
            }
        }

        private static readonly ConcurrentDictionary<string, ChannelState> Channels = new();
        
        public static void HandleEvent(NetPeer peer, NetPacketReader reader)
        {
            if (!reader.TryGetByte(out byte sub)) { reader.Recycle(); return; }

            if (sub == BasisNetworkCommons.PubSub_Subscribe)
            {
                var req = new SerializableBasis.PubSubSubscribeRequest();
                if (req.Deserialize(reader))
                    HandleSubscribeRequest(peer, req);
            }
            else if (sub == BasisNetworkCommons.PubSub_Unsubscribe)
            {
                var req = new SerializableBasis.PubSubUnsubscribeRequest();
                if (req.Deserialize(reader))
                    HandleUnsubscribeRequest(peer, req);
            }
            reader.Recycle();
        }

        public static void RegisterChannel(string name, IPubSubDataProvider provider)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Channel name cannot be empty", nameof(name));
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            if (!Channels.TryAdd(name, new ChannelState(name, provider)))
            {
                throw new InvalidOperationException($"Channel '{name}' is already registered.");
            }
        }

        public static bool UnregisterChannel(string name)
        {
            return Channels.TryRemove(name, out _);
        }

        public static void HandleSubscribeRequest(NetPeer peer, SerializableBasis.PubSubSubscribeRequest request)
        {
            if (!Channels.TryGetValue(request.ChannelName, out var channel))
            {
                BNL.LogWarning($"Peer {peer.Id} tried to subscribe to non-existent channel: {request.ChannelName}");
                return;
            }

            List<byte[]> initialStateMessages = null;
            lock (channel.Lock)
            {
                var peerToSubscription = channel.PeerToSubscriptionsDict.GetOrAdd(peer.Id, _ => new HashSet<Guid>());
                peerToSubscription.Add(request.RequestID);
                initialStateMessages = channel.Provider.GetInitialState();
            }

            foreach (byte[] initialState in initialStateMessages)
            {
                var initial = new SerializableBasis.PubSubInitialState
                {
                    ChannelName = request.ChannelName,
                    Data = initialState,
                    RequestID = request.RequestID
                };
                SendMessageToSpecificPeer(peer, BasisNetworkCommons.PubSub_Initial, initial);
            }
        }

        public static void HandleUnsubscribeRequest(NetPeer peer, SerializableBasis.PubSubUnsubscribeRequest request)
        {
            if (!Channels.TryGetValue(request.ChannelName, out var channel))
            {
                return;
            }

            lock (channel.Lock)
            {
                if (channel.PeerToSubscriptionsDict.TryGetValue(peer.Id, out var peerToSubscription))
                {
                    peerToSubscription.Remove(request.RequestID);
                    if (peerToSubscription.Count == 0)
                    {
                        channel.PeerToSubscriptionsDict.TryRemove(peer.Id, out _);
                    }
                }
            }
        }

        public static void Publish(string channelName, byte[] data)
        {
            if (!Channels.TryGetValue(channelName, out var channel))
            {
                return;
            }

            int[] targets;
            lock (channel.Lock)
            {
                targets = channel.PeerToSubscriptionsDict.Keys.ToArray();
            }

            if (targets.Length == 0) return;

            var update = new SerializableBasis.PubSubMessage
            {
                ChannelName = channelName,
                Data = data
            };

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(BasisNetworkCommons.PubSub_Message);
            update.Serialize(writer);

            foreach (var peerId in targets)
            {
                if (NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out var peer))
                {
                    peer.Send(writer, BasisNetworkCommons.PubSubChannel, DeliveryMethod.ReliableOrdered);
                }
            }

            NetworkServer.ReturnWriter(writer);
        }

        public static void RemovePlayerSubscriptions(int peerId)
        {
            foreach (var channel in Channels.Values)
            {
                lock (channel.Lock)
                {
                    channel.PeerToSubscriptionsDict.TryRemove(peerId, out _);
                }
            }
        }

        private static void SendMessageToSpecificPeer(NetPeer peer, byte subType, SerializableBasis.PubSubInitialState message)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(subType);
            
            message.Serialize(writer);
            
            peer.Send(writer, BasisNetworkCommons.PubSubChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
