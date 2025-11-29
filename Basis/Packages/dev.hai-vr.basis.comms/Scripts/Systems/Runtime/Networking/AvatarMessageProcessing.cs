using System;
using Basis.Scripts.Behaviour;
using HVR.Basis.Comms.HVRUtility;
using Basis.Network.Core;
using UnityEngine;

namespace HVR.Basis.Comms
{
    // Enforces the following:
    // - Only the wearer can send a ready signal to the remotes.
    // - Only remotes can send an initialization request to the wearer.
    // - Only the wearer can send data to remotes.
    // - Remotes cannot send messages to each other.
    public class AvatarMessageProcessing
    {
        public const byte NewNet_WearerData = 0;
        public const byte NewNet_WearerReady = 1;
        public const byte NewNet_RemoteRequestsInitialization = 2;

        private readonly IHVRTransmitter _transmitter;
        private readonly bool _isWearer;
        private readonly ushort _wearerNetId;
        private readonly ResyncEveryoneRequestedDelegate _onResyncEveryoneRequested;
        private readonly ResyncRequestedDelegate _onResyncRequested;
        private readonly PacketReceivedDelegate _onPacketReceived;

        public delegate void ResyncEveryoneRequestedDelegate();
        public delegate void ResyncRequestedDelegate(ushort remoteUser);
        public delegate void PacketReceivedDelegate(byte localIdentifier, ArraySegment<byte> subBuffer);

        public static AvatarMessageProcessing ForFeature(IHVRTransmitter transmitter, bool isWearer, ushort wearerNetId, IFeatureReceiver receiver)
        {
            return new AvatarMessageProcessing(transmitter, isWearer, wearerNetId, receiver.OnResyncEveryoneRequested, remoteUser => receiver.OnResyncRequested(new[] { remoteUser }), receiver.OnPacketReceived);
        }

        public AvatarMessageProcessing(IHVRTransmitter transmitter, bool isWearer, ushort wearerNetId, ResyncEveryoneRequestedDelegate onResyncEveryoneRequested, ResyncRequestedDelegate onResyncRequested, PacketReceivedDelegate onPacketReceived)
        {
            _transmitter = transmitter;
            _isWearer = isWearer;
            _wearerNetId = wearerNetId;
            _onResyncEveryoneRequested = onResyncEveryoneRequested;
            _onResyncRequested = onResyncRequested;
            _onPacketReceived = onPacketReceived;
        }

        public void OnNetworkMessageReceived(ushort remoteUser, byte[] buffer, DeliveryMethod _)
        {
            if (buffer.Length == 0) { HVRLogging.ProtocolError("Buffer was 0 bytes."); return; }
            if (!_isWearer && remoteUser != _wearerNetId) { HVRLogging.ProtocolError("Illegal sender."); return; }

            var packetId = buffer[0];
            switch (packetId)
            {
                case NewNet_WearerReady:
                {
                    if (_isWearer) { HVRLogging.ProtocolError("Illegal recipient."); return; }
                    if (remoteUser != _wearerNetId) { HVRLogging.ProtocolError("Illegal sender."); return; }
                    if (buffer.Length != 1) { HVRLogging.ProtocolError("Illegal buffer length."); return; }
                    // Do nothing
                    break;
                }
                case NewNet_RemoteRequestsInitialization:
                {
                    if (!_isWearer) { HVRLogging.ProtocolError("Illegal recipient."); return; }
                    if (remoteUser == _wearerNetId) { HVRLogging.ProtocolError("Illegal sender."); return; }
                    if (buffer.Length != 1) { HVRLogging.ProtocolError("Illegal buffer length."); return; }
                    _onResyncRequested.Invoke(remoteUser);
                    break;
                }
                case NewNet_WearerData:
                {
                    // This can be received without the server reduction system after we requested initialization.
                    if (_isWearer) { HVRLogging.ProtocolError("Illegal recipient."); return; }
                    if (remoteUser != _wearerNetId) { HVRLogging.ProtocolError("Illegal sender."); return; }
                    if (buffer.Length < 2) { HVRLogging.ProtocolError("Illegal buffer length."); return; }
                    var localIdentifier = buffer[1];
                    _onPacketReceived.Invoke(localIdentifier, SubBuffer(buffer));
                    break;
                }
                default:
                {
                    HVRLogging.ProtocolError("Illegal message.");
                    break;
                }
            }
        }

        public void SendInitialPacket()
        {
            if (_isWearer)
            {
                _transmitter.NetworkMessageSend(new[] { NewNet_WearerReady }, DeliveryMethod.ReliableSequenced);
                _onResyncEveryoneRequested.Invoke();
            }
            else
            {
                _transmitter.NetworkMessageSend(new[] { NewNet_RemoteRequestsInitialization }, DeliveryMethod.ReliableSequenced, new[] { _wearerNetId });
            }
        }

        public void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            if (buffer.Length == 0) { HVRLogging.ProtocolError("Buffer was 0 bytes."); return; }

            var packetId = buffer[0];
            switch (packetId)
            {
                case NewNet_WearerReady:
                {
                    HVRLogging.ProtocolError("Illegal message must not be received by reduction system (NewNet_WearerReady).");
                    break;
                }
                case NewNet_RemoteRequestsInitialization:
                {
                    HVRLogging.ProtocolError("Illegal message must not be received by reduction system (NewNet_RemoteRequestsInitialization).");
                    break;
                }
                case NewNet_WearerData:
                {
                    if (_isWearer) { HVRLogging.ProtocolError("Illegal recipient."); return; }
                    if (buffer.Length < 2) { HVRLogging.ProtocolError("Illegal buffer length."); return; }
                    var localIdentifier = buffer[1];
                    _onPacketReceived.Invoke(localIdentifier, SubBuffer(buffer));
                    break;
                }
                default:
                {
                    HVRLogging.ProtocolError("Illegal message.");
                    break;
                }
            }
        }

        internal static ArraySegment<byte> SubBuffer(byte[] unsafeBuffer)
        {
            return new ArraySegment<byte>(unsafeBuffer, 2, unsafeBuffer.Length - 2);
        }
    }
}
