using System;
using System.Collections.Generic;
using System.IO;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using TMPro;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// A spawned image pickup. Front face shows the image on an unlit material; the back carries the
    /// Hide/Save/Delete controls and the spawner label. Any client can grab it; grabbing claims movement
    /// authority and that client broadcasts the transform until someone else grabs it.
    /// </summary>
    public class BasisImagePickupObject : MonoBehaviour
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;

        public Guid ImageId;
        public ushort OwnerId;
        public string OwnerName;
        public bool IsOwner;

        public TextMeshProUGUI HideLabel;
        public TextMeshProUGUI DeleteLabel;

        private Texture2D _posterTexture;
        private byte[] _cleanPng;
        private bool _posterHasAlpha;
        private bool _isLoading;
        private Material _material;
        private MeshRenderer _frontRenderer;
        private Transform _cardTransform;
        private bool _managed;
        private BasisAnimatedImagePlayer _animatedImagePlayer;

        private bool _hidden;
        private bool _deleteArmed;
        private bool _isController;
        private Rigidbody _body;
        private BoxCollider _ownCollider;
        private GameObject _backPanel;
        private BasisPickupInteractable _interactable;
        private bool _hasRemoteTarget;
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private float _targetScale = 1f;

        public float LastSendTime;
        public Vector3 LastSentPosition;
        public Quaternion LastSentRotation = Quaternion.identity;
        public float LastSentScale = 1f;
        public bool IsController => _isController;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly Dictionary<Collider, BasisImagePickupObject> PickupByCollider = new();

        public byte[] CleanPng => _cleanPng;
        internal long PosterPixelCount => _posterTexture != null ? (long)_posterTexture.width * _posterTexture.height : 0L;
        public bool IsHidden => _hidden;
        /// <summary>
        /// True between <see cref="Build"/> and <see cref="ApplyLoadedImage"/>: the card is showing the
        /// placeholder while its poster is still decoding locally or still arriving over the network.
        /// </summary>
        public bool IsLoading => _isLoading;
        internal bool BackPanelVisible => _backPanel != null && _backPanel.activeSelf;
        internal bool HasFrontRenderer =>
            _frontRenderer != null
            && _cardTransform != null
            && _frontRenderer.enabled
            && _frontRenderer.gameObject.activeInHierarchy;
        internal Bounds FrontRendererBounds =>
            _frontRenderer != null ? _frontRenderer.bounds : default;
        internal int FrontRendererLayer =>
            _frontRenderer != null ? _frontRenderer.gameObject.layer : gameObject.layer;
        public BasisAnimatedImagePlayer AnimatedImagePlayer => _animatedImagePlayer;

        /// <summary>
        /// Builds a pickup card. Pass a null <paramref name="texture"/> to raise the card immediately in its
        /// loading state — it shows the placeholder until <see cref="ApplyLoadedImage"/> supplies the poster.
        /// <paramref name="width"/> and <paramref name="height"/> are the poster's final dimensions, so the
        /// card carries its true aspect from the first frame and never resizes under the viewer.
        /// </summary>
        public static BasisImagePickupObject Build(Guid id, ushort ownerId, string ownerName, bool isOwner, int width, int height, Texture2D texture, byte[] cleanPng, bool cutout, Vector3 position, Quaternion rotation)
        {
            var root = new GameObject($"BasisImagePickup_{ShortId(id)}");
            root.transform.SetPositionAndRotation(position, rotation);

            int interactableLayer = LayerMask.NameToLayer("Interactable");
            if (interactableLayer >= 0) root.layer = interactableLayer;

            var pickup = root.AddComponent<BasisImagePickupObject>();
            pickup._managed = true;
            pickup.ImageId = id;
            pickup.OwnerId = ownerId;
            pickup.OwnerName = string.IsNullOrEmpty(ownerName) ? "Unknown" : ownerName;
            pickup.IsOwner = isOwner;
            pickup._posterTexture = texture;
            pickup._cleanPng = cleanPng;
            pickup._posterHasAlpha = cutout;
            pickup._isLoading = texture == null;

            CalculateCardSize(width, height, out float panelWidth, out float panelHeight);

            var card = new GameObject("Card");
            if (interactableLayer >= 0) card.layer = interactableLayer;
            card.transform.SetParent(root.transform, false);
            card.transform.localScale = new Vector3(panelWidth, panelHeight, 1f);
            pickup._cardTransform = card.transform;
            card.AddComponent<MeshFilter>().sharedMesh = GetCardMesh();

            pickup._material = new Material(BundledContentHolder.Instance.UnlitUrpShader);
            if (pickup._material.HasProperty(BaseColorId)) pickup._material.SetColor(BaseColorId, Color.white);
            pickup.SetDisplayTexture(texture != null ? texture : GetPlaceholderTexture());
            if (cutout && texture != null) ConfigureCutout(pickup._material);

            pickup._frontRenderer = card.AddComponent<MeshRenderer>();
            pickup._frontRenderer.allowOcclusionWhenDynamic = true;
            pickup._frontRenderer.sharedMaterials = new[] { pickup._material, GetSharedBackMaterial() };
            pickup._frontRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pickup._frontRenderer.receiveShadows = false;

            pickup._isController = isOwner;

            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(panelWidth, panelHeight, 0.02f);
            pickup._ownCollider = box;
            RegisterCollider(box, pickup);

            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = !isOwner;
            body.useGravity = false;
            body.linearDamping = 1.5f;
            body.angularDamping = 2.5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            pickup._body = body;

            var interactable = root.AddComponent<BasisPickupInteractable>();
            interactable.RigidRef = body;
            interactable.GenerateColliderMesh = false;
            interactable.enableScaleWithGesture = true;
            interactable.minScalePercent = 25f;
            interactable.maxScalePercent = 400f;
            pickup._interactable = interactable;
            interactable.OnInteractStartEvent.AddListener(pickup.OnLocalGrabbed);

            return pickup;
        }

        /// <summary>
        /// Shows or hides the Hide/Save/Delete controls on the card's back, following the main menu. The panel
        /// is a world-space canvas with its own graphic raycaster, so it is built on first show and only
        /// toggled after that: a room full of pickups would otherwise each keep a live canvas, and each
        /// raycast against controls nobody can click while the menu is shut. Grabbing and moving the card is
        /// unaffected — that runs off the pickup's own collider, not this canvas.
        /// </summary>
        internal void SetBackPanelVisible(bool visible)
        {
            if (!visible)
            {
                if (_backPanel == null)
                    return;
                CancelInvoke(nameof(DisarmDelete));
                DisarmDelete();
                _backPanel.SetActive(false);
                return;
            }

            if (_backPanel == null)
            {
                _backPanel = BasisImagePickupBackPanel.Build(
                    transform,
                    this,
                    BasisImagePickupSettings.BaseHeightMeters
                );
                return;
            }
            _backPanel.SetActive(true);
        }

        /// <summary>
        /// Swaps a loading card over to its decoded poster: the local GIF pipeline calls this when its Burst
        /// decode lands, and receivers call it when the final network chunk arrives. Callers guarantee the
        /// poster matches the dimensions the card was built with, so this is a texture swap, not a respawn —
        /// the pickup keeps its identity, pose, and any movement authority a grabber took while it loaded.
        /// </summary>
        internal void ApplyLoadedImage(Texture2D texture, byte[] cleanPng, bool cutout, int width, int height)
        {
            if (texture == null)
                return;

            _posterTexture = texture;
            _cleanPng = cleanPng;
            _posterHasAlpha = cutout;
            _isLoading = false;

            CalculateCardSize(width, height, out float panelWidth, out float panelHeight);
            if (_cardTransform != null) _cardTransform.localScale = new Vector3(panelWidth, panelHeight, 1f);
            if (_ownCollider != null) _ownCollider.size = new Vector3(panelWidth, panelHeight, 0.02f);

            if (_material == null)
                return;
            SetDisplayTexture(texture);
            if (cutout) ConfigureCutout(_material);
            else ConfigureOpaque(_material);
        }

        private static void CalculateCardSize(int width, int height, out float panelWidth, out float panelHeight)
        {
            float aspect = height > 0 ? (float)width / height : 1f;
            panelHeight = BasisImagePickupSettings.BaseHeightMeters;
            panelWidth = panelHeight * Mathf.Max(0.05f, aspect);
        }

        internal void SimulateRemoteTransform(float deltaTime)
        {
            if (_isController || !_hasRemoteTarget) return;
            transform.GetPositionAndRotation(out Vector3 currentPos, out Quaternion currentRot);
            float t = CalculateRemoteTransformLerpFactor(deltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(currentPos, _targetPosition, t),
                Quaternion.Slerp(currentRot, _targetRotation, t));
            float scale = Mathf.Lerp(transform.localScale.x, _targetScale, t);
            transform.localScale = new Vector3(scale, scale, scale);
        }

        internal static float CalculateRemoteTransformLerpFactor(float deltaTime)
        {
            return 1f - Mathf.Exp(-12f * Mathf.Max(0f, deltaTime));
        }

        private void OnLocalGrabbed(BasisInput input)
        {
            if (_interactable != null) _interactable._previousKinematicValue = false;
            if (!_isController && _managed) BasisImagePickupManager.ClaimControl(ImageId);
        }

        public void SetController(bool value)
        {
            _isController = value;
            if (!value)
            {
                transform.GetPositionAndRotation(out _targetPosition, out _targetRotation);
                _targetScale = transform.localScale.x;
                if (_body != null && !_body.isKinematic)
                {
                    _body.linearVelocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                    _body.isKinematic = true;
                }
            }
        }

        public void SetRemoteTarget(Vector3 position, Quaternion rotation, float scale)
        {
            _targetPosition = position;
            _targetRotation = rotation;
            _targetScale = scale;
            if (!_hasRemoteTarget)
            {
                transform.SetPositionAndRotation(position, rotation);
                transform.localScale = new Vector3(scale, scale, scale);
                _hasRemoteTarget = true;
            }
        }

        public void OnHidePressed()
        {
            _hidden = !_hidden;
            if (_frontRenderer != null) _frontRenderer.enabled = !_hidden;
            if (HideLabel != null) HideLabel.text = _hidden ? "Show" : "Hide";
        }

        /// <summary>
        /// Upgrades this pickup from its static poster to synchronized animated playback.
        /// The poster remains owned by the pickup for saving and backward-compatible display.
        /// </summary>
        public bool TrySetAnimation(BasisAnimatedImageData data, long playbackEpochUtcTicks = 0)
        {
            return TrySetAnimation(data, playbackEpochUtcTicks, null);
        }

        internal bool TrySetAnimation(
            BasisAnimatedImageData data,
            long playbackEpochUtcTicks,
            BasisNativeAnimationPayload reloadPayload
        )
        {
            if (data == null || _animatedImagePlayer != null)
                return false;
            if (
                _posterTexture == null
                || data.CanvasWidth != _posterTexture.width
                || data.CanvasHeight != _posterTexture.height
            )
            {
                BasisDebug.LogWarning("Animated image canvas does not match its static poster dimensions.", LogTag);
                return false;
            }

            BasisAnimatedImagePlayer player =
                gameObject.AddComponent<BasisAnimatedImagePlayer>();
            if (!player.Initialize(data, this, playbackEpochUtcTicks, false, reloadPayload))
            {
                Destroy(player);
                return false;
            }

            _animatedImagePlayer = player;
            return true;
        }

        internal void GetFrontFacePose(out Vector3 center, out Vector3 normal)
        {
            Transform faceTransform =
                _cardTransform != null ? _cardTransform : transform;
            faceTransform.GetPositionAndRotation(out center, out Quaternion rotation);
            normal = -(rotation * Vector3.forward);
        }

        internal Vector3 GetFrontFaceOcclusionSample(int sampleIndex, Vector3 frontNormal)
        {
            Transform faceTransform =
                _cardTransform != null ? _cardTransform : transform;
            float extent =
                BasisImagePickupSettings.AnimationFaceOcclusionSampleHalfExtent;
            Vector2 sample = sampleIndex switch
            {
                0 => Vector2.zero,
                1 => new Vector2(-extent, extent),
                2 => new Vector2(extent, extent),
                3 => new Vector2(-extent, -extent),
                4 => new Vector2(extent, -extent),
                _ => throw new ArgumentOutOfRangeException(nameof(sampleIndex)),
            };

            Vector3 point = faceTransform.TransformPoint(new Vector3(sample.x, sample.y, 0f));
            return point
                + frontNormal
                    * BasisImagePickupSettings.AnimationFaceOcclusionSurfaceOffsetMeters;
        }

        internal bool OwnsCollider(Collider collider)
        {
            if (collider == null)
                return false;
            Transform colliderTransform = collider.transform;
            return colliderTransform == transform
                || colliderTransform.IsChildOf(transform);
        }

        internal static void RegisterCollider(Collider collider, BasisImagePickupObject pickup)
        {
            if (collider == null || pickup == null)
                return;
            PickupByCollider[collider] = pickup;
        }

        internal static void UnregisterCollider(Collider collider)
        {
            if (collider != null)
                PickupByCollider.Remove(collider);
        }

        internal static bool TryGetPickup(Collider collider, out BasisImagePickupObject pickup)
        {
            pickup = null;
            return collider != null
                && PickupByCollider.TryGetValue(collider, out pickup)
                && pickup != null;
        }

        internal void SetAnimatedDisplayTexture(Texture texture, bool hasAnyAlpha, bool hasPartialAlpha)
        {
            if (_material == null || texture == null)
                return;
            SetDisplayTexture(texture);

            if (hasPartialAlpha)
                ConfigurePremultipliedTransparent(_material);
            else if (hasAnyAlpha)
                ConfigureCutout(_material);
            else
                ConfigureOpaque(_material);
        }

        internal void SetPosterDisplayTexture()
        {
            if (_material == null || _posterTexture == null)
                return;
            SetDisplayTexture(_posterTexture);
            if (_posterHasAlpha)
                ConfigureCutout(_material);
            else
                ConfigureOpaque(_material);
        }

        private void SetDisplayTexture(Texture texture)
        {
            if (_material.HasProperty(BaseMapId))
                _material.SetTexture(BaseMapId, texture);
            else
                _material.mainTexture = texture;
        }

        public void OnSavePressed()
        {
            if (_cleanPng == null || _cleanPng.Length == 0) return;
            try
            {
                string folder = SaveFolder();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, BasisImageSecurity.GenerateSafeFileName());
                File.WriteAllBytes(path, _cleanPng);
                BasisDebug.Log($"Image pickup saved to {path}", LogTag);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Image pickup save failed: {e.Message}", LogTag);
            }
        }

        public void OnDeletePressed()
        {
            if (!_deleteArmed)
            {
                _deleteArmed = true;
                if (DeleteLabel != null) DeleteLabel.text = "Confirm?";
                CancelInvoke(nameof(DisarmDelete));
                Invoke(nameof(DisarmDelete), 3f);
                return;
            }

            CancelInvoke(nameof(DisarmDelete));
            _deleteArmed = false;
            if (_managed) BasisImagePickupManager.RequestDespawn(ImageId);
        }

        private void DisarmDelete()
        {
            _deleteArmed = false;
            if (DeleteLabel != null) DeleteLabel.text = "Delete";
        }

        private static string SaveFolder()
        {
#if UNITY_STANDALONE_WIN
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Basis");
#else
            return Path.Combine(Application.persistentDataPath, "Basis");
#endif
        }

        private static Mesh _cardMesh;
        private static Material _sharedBackMaterial;
        private static Texture2D _placeholderTexture;

        /// <summary>
        /// The neutral fill shown while a poster decodes or downloads. Shared across every loading card and
        /// never assigned to <c>_posterTexture</c>, so <see cref="OnDestroy"/> cannot destroy it out from
        /// under the cards still using it.
        /// </summary>
        private static Texture2D GetPlaceholderTexture()
        {
            if (_placeholderTexture != null) return _placeholderTexture;

            _placeholderTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, false)
            {
                name = "BasisImagePickupPlaceholder",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            _placeholderTexture.SetPixel(0, 0, new Color(0.16f, 0.17f, 0.20f, 1f));
            _placeholderTexture.Apply(false, true);
            return _placeholderTexture;
        }

        private static Mesh GetCardMesh()
        {
            if (_cardMesh != null) return _cardMesh;

            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            if (!temp.TryGetComponent(out MeshFilter meshFilter))
            {
                DestroyImmediate(temp);
                throw new InvalidOperationException("Unity quad primitive did not provide a MeshFilter.");
            }
            Mesh quad = meshFilter.sharedMesh;
            Vector3[] verts = quad.vertices;
            Vector2[] uv = quad.uv;
            int[] front = quad.triangles;
            DestroyImmediate(temp);

            int frontIndexCount = front.Length;
            int[] back = new int[frontIndexCount];
            for (int i = 0; i < frontIndexCount; i += 3)
            {
                back[i] = front[i];
                back[i + 1] = front[i + 2];
                back[i + 2] = front[i + 1];
            }

            _cardMesh = new Mesh { name = "BasisImagePickupCard" };
            _cardMesh.vertices = verts;
            _cardMesh.uv = uv;
            _cardMesh.subMeshCount = 2;
            _cardMesh.SetTriangles(front, 0);
            _cardMesh.SetTriangles(back, 1);
            _cardMesh.RecalculateBounds();
            return _cardMesh;
        }

        private static Material GetSharedBackMaterial()
        {
            if (_sharedBackMaterial != null) return _sharedBackMaterial;
            _sharedBackMaterial = new Material(BundledContentHolder.Instance.UnlitUrpShader) { name = "BasisImagePickupBack" };
            if (_sharedBackMaterial.HasProperty(BaseColorId)) _sharedBackMaterial.SetColor(BaseColorId, Color.white);
            else _sharedBackMaterial.color = Color.white;
            return _sharedBackMaterial;
        }

        private static void ConfigureOpaque(Material material)
        {
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetFloat("_ZWrite", 1f);
            material.SetFloat("_AlphaClip", 0f);
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            material.SetOverrideTag("RenderType", "Opaque");
        }

        private static void ConfigureCutout(Material material)
        {
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetFloat("_ZWrite", 1f);
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", 0.5f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            material.SetOverrideTag("RenderType", "TransparentCutout");
        }

        private static void ConfigurePremultipliedTransparent(Material material)
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 1f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            // The animation canvas already stores premultiplied RGB, so the display shader
            // must not multiply by alpha again even if URP later adds this keyword to Unlit.
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");
        }

        private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);

        private void OnDestroy()
        {
            if (_managed)
            {
                _managed = false;
                BasisImagePickupManager.OnPickupDestroyed(this);
            }
            UnregisterCollider(_ownCollider);
            if (_material != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(_material);
                else
#endif
                    Destroy(_material);
            }
            if (_posterTexture != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(_posterTexture);
                else
#endif
                    Destroy(_posterTexture);
            }
        }
    }
}
