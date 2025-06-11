using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/Avatar Comms")]
    public class HVRAvatarComms : BasisAvatarMonoBehaviour
    {
        // Use Sequenced, because we know this delivery type works
        private const DeliveryMethod MainMessageDeliveryMethod = DeliveryMethod.Sequenced;
        private const DeliveryMethod NegotiationDelivery = DeliveryMethod.ReliableOrdered;

        private const int BytesPerGuid = 16;

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private FeatureNetworking featureNetworking;

        private bool _isWearer;
        private ushort _wearerNetId;
        private ushort[] _wearerNetIdRecipient;
        private Guid[] _negotiatedGuids;
        private Dictionary<int, int> _fromTheirsToOurs;
        private Stopwatch _debugRetrySendNegotiationOptional;
        private bool _alreadyInitialized;

        private void Awake()
        {
            if (avatar == null) avatar = CommsUtil.GetAvatar(this);
            if (featureNetworking == null) featureNetworking = CommsUtil.FeatureNetworkingFromAvatar(avatar);
            if (avatar == null || featureNetworking == null)
            {
                throw new InvalidOperationException("Broke assumption: Avatar and/or FeatureNetworking cannot be found.");
            }

            avatar.OnAvatarNetworkReady -= OnAvatarNetworkReady;
            avatar.OnAvatarNetworkReady += OnAvatarNetworkReady;
        }

        private void OnDestroy()
        {
            avatar.OnAvatarNetworkReady -= OnAvatarNetworkReady;
        }

        private void OnAvatarNetworkReady(bool IsOwner)
        {
            if (_alreadyInitialized) return;
            _alreadyInitialized = true;

            _isWearer = avatar.IsOwnedLocally;
            _wearerNetId = avatar.LinkedPlayerID;
            _wearerNetIdRecipient = new[] { _wearerNetId };

            featureNetworking.AssignGuids(_isWearer);
            if (_isWearer)
            {
                // _debugRetrySendNegotiationOptional = new Stopwatch();
                // _debugRetrySendNegotiationOptional.Restart();

                // Initialize other users.
                Debug.Log($"Sending Negotiation Packet to everyone...");
                NetworkMessageSend(featureNetworking.GetNegotiationPacket(), MainMessageDeliveryMethod);
                featureNetworking.TryResyncEveryone();
            }
            else
            {
                Debug.Log($"Sending ReservedPacket_RemoteRequestsInitializationMessage to {_wearerNetId}...");
                // Handle late-joining, or loading the avatar after the wearer does. Ask the wearer to initialize us.
                // - If the wearer has not finished loading their own avatar, they will initialize us anyways.
                NetworkMessageSend(featureNetworking.GetRemoteRequestsInitializationPacket(), MainMessageDeliveryMethod, _wearerNetIdRecipient);
            }
        }
        private void Update()
        {
            if (!_isWearer) return;

            // This is a hack: Send the negotiation packet every so often, because the negotiation packets are currently not going through properly.
            if (_debugRetrySendNegotiationOptional != null && _debugRetrySendNegotiationOptional.ElapsedMilliseconds > 5000)
            {
                _debugRetrySendNegotiationOptional.Restart();
                NetworkMessageSend(featureNetworking.GetNegotiationPacket(), MainMessageDeliveryMethod);
            }
        }

        public override void OnNetworkMessageReceived(ushort whoSentThis, byte[] unsafeBuffer, DeliveryMethod DeliveryMethod)
        {
            // Ignore all net messages as long as this is disabled
            if (!isActiveAndEnabled) return;

            if (unsafeBuffer.Length == 0) { ProtocolError("Protocol error: Missing sub-packet identifier"); return; }

            var isSentByWearer = _wearerNetId == whoSentThis;

            var theirs = unsafeBuffer[0];
            if (theirs == FeatureNetworking.NegotiationPacket)
            {
                if (!isSentByWearer) { ProtocolError("Protocol error: Only the wearer can send us this message."); return; }
                if (_isWearer) { ProtocolError("Protocol error: The wearer cannot receive this message."); return; }

                Debug.Log($"Receiving Negotiation packet from {whoSentThis}...");
                DecodeNegotiationPacket(SubBuffer(unsafeBuffer));
            }
            else if (theirs == FeatureNetworking.ReservedPacket)
            {
                Debug.Log($"Decoding reserved packet...");

                // Reserved packets are not necessarily sent by the wearer.
                DecodeReservedPacket(SubBuffer(unsafeBuffer), whoSentThis, isSentByWearer);
            }
            else // Transmission packet
            {
                if (!isSentByWearer) { ProtocolError("Protocol error: Only the wearer can send us this message."); return; }
                if (_isWearer) { ProtocolError("Protocol error: The wearer cannot receive this message."); return; }

                if (_fromTheirsToOurs == null) { ProtocolWarning("Protocol warning: No valid Networking packet was previously received yet."); return; }

                if (_fromTheirsToOurs.TryGetValue(theirs, out var ours))
                {
                    featureNetworking.OnPacketReceived(ours, SubBuffer(unsafeBuffer));
                }
                else
                {
                    // Either:
                    // - Mismatching avatar structures, or
                    // - Protocol asset mismatch: Remote has sent a GUID index greater or equal to the previously negotiated GUIDs.
                    ProtocolAssetMismatch($"Protocol asset mismatch: Cannot handle GUID with index {theirs}");
                }
            }
        }

        private static ArraySegment<byte> SubBuffer(byte[] unsafeBuffer)
        {
            return new ArraySegment<byte>(unsafeBuffer, 1, unsafeBuffer.Length - 1);
        }

        private bool DecodeNegotiationPacket(ArraySegment<byte> unsafeGuids)
        {
            if (unsafeGuids.Count % BytesPerGuid != 0) { ProtocolError("Protocol error: Unexpected message length."); return false;}

            // Safe after this point
            var safeGuids = unsafeGuids;

            var guidCount = safeGuids.Count / BytesPerGuid;
            _negotiatedGuids = new Guid[guidCount];
            _fromTheirsToOurs = new Dictionary<int, int>();
            if (guidCount == 0)
            {
                return true;
            }

            for (var guidIndex = 0; guidIndex < guidCount; guidIndex++)
            {
                var guid = new Guid(safeGuids.Slice(guidIndex * BytesPerGuid, BytesPerGuid));
                _negotiatedGuids[guidIndex] = guid;
            }

            var lookup = featureNetworking.GetOrderedGuids().ToList();

            for (var theirIndex = 0; theirIndex < _negotiatedGuids.Length; theirIndex++)
            {
                var theirGuid = _negotiatedGuids[theirIndex];
                var ourIndexOptional = lookup.IndexOf(theirGuid);
                if (ourIndexOptional != -1)
                {
                    _fromTheirsToOurs[theirIndex] = ourIndexOptional;
                }
            }

            return true;
        }

        private void DecodeReservedPacket(ArraySegment<byte> data, ushort whoSentThis, bool isSentByWearer)
        {
            if (data.Count == 0) { ProtocolError("Protocol error: Missing data identifier"); return; }

            var reservedPacketIdentifier = data.get_Item(0);

            if (reservedPacketIdentifier == FeatureNetworking.ReservedPacket_RemoteRequestsInitializationMessage)
            {
                if (isSentByWearer) { ProtocolError("Protocol error: Only remote users can send this message."); return; }
                if (!_isWearer) { ProtocolError("Protocol error: Only the wearer can receive this message."); return; }
                if (data.Count != 1) { ProtocolError("Protocol error: Unexpected message length."); return; }

                // TODO: We need a way to ignore incoming initialization requests if the avatar isn't the correct one

                Debug.Log($"Valid ReservedPacket_RemoteRequestsInitializationMessage received, sending negotiation packet now to {whoSentThis}...");
                NetworkMessageSend(featureNetworking.GetNegotiationPacket(), MainMessageDeliveryMethod, new[] { whoSentThis });
                featureNetworking.TryResyncSome(new [] { whoSentThis });
            }
            else
            {
                ProtocolError("Protocol error: This reserved packet is not known.");
            }
        }

        internal static void ProtocolError(string message)
        {
            Debug.LogError(message);
        }

        internal static void ProtocolWarning(string message)
        {
            Debug.LogWarning(message);
        }

        internal static void ProtocolAssetMismatch(string message)
        {
            Debug.LogError(message);
        }

        public override void OnNetworkChange(byte messageIndex, bool IsLocallyOwned)
        {
        }
        public override void OnNetworkMessageServerReductionSystem(byte[] unsafeBuffer)
        {
            // Ignore all net messages as long as this is disabled
            if (!isActiveAndEnabled) return;

            if (unsafeBuffer.Length == 0) { ProtocolError("Protocol error: Missing sub-packet identifier"); return; }

            var isSentByWearer = true;

            var theirs = unsafeBuffer[0];
            if (theirs == FeatureNetworking.NegotiationPacket)
            {
                if (!isSentByWearer) { ProtocolError("Protocol error: Only the wearer can send us this message."); return; }
                if (_isWearer) { ProtocolError("Protocol error: The wearer cannot receive this message."); return; }

                Debug.Log($"Receiving Negotiation packet from local...");
                DecodeNegotiationPacket(SubBuffer(unsafeBuffer));
            }
            else if (theirs == FeatureNetworking.ReservedPacket)
            {
                Debug.Log($"Decoding reserved packet...");

                // Reserved packets are not necessarily sent by the wearer.
                DecodeReservedPacket(SubBuffer(unsafeBuffer), _wearerNetId, isSentByWearer);
            }
            else // Transmission packet
            {
                if (!isSentByWearer) { ProtocolError("Protocol error: Only the wearer can send us this message."); return; }
                if (_isWearer) { ProtocolError("Protocol error: The wearer cannot receive this message."); return; }

                if (_fromTheirsToOurs == null) { ProtocolWarning("Protocol warning: No valid Networking packet was previously received yet."); return; }

                if (_fromTheirsToOurs.TryGetValue(theirs, out var ours))
                {
                    featureNetworking.OnPacketReceived(ours, SubBuffer(unsafeBuffer));
                }
                else
                {
                    // Either:
                    // - Mismatching avatar structures, or
                    // - Protocol asset mismatch: Remote has sent a GUID index greater or equal to the previously negotiated GUIDs.
                    ProtocolAssetMismatch($"Protocol asset mismatch: Cannot handle GUID with index {theirs}");
                }
            }
        }
    }
}
