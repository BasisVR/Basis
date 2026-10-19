using System.Collections.Generic;
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
        /// Center-eye position in avatar-local metres relative to the animator root, packed as
        /// (x = height above the root, y = forward offset). Lateral offset is not authored — the
        /// point is always on the avatar's midline. Convert with
        /// <c>BasisHelpers.AvatarPositionConversion</c>; <c>x</c> alone is the avatar's eye height.
        /// </summary>
        public Vector2 AvatarEyePosition;

        /// <summary>
        /// Mouth position in avatar-local metres relative to the animator root, packed as
        /// (x = height above the root, y = forward offset). Lateral offset is not authored — the
        /// point is always on the avatar's midline. Convert with
        /// <c>BasisHelpers.AvatarPositionConversion</c>.
        /// </summary>
        public Vector2 AvatarMouthPosition;

        /// <summary>
        /// How lively the avatar's eyes feel. Low values = calm and settled, high values = active and expressive.
        /// </summary>
        public float EyeLiveliness = 0.5f;

        /// <summary>
        /// How attentive the avatar's gaze feels. Low values = avoidant and wandering, high values = direct and focused.
        /// </summary>
        public float EyeAttentiveness = 0.5f;

        public const float MinEyeMaxLookAngle = 1f, MaxEyeMaxLookAngle = 45f, DefaultEyeMaxLookAngle = 25f;
        public bool EyeMaxLookAngleEnabled;
        public float EyeMaxLookAngle = DefaultEyeMaxLookAngle;
        public static float ClampEyeMaxLookAngle(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees) || degrees <= 0f) return DefaultEyeMaxLookAngle;
            return Mathf.Clamp(degrees, MinEyeMaxLookAngle, MaxEyeMaxLookAngle);
        }

        /// <summary>
        /// Blend shape indices for facial viseme movement; -1 entries indicate unused slots.
        /// </summary>
        public int[] FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };

        /// <summary>
        /// Optional per-viseme response shaping, parallel to <see cref="FaceVisemeMovement"/>.
        /// Null or short entries fall back to <see cref="BasisVisemeProfile.Default"/>, which
        /// reproduces the untouched probability-to-weight behaviour.
        /// </summary>
        public BasisVisemeProfile[] FaceVisemeProfiles;

        /// <summary>
        /// Avatar-wide lip-sync response settings shared by every viseme.
        /// </summary>
        public BasisVisemeDriveConfig FaceVisemeDrive = new BasisVisemeDriveConfig();

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
        public bool IsOwnedLocally { get; set; }
        public override bool SpawnedByLocalPlayer => IsOwnedLocally;

        /// <summary>
        /// True once avatar setup has completed and readiness callbacks have fired for this instance.
        /// </summary>
        public bool IsReady { get; private set; }

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

        // DX12/Vulkan content loading keeps the GameObject active so Awake/OnEnable and avatar
        // calibration can run, but suppresses every renderer atomically until the first solved
        // pose. forceRenderingOff preserves authored Renderer.enabled state and any setup-time
        // changes to it, avoiding both a visible T-pose and partial clothing reveals.
        [System.NonSerialized] private Renderer[] loadVisibilityRenderers;
        [System.NonSerialized] private bool[] loadVisibilityPreviousForceOff;
        public bool HasLoadVisibilityGate => loadVisibilityRenderers != null;

        public void BeginLoadVisibilityGate(IList<Renderer> renderers)
        {
            if (loadVisibilityRenderers != null || renderers == null || renderers.Count == 0)
            {
                return;
            }
            loadVisibilityRenderers = new Renderer[renderers.Count];
            loadVisibilityPreviousForceOff = new bool[renderers.Count];
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                loadVisibilityRenderers[i] = renderer;
                if (renderer != null)
                {
                    loadVisibilityPreviousForceOff[i] = renderer.forceRenderingOff;
                    renderer.forceRenderingOff = true;
                }
            }
        }

        public void CompleteLoadVisibilityGate()
        {
            if (loadVisibilityRenderers == null)
            {
                return;
            }
            for (int i = 0; i < loadVisibilityRenderers.Length; i++)
            {
                Renderer renderer = loadVisibilityRenderers[i];
                if (renderer != null)
                {
                    renderer.forceRenderingOff = loadVisibilityPreviousForceOff[i];
                }
            }
            loadVisibilityRenderers = null;
            loadVisibilityPreviousForceOff = null;
        }

        /// <summary>
        /// True when this avatar is a runtime-built far LOD proxy standing in for the real
        /// avatar (out of range, downloading, or platform-missing). It goes through the exact
        /// same load/calibration/registration pipeline as every other avatar.
        /// </summary>
        [System.NonSerialized]
        public bool IsFarLodAvatar;

        /// <summary>
        /// Shared far LOD version this avatar was built from, mirrored here from the wearer's
        /// BasisFarAvatarInstance so the transmit tick can test staleness with a field read.
        /// That component stays the owner — it keys the shared-asset release in its OnDestroy —
        /// but reaching it took a GetComponent per far-LOD player per tick. Null on the
        /// prototype and on every avatar that isn't a far LOD.
        /// </summary>
        [System.NonSerialized]
        public string FarLodSharedVersion;

        [System.NonSerialized]
        public SkinnedMeshRenderer[] SkinnedMeshRenderers;

        [System.NonSerialized]
        public BasisAuthoredMotion[] AuthoredMotions;

        /// <summary>
        /// Humanoid bone transforms captured at build time, indexed by HumanBodyBones, so
        /// runtime calibration can skip the per-bone Animator.GetBoneTransform lookups.
        /// Null/empty on avatars built before this existed — callers fall back to detection.
        /// </summary>
        [SerializeField]
        public BasisAvatarTransformStorage TransformStorage;

        /// <summary>
        /// Delegate fired when the owner of this avatar is ready for data requests.
        /// </summary>
        /// <param name="IsOwner">True if the owner of this object is local; false if remote.</param>
        public delegate void OnReady(bool IsOwner);

        /// <summary>
        /// Event triggered when the avatar is ready for further initialization or data queries.
        /// </summary>
        public OnReady OnAvatarReady {get; set;}

        /// <summary>
        /// Marks this avatar as ready and notifies listeners with the owner locality.
        /// </summary>
        /// <param name="isOwner">True when this avatar belongs to the local player.</param>
        public void NotifyAvatarReady(bool isOwner)
        {
            IsOwnedLocally = isOwner;
            IsReady = true;
            OnAvatarReady?.Invoke(isOwner);
        }

        public static GameObject GetGameObject(object o)
        {
            GameObject currentGameobject = null;
            if (o is GameObject go)
            {
                currentGameobject = go;
            }
            else if (o is Component c)
            {
                currentGameobject = c.gameObject;
            }

            if (currentGameobject == null)
            {
                Debug.LogError($"Object {o} is not a GameObject or Component.");
                return null;
            }

            while (currentGameobject != null)
            {
                if (currentGameobject.TryGetComponent<BasisAvatar>(out _))
                {
                    return currentGameobject;
                }

                Transform parent = currentGameobject.transform.parent;
                if (parent == null)
                {
                    break;
                }

                currentGameobject = parent.gameObject;
            }

            Debug.LogError($"Object {o} is not part of an avatar hierarchy.");
            return null;
        }

        /// <summary>
        /// Processing options used when the avatar is processed. This is always null after the avatar is processed.
        /// </summary>
        public BasisProcessingAvatarOptions ProcessingAvatarOptions;

        /// <summary>
        /// the animators humanScale, Cached here to stop requesting it from the animator per frame.
        /// </summary>

        public float HumanScale = 1;
    }

    /// <summary>
    /// Build-time snapshot of an avatar's humanoid bone transforms, indexed by HumanBodyBones.
    /// Lets runtime detection bypass Animator.GetBoneTransform. <see cref="HasData"/> is false
    /// for avatars built before this was captured, signalling callers to fall back to the Animator.
    /// </summary>
    [System.Serializable]
    public class BasisAvatarTransformStorage
    {
        public Transform[] HumanoidBones;

        public bool HasData => HumanoidBones != null && HumanoidBones.Length == (int)HumanBodyBones.LastBone;

        public Transform Get(HumanBodyBones bone)
        {
            int index = (int)bone;
            if (HumanoidBones == null || index < 0 || index >= HumanoidBones.Length)
            {
                return null;
            }
            return HumanoidBones[index];
        }

        public static BasisAvatarTransformStorage CaptureFrom(Animator animator)
        {
            if (animator == null || !animator.isHuman)
            {
                return null;
            }

            int count = (int)HumanBodyBones.LastBone;
            BasisAvatarTransformStorage storage = new BasisAvatarTransformStorage
            {
                HumanoidBones = new Transform[count]
            };
            for (int index = 0; index < count; index++)
            {
                storage.HumanoidBones[index] = animator.GetBoneTransform((HumanBodyBones)index);
            }
            return storage;
        }
    }
}
