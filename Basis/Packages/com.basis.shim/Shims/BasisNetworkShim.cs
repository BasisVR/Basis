using static BasisNetworkCommon;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using Cilbox;

namespace Basis.Shims
{
    public class BasisNetworkShim : BasisNetworkBehaviour, IBasisNetworkShimCompatible, CilboxShimI
	{
		public delegate void NetworkReadyEvent();
		public delegate void ServerOwnershipDestroyedEvent();
		public delegate void OwnershipTransferEvent(BasisNetworkPlayer NewOwner);
		public delegate void NetworkMessageEvent(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod);
		public delegate void PlayerJoinedEvent(BasisNetworkPlayer player);
		public delegate void PlayerLeftEvent(BasisNetworkPlayer player);

		public new NetworkReadyEvent NetworkReady { set; get; }
		public new OwnershipTransferEvent OwnershipTransfer { set; get; }
		public new ServerOwnershipDestroyedEvent ServerOwnershipDestroyed { set; get; }
		public new NetworkMessageEvent NetworkMessageReceived { set; get; }
		public new PlayerLeftEvent PlayerLeft { set; get; }
		public new PlayerJoinedEvent PlayerJoined { set; get; }

		bool IBasisNetworkShimCompatible.IsOwnedLocallyOnServer => IsOwnedLocallyOnServer;
		bool IBasisNetworkShimCompatible.IsOwnedLocallyOnClient => IsOwnedLocallyOnClient;
		ushort IBasisNetworkShimCompatible.CurrentOwnerId => CurrentOwnerId;
		BasisNetworkPlayer IBasisNetworkShimCompatible.currentOwnedPlayer => currentOwnedPlayer;

        public override void OnNetworkReady()
        {
			NetworkReady?.Invoke();
        }
        public override void OnServerOwnershipDestroyed()
        {
			ServerOwnershipDestroyed?.Invoke();
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

        public override void RequestOwnershipIfNone()
        {
	        RequestWhoIsOwnershipAsync();
        }
	}
}
