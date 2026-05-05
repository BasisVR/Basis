using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.Threading.Tasks;

namespace Basis.Shims
{
    public interface IBasisNetworkShimCompatible
    {
        bool IsOwnedLocallyOnServer { get; }
        bool IsOwnedLocallyOnClient { get; }
        ushort CurrentOwnerId { get; }
        BasisNetworkPlayer currentOwnedPlayer { get; }

        BasisNetworkShim.NetworkReadyEvent NetworkReady { set; get; }
        BasisNetworkShim.OwnershipTransferEvent OwnershipTransfer { set; get; }
        BasisNetworkShim.ServerOwnershipDestroyedEvent ServerOwnershipDestroyed { set; get; }
        BasisNetworkShim.NetworkMessageEvent NetworkMessageReceived { set; get; }
        BasisNetworkShim.PlayerLeftEvent PlayerLeft { set; get; }
        BasisNetworkShim.PlayerJoinedEvent PlayerJoined { set; get; }

        bool IsLocalOwner();
        void SendCustomNetworkEvent(byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null);
        void TakeOwnership();
        Task<BasisOwnershipResult> TakeOwnershipAsync(int Timout = 5000);
        Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(int Timout = 5000);
        void RequestOwnershipIfNone();
    }
}
