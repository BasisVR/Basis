using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Creates a secondary camera that renders only the local avatar layer (layer 6)
    /// and displays the result on a HUD sprite spanning the bottom-right half of the screen.
    /// Off by default; toggled via the AvatarPreview setting.
    /// All objects are created at runtime and cleaned up on disable/destroy.
    /// </summary>
    [System.Serializable]
    public class BasisLocalAvatarPreviewDriver
    {
        [Header("Render Texture")]
        public static int TextureWidth = 768;
        public static int TextureHeight = 1024;

        /// <summary>On-screen size as a multiple of the original (1.0 = bottom-right quadrant height).</summary>
        public static float DisplaySizeScale = 1.1f;

        [Header("Preview Camera")]
        /// <summary>Vertical FOV in degrees. Camera distance is auto-computed each frame to fit the target framing.</summary>
        public float CameraFieldOfView = 40f;

        public Vector3 VRDisplayAnchorOffset = new Vector3(0.2f, 0.15f, 0.5f);
        public float VRDisplayHeight = 0.2f;

        public enum PreviewFraming { FullBody, UpperBody, Face }
        public enum PreviewAnchor { BottomRight, BottomLeft, MiddleRight, MiddleLeft, TopRight, TopLeft }
        public enum PreviewRotation { LockFace, AllowYaw, AllowPitch, AllowAll }

        // Vertical framing target as fractions of player height: bottom of frame is just under
        // the hips, top is just above the head. Horizontal extent follows from the texture aspect.
        private const float FrameBottomFrac = 0.45f;
        private const float FrameTopFrac    = 1.15f;

        // When framing from avatar geometry: the frame bottom sits this fraction of the hip→crown
        // span below the hips bone, and the frame top this fraction above the crown.
        private const float HipDropFrac = 0.15f;
        private const float CrownHeadroomFrac = 0.08f;

        // Hand-immunity cap: the frame top never rises above the head bone by more than this
        // fraction of the hip→head-bone distance, so a raised arm — which also lands inside the
        // renderer bounds — can't blow out the shot. Generous for hair/most hats; very tall hats clip.
        private const float CrownCapRatio = 0.8f;
        private const float FullBodyFloorPadFrac = 0.03f;
        private const float FaceNeckDropFrac = 0.15f;
        private const float FaceNeckPadFrac = 0.05f;
        private const float FullBodyFallbackBottomFrac = -0.03f;
        private const float FaceFallbackBottomFrac = 0.78f;
        private const float MaxOrbitPitchDegrees = 80f;

        [System.NonSerialized] public Camera PreviewCamera;
        [System.NonSerialized] public RenderTexture PreviewRT;

        private BasisLocalCameraDriver cachedDriver;
        private GameObject cameraGO;
        private GameObject parentOfUIGO;
        private GameObject displayGO;
        private SpriteRenderer displaySpriteRenderer;
        private MaterialPropertyBlock propertyBlock;
        private Texture2D dummyTexture;
        private readonly Vector3[] frustumCorners = new Vector3[4];
        private bool initialized;
        private bool active;
        private Basis.BasisRenderRateLimiter renderRateLimiter;
        private bool layoutValid;
        private bool layoutVR;
        private float layoutFov;
        private float layoutAspect;
        private float layoutSizeScale;
        private float layoutVRHeight;
        private Vector3 layoutVROffset;
        private PreviewFraming framing = PreviewFraming.UpperBody;
        private PreviewAnchor anchor = PreviewAnchor.BottomRight;
        private PreviewRotation rotationMode = PreviewRotation.AllowAll;
        private Vector3 lastOrbitYaw = Vector3.forward;
        private Object crownAvatar;
        private float crownAboveHeadRatio;
        private float sizeScale = 1f;
        private float zoom = 1f;
        private float offsetX;
        private float offsetY;
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexSTId = Shader.PropertyToID("_MainTex_ST");
        private static readonly int CullModeId = Shader.PropertyToID("_CullMode");

        /// <summary>
        /// Stores the driver reference and reads the saved setting.
        /// Only creates rendering objects if the setting is enabled.
        /// </summary>
        public void Initialize(BasisLocalCameraDriver cameraDriver)
        {
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

        public void SetMirror(bool mirrored)
        {
            if (displaySpriteRenderer != null && propertyBlock != null)
            {
                propertyBlock.SetVector(MainTexSTId, mirrored ? new Vector4(-1f, 1f, 1f, 0f) : new Vector4(1f, 1f, 0f, 0f));
                displaySpriteRenderer.SetPropertyBlock(propertyBlock);
            }
        }

        public void SetFraming(string value)
        {
            framing = ParseFraming(value);
        }

        public void SetPosition(string value)
        {
            anchor = ParseAnchor(value);
            layoutValid = false;
        }

        public void SetRotation(string value)
        {
            rotationMode = ParseRotation(value);
        }

        public void SetSize(float value)
        {
            sizeScale = Mathf.Clamp(value, 0.25f, 4f);
            layoutValid = false;
        }

        public void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, 0.25f, 4f);
        }

        public void SetOffsetX(float value)
        {
            offsetX = value;
            layoutValid = false;
        }

        public void SetOffsetY(float value)
        {
            offsetY = value;
            layoutValid = false;
        }

        public static PreviewFraming ParseFraming(string value)
        {
            switch (value != null ? value.ToLowerInvariant() : string.Empty)
            {
                case "fullbody": return PreviewFraming.FullBody;
                case "face": return PreviewFraming.Face;
                default: return PreviewFraming.UpperBody;
            }
        }

        public static PreviewAnchor ParseAnchor(string value)
        {
            switch (value != null ? value.ToLowerInvariant() : string.Empty)
            {
                case "bottomleft": return PreviewAnchor.BottomLeft;
                case "middleright": return PreviewAnchor.MiddleRight;
                case "middleleft": return PreviewAnchor.MiddleLeft;
                case "topright": return PreviewAnchor.TopRight;
                case "topleft": return PreviewAnchor.TopLeft;
                default: return PreviewAnchor.BottomRight;
            }
        }

        public static PreviewRotation ParseRotation(string value)
        {
            switch (value != null ? value.ToLowerInvariant() : string.Empty)
            {
                case "lockface": return PreviewRotation.LockFace;
                case "allowyaw": return PreviewRotation.AllowYaw;
                case "allowpitch": return PreviewRotation.AllowPitch;
                default: return PreviewRotation.AllowAll;
            }
        }

        private void CreateObjects()
        {
            if (initialized) return;
            if (cachedDriver == null) return;

            // --- Render Texture ---
            var desc = new RenderTextureDescriptor(TextureWidth, TextureHeight, RenderTextureFormat.ARGB32, 16)
            {
                msaaSamples = BasisCameraTargetMsaa.Clamp(2),
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

            parentOfUIGO = new GameObject("AvatarPreviewParentOfUI");
            parentOfUIGO.layer = LayerMask.NameToLayer("UI");
            parentOfUIGO.transform.SetParent(cachedDriver.transform, false);

            displayGO = new GameObject("AvatarPreviewDisplay");
            displayGO.layer = LayerMask.NameToLayer("UI");
            displayGO.transform.SetParent(parentOfUIGO.transform, false);
            displayGO.transform.localRotation = Quaternion.identity;

            displaySpriteRenderer = displayGO.AddComponent<SpriteRenderer>();
            displaySpriteRenderer.sharedMaterial = new Material(Shader.Find("Basis/UI/Main"));
            displaySpriteRenderer.sharedMaterial.SetInt(CullModeId, (int)UnityEngine.Rendering.CullMode.Off);

            // Create a dummy sprite for mesh/UV generation (full 0-1 UVs)
            dummyTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            dummyTexture.SetPixel(0, 0, Color.white);
            dummyTexture.Apply();
            displaySpriteRenderer.sprite = Sprite.Create(dummyTexture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

            // MaterialPropertyBlock overrides SpriteRenderer's internal texture
            propertyBlock = new MaterialPropertyBlock();
            propertyBlock.SetTexture(MainTexId, PreviewRT);
            displaySpriteRenderer.SetPropertyBlock(propertyBlock);
            SetMirror(BasisSettingsDefaults.AvatarPreviewMirror.RawValue);

            framing = ParseFraming(BasisSettingsDefaults.AvatarPreviewFraming.RawValue);
            anchor = ParseAnchor(BasisSettingsDefaults.AvatarPreviewPosition.RawValue);
            rotationMode = ParseRotation(BasisSettingsDefaults.AvatarPreviewRotation.RawValue);
            sizeScale = Mathf.Clamp(BasisSettingsDefaults.AvatarPreviewSize.RawValue, 0.25f, 4f);
            zoom = Mathf.Clamp(BasisSettingsDefaults.AvatarPreviewZoom.RawValue, 0.25f, 4f);
            offsetX = BasisSettingsDefaults.AvatarPreviewOffsetX.RawValue;
            offsetY = BasisSettingsDefaults.AvatarPreviewOffsetY.RawValue;
            layoutValid = false;
            initialized = true;
            active = true;
        }

        private void DestroyObjects()
        {
            initialized = false;
            active = false;

            if (displaySpriteRenderer != null)
            {
                if (displaySpriteRenderer.sharedMaterial != null) Object.Destroy(displaySpriteRenderer.sharedMaterial);
                if (displaySpriteRenderer.sprite != null) Object.Destroy(displaySpriteRenderer.sprite);
            }

            if (cameraGO != null) { Object.Destroy(cameraGO); cameraGO = null; }
            if (displayGO != null) { Object.Destroy(displayGO); displayGO = null; }
            if (parentOfUIGO != null) { Object.Destroy(parentOfUIGO); parentOfUIGO = null; }

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
            if (!active)
            {
                return;
            }
            Vector3 feetPos = BasisLocalPlayer.Instance.transform.position;
            float playerHeight = BasisHeightDriver.SelectedScaledPlayerHeight;

            // Frame from just under the hips to just above the crown using the avatar's own
            // geometry, so the shot fits each avatar's proportions and re-fits when swapped.
            // Falls back to player-height fractions while the avatar/bones aren't ready yet.
            if (!TryGetAvatarFraming(feetPos, playerHeight, out Vector3 frameCenter, out float frameBottomY, out float frameTopY))
            {
                frameCenter = feetPos;
                float bottomFrac = framing == PreviewFraming.FullBody ? FullBodyFallbackBottomFrac : framing == PreviewFraming.Face ? FaceFallbackBottomFrac : FrameBottomFrac;
                frameBottomY = feetPos.y + playerHeight * bottomFrac;
                frameTopY = feetPos.y + playerHeight * FrameTopFrac;
            }

            float verticalSpan = frameTopY - frameBottomY;
            frameCenter.y = (frameBottomY + frameTopY) * 0.5f;

            // Distance is bound by the vertical target (hips-to-above-head). Horizontal extent is
            // whatever the texture aspect allows at that distance — with the portrait texture this
            // is ~0.3× player height wide, enough for the body but not spread arms.
            float aspect = (float)TextureWidth / (float)TextureHeight;
            float halfFovTan = Mathf.Tan(CameraFieldOfView * 0.5f * Mathf.Deg2Rad);
            if (halfFovTan < 1e-4f) halfFovTan = 1e-4f;
            float cameraDistance = (verticalSpan * 0.5f) / (halfFovTan * zoom);

            Vector3 forward = OrbitDirection();
            Vector3 cameraPos = frameCenter + forward * cameraDistance;

            PreviewCamera.transform.SetPositionAndRotation(
                cameraPos,
                Quaternion.LookRotation(frameCenter - cameraPos));

            UpdateDisplayLayout(aspect);

            PreviewCamera.enabled = renderRateLimiter.AllowThisFrame(
                Time.unscaledDeltaTime, BasisSettingsDefaults.AvatarPreviewRenderHz.RawValue,
                BasisSettingsDefaults.LimitAvatarPreviewRate.RawValue);
        }

        private Vector3 OrbitDirection()
        {
            bool followYaw = rotationMode == PreviewRotation.LockFace || rotationMode == PreviewRotation.AllowPitch;
            bool followPitch = rotationMode == PreviewRotation.LockFace || rotationMode == PreviewRotation.AllowYaw;
            Vector3 headForward = HeadForward();
            Vector3 yawForward = Vector3.zero;
            if (!followYaw)
            {
                var hips = BasisLocalBoneDriver.HipsControl;
                if (hips != null && hips.HasStore)
                {
                    yawForward = hips.OutgoingWorldData.rotation * Vector3.forward;
                    yawForward.y = 0f;
                }
            }
            if (yawForward.sqrMagnitude < 1e-6f)
            {
                yawForward = headForward;
                yawForward.y = 0f;
            }
            if (yawForward.sqrMagnitude < 1e-6f)
            {
                yawForward = lastOrbitYaw;
            }
            yawForward.Normalize();
            lastOrbitYaw = yawForward;
            if (!followPitch)
            {
                return yawForward;
            }
            float pitch = Mathf.Asin(Mathf.Clamp(headForward.y, -1f, 1f));
            float maxPitch = MaxOrbitPitchDegrees * Mathf.Deg2Rad;
            pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
            return yawForward * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
        }

        private static Vector3 HeadForward()
        {
            var head = BasisLocalBoneDriver.HeadControl;
            if (head != null && head.HasStore)
            {
                return head.OutgoingWorldData.rotation * Vector3.forward;
            }
            return BasisLocalCameraDriver.HeadForward();
        }

        private void UpdateDisplayLayout(float aspect)
        {
            if (displayGO == null || parentOfUIGO == null || cachedDriver == null || cachedDriver.Camera == null)
            {
                return;
            }
            Camera cam = cachedDriver.Camera;
            bool inVR = cachedDriver.CameraData != null && cachedDriver.CameraData.allowXRRendering;
            float fov = cam.fieldOfView;
            float camAspect = cam.aspect;
            if (layoutValid && layoutVR == inVR && layoutFov == fov && layoutAspect == camAspect && layoutSizeScale == DisplaySizeScale && layoutVRHeight == VRDisplayHeight && layoutVROffset == VRDisplayAnchorOffset)
            {
                return;
            }
            layoutValid = true;
            layoutVR = inVR;
            layoutFov = fov;
            layoutAspect = camAspect;
            layoutSizeScale = DisplaySizeScale;
            layoutVRHeight = VRDisplayHeight;
            layoutVROffset = VRDisplayAnchorOffset;

            float sx = anchor == PreviewAnchor.BottomLeft || anchor == PreviewAnchor.MiddleLeft || anchor == PreviewAnchor.TopLeft ? -1f : 1f;
            float sy = anchor == PreviewAnchor.TopLeft || anchor == PreviewAnchor.TopRight ? 1f : anchor == PreviewAnchor.MiddleLeft || anchor == PreviewAnchor.MiddleRight ? 0f : -1f;
            float displayHeight;
            Vector3 displayLocal;
            Quaternion displayRotation;
            if (inVR)
            {
                displayHeight = VRDisplayHeight * sizeScale;
                displayLocal = new Vector3(sx * VRDisplayAnchorOffset.x + offsetX, sy * VRDisplayAnchorOffset.y + offsetY, VRDisplayAnchorOffset.z);
                displayRotation = displayLocal.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(displayLocal) : Quaternion.identity;
            }
            else
            {
                cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Mono, frustumCorners);
                float halfW = (frustumCorners[2] - frustumCorners[1]).magnitude * 0.5f;
                float halfH = (frustumCorners[1] - frustumCorners[0]).magnitude * 0.5f;
                displayHeight = halfH * DisplaySizeScale * sizeScale;
                float displayWidth = displayHeight * aspect;

                // Anchor the sprite's edges to the matching frustum edges, then apply the user's nudge.
                displayLocal = new Vector3(sx * (halfW - displayWidth * 0.5f) + offsetX * 2f * halfW, sy * (halfH - displayHeight * 0.5f) + offsetY * 2f * halfH, 1f);
                displayRotation = Quaternion.identity;
            }
            parentOfUIGO.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            displayGO.transform.SetLocalPositionAndRotation(displayLocal, displayRotation);
            displayGO.transform.localScale = new Vector3(displayHeight * aspect, displayHeight, 1f);
        }

        /// <summary>
        /// Computes the world-space frame centre and vertical bounds from the avatar's hips bone and
        /// the top of its renderer bounds (capped to a head-bone-relative ceiling so raised arms don't
        /// blow the framing out), so tall heads fit and the framing re-fits on avatar swap. Returns
        /// false when the avatar isn't ready.
        /// </summary>
        private bool TryGetAvatarFraming(Vector3 feetPos, float playerHeight, out Vector3 frameCenter, out float bottomY, out float topY)
        {
            frameCenter = feetPos;
            bottomY = 0f;
            topY = 0f;

            var localPlayer = BasisLocalPlayer.Instance;
            var avatar = localPlayer != null ? localPlayer.BasisAvatar : null;
            if (avatar == null)
            {
                return false;
            }

            var mapping = BasisLocalAvatarDriver.Mapping;
            bool hasHips = mapping != null && mapping.HasHips && mapping.Hips != null;
            bool hasHead = mapping != null && mapping.Hashead && mapping.head != null;
            bool hasNeck = mapping != null && mapping.Hasneck && mapping.neck != null;
            float hipY = feetPos.y + playerHeight * FrameBottomFrac;
            if (hasHips)
            {
                Vector3 hipsPosition = mapping.Hips.position;
                hipY = hipsPosition.y;
                frameCenter.x = hipsPosition.x;
                frameCenter.z = hipsPosition.z;
            }
            Vector3 headPosition = hasHead ? mapping.head.position : Vector3.zero;
            float headReference = hasHead && hasHips ? headPosition.y - hipY : 0f;
            if (headReference < 1e-3f)
            {
                headReference = playerHeight * 0.5f;
            }

            float crownY;
            if (hasHead && crownAvatar == avatar)
            {
                crownY = headPosition.y + crownAboveHeadRatio * playerHeight;
            }
            else
            {
                if (!TryGetRenderersTop(avatar.SkinnedMeshRenderers, out crownY))
                {
                    return false;
                }
                // Renderer bounds give a tight fit to the real crown (hair/ears/hat), but a raised arm
                // also lands inside them — so clamp the top to one head-height above the head bone.
                if (hasHead)
                {
                    float ceiling = headPosition.y + headReference * CrownCapRatio;
                    if (crownY > ceiling)
                    {
                        crownY = ceiling;
                    }
                    if (crownY < headPosition.y)
                    {
                        crownY = headPosition.y;
                    }
                    if (playerHeight > 1e-3f)
                    {
                        crownAvatar = avatar;
                        crownAboveHeadRatio = (crownY - headPosition.y) / playerHeight;
                    }
                }
            }

            float span = crownY - hipY;
            if (span <= 1e-3f)
            {
                return false;
            }

            switch (framing)
            {
                case PreviewFraming.FullBody:
                {
                    float fullSpan = crownY - feetPos.y;
                    if (fullSpan <= 1e-3f)
                    {
                        return false;
                    }
                    bottomY = feetPos.y - fullSpan * FullBodyFloorPadFrac;
                    topY = crownY + fullSpan * CrownHeadroomFrac;
                    return true;
                }
                case PreviewFraming.Face:
                {
                    if (!hasHead)
                    {
                        return false;
                    }
                    float neckY = hasNeck ? mapping.neck.position.y : headPosition.y - span * FaceNeckDropFrac;
                    float faceSpan = crownY - neckY;
                    if (faceSpan <= 1e-3f)
                    {
                        return false;
                    }
                    frameCenter.x = headPosition.x;
                    frameCenter.z = headPosition.z;
                    bottomY = neckY - faceSpan * FaceNeckPadFrac;
                    topY = crownY + faceSpan * CrownHeadroomFrac;
                    return true;
                }
                default:
                    bottomY = hipY - span * HipDropFrac;
                    topY = crownY + span * CrownHeadroomFrac;
                    return true;
            }
        }

        private static bool TryGetRenderersTop(SkinnedMeshRenderer[] renderers, out float boundsTop)
        {
            boundsTop = float.NegativeInfinity;
            if (renderers == null)
            {
                return false;
            }
            bool found = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                float rendererTop = renderer.bounds.max.y;
                if (rendererTop > boundsTop)
                {
                    boundsTop = rendererTop;
                }
                found = true;
            }
            return found;
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
