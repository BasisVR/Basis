using System;
using System.Collections.Generic;
using Basis.Network.Core;
using Basis.Scripts.Networking;

namespace Basis.Shims
{
    /// <summary>
    /// Provides access to the custom data CustomServerData channel of the currently connected server.<br/>
    /// <br/>
    /// The custom server data channels are meant for the server to provide custom live data to props
    /// or other content that requests it, such as a list of players who recently joined the server,
    /// weather forecasts, integration with Discord, etc.<br/>
    /// <br/>
    /// The available capabilities are entirely dependent on the server.<br/>
    /// <br/>
    /// You should call UnsubscribeAll() in your OnDestroy() method.
    /// <br/>
    /// The custom server data channels are NOT designed for props or other content to communicate with each other:
    /// the clients cannot publish messages to the channels; only the server can send messages to the clients.
    /// </summary>
    public class BasisCustomServerDataSubscriberShim
    {
        private readonly Dictionary<string, Guid> _subscriptions = new();
        private bool _isHooked;

        public delegate void InitialStateReceivedDelegate(string channelName, byte[] data);
        public delegate void MessageReceivedDelegate(string channelName, byte[] data);

        public event InitialStateReceivedDelegate InitialStateReceived;
        public event MessageReceivedDelegate MessageReceived;
        
        ~BasisCustomServerDataSubscriberShim()
        {
            BasisNetworkHandleCustomServerData.OnCustomServerDataMessageReceived -= OnCustomServerDataMessageReceived;
        }
        
        private void OnCustomServerDataMessageReceived(byte[] buffer, DeliveryMethod deliveryMethod)
        {
            try
            {
                if (buffer == null || buffer.Length == 0) return;

                var reader = new NetDataReader(buffer);
                if (!reader.TryGetByte(out byte subType)) return;

                if (subType == BasisNetworkCommons.CustomServerData_Message)
                {
                    var msg = new SerializableBasis.CustomServerDataMessage();
                    if (msg.Deserialize(reader))
                    {
                        if (_subscriptions.ContainsKey(msg.ChannelName))
                        {
                            MessageReceived?.Invoke(msg.ChannelName, msg.Data);
                        }
                    }
                }
                else if (subType == BasisNetworkCommons.CustomServerData_InitialState)
                {
                    var initialState = new SerializableBasis.CustomServerDataInitialState();
                    if (initialState.Deserialize(reader))
                    {
                        if (_subscriptions.ContainsKey(initialState.ChannelName))
                        {
                            InitialStateReceived?.Invoke(initialState.ChannelName, initialState.Data);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisCustomServerDataShim] Error while processing CustomServerData message, this exception will not be re-thrown: {e}");
                // Do not throw the exception, as we want to avoid Cilbox props disrupting the network message processing.
            }
        }

        /// <summary>
        /// Subscribes to a CustomServerData channel.<br/>
        /// <br/>
        /// Channels can only be subscribed to once per instance of the shim.<br/>
        /// When subscribing, the InitialStateReceived will trigger if that channel provides an initial state.<br/>
        /// Multiple props can subscribe to the same channel:<br/>
        /// - Each prop may receive a different initial state, depending on when that prop subscribes.<br/>
        /// - Non-initial state messages are sent from the server to the user once, and then dispatched to all the shims that require it.
        /// </summary>
        /// <param name="channelName">Name of the CustomServerData channel</param>
        public void Subscribe(string channelName)
        {
            if (string.IsNullOrEmpty(channelName)) return;
            if (_subscriptions.TryGetValue(channelName, out _)) return;

            if (!_isHooked)
            {
                _isHooked = true;
                BasisNetworkHandleCustomServerData.OnCustomServerDataMessageReceived += OnCustomServerDataMessageReceived; 
            }

            Guid requestID = Guid.NewGuid();
            _subscriptions[channelName] = requestID;

            var request = new SerializableBasis.CustomServerDataSubscribeRequest
            {
                ChannelName = channelName,
                RequestID = requestID
            };

            NetDataWriter writer = new NetDataWriter();
            writer.Put(BasisNetworkCommons.CustomServerData_Subscribe);
            request.Serialize(writer);

            BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.CustomServerDataChannel, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Unsubscribes from a CustomServerData channel.
        /// </summary>
        /// <param name="channelName"></param>
        public void Unsubscribe(string channelName)
        {
            if (string.IsNullOrEmpty(channelName)) return;
            if (!_subscriptions.TryGetValue(channelName, out Guid requestID)) return;

            var request = new SerializableBasis.CustomServerDataUnsubscribeRequest
            {
                ChannelName = channelName,
                RequestID = requestID
            };

            NetDataWriter writer = new NetDataWriter();
            writer.Put(BasisNetworkCommons.CustomServerData_Unsubscribe);
            request.Serialize(writer);

            BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.CustomServerDataChannel, DeliveryMethod.ReliableOrdered);
            _subscriptions.Remove(channelName);
            
            if (_subscriptions.Count == 0)
            {
                BasisNetworkHandleCustomServerData.OnCustomServerDataMessageReceived -= OnCustomServerDataMessageReceived;
                _isHooked = false;
            }
        }

        /// <summary>
        /// Unsubscribes from all CustomServerData channels.
        /// </summary>
        public void UnsubscribeAll()
        {
            List<string> channels = new List<string>(_subscriptions.Keys);
            foreach (var channel in channels)
            {
                Unsubscribe(channel);
            }

            BasisNetworkHandleCustomServerData.OnCustomServerDataMessageReceived -= OnCustomServerDataMessageReceived;
            _isHooked = false;
        }
    }
}
