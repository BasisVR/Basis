using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEngine;
namespace Basis.Shims
{
    [DisallowMultipleComponent]
    public sealed class BasisAvatarShim : CilboxShim
    {
        private static readonly int[] DefaultFaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        private static readonly int[] DefaultBlinkViseme = new int[] { -1 };

        public delegate void OnReady(bool IsOwner);
        public delegate void AvatarReadyEvent(bool isLocalPlayer);

        public Vector2 AvatarEyePosition;
        public Vector2 AvatarMouthPosition;
        public Vector3 AnimatorHumanScale = Vector3.one;

        private BasisAvatar avatar;
        private bool isLocalPlayer;
        private OnReady onAvatarReady;
        private BasisPlayspaceInputShim playspaceInput;

        public BasisPlayspaceInputShim PlayspaceInput => playspaceInput ??= new BasisPlayspaceInputShim(this);

        public OnReady OnAvatarReady
        {
            get => onAvatarReady;
            set
            {
                OnReady previousHandlers = onAvatarReady;
                onAvatarReady = value;

                // Always keep the newly assigned handlers. If the avatar is not ready yet,
                // they remain registered and AvatarReady(...) will invoke them later.
                bool shouldReplayImmediately = IsReady && onAvatarReady != null;
                if (!shouldReplayImmediately)
                {
                    return;
                }

                ReplayNewReadyHandlers(previousHandlers, onAvatarReady, isLocalPlayer);
            }
        }

        public bool IsReady => avatar != null && avatar.IsReady;
        public bool IsLocalPlayer => isLocalPlayer;
        public bool IsOwner => isLocalPlayer;
        public BasisAvatar Avatar => avatar;
        public bool HasLinkedPlayer => avatar != null && avatar.HasLinkedPlayer;
        public Animator Animator
        {
            get => avatar != null ? avatar.Animator : null;
        }

        public SkinnedMeshRenderer FaceVisemeMesh
        {
            get => avatar != null ? avatar.FaceVisemeMesh : null;
        }

        public SkinnedMeshRenderer FaceBlinkMesh
        {
            get => avatar != null ? avatar.FaceBlinkMesh : null;
        }

        public int[] FaceVisemeMovement
        {
            get => avatar != null ? avatar.FaceVisemeMovement : DefaultFaceVisemeMovement;
        }

        public int[] BlinkViseme
        {
            get => avatar != null ? avatar.BlinkViseme : DefaultBlinkViseme;
        }

        public int laughterBlendTarget
        {
            get => avatar != null ? avatar.laughterBlendTarget : -1;
        }

        public bool IsOwnedLocally
        {
            get => avatar != null ? avatar.IsOwnedLocally : isLocalPlayer;
        }

        public float HumanScale
        {
            get => avatar != null ? avatar.HumanScale : 1;
            set
            {
                if (avatar != null)
                {
                    avatar.HumanScale = value;
                }
            }
        }

        public ushort LinkedPlayerID
        {
            get => avatar != null ? avatar.LinkedPlayerID : (ushort)0;
        }

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

            SyncFieldsFromAvatar();
            avatar.OnAvatarReady -= AvatarReady;
            avatar.OnAvatarReady += AvatarReady;

            if (avatar.IsReady)
            {
                AvatarReady(avatar.IsOwnedLocally);
            }
        }

        private void OnDestroy()
        {
            if (avatar != null)
            {
                avatar.OnAvatarReady -= AvatarReady;
            }
        }

        private void AvatarReady(bool ownerIsLocal)
        {
            isLocalPlayer = ownerIsLocal;
            SyncFieldsFromAvatar();
            OnAvatarReady?.Invoke(ownerIsLocal);
        }

        public bool TryGetLinkedPlayer(out ushort Id)
        {
            if (avatar == null)
            {
                Id = 0;
                return false;
            }

            return avatar.TryGetLinkedPlayer(out Id);
        }

        private void NotifyAvatarReady(bool isOwner)
        {
            if (avatar == null)
            {
                return;
            }

            avatar.NotifyAvatarReady(isOwner);
            SyncFieldsFromAvatar();
        }

        private static void ReplayNewReadyHandlers(OnReady previousHandlers, OnReady currentHandlers, bool ownerIsLocal)
        {
            System.Collections.Generic.Dictionary<System.Delegate, int> existingHandlers = new System.Collections.Generic.Dictionary<System.Delegate, int>();
            if (previousHandlers != null)
            {
                foreach (System.Delegate handler in previousHandlers.GetInvocationList())
                {
                    if (existingHandlers.TryGetValue(handler, out int count))
                    {
                        existingHandlers[handler] = count + 1;
                    }
                    else
                    {
                        existingHandlers[handler] = 1;
                    }
                }
            }

            foreach (System.Delegate handler in currentHandlers.GetInvocationList())
            {
                if (existingHandlers.TryGetValue(handler, out int count) && count > 0)
                {
                    existingHandlers[handler] = count - 1;
                    continue;
                }

                ((OnReady)handler).Invoke(ownerIsLocal);
            }
        }
        // Cilbox remaps BasisAvatar.GetGameObject(...) to this shim type, so forward to the real avatar helper.
        public static GameObject GetGameObject(object o)
        {
            return BasisAvatar.GetGameObject(o);
        }

        private void SyncFieldsFromAvatar()
        {
            if (avatar == null)
            {
                return;
            }

            AvatarEyePosition = avatar.AvatarEyePosition;
            AvatarMouthPosition = avatar.AvatarMouthPosition;
            AnimatorHumanScale = avatar.AnimatorHumanScale;
            isLocalPlayer = avatar.IsOwnedLocally;
        }
    }
}
