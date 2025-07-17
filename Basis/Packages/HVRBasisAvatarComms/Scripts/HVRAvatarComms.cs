using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;
namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/Avatar Comms")]
    public class HVRAvatarComms : BasisAvatarMonoBehaviour
    {
        private const int BytesPerGuid = 16;
        [HideInInspector]
        [SerializeField]
        private BasisAvatar avatar;
        [HideInInspector]
        [SerializeField]
        private FeatureNetworking featureNetworking;
        private bool _isWearer;
        private Guid[] _negotiatedGuids;
        private Dictionary<int, int> _fromTheirsToOurs = new Dictionary<int, int>();
        private void Awake()
        {
            if (avatar == null)
            {
                avatar = CommsUtil.GetAvatar(this);
            }
            if (featureNetworking == null)
            {
                featureNetworking = CommsUtil.FeatureNetworkingFromAvatar(avatar);
            }
            if (avatar == null || featureNetworking == null)
            {
                throw new InvalidOperationException("Broke assumption: Avatar and/or FeatureNetworking cannot be found.");
            }

        }
        public override void OnNetworkReady(bool IsLocallyOwned)
        {
            _isWearer = IsLocallyOwned;
            featureNetworking.AssignGuids(IsLocallyOwned);
            if (IsLocallyOwned)
            {
                // Initialize other users.
                ProtocolDebug($"Sending Negotiation Packet to everyone...");
                NetworkMessageSend(featureNetworking.GetNegotiationPacket(), DeliveryMethod.ReliableOrdered);
                featureNetworking.TryResyncEveryone();
            }
            else
            {
                ProtocolDebug($"Sending ReservedPacket_RemoteRequestsInitializationMessage to {NetworkedPlayer.playerId}...");
                // Handle late-joining, or loading the avatar after the wearer does. Ask the wearer to initialize us.
                // - If the wearer has not finished loading their own avatar, they will initialize us anyways.
                NetworkMessageSend(featureNetworking.GetRemoteRequestsInitializationPacket(), DeliveryMethod.ReliableOrdered, new ushort[] { NetworkedPlayer.playerId });
            }
        }
        public override void OnNetworkMessageReceived(ushort whoSentThis, byte[] unsafeBuffer, DeliveryMethod DeliveryMethod, bool IsADifferentAvatarLocally)
        {
            if(IsADifferentAvatarLocally)
            {
               // ProtocolError("Protocol error: IsADifferentAvatarLocally");
                return;
            }
            // Ignore all net messages as long as this is disabled
            if (!isActiveAndEnabled)
            {
                return;
            }

            if (unsafeBuffer.Length == 0)
            {
                ProtocolError("Protocol error: Missing sub-packet identifier");
                return;
            }
            if (avatar.TryGetLinkedPlayer(out ushort _wearerNetId) == false)
            {
                ProtocolError("no wearer netid!");
                return;
            }
            var isSentByWearer = _wearerNetId == whoSentThis;
            byte theirs = unsafeBuffer[0];

            if (theirs == FeatureNetworking.NegotiationPacket)
            {
                if (!isSentByWearer)
                {
                    ProtocolError("Protocol error: Only the wearer can send us this message.");
                    return;
                }
                if (_isWearer)
                {
                    ProtocolError("Protocol error: The wearer cannot receive this message.");
                    return;
                }

                ProtocolDebug($"Receiving Negotiation packet from {whoSentThis}...");
                DecodeNegotiationPacket(SubBuffer(unsafeBuffer));
            }
            else if (theirs == FeatureNetworking.ReservedPacket)
            {
                BasisDebug.Log($"Decoding reserved packet...");

                // Reserved packets are not necessarily sent by the wearer.
                DecodeReservedPacket(SubBuffer(unsafeBuffer), whoSentThis, isSentByWearer);
            }
            else // Transmission packet
            {
                if (!isSentByWearer)
                {
                    ProtocolError("Protocol error: Only the wearer can send us this message.");
                    return;
                }
                if (_isWearer)
                {
                    ProtocolError("Protocol error: The wearer cannot receive this message.");
                    return;
                }
                if (_fromTheirsToOurs == null)
                {
                    ProtocolWarning("Protocol warning: No valid Networking packet was previously received yet.");
                    return;
                }

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
            if (unsafeGuids.Count % BytesPerGuid != 0) { ProtocolError("Protocol error: Unexpected message length."); return false; }

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
            if (data.Count == 0)
            {
                ProtocolError("Protocol error: Missing data identifier");
                return;
            }

            var reservedPacketIdentifier = data.get_Item(0);

            if (reservedPacketIdentifier == FeatureNetworking.ReservedPacket_RemoteRequestsInitializationMessage)
            {
                if (isSentByWearer)
                {
                    ProtocolError("Protocol error: Only remote users can send this message.");
                    return;
                }
                if (!_isWearer)
                {
                    ProtocolError("Protocol error: Only the wearer can receive this message.");
                    return;
                }
                if (data.Count != 1)
                {
                    ProtocolError("Protocol error: Unexpected message length.");
                    return;
                }

                // TODO: We need a way to ignore incoming initialization requests if the avatar isn't the correct one

                ProtocolDebug($"Valid ReservedPacket_RemoteRequestsInitializationMessage received, sending negotiation packet now to {whoSentThis}...");
                NetworkMessageSend(featureNetworking.GetNegotiationPacket(), DeliveryMethod.ReliableOrdered, new[] { whoSentThis });
                featureNetworking.TryResyncSome(new[] { whoSentThis });
            }
            else
            {
                ProtocolError("Protocol error: This reserved packet is not known.");
            }
        }
        internal static void ProtocolError(string message)
        {
            BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        }

        internal static void ProtocolWarning(string message)
        {
            BasisDebug.LogWarning(message, BasisDebug.LogTag.Avatar);
        }

        internal static void ProtocolAssetMismatch(string message)
        {
            BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        }
        internal static void ProtocolDebug(string message)
        {
            BasisDebug.Log(message, BasisDebug.LogTag.Avatar);
        }
        public override void OnNetworkMessageServerReductionSystem(byte[] unsafeBuffer, bool IsSameAvatar)
        {
            if (IsSameAvatar == false)
            {
                return;
            }
            // Ignore all net messages as long as this is disabled
            if (!isActiveAndEnabled) return;

            if (unsafeBuffer.Length == 0)
            {
                ProtocolError("Protocol error: Missing sub-packet identifier");
                return;
            }

            var isSentByWearer = true;

            var theirs = unsafeBuffer[0];
            if (theirs == FeatureNetworking.NegotiationPacket)
            {
                if (!isSentByWearer)
                {
                    ProtocolError("Protocol error: Only the wearer can send us this message.");
                    return;
                }
                if (_isWearer)
                {
                    ProtocolError("Protocol error: The wearer cannot receive this message.");
                    return;
                }

                ProtocolDebug($"Receiving Negotiation packet from local...");
                DecodeNegotiationPacket(SubBuffer(unsafeBuffer));
            }
            else if (theirs == FeatureNetworking.ReservedPacket)
            {
                ProtocolDebug($"Decoding reserved packet...");

                if (avatar.TryGetLinkedPlayer(out ushort _wearerNetId) == false)
                {
                    ProtocolError("no wearer netid!");
                    return;
                }
                // Reserved packets are not necessarily sent by the wearer.
                DecodeReservedPacket(SubBuffer(unsafeBuffer), _wearerNetId, isSentByWearer);
            }
            else // Transmission packet
            {
                if (!isSentByWearer)
                {
                    ProtocolError("Protocol error: Only the wearer can send us this message.");
                    return;
                }
                if (_isWearer)
                {
                    ProtocolError("Protocol error: The wearer cannot receive this message.");
                    return;
                }

                if (_fromTheirsToOurs == null)
                {
                    ProtocolWarning("Protocol warning: No valid Networking packet was previously received yet.");
                    return;
                }

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
