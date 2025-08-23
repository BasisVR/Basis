using UnityEngine;

namespace Basis.Scripts.BasisSdk
{
    public class BasisAvatar : BasisContentBase
    {
        public Animator Animator;
        public SkinnedMeshRenderer FaceVisemeMesh;
        public SkinnedMeshRenderer FaceBlinkMesh;
        public Vector2 AvatarEyePosition;
        public Vector2 AvatarMouthPosition;
        public int[] FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        public int[] BlinkViseme = new int[] { -1 };
        public int laughterBlendTarget = -1;
        public Vector3 AnimatorHumanScale = Vector3.one;//stops us needing to go over the mashal.
        private ushort linkedPlayerID;
        public bool HasLinkedPlayer { get; private set; } = false;
        public bool IsOwnedLocally;
        // Property for LinkedPlayerID with logic to set HasLinkedPlayer
        public ushort LinkedPlayerID
        {
            get => linkedPlayerID;
            set
            {
                linkedPlayerID = value;
                HasLinkedPlayer = true;
            }
        }

        // Try to set the linked player
        public bool TryGetLinkedPlayer(out ushort Id)
        {
            if (HasLinkedPlayer)
            {
                Id = LinkedPlayerID;
                return true;
            }
            else
            {
                Id = 0;
            }
            return false;
        }

        [SerializeField]
        public Renderer[] Renders;
        public OnReady OnAvatarReady;
        /// <summary>
        /// this is called when the owner of this gameobject is ready for you to request data about it
        /// </summary>
        public delegate void OnReady(bool IsOwner);
    }
}
