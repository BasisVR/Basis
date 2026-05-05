using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking.Behaviour;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Shims
{
    public sealed class BasisAvatarNetworkShim : BasisNetworkAvatarBehaviour, IBasisNetworkShimCompatible, CilboxShimI
    {
        new public static bool VisibleInAvatarMenu = false;

        public bool IsOwnedLocallyOnServer = false;
        public bool IsOwnedLocallyOnClient = false;
        public ushort CurrentOwnerId;
        public BasisNetworkPlayer currentOwnedPlayer;
        [SerializeField] private MonoBehaviour ownerComponent;

        public BasisNetworkShim.NetworkReadyEvent NetworkReady { set; get; }
        public BasisNetworkShim.OwnershipTransferEvent OwnershipTransfer { set; get; }
        public BasisNetworkShim.ServerOwnershipDestroyedEvent ServerOwnershipDestroyed { set; get; }
        public BasisNetworkShim.NetworkMessageEvent NetworkMessageReceived { set; get; }
        public BasisNetworkShim.PlayerLeftEvent PlayerLeft { set; get; }
        public BasisNetworkShim.PlayerJoinedEvent PlayerJoined { set; get; }

        bool IBasisNetworkShimCompatible.IsOwnedLocallyOnServer => IsOwnedLocallyOnServer;
        bool IBasisNetworkShimCompatible.IsOwnedLocallyOnClient => IsOwnedLocallyOnClient;
        ushort IBasisNetworkShimCompatible.CurrentOwnerId => CurrentOwnerId;
        BasisNetworkPlayer IBasisNetworkShimCompatible.currentOwnedPlayer => currentOwnedPlayer;

        public bool IsAssignedTo(MonoBehaviour owner)
        {
            return ownerComponent == owner;
        }

        public void AssignScriptOwner(MonoBehaviour owner)
        {
            ownerComponent = owner;
        }

        private void Start()
        {
            BasisNetworkPlayer.OnPlayerJoined += OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft += OnPlayerLeft;

            EnsureNetworkAssigned();

            if (IsInitalized && NetworkedPlayer != null)
            {
                SyncOwnerState(NetworkedPlayer, NetworkedPlayer.IsLocal);
            }
        }

        private void OnDestroy()
        {
            BasisNetworkPlayer.OnPlayerJoined -= OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft -= OnPlayerLeft;
        }

        public bool IsLocalOwner()
        {
            return IsOwnedLocallyOnClient || IsOwnedLocallyOnServer;
        }

        public void SendCustomNetworkEvent(byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null)
        {
            if (IsInitalized)
            {
                if (NetworkMessageReceived == null && Recipients == null && DeliveryMethod == Basis.Network.Core.DeliveryMethod.Unreliable)
                {
                    ServerReductionSystemMessageSend(buffer);
                }
                else
                {
                    NetworkMessageSend(buffer, DeliveryMethod, Recipients);
                }
                return;
            }

            BasisDebug.LogError("Avatar network shim is not ready yet.", gameObject, BasisDebug.LogTag.Shims);
        }

        public async void TakeOwnership()
        {
            await TakeOwnershipAsync();
        }

        public Task<BasisOwnershipResult> TakeOwnershipAsync(int Timout = 5000)
        {
            RefreshOwnerState();
            return Task.FromResult(new BasisOwnershipResult(IsLocalOwner(), CurrentOwnerId));
        }

        public Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(int Timout = 5000)
        {
            RefreshOwnerState();
            bool hasOwner = CurrentOwnerId != 0 || currentOwnedPlayer != null;
            return Task.FromResult(new BasisOwnershipResult(hasOwner, CurrentOwnerId));
        }

        public void RequestOwnershipIfNone()
        {
            if (CurrentOwnerId == 0)
            {
                RefreshOwnerState();
            }
        }

        public bool EnsureNetworkAssigned()
        {
            if (!TryLateBind())
            {
                return IsInitalized;
            }

            return true;
        }

        public override void OnNetworkReady(bool IsLocallyOwned)
        {
            HandleAvatarReady(NetworkedPlayer, IsLocallyOwned);
        }

        public void OnNetworkReady()
        {
            NetworkReady?.Invoke();
        }

        public void OnServerOwnershipDestroyed()
        {
            ServerOwnershipDestroyed?.Invoke();
        }

        public void OnOwnershipTransfer(BasisNetworkPlayer NetIdNewOwner)
        {
            OwnershipTransfer?.Invoke(NetIdNewOwner);
        }

        public void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            NetworkMessageReceived?.Invoke(PlayerID, buffer, DeliveryMethod);
        }

        public void OnPlayerLeft(BasisNetworkPlayer player)
        {
            PlayerLeft?.Invoke(player);
        }

        public void OnPlayerJoined(BasisNetworkPlayer player)
        {
            PlayerJoined?.Invoke(player);
        }

        public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            OnNetworkMessage(RemoteUser, buffer, DeliveryMethod);
        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            OnNetworkMessage(CurrentOwnerId, buffer, DeliveryMethod.Unreliable);
        }

        private void HandleAvatarReady(BasisNetworkPlayer owner, bool isLocallyOwned)
        {
            ushort previousOwnerId = CurrentOwnerId;
            bool previousLocalOwnership = IsOwnedLocallyOnClient || IsOwnedLocallyOnServer;
            bool wasReady = currentOwnedPlayer != null || previousOwnerId != 0;

            SyncOwnerState(owner, isLocallyOwned);

            if (!wasReady)
            {
                OnNetworkReady();
                return;
            }

            if (previousOwnerId != CurrentOwnerId || previousLocalOwnership != IsLocalOwner())
            {
                OnOwnershipTransfer(currentOwnedPlayer);
            }
        }

        private void RefreshOwnerState()
        {
            if (IsInitalized && NetworkedPlayer != null)
            {
                SyncOwnerState(NetworkedPlayer, NetworkedPlayer.IsLocal);
                return;
            }

            if (TryResolveAvatar(out BasisAvatar avatar) && avatar.TryGetLinkedPlayer(out ushort playerId))
            {
                BasisNetworkPlayer.GetPlayerById(playerId, out BasisNetworkPlayer owner);
                SyncOwnerState(owner, avatar.IsOwnedLocally);
            }
        }

        private bool TryLateBind()
        {
            if (IsInitalized)
            {
                return true;
            }

            if (!TryResolveAvatar(out BasisAvatar avatar) || !avatar.TryGetLinkedPlayer(out ushort playerId))
            {
                return false;
            }

            if (!BasisNetworkPlayer.GetPlayerById(playerId, out BasisNetworkPlayer player))
            {
                return false;
            }

            return player.TryRegisterNetworkBehaviour(this);
        }

        private void SyncOwnerState(BasisNetworkPlayer owner, bool isLocallyOwned)
        {
            currentOwnedPlayer = owner;
            CurrentOwnerId = owner != null ? owner.playerId : TryGetAvatarOwnerId();
            IsOwnedLocallyOnClient = isLocallyOwned;
            IsOwnedLocallyOnServer = isLocallyOwned;
        }

        private ushort TryGetAvatarOwnerId()
        {
            if (TryResolveAvatar(out BasisAvatar avatar) && avatar.TryGetLinkedPlayer(out ushort playerId))
            {
                return playerId;
            }

            return 0;
        }

        private bool TryResolveAvatar(out BasisAvatar avatar)
        {
            avatar = GetComponent<BasisAvatar>();
            if (avatar == null)
            {
                avatar = GetComponentInParent<BasisAvatar>(true);
            }

            return avatar != null;
        }
    }
}
