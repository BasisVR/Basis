using UnityEngine;

namespace Basis.Scripts.BasisSdk
{
    /// <summary>
    /// Represents an avatar in the Basis system, including face viseme/blink meshes,
    /// animation scale, ownership data, and linked player references.
    /// </summary>
    public class BasisAvatar : BasisContentBase
    {
        /// <summary>
        /// Animator component controlling the avatar.
        /// </summary>
        public Animator Animator;

        /// <summary>
        /// Skinned mesh renderer used for viseme-based facial animation.
        /// </summary>
        public SkinnedMeshRenderer FaceVisemeMesh;

        /// <summary>
        /// Skinned mesh renderer used for blink animations.
        /// </summary>
        public SkinnedMeshRenderer FaceBlinkMesh;

        /// <summary>
        /// Position of the avatar's eyes in normalized screen or UI space.
        /// </summary>
        public Vector2 AvatarEyePosition;

        /// <summary>
        /// Position of the avatar's mouth in normalized screen or UI space.
        /// </summary>
        public Vector2 AvatarMouthPosition;

        /// <summary>
        /// Blend shape indices for facial viseme movement; -1 entries indicate unused slots.
        /// </summary>
        public int[] FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };

        /// <summary>
        /// Blend shape indices used for blink animation; -1 indicates unused.
        /// </summary>
        public int[] BlinkViseme = new int[] { -1 };

        /// <summary>
        /// Blend shape index for laughter expression; -1 if unused.
        /// </summary>
        public int laughterBlendTarget = -1;

        /// <summary>
        /// Scale of the avatar's Animator in human bone space. Defaults to <see cref="Vector3.one"/>.
        /// </summary>
        public Vector3 AnimatorHumanScale = Vector3.one;

        private ushort linkedPlayerID;

        /// <summary>
        /// Indicates whether this avatar is linked to a remote or local player.
        /// </summary>
        public bool HasLinkedPlayer { get; private set; } = false;

        /// <summary>
        /// True if this avatar is owned by the local player.
        /// </summary>
        public bool IsOwnedLocally;

        /// <summary>
        /// Gets or sets the linked player ID. Setting also marks <see cref="HasLinkedPlayer"/> true.
        /// </summary>
        public ushort LinkedPlayerID
        {
            get => linkedPlayerID;
            set
            {
                linkedPlayerID = value;
                HasLinkedPlayer = true;
            }
        }

        /// <summary>
        /// Attempts to retrieve the linked player ID.
        /// </summary>
        /// <param name="Id">Outputs the player ID if linked.</param>
        /// <returns><c>true</c> if the avatar has a linked player; otherwise <c>false</c>.</returns>
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

        /// <summary>
        /// Optional renderers associated with the avatar (e.g., clothing, accessories).
        /// </summary>
        [SerializeField]
        public Renderer[] Renders;

        /// <summary>
        /// Delegate fired when the owner of this avatar is ready for data requests.
        /// </summary>
        /// <param name="IsOwner">True if the owner of this object is local; false if remote.</param>
        public delegate void OnReady(bool IsOwner);

        /// <summary>
        /// Event triggered when the avatar is ready for further initialization or data queries.
        /// </summary>
        public OnReady OnAvatarReady;

        /// <summary>
        /// Processing options used when the avatar is processed. This is always null after the avatar is processed.
        /// </summary>
        public BasisProcessingAvatarOptions ProcessingAvatarOptions;

        /// <summary>
        /// the animators humanScale, Cached here to stop requesting it from the animator per frame.
        /// </summary>

        public float HumanScale = 1;
    }
}
