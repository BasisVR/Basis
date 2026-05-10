using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Creates a secondary camera that renders only the local avatar layer (layer 6)
    /// and displays the result on a HUD sprite near the microphone icon.
    /// Off by default; toggled via the AvatarPreview setting.
    /// All objects are created at runtime and cleaned up on disable/destroy.
    /// </summary>
    [System.Serializable]
    public class BasisLocalAvatarPreviewDriver
    {
        [Header("Render Texture")]
        public static int TextureWidth = 512;
        public static int TextureHeight = 1024;

        [Header("Preview Camera")]
        /// <summary>Distance in front of the player's face the camera is placed.</summary>
        public static float CameraDistance = 3f;
        /// <summary>Where on the body the camera looks at, as a fraction of player height (0 = feet, 1 = head).</summary>
        public static float BodyFocusHeight = 1f;
        public float CameraFieldOfView = 40f;

        [Header("HUD Display")]
        /// <summary>
        /// Normalized offset from the mic icon in frustum-relative units (fraction of half-frustum).
        /// X moves right, Y moves up. Stays at the same relative screen position on resize.
        /// </summary>
        public static Vector2 DisplayNormalizedOffset = new Vector2(0.15f, 0.03f);
        /// <summary>
        /// Local scale of the display sprite within ParentOfUI.
        /// </summary>
        public static float DisplayScale = 50f;

        [System.NonSerialized] public Camera PreviewCamera;
        [System.NonSerialized] public RenderTexture PreviewRT;

        private BasisLocalCameraDriver cachedDriver;
        private GameObject cameraGO;
        private GameObject displayGO;
        private SpriteRenderer displaySpriteRenderer;
        private MaterialPropertyBlock propertyBlock;
        private Texture2D dummyTexture;
        private readonly Vector3[] frustumCorners = new Vector3[4];
        private bool initialized;
        private bool active;

        /// <summary>
        /// Stores the driver reference and reads the saved setting.
        /// Only creates rendering objects if the setting is enabled.
        /// </summary>
        public void Initialize(BasisLocalCameraDriver cameraDriver)
        {
            // TODO: re-enable when avatar preview is finished
            return;
            cachedDriver = cameraDriver;

            // Apply the persisted setting
            bool enabled = BasisSettingsDefaults.AvatarPreview.RawValue;
            if (enabled)
            {
                CreateObjects();
            }
        }

        /// <summary>
        /// Enables or disables the avatar preview at runtime.
        /// Called by the settings module when the user toggles the setting.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (enabled && !active)
            {
                CreateObjects();
            }
            else if (!enabled && active)
            {
                DestroyObjects();
            }
        }

        private void CreateObjects()
        {
            if (initialized) return;
            if (cachedDriver == null) return;

            // --- Render Texture ---
            var desc = new RenderTextureDescriptor(TextureWidth, TextureHeight, RenderTextureFormat.ARGB32, 16)
            {
                msaaSamples = 2,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear
            };
            PreviewRT = new RenderTexture(desc) { name = "AvatarPreviewRT" };
            PreviewRT.Create();

            // --- Camera (world-space, not parented to anything) ---
            cameraGO = new GameObject("BasisAvatarPreviewCamera");
            PreviewCamera = cameraGO.AddComponent<Camera>();
            PreviewCamera.cullingMask = 1 << BasisLayerMapper.LocalAvatarLayer;
            PreviewCamera.targetTexture = PreviewRT;
            PreviewCamera.clearFlags = CameraClearFlags.SolidColor;
            PreviewCamera.backgroundColor = Color.clear;
            PreviewCamera.depth = -10;
            PreviewCamera.fieldOfView = CameraFieldOfView;
            PreviewCamera.nearClipPlane = 0.01f;
            PreviewCamera.farClipPlane = 10f;
            PreviewCamera.allowHDR = false;
            PreviewCamera.allowMSAA = true;
            PreviewCamera.useOcclusionCulling = false;

            var urpData = cameraGO.GetComponent<UniversalAdditionalCameraData>();
            if (urpData != null)
            {
                urpData.allowXRRendering = false;
            }

            // --- Display Sprite (child of ParentOfUI, next to the mic icon) ---
            displayGO = new GameObject("AvatarPreviewDisplay");
            displayGO.layer = LayerMask.NameToLayer("UI");
            displayGO.transform.SetParent(cachedDriver.ParentOfUI, false);
            displayGO.transform.localScale = new Vector3(DisplayScale, DisplayScale, 1f);
            displayGO.transform.localRotation = Quaternion.identity;

            displaySpriteRenderer = displayGO.AddComponent<SpriteRenderer>();
            displaySpriteRenderer.sharedMaterial = new Material(Shader.Find("Basis/UI/Main"));

            // Create a dummy sprite for mesh/UV generation (full 0-1 UVs)
            dummyTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            dummyTexture.SetPixel(0, 0, Color.white);
            dummyTexture.Apply();
            displaySpriteRenderer.sprite = Sprite.Create(dummyTexture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

            // MaterialPropertyBlock overrides SpriteRenderer's internal texture
            propertyBlock = new MaterialPropertyBlock();
            propertyBlock.SetTexture("_MainTex", PreviewRT);
            displaySpriteRenderer.SetPropertyBlock(propertyBlock);

            initialized = true;
            active = true;
        }

        private void DestroyObjects()
        {
            initialized = false;
            active = false;

            if (cameraGO != null) { Object.Destroy(cameraGO); cameraGO = null; }
            if (displayGO != null) { Object.Destroy(displayGO); displayGO = null; }

            if (PreviewRT != null)
            {
                PreviewRT.Release();
                Object.Destroy(PreviewRT);
                PreviewRT = null;
            }

            if (dummyTexture != null)
            {
                Object.Destroy(dummyTexture);
                dummyTexture = null;
            }

            displaySpriteRenderer = null;
            propertyBlock = null;
            PreviewCamera = null;
        }

        /// <summary>
        /// Positions the preview camera in front of the local avatar.
        /// The avatar head remains at normal scale during this camera's render
        /// because head-scaling is only applied to the main camera via entity ID checks.
        /// </summary>
        public void Simulate()
        {
            if (!active || PreviewCamera == null) return;
            if (!BasisLocalPlayer.PlayerReady || BasisLocalPlayer.Instance == null) return;
            if (!BasisLocalAvatarDriver.Mapping.Hashead || BasisLocalAvatarDriver.Mapping.head == null) return;

            Transform head = BasisLocalAvatarDriver.Mapping.head;

            // Flatten head forward to horizontal (ignore pitch)
            Vector3 flatForward = head.forward;
            flatForward.y = 0f;
            flatForward.Normalize();

            // Place camera in front of the avatar at body center height
            Vector3 feetPos = BasisLocalPlayer.Instance.transform.position;
            float bodyCenter = BasisHeightDriver.SelectedScaledPlayerHeight * BodyFocusHeight;
            Vector3 bodyTarget = feetPos + Vector3.up * bodyCenter;

            Vector3 cameraPos = bodyTarget + flatForward * CameraDistance;

            PreviewCamera.transform.SetPositionAndRotation(
                cameraPos,
                Quaternion.LookRotation(bodyTarget - cameraPos));

            // Reposition display relative to frustum so it stays in the same screen-relative spot
            if (displayGO != null && cachedDriver != null && cachedDriver.Camera != null)
            {
                Camera cam = cachedDriver.Camera;
                cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Left, frustumCorners);
                float halfW = (frustumCorners[2] - frustumCorners[1]).magnitude * 0.5f;
                float halfH = (frustumCorners[1] - frustumCorners[0]).magnitude * 0.5f;
                displayGO.transform.localPosition = new Vector3(
                    DisplayNormalizedOffset.x * halfW,
                    DisplayNormalizedOffset.y * halfH,
                    0f);
            }
        }

        /// <summary>
        /// Destroys all runtime objects. Safe to call multiple times.
        /// </summary>
        public void Cleanup()
        {
            DestroyObjects();
            cachedDriver = null;
        }
    }
}
