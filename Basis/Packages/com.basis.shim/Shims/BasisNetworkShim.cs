using static BasisNetworkCommon;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using System;

namespace Basis
{
    public class BasisNetworkShim : BasisNetworkBehaviour
	{
		public delegate void NetworkReadyEvent();
		public delegate void ServerOwnershipDestroyedEvent();
		public delegate void OwnershipTransferEvent(BasisNetworkPlayer NewOwner);
		public delegate void NetworkMessageEvent(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod);
		public delegate void PlayerJoinedEvent(BasisNetworkPlayer player);
		public delegate void PlayerLeftEvent(BasisNetworkPlayer player);

		public NetworkReadyEvent             NetworkReady { set; get; }
		public OwnershipTransferEvent        OwnershipTransfer { set; get; }
		public ServerOwnershipDestroyedEvent ServerOwnershipDestroyedE { set; get; }
		public NetworkMessageEvent           NetworkMessageReceived { set; get; }
		public PlayerLeftEvent               PlayerLeft { set; get; }
		public PlayerJoinedEvent             PlayerJoined { set; get; }

        public override void OnNetworkReady()
        {
			NetworkReady?.Invoke();
        }
        public override void ServerOwnershipDestroyed()
        {
			ServerOwnershipDestroyedE?.Invoke();
        }
        public override void OnOwnershipTransfer(BasisNetworkPlayer NetNewOwner)
        {
			OwnershipTransfer?.Invoke(NetNewOwner);
        }
        public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
			NetworkMessageReceived?.Invoke( PlayerID, buffer, DeliveryMethod );
        }
        public override void OnPlayerLeft(BasisNetworkPlayer player)
        {
			PlayerLeft?.Invoke( player );
        }
        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
			PlayerJoined?.Invoke( player );
        }
	}
}
