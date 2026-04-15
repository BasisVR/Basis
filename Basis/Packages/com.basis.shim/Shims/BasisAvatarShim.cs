using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
    [DisallowMultipleComponent]
    public sealed class BasisAvatarShim : CilboxShim
    {
        public delegate void AvatarReadyEvent(bool isLocalPlayer);

        private AvatarReadyEvent avatarReadyHandlers;
        private BasisAvatar avatar;
        private bool isReady;
        private bool isLocalPlayer;

        public event AvatarReadyEvent AvatarReady
        {
            add
            {
                avatarReadyHandlers += value;
                if (isReady)
                {
                    value?.Invoke(isLocalPlayer);
                }
            }
            remove => avatarReadyHandlers -= value;
        }

        public bool IsReady => isReady;
        public bool IsLocalPlayer => isLocalPlayer;
        public bool IsOwner => isLocalPlayer;
        public BasisAvatar Avatar => avatar;

        private void Awake()
        {
            avatar = GetComponent<BasisAvatar>();
            if (avatar == null)
            {
                avatar = GetComponentInParent<BasisAvatar>(true);
            }

            if (avatar == null)
            {
                BasisDebug.LogError("[BasisAvatarShim] Could not resolve a BasisAvatar for this shim.");
                return;
            }

            avatar.OnAvatarReady -= OnAvatarReady;
            avatar.OnAvatarReady += OnAvatarReady;

            if (avatar.IsReady)
            {
                OnAvatarReady(avatar.IsOwnedLocally);
            }
        }

        private void OnDestroy()
        {
            if (avatar != null)
            {
                avatar.OnAvatarReady -= OnAvatarReady;
            }
        }

        private void OnAvatarReady(bool ownerIsLocal)
        {
            isReady = true;
            isLocalPlayer = ownerIsLocal;
            avatarReadyHandlers?.Invoke(ownerIsLocal);
        }
    }
}
