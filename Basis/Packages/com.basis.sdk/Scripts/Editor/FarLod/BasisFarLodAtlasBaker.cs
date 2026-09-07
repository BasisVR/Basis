using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Bakes the avatar's rendered appearance into the far avatar atlas. The avatar is rendered with
/// its real materials from a ring of orthographic body views plus tightly-framed close-up
/// passes for hands, feet and head (small parts are only a handful of pixels in a whole-body
/// frame — the close-ups are what keep them legible). Every view is captured twice (black and
/// white background) so coverage comes from difference matting — robust against arbitrary
/// shaders that write no meaningful alpha — and colors are un-premultiplied by that coverage
/// so silhouette edges don't keep background darkening.
///
/// Scene lighting is overridden with flat white ambient during capture so the atlas stores an
/// unlit/albedo-like response; the runtime far avatar shader re-lights it from the world's
/// ambient probe and main light.
///
/// Each atlas texel is projected into the best-facing valid views (close-ups outrank body
/// views), occlusion-tested with a raycast against a temporary collider of the decimated mesh,
/// sampled bilinearly with coverage weighting, dilated, mipped, and finally compressed to BC1
/// (desktop) and ASTC 6x6 (mobile) payloads.
/// </summary>
public static class BasisFarLodAtlasBaker
{
    private const int DilatePasses = 16;
    private const float MinCoverage = 0.4f;
    private const float FallbackCoverage = 0.2f;
    private const int OcclusionLayer = 2; // Ignore Raycast: invisible to default queries, targetable by mask
    private const float RegionScoreBias = 0.5f;
    private const float MinViewFacing = 0.25f;
    private const float RegionMinFacing = 0.35f;

    // Set per bake by DetectSampleFlip: true when captured rows come back top-down on this
    // platform/pipeline, so sampling must mirror Y to line up with the projection math.
    private static bool sFlipSampleY;

    // Set per bake by DetectCaptureMode. Manual offscreen camera rendering is pipeline-
    // dependent: some URP configurations render nothing for Camera.Render(), others land the
    // wrong buffer for render requests, and MSAA targets (used to defeat forced depth
    // priming) don't survive every environment either — so a ladder of candidate modes is
    // probed with real geometry and the first one that provably renders wins.
    private static bool sUseRenderRequest;
    private static bool sUseMsaaTargets;

    /// <summary>A body part that deserves its own close-up capture set (bounds in root space).</summary>
    public struct RegionOfInterest
    {
        public string Name;
        public Bounds RootBounds;
    }

    /// <summary>
    /// Part-isolation data: the pre-decimation snapshot geometry, vertex-colored by body group
    /// (torso/head/arms/legs), rendered per view as an id mask. A texel then only accepts
    /// capture pixels of its own group — side views can't paint the torso onto an arm that
    /// happens to sit in front of it.
    /// </summary>
    public struct BakeMask
    {
        public Vector3[] Positions;  // root space, pre-decimation
        public Color32[] Colors;     // EncodeGroup in r
        public int[] Indices;
        public byte[] TexelVertexGroup; // per decimated vertex
        public byte[] TexelHidden;      // per decimated vertex: 1 = exterior-invisible remnant

        public bool IsValid => Positions != null && Colors != null && Indices != null &&
                               Positions.Length > 0 && Colors.Length == Positions.Length && Indices.Length >= 3;
    }

    public static byte EncodeGroup(byte group)
    {
        return (byte)(40 + group * 40);
    }

    private static byte DecodeGroup(byte encoded)
    {
        return encoded < 20 ? (byte)255 : (byte)Mathf.Clamp(Mathf.RoundToInt((encoded - 40f) / 40f), 0, 5);
    }

    private struct CaptureView
    {
        public Vector3 DirectionWorld;
        public Matrix4x4 WorldToPixel;
        public Color32[] Pixels; // rgb = un-premultiplied color, a = coverage
        public byte[] GroupIds;  // per pixel body group (255 = background); null when no mask
        public ushort[] Depth16; // per pixel normalized [near,far] depth; null when no mask
        public Vector3 CameraPositionWorld;
        public float DepthNear;
        public float DepthFar;
        public float DepthToleranceMeters;
        public int Size;
        public bool IsRegion;
        public Bounds ValidBoundsRoot; // region views only serve texels inside this
    }

    public static BasisFarLodPayload.FarLodTexture[] Bake(Transform root, Mesh decimatedMesh,
        Vector3[] positions, Vector3[] normals, Vector2[] uv, int[] indices, int atlasSize, int captureSize,
        RegionOfInterest[] regions = null, BakeMask mask = default)
    {
        Bounds rootBounds = new Bounds(positions[0], Vector3.zero);
        for (int i = 1; i < positions.Length; i++)
        {
            rootBounds.Encapsulate(positions[i]);
        }
        Matrix4x4 rootToWorld = root.localToWorldMatrix;
        Vector3 centerWorld = rootToWorld.MultiplyPoint3x4(rootBounds.center);
        float radius = 0.001f;
        Vector3 extents = rootBounds.extents;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 cornerLocal = rootBounds.center + Vector3.Scale(extents, new Vector3(
                (corner & 1) == 0 ? -1f : 1f,
                (corner & 2) == 0 ? -1f : 1f,
                (corner & 4) == 0 ? -1f : 1f));
            radius = Mathf.Max(radius, (rootToWorld.MultiplyPoint3x4(cornerLocal) - centerWorld).magnitude);
        }

        // Close-up passes scale with the atlas so hand/head charts stay source-fed at 1024/2048.
        int regionCaptureSize = Mathf.Clamp(atlasSize / 2, 512, 1024);

        Quaternion rootRotation = root.rotation;
        LightingScope lighting = LightingScope.Push();
        // Forced depth priming hides any shader whose DepthOnly pass is missing or broken
        // from manual captures (the whole avatar, with common toon shaders). MSAA targets
        // dodge priming where they work, but not every live-editor session cooperates —
        // switching priming off at the source for the bake's duration makes the plain 1x
        // path render everything. Reflection-based so this assembly needs no URP reference;
        // a no-op on pipelines without the setting.
        DepthPrimingScope depthPriming = DepthPrimingScope.Push();
        GameObject cameraObject = null;
        GameObject colliderObject = null;
        GameObject maskObject = null;
        Mesh maskMesh = null;
        Material maskMaterial = null;
        RenderTexture bodyTexture = null;
        RenderTexture regionTexture = null;
        RenderTexture maskBodyTexture = null;
        RenderTexture maskRegionTexture = null;
        Texture2D bodyReadback = null;
        Texture2D regionReadback = null;
        Renderer[] avatarRenderers = null;
        bool[] avatarRendererStates = null;
        try
        {
            cameraObject = new GameObject("FarLodBakeCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.cullingMask = ~0;
            camera.allowHDR = false;
            // MSAA must stay allowed or URP downgrades the 4x capture targets to 1 sample,
            // re-enabling depth priming — see GetCaptureTarget.
            camera.allowMSAA = true;
            camera.useOcclusionCulling = false;
            // Every capture target is square; pinning the aspect keeps the projection math
            // identical whether the pipeline renders via targetTexture or a request destination.
            camera.aspect = 1f;

            bodyReadback = NewReadback(captureSize);
            regionReadback = NewReadback(regionCaptureSize);

            if (!DetectCaptureMode(camera, bodyReadback, captureSize))
            {
                // Errors, not warnings, for every skip: the far avatar silently vanishing from a
                // bundle must surface in the same red stream creators actually read.
                BasisFarLodGenerator.LastFailureReason = "no capture path passed the geometry probe (render requests and Camera.Render, MSAA and 1x targets)";
                Debug.LogError("[FarAvatar] No capture path renders correctly on this pipeline (render requests and Camera.Render, with both MSAA and 1x targets, all failed the geometry probe). Skipping the far avatar instead of baking garbage.");
                return null;
            }
            Debug.Log($"[FarAvatar] Capture mode: {(sUseRenderRequest ? "render request" : "Camera.Render")} into {(sUseMsaaTargets ? "4x MSAA" : "1x")} targets.");

            MeasureBrightnessResponse(camera, centerWorld, rootRotation, radius);

            // MSAA beauty targets are preferred: URP disables depth priming for MSAA targets,
            // restoring the plain forward path for arbitrary avatar shaders (under a forced-
            // depth-priming renderer, shaders whose DepthOnly pass is missing or broken are
            // otherwise invisible in manual captures — probe-verified on stock URP 17.4). The
            // resolve on readback is plain anti-aliasing, harmless for beauty. The 1x rungs
            // exist for environments where MSAA capture itself misbehaves; the coverage guard
            // below still aborts if priming empties them. Mask targets are always 1x because
            // sample-averaging would corrupt the encoded ids/depth; the part-id shader carries
            // its own DepthOnly pass, so priming is satisfied there.
            bodyTexture = GetCaptureTarget(captureSize, sUseMsaaTargets);
            regionTexture = GetCaptureTarget(regionCaptureSize, sUseMsaaTargets);

            if (mask.IsValid)
            {
                Shader maskShader = Shader.Find("Hidden/BasisFarLodPartId");
                if (maskShader != null)
                {
                    maskMesh = new Mesh
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                    };
                    maskMesh.SetVertices(mask.Positions);
                    maskMesh.SetColors(mask.Colors);
                    maskMesh.SetTriangles(mask.Indices, 0);
                    maskMesh.RecalculateBounds();
                    maskMaterial = new Material(maskShader) { hideFlags = HideFlags.HideAndDontSave };
                    maskObject = new GameObject("FarLodBakeMask") { hideFlags = HideFlags.HideAndDontSave };
                    maskObject.transform.SetPositionAndRotation(root.position, root.rotation);
                    maskObject.transform.localScale = root.lossyScale;
                    maskObject.AddComponent<MeshFilter>().sharedMesh = maskMesh;
                    MeshRenderer maskRenderer = maskObject.AddComponent<MeshRenderer>();
                    maskRenderer.sharedMaterial = maskMaterial;
                    maskRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    maskObject.SetActive(false);
                    avatarRenderers = root.GetComponentsInChildren<Renderer>(false);
                    avatarRendererStates = new bool[avatarRenderers.Length];
                    // Mask bytes must round-trip exactly — render into linear targets so no
                    // sRGB encode remaps the group ids.
                    maskBodyTexture = RenderTexture.GetTemporary(captureSize, captureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    maskRegionTexture = RenderTexture.GetTemporary(regionCaptureSize, regionCaptureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                }
                else
                {
                    Debug.LogWarning("[FarAvatar] Hidden/BasisFarLodPartId shader missing — baking without part isolation.");
                }
            }

            List<CaptureView> views = new List<CaptureView>(64);

            // Whole-body ring: equator, upper ring, lower ring (palms face down in T-pose —
            // without under-views they only ever see the single bottom capture), poles.
            // Views whose raw reads don't show the requested backgrounds carry a wrong buffer
            // (not this camera's render) and are dropped; a mostly-poisoned set aborts.
            Vector3[] bodyDirections = BuildBodyViewDirections();
            int staleBodyViews = 0;
            for (int v = 0; v < bodyDirections.Length; v++)
            {
                Vector3 directionWorld = (rootRotation * bodyDirections[v]).normalized;
                CaptureView view = CaptureOne(camera, bodyTexture, bodyReadback, captureSize,
                    centerWorld, directionWorld, rootRotation, radius, isRegion: false, default,
                    maskObject, maskBodyTexture, avatarRenderers, avatarRendererStates, radius, out bool backgroundFresh);
                if (backgroundFresh)
                {
                    views.Add(view);
                }
                else
                {
                    staleBodyViews++;
                }
            }
            if (staleBodyViews > 0)
            {
                Debug.LogWarning($"[FarAvatar] Dropped {staleBodyViews} of {bodyDirections.Length} body captures whose readback did not contain this camera's render.");
            }
            if (bodyDirections.Length - staleBodyViews < 6)
            {
                BasisFarLodGenerator.LastFailureReason = $"beauty captures failed freshness validation ({staleBodyViews} of {bodyDirections.Length} body views poisoned, capture mode {(sUseRenderRequest ? "request" : "render")}+{(sUseMsaaTargets ? "4x" : "1x")})";
                Debug.LogError($"[FarAvatar] Most body captures contained a wrong buffer ({staleBodyViews} of {bodyDirections.Length}) — skipping the far avatar instead of baking garbage.");
                return null;
            }

            // Close-up passes.
            if (regions != null)
            {
                Vector3[] regionDirections =
                {
                    Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
                };
                for (int r = 0; r < regions.Length; r++)
                {
                    Bounds region = regions[r].RootBounds;
                    Vector3 regionCenterWorld = rootToWorld.MultiplyPoint3x4(region.center);
                    float regionRadius = Mathf.Max(rootToWorld.MultiplyVector(region.extents).magnitude, 0.01f);
                    Bounds valid = region;
                    valid.Expand(region.size.magnitude * 0.1f + 0.005f);
                    for (int d = 0; d < regionDirections.Length; d++)
                    {
                        Vector3 directionWorld = (rootRotation * regionDirections[d]).normalized;
                        // Close-ups can legitimately fill their corners with avatar, so the
                        // freshness verdict is ignored — the body-view gate above already
                        // proved or condemned this capture mode.
                        views.Add(CaptureOne(camera, regionTexture, regionReadback, regionCaptureSize,
                            regionCenterWorld, directionWorld, rootRotation, regionRadius, isRegion: true, valid,
                            maskObject, maskRegionTexture, avatarRenderers, avatarRendererStates, radius, out _));
                    }
                }
            }
            camera.targetTexture = null;

            // Sanity: if the captures show (almost) no avatar, the camera rendered nothing —
            // the atlas would be flat gray. A fraction threshold rather than exactly-zero:
            // stray noise pixels must not sneak an empty bake through (the avatar fills a
            // large part of every body view, so real captures sit far above this).
            long coveredPixels = 0;
            long sampledPixels = 0;
            for (int v = 0; v < views.Count; v++)
            {
                Color32[] pixels = views[v].Pixels;
                for (int p = 0; p < pixels.Length; p += 7)
                {
                    sampledPixels++;
                    if (pixels[p].a > 128)
                    {
                        coveredPixels++;
                    }
                }
            }
            if (coveredPixels < sampledPixels / 200)
            {
                // Shipping a flat gray imposter is worse than shipping none — abort so the
                // bundle builds without a far avatar and the console says why.
                BasisFarLodGenerator.LastFailureReason = $"captures show (almost) no avatar pixels: {coveredPixels} of {sampledPixels} samples covered, capture mode {(sUseRenderRequest ? "request" : "render")}+{(sUseMsaaTargets ? "4x" : "1x")}";
                Debug.LogError($"[FarAvatar] Captures show (almost) no avatar pixels ({coveredPixels} of {sampledPixels} samples covered, capture mode {(sUseRenderRequest ? "request" : "render")}+{(sUseMsaaTargets ? "4x" : "1x")}) — skipping the far avatar instead of baking a flat atlas. Check that the avatar's renderers are enabled and visible during build.");
                return null;
            }

            colliderObject = new GameObject("FarLodBakeCollider") { hideFlags = HideFlags.HideAndDontSave, layer = OcclusionLayer };
            colliderObject.transform.SetPositionAndRotation(root.position, root.rotation);
            colliderObject.transform.localScale = root.lossyScale;
            MeshCollider collider = colliderObject.AddComponent<MeshCollider>();
            collider.sharedMesh = decimatedMesh;
            Physics.SyncTransforms();

            float[] vertexAo = ComputeVertexAo(positions, normals, indices, rootToWorld, rootRotation, radius);

            sFlipSampleY = DetectSampleFlip(views, rootToWorld, rootRotation, positions, normals);
            if (sFlipSampleY)
            {
                Debug.LogWarning("[FarAvatar] Capture rows came back top-down on this pipeline — sampling with mirrored Y.");
            }

            Color32[] atlas = ProjectAtlas(views, rootToWorld, rootRotation, positions, normals, uv, indices, atlasSize, radius, mask.TexelVertexGroup, mask.TexelHidden, vertexAo);
            if (atlas == null)
            {
                return null;
            }
            return CompressAtlas(atlas, atlasSize);
        }
        finally
        {
            if (avatarRenderers != null && avatarRendererStates != null)
            {
                for (int r = 0; r < avatarRenderers.Length; r++)
                {
                    if (avatarRendererStates[r] && avatarRenderers[r] != null && !avatarRenderers[r].enabled)
                    {
                        avatarRenderers[r].enabled = true;
                    }
                }
            }
            if (maskObject != null)
            {
                Object.DestroyImmediate(maskObject);
            }
            if (maskMesh != null)
            {
                Object.DestroyImmediate(maskMesh);
            }
            if (maskMaterial != null)
            {
                Object.DestroyImmediate(maskMaterial);
            }
            if (maskBodyTexture != null)
            {
                RenderTexture.ReleaseTemporary(maskBodyTexture);
            }
            if (maskRegionTexture != null)
            {
                RenderTexture.ReleaseTemporary(maskRegionTexture);
            }
            if (bodyTexture != null)
            {
                RenderTexture.ReleaseTemporary(bodyTexture);
            }
            if (regionTexture != null)
            {
                RenderTexture.ReleaseTemporary(regionTexture);
            }
            if (bodyReadback != null)
            {
                Object.DestroyImmediate(bodyReadback);
            }
            if (regionReadback != null)
            {
                Object.DestroyImmediate(regionReadback);
            }
            if (cameraObject != null)
            {
                Object.DestroyImmediate(cameraObject);
            }
            if (colliderObject != null)
            {
                Object.DestroyImmediate(colliderObject);
            }
            depthPriming.Pop();
            lighting.Pop();
        }
    }

    /// <summary>
    /// Temporarily sets every renderer data of the active URP asset to DepthPrimingMode
    /// Disabled and restores the original values afterwards. Purely in-memory reflection over
    /// the serialized fields (nothing is saved to disk); SetDirty makes URP rebuild its
    /// renderers so the change actually applies to the bake's manual renders.
    /// </summary>
    private struct DepthPrimingScope
    {
        private List<(UnityEngine.Object rendererData, System.Reflection.FieldInfo field, int originalMode)> _changed;

        public static DepthPrimingScope Push()
        {
            DepthPrimingScope scope = new DepthPrimingScope
            {
                _changed = new List<(UnityEngine.Object, System.Reflection.FieldInfo, int)>(),
            };
            try
            {
                UnityEngine.Rendering.RenderPipelineAsset pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                if (pipeline == null)
                {
                    return scope;
                }
                System.Reflection.FieldInfo listField = pipeline.GetType().GetField("m_RendererDataList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (listField == null || listField.GetValue(pipeline) is not System.Collections.IEnumerable rendererDatas)
                {
                    return scope;
                }
                foreach (object entry in rendererDatas)
                {
                    if (entry is not UnityEngine.Object rendererData || rendererData == null)
                    {
                        continue;
                    }
                    System.Reflection.FieldInfo modeField = rendererData.GetType().GetField("m_DepthPrimingMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (modeField == null)
                    {
                        continue;
                    }
                    int original = Convert.ToInt32(modeField.GetValue(rendererData));
                    if (original == 0)
                    {
                        continue;
                    }
                    modeField.SetValue(rendererData, 0); // DepthPrimingMode.Disabled
                    InvokeSetDirty(rendererData);
                    scope._changed.Add((rendererData, modeField, original));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FarAvatar] Could not adjust depth priming for the bake: {ex.Message}");
            }
            if (scope._changed.Count > 0)
            {
                Debug.Log($"[FarAvatar] Depth priming disabled on {scope._changed.Count} renderer(s) for the bake.");
            }
            return scope;
        }

        public void Pop()
        {
            if (_changed == null)
            {
                return;
            }
            for (int Index = 0; Index < _changed.Count; Index++)
            {
                (UnityEngine.Object rendererData, System.Reflection.FieldInfo field, int originalMode) = _changed[Index];
                if (rendererData != null)
                {
                    field.SetValue(rendererData, originalMode);
                    InvokeSetDirty(rendererData);
                }
            }
        }

        private static void InvokeSetDirty(UnityEngine.Object rendererData)
        {
            System.Reflection.MethodInfo setDirty = rendererData.GetType().GetMethod("SetDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            setDirty?.Invoke(rendererData, null);
        }
    }

    private static Texture2D NewReadback(int size)
    {
        return new Texture2D(size, size, TextureFormat.RGBA32, false, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    private static RenderTexture GetCaptureTarget(int size, bool msaa)
    {
        RenderTextureDescriptor descriptor = new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32, 24)
        {
            msaaSamples = msaa ? 4 : 1,
        };
        return RenderTexture.GetTemporary(descriptor);
    }

    private static Vector3[] BuildBodyViewDirections()
    {
        List<Vector3> directions = new List<Vector3>(18);
        for (int yaw = 0; yaw < 360; yaw += 45)
        {
            float radians = yaw * Mathf.Deg2Rad;
            directions.Add(new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)));
        }
        for (int yaw = 0; yaw < 360; yaw += 90)
        {
            float radians = yaw * Mathf.Deg2Rad;
            directions.Add(new Vector3(Mathf.Sin(radians), 0.84f, Mathf.Cos(radians)).normalized);
        }
        for (int yaw = 45; yaw < 360; yaw += 90)
        {
            float radians = yaw * Mathf.Deg2Rad;
            directions.Add(new Vector3(Mathf.Sin(radians), -0.84f, Mathf.Cos(radians)).normalized);
        }
        directions.Add(Vector3.up);
        directions.Add(Vector3.down);
        return directions.ToArray();
    }

    private static CaptureView CaptureOne(Camera camera, RenderTexture target, Texture2D readback, int size,
        Vector3 centerWorld, Vector3 directionWorld, Quaternion rootRotation, float frameRadius, bool isRegion, Bounds validBoundsRoot,
        GameObject maskObject, RenderTexture maskTarget, Renderer[] avatarRenderers, bool[] avatarRendererStates,
        float clearanceRadius, out bool backgroundFresh)
    {
        Vector3 up = Mathf.Abs(Vector3.Dot(directionWorld, Vector3.up)) > 0.95f ? rootRotation * Vector3.forward : Vector3.up;
        camera.targetTexture = target;
        camera.orthographicSize = frameRadius * (isRegion ? 1.1f : 1f);
        if (isRegion)
        {
            // Ortho framing is distance-independent, so sit OUTSIDE the whole body (a below-head
            // camera at 2x head radius is inside the chest — the mask renders backface-culled
            // while double-sided beauty materials don't, letting shirt interiors pass as "head").
            // The near plane starts just before the region, clipping every occluder in front of
            // it out of beauty, mask and depth alike.
            float cameraDistance = clearanceRadius * 2f + frameRadius;
            camera.nearClipPlane = Mathf.Max(0.01f, cameraDistance - frameRadius * 2.5f);
            camera.farClipPlane = cameraDistance + frameRadius * 2.5f;
            camera.transform.SetPositionAndRotation(centerWorld - directionWorld * cameraDistance, Quaternion.LookRotation(directionWorld, up));
        }
        else
        {
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = frameRadius * 4f;
            camera.transform.SetPositionAndRotation(centerWorld - directionWorld * (frameRadius * 2f), Quaternion.LookRotation(directionWorld, up));
        }

        Color32[] onBlack = RenderAndRead(camera, readback, size, new Color(0f, 0f, 0f, 0f));
        Color32[] onWhite = RenderAndRead(camera, readback, size, new Color(1f, 1f, 1f, 0f));
        // Freshness: the raw reads must actually contain THIS camera's renders — the black
        // pass shows a black background and the white pass a white one. A capture path that
        // hands back some other buffer (observed on live-editor render requests) fails this
        // on every corner, no matter what it contains.
        backgroundFresh = CornersMatch(onBlack, size, expectDark: true) && CornersMatch(onWhite, size, expectDark: false);
        for (int p = 0; p < onBlack.Length; p++)
        {
            int difference = (Mathf.Abs(onWhite[p].r - onBlack[p].r) + Mathf.Abs(onWhite[p].g - onBlack[p].g) + Mathf.Abs(onWhite[p].b - onBlack[p].b)) / 3;
            int coverage = 255 - difference;
            // Un-premultiply: the color rendered on black is truth × coverage — divide the
            // background attenuation back out so silhouette edges don't bake in dark fringes.
            if (coverage > 6 && coverage < 255)
            {
                float scale = 255f / coverage;
                onBlack[p].r = (byte)Mathf.Min(255f, onBlack[p].r * scale);
                onBlack[p].g = (byte)Mathf.Min(255f, onBlack[p].g * scale);
                onBlack[p].b = (byte)Mathf.Min(255f, onBlack[p].b * scale);
            }
            onBlack[p].a = (byte)coverage;
        }

        // Part-id + depth pass: same camera, only the vertex-colored snapshot mesh visible.
        byte[] groupIds = null;
        ushort[] depth16 = null;
        if (maskObject != null && maskTarget != null)
        {
            for (int r = 0; r < avatarRenderers.Length; r++)
            {
                Renderer avatarRenderer = avatarRenderers[r];
                avatarRendererStates[r] = avatarRenderer != null && avatarRenderer.enabled;
                if (avatarRendererStates[r])
                {
                    avatarRenderer.enabled = false;
                }
            }
            maskObject.SetActive(true);
            camera.targetTexture = maskTarget;
            Color32[] maskPixels = RenderAndRead(camera, readback, size, new Color(0f, 0f, 0f, 0f));
            camera.targetTexture = target;
            maskObject.SetActive(false);
            for (int r = 0; r < avatarRenderers.Length; r++)
            {
                if (avatarRendererStates[r] && avatarRenderers[r] != null)
                {
                    avatarRenderers[r].enabled = true;
                }
            }
            groupIds = new byte[maskPixels.Length];
            depth16 = new ushort[maskPixels.Length];
            for (int p = 0; p < maskPixels.Length; p++)
            {
                Color32 maskPixel = maskPixels[p];
                groupIds[p] = DecodeGroup(maskPixel.r);
                float depth01 = maskPixel.g * (1f / 255f) + maskPixel.b * (1f / 65025f);
                depth16[p] = (ushort)Mathf.Clamp(Mathf.RoundToInt(depth01 * 65535f), 0, 65535);
            }
        }

        Matrix4x4 clip = camera.projectionMatrix * camera.worldToCameraMatrix;
        Matrix4x4 ndcToPixel = Matrix4x4.TRS(new Vector3(size * 0.5f, size * 0.5f, 0f), Quaternion.identity, new Vector3(size * 0.5f, size * 0.5f, 1f));
        return new CaptureView
        {
            DirectionWorld = directionWorld,
            WorldToPixel = ndcToPixel * clip,
            Pixels = onBlack,
            GroupIds = groupIds,
            Depth16 = depth16,
            CameraPositionWorld = camera.transform.position,
            DepthNear = camera.nearClipPlane,
            DepthFar = camera.farClipPlane,
            // Absorbs the decimated-vs-original surface offset; anything deeper is another
            // surface in front of (or behind) the one this texel belongs to.
            DepthToleranceMeters = Mathf.Max(0.015f, frameRadius * 0.03f),
            Size = size,
            IsRegion = isRegion,
            ValidBoundsRoot = validBoundsRoot,
        };
    }

    /// <summary>
    /// Proves which manual capture mode actually works on this pipeline before the bake uses
    /// it. A mode must render a red unlit probe cube in the frame center AND hand back two
    /// successive, distinct background colors around it. That kills every failure mode seen in
    /// the wild: clears-but-draws-nothing (one URP setup's Camera.Render), a wrong/stale
    /// buffer in the readback (another setup's render request), MSAA targets misbehaving in a
    /// live editor session, and plain no-op paths. Candidates run best-first — render request
    /// then Camera.Render, MSAA targets then 1x — and the first that provably renders sets
    /// <see cref="sUseRenderRequest"/>/<see cref="sUseMsaaTargets"/>. False when nothing
    /// works — the bake then ships no far avatar instead of garbage.
    /// </summary>
    private static bool DetectCaptureMode(Camera camera, Texture2D readback, int size)
    {
        int savedMask = camera.cullingMask;
        RenderTexture savedTarget = camera.targetTexture;
        GameObject probe = null;
        Material probeMaterial = null;
        RenderTexture probeTarget = null;
        try
        {
            probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            probe.hideFlags = HideFlags.HideAndDontSave;
            if (probe.TryGetComponent(out Collider probeCollider))
            {
                Object.DestroyImmediate(probeCollider);
            }
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null)
            {
                unlit = Shader.Find("Unlit/Color");
            }
            if (unlit != null)
            {
                probeMaterial = new Material(unlit) { hideFlags = HideFlags.HideAndDontSave, color = Color.red };
                probe.GetComponent<MeshRenderer>().sharedMaterial = probeMaterial;
            }
            probe.transform.position = new Vector3(0f, -8192f, 0f);

            camera.cullingMask = ~0;
            camera.orthographicSize = 1.2f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            camera.transform.SetPositionAndRotation(probe.transform.position - Vector3.forward * 3f, Quaternion.identity);

            // Camera.Render rungs first: render requests have been observed passing this
            // probe and then landing a wrong internal buffer in the readback mid-sequence on
            // live editor sessions — Camera.Render has not. The per-view freshness check in
            // CaptureOne is the backstop either way.
            (bool useRequest, bool useMsaa)[] candidates =
            {
                (false, true), (false, false), (true, true), (true, false),
            };
            foreach ((bool useRequest, bool useMsaa) in candidates)
            {
                if (probeTarget != null)
                {
                    RenderTexture.ReleaseTemporary(probeTarget);
                }
                probeTarget = GetCaptureTarget(size, useMsaa);
                camera.targetTexture = probeTarget;
                sUseRenderRequest = useRequest;
                sUseMsaaTargets = useMsaa;
                if (CaptureProbeWorks(camera, readback, size))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            camera.cullingMask = savedMask;
            camera.targetTexture = savedTarget;
            if (probeTarget != null)
            {
                RenderTexture.ReleaseTemporary(probeTarget);
            }
            if (probe != null)
            {
                Object.DestroyImmediate(probe);
            }
            if (probeMaterial != null)
            {
                Object.DestroyImmediate(probeMaterial);
            }
        }
    }

    private static bool CaptureProbeWorks(Camera camera, Texture2D readback, int size)
    {
        // Green pass: red cube must land in the center, green background in the corner.
        Color32[] onGreen = RenderAndRead(camera, readback, size, new Color(0f, 1f, 0f, 1f));
        Color32 center = onGreen[(size / 2) * size + size / 2];
        Color32 corner = onGreen[(size / 8) * size + size / 8];
        bool geometryRendered = center.r > 180 && center.g < 100 && center.b < 100;
        // Row order is pipeline-dependent (see DetectSampleFlip) but the probe is centered, so
        // only the corner needs care — all four corners are background in this framing anyway.
        bool clearedGreen = corner.g > 180 && corner.r < 100 && corner.b < 100;
        if (!geometryRendered || !clearedGreen)
        {
            return false;
        }
        // Magenta pass: the corner must follow the new background — stale content can't.
        Color32[] onMagenta = RenderAndRead(camera, readback, size, new Color(1f, 0f, 1f, 1f));
        Color32 cornerMagenta = onMagenta[(size / 8) * size + size / 8];
        return cornerMagenta.r > 180 && cornerMagenta.b > 180 && cornerMagenta.g < 100;
    }

    /// <summary>
    /// At least two of the four corners must agree with the requested background brightness.
    /// Body views frame the avatar inscribed in the capture circle, so corners are normally
    /// background — but a scene floor can legitimately occupy the lower ones, hence the
    /// majority rule. A wrong buffer (uniform depth/id-like content) fails on every corner in
    /// both passes and can't sneak through.
    /// </summary>
    private static bool CornersMatch(Color32[] pixels, int size, bool expectDark)
    {
        int margin = size / 16;
        int[] xs = { margin, size - 1 - margin };
        int[] ys = { margin, size - 1 - margin };
        int matching = 0;
        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                Color32 pixel = pixels[y * size + x];
                int brightness = (pixel.r + pixel.g + pixel.b) / 3;
                if (expectDark ? brightness <= 64 : brightness >= 191)
                {
                    matching++;
                }
            }
        }
        return matching >= 2;
    }

    private static void SubmitRender(Camera camera)
    {
        if (sUseRenderRequest)
        {
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest();
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
            {
                // Docs-faithful request usage: the RT goes in request.destination ONLY.
                // Leaving camera.targetTexture set while submitting makes some stock URP
                // versions land the wrong buffer in the readback.
                RenderTexture destination = camera.targetTexture;
                camera.targetTexture = null;
                try
                {
                    request.destination = destination;
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
                }
                finally
                {
                    camera.targetTexture = destination;
                }
            }
            else
            {
                camera.Render();
            }
        }
        else
        {
            camera.Render();
        }
    }

    private static Color32[] RenderAndRead(Camera camera, Texture2D readback, int captureSize, Color background)
    {
        camera.backgroundColor = background;
        SubmitRender(camera);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = camera.targetTexture;
        readback.ReadPixels(new Rect(0, 0, captureSize, captureSize), 0, 0, false);
        RenderTexture.active = previous;
        return readback.GetPixels32();
    }

    /// <summary>Measured lighting response of the last bake — consumed by the generator.</summary>
    public static float LastMinBrightness;
    public static float LastMaxBrightness = 4f;

    /// <summary>
    /// Measures the avatar's own lighting clamps the same shader-agnostic way the matte works:
    /// render under black, reference-white and 2x-overbright ambient into an HDR target and
    /// compare per-pixel luminance. Toon shaders floor their light term (avatars never go
    /// fully dark) and cap it (never blow out) — matching that at runtime is what keeps the
    /// far avatar from standing out in dark or bright worlds. Median ratios so emissive
    /// patches and rim tricks don't skew the estimate.
    /// </summary>
    private static void MeasureBrightnessResponse(Camera camera, Vector3 centerWorld, Quaternion rootRotation, float radius)
    {
        LastMinBrightness = 0f;
        LastMaxBrightness = 4f;
        const int probeSize = 256;
        RenderTexture savedTarget = camera.targetTexture;
        bool savedHdr = camera.allowHDR;
        Color savedAmbient = RenderSettings.ambientLight;
        RenderTexture hdrTarget = null;
        Texture2D hdrReadback = null;
        try
        {
            hdrTarget = RenderTexture.GetTemporary(probeSize, probeSize, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            hdrReadback = new Texture2D(probeSize, probeSize, TextureFormat.RGBAHalf, false, true) { hideFlags = HideFlags.HideAndDontSave };
            camera.allowHDR = true;
            camera.targetTexture = hdrTarget;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);

            List<float> minRatios = new List<float>(8192);
            List<float> capRatios = new List<float>(8192);
            Vector3[] probeDirections = { rootRotation * Vector3.forward, rootRotation * Vector3.right };
            for (int d = 0; d < probeDirections.Length; d++)
            {
                Vector3 direction = probeDirections[d].normalized;
                camera.orthographicSize = radius;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = radius * 4f;
                camera.transform.SetPositionAndRotation(centerWorld - direction * (radius * 2f), Quaternion.LookRotation(direction, Vector3.up));

                RenderSettings.ambientLight = Color.white;
                Color[] reference = RenderHdr(camera, hdrReadback, probeSize);
                RenderSettings.ambientLight = Color.black;
                Color[] dark = RenderHdr(camera, hdrReadback, probeSize);
                RenderSettings.ambientLight = new Color(2f, 2f, 2f, 1f);
                Color[] bright = RenderHdr(camera, hdrReadback, probeSize);

                for (int p = 0; p < reference.Length; p += 3)
                {
                    float referenceLum = Mathf.Max(reference[p].r, Mathf.Max(reference[p].g, reference[p].b));
                    if (referenceLum < 0.02f)
                    {
                        continue;
                    }
                    float darkLum = Mathf.Max(dark[p].r, Mathf.Max(dark[p].g, dark[p].b));
                    float brightLum = Mathf.Max(bright[p].r, Mathf.Max(bright[p].g, bright[p].b));
                    minRatios.Add(Mathf.Clamp01(darkLum / referenceLum));
                    // 1.0 == the shader kept scaling linearly to 2x (no cap in reach).
                    capRatios.Add(Mathf.Clamp(brightLum / referenceLum * 0.5f, 0f, 1.5f));
                }
            }

            if (minRatios.Count >= 256)
            {
                minRatios.Sort();
                capRatios.Sort();
                float medianMin = minRatios[minRatios.Count / 2];
                float medianCap = capRatios[capRatios.Count / 2];
                LastMinBrightness = Mathf.Clamp(medianMin, 0f, 0.5f);
                LastMaxBrightness = medianCap >= 0.95f ? 4f : Mathf.Clamp(medianCap * 2f, 0.5f, 4f);
                Debug.Log($"[FarAvatar] Lighting response: min {LastMinBrightness:0.###}, max {(LastMaxBrightness >= 4f ? "uncapped" : LastMaxBrightness.ToString("0.##"))} ({minRatios.Count} samples).");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FarAvatar] Lighting response probe failed ({e.Message}) — using unclamped lighting.");
            LastMinBrightness = 0f;
            LastMaxBrightness = 4f;
        }
        finally
        {
            RenderSettings.ambientLight = savedAmbient;
            camera.targetTexture = savedTarget;
            camera.allowHDR = savedHdr;
            if (hdrTarget != null)
            {
                RenderTexture.ReleaseTemporary(hdrTarget);
            }
            if (hdrReadback != null)
            {
                Object.DestroyImmediate(hdrReadback);
            }
        }
    }

    private static Color[] RenderHdr(Camera camera, Texture2D readback, int size)
    {
        SubmitRender(camera);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = camera.targetTexture;
        readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
        RenderTexture.active = previous;
        return readback.GetPixels();
    }

    private const float AoBakeStrength = 0.45f;
    private const int AoRayCount = 12;

    /// <summary>
    /// Coarse ambient occlusion per decimated vertex (hemisphere raycasts against the bake
    /// collider), baked gently into the atlas. The flat-ambient capture strips all depth cues;
    /// this restores the under-chin / between-limbs shading that makes shapes read at range.
    /// Vertices buried inside layered geometry (cloth shells) read as fully occluded and would
    /// smear darkness onto visible surfaces — they're neutralized, and the result is smoothed
    /// over the mesh so 12-ray noise doesn't bake in as splotches.
    /// </summary>
    private static float[] ComputeVertexAo(Vector3[] positions, Vector3[] normals, int[] indices, Matrix4x4 rootToWorld, Quaternion rootRotation, float radius)
    {
        int layerMask = 1 << OcclusionLayer;
        float rayLength = Mathf.Max(radius * 0.5f, 0.2f);
        float bias = Mathf.Max(0.004f, radius * 0.008f);
        float buriedDistance = bias * 6f;

        // Fixed cosine-weighted hemisphere set (tangent space, z = up).
        Vector3[] hemisphere = new Vector3[AoRayCount];
        for (int i = 0; i < AoRayCount; i++)
        {
            float u = (i + 0.5f) / AoRayCount;
            float phi = i * 2.3999632f; // golden angle
            float sinTheta = Mathf.Sqrt(u);
            hemisphere[i] = new Vector3(Mathf.Cos(phi) * sinTheta, Mathf.Sin(phi) * sinTheta, Mathf.Sqrt(1f - u));
        }

        // One RaycastCommand batch instead of vertexCount x AoRayCount synchronous
        // Physics.Raycast calls — the queries fan across the worker pool and the
        // evaluation below reads the same hit distances the serial form did.
        float[] ao = new float[positions.Length];
        int vertexCount = positions.Length;
        var commands = new NativeArray<RaycastCommand>(vertexCount * AoRayCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var hits = new NativeArray<RaycastHit>(vertexCount * AoRayCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        try
        {
            QueryParameters query = new QueryParameters(layerMask, false, QueryTriggerInteraction.UseGlobal, false);
            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 normalWorld = (rootRotation * normals[v]).normalized;
                Vector3 origin = rootToWorld.MultiplyPoint3x4(positions[v]) + normalWorld * bias;
                Vector3 tangent = Vector3.Cross(normalWorld, Mathf.Abs(normalWorld.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 bitangent = Vector3.Cross(normalWorld, tangent);
                int baseIndex = v * AoRayCount;
                for (int r = 0; r < AoRayCount; r++)
                {
                    Vector3 direction = tangent * hemisphere[r].x + bitangent * hemisphere[r].y + normalWorld * hemisphere[r].z;
                    commands[baseIndex + r] = new RaycastCommand(origin, direction, query, rayLength);
                }
            }

            RaycastCommand.ScheduleBatch(commands, hits, 64).Complete();

            for (int v = 0; v < vertexCount; v++)
            {
                int occluded = 0;
                int nearHits = 0;
                int baseIndex = v * AoRayCount;
                for (int r = 0; r < AoRayCount; r++)
                {
                    RaycastHit hit = hits[baseIndex + r];
                    if (hit.colliderEntityId != EntityId.None)
                    {
                        occluded++;
                        if (hit.distance < buriedDistance)
                        {
                            nearHits++;
                        }
                    }
                }
                // Buried inside another shell — its texels are hidden anyway; stay neutral so
                // smoothing doesn't drag the darkness onto the visible surface above it.
                ao[v] = nearHits >= (AoRayCount * 3) / 4 ? 1f : 1f - (occluded / (float)AoRayCount) * 0.9f;
            }
        }
        finally
        {
            commands.Dispose();
            hits.Dispose();
        }

        // Two rounds of neighbor smoothing over the triangle graph.
        float[] neighborSum = new float[positions.Length];
        int[] neighborCount = new int[positions.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            System.Array.Clear(neighborSum, 0, neighborSum.Length);
            System.Array.Clear(neighborCount, 0, neighborCount.Length);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                neighborSum[a] += ao[b] + ao[c]; neighborCount[a] += 2;
                neighborSum[b] += ao[a] + ao[c]; neighborCount[b] += 2;
                neighborSum[c] += ao[a] + ao[b]; neighborCount[c] += 2;
            }
            for (int v = 0; v < ao.Length; v++)
            {
                if (neighborCount[v] > 0)
                {
                    ao[v] = ao[v] * 0.5f + (neighborSum[v] / neighborCount[v]) * 0.5f;
                }
            }
        }
        return ao;
    }

    private static Color32 ApplyAo(Color32 color, float aoFactor)
    {
        return new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * aoFactor), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * aoFactor), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * aoFactor), 0, 255),
            255);
    }

    private static Color32[] ProjectAtlas(List<CaptureView> views, Matrix4x4 rootToWorld, Quaternion rootRotation,
        Vector3[] positions, Vector3[] normals, Vector2[] uv, int[] indices, int atlasSize, float radius, byte[] texelGroups, byte[] texelHidden, float[] vertexAo)
    {
        int texelCount = atlasSize * atlasSize;
        Color32[] atlas = new Color32[texelCount];
        // 0 = unwritten, 1 = edge-slack sample (gutter), 2 = interior sample. Interior always
        // wins — a neighboring triangle's slack texels must never stomp real surface samples.
        byte[] texelQuality = new byte[texelCount];
        float rayBias = Mathf.Max(0.004f, radius * 0.01f);
        int layerMask = 1 << OcclusionLayer;
        int viewCount = views.Count;
        CaptureView[] viewArray = views.ToArray();

        // The projection is pure array math EXCEPT the collider raycast fallback taken when a
        // view carries no depth mask — Physics queries are main-thread only, so that path runs
        // serial. With depth on every view the atlas splits into disjoint row bands that run in
        // parallel: a texel belongs to exactly one band and every band replays the triangles in
        // the same order, so per-texel arbitration (texelQuality) resolves identically to the
        // serial walk. candidateOrder/candidateScore are per-band — they were the shared
        // scratch that made the old single-threaded form mandatory.
        void ProjectRows(int bandStartY, int bandEndY)
        {
        int[] candidateOrder = new int[viewCount];
        float[] candidateScore = new float[viewCount];

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];

            // Exterior-invisible remnants (hem seals kept by the fail-open cull) never show a
            // texel: skip the whole view search and let flood fill inherit their in-chart
            // neighbors' color — faster, and cleaner than a widened-depth rescue sample.
            if (texelHidden != null && texelHidden[i0] != 0 && texelHidden[i1] != 0 && texelHidden[i2] != 0)
            {
                continue;
            }

            Vector2 uv0 = uv[i0] * atlasSize, uv1 = uv[i1] * atlasSize, uv2 = uv[i2] * atlasSize;

            float minX = Mathf.Min(uv0.x, Mathf.Min(uv1.x, uv2.x)) - 1f;
            float maxX = Mathf.Max(uv0.x, Mathf.Max(uv1.x, uv2.x)) + 1f;
            float minY = Mathf.Min(uv0.y, Mathf.Min(uv1.y, uv2.y)) - 1f;
            float maxY = Mathf.Max(uv0.y, Mathf.Max(uv1.y, uv2.y)) + 1f;
            if (Mathf.CeilToInt(maxY) < bandStartY || Mathf.FloorToInt(minY) > bandEndY)
            {
                continue;
            }
            int startX = Mathf.Clamp(Mathf.FloorToInt(minX), 0, atlasSize - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt(maxX), 0, atlasSize - 1);
            int startY = Mathf.Clamp(Mathf.FloorToInt(minY), bandStartY, bandEndY);
            int endY = Mathf.Clamp(Mathf.CeilToInt(maxY), bandStartY, bandEndY);

            Vector2 edge0 = uv1 - uv0;
            Vector2 edge1 = uv2 - uv0;
            float denominator = edge0.x * edge1.y - edge0.y * edge1.x;
            if (Mathf.Abs(denominator) < 1e-8f)
            {
                continue;
            }
            float inverseDenominator = 1f / denominator;

            // Groups this triangle's texels may sample from (255 = unrestricted). Seam
            // triangles list up to three groups so shoulder/hip transitions stay seamless.
            byte allowedGroup0 = 255, allowedGroup1 = 255, allowedGroup2 = 255;
            if (texelGroups != null)
            {
                allowedGroup0 = texelGroups[i0];
                allowedGroup1 = texelGroups[i1];
                allowedGroup2 = texelGroups[i2];
            }

            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    Vector2 point = new Vector2(x + 0.5f, y + 0.5f) - uv0;
                    float baryB = (point.x * edge1.y - point.y * edge1.x) * inverseDenominator;
                    float baryC = (edge0.x * point.y - edge0.y * point.x) * inverseDenominator;
                    float baryA = 1f - baryB - baryC;
                    const float slack = -0.08f;
                    if (baryA < slack || baryB < slack || baryC < slack)
                    {
                        continue;
                    }
                    bool interior = baryA >= 0f && baryB >= 0f && baryC >= 0f;
                    int texelIndex = y * atlasSize + x;
                    if (texelQuality[texelIndex] >= (interior ? (byte)2 : (byte)1))
                    {
                        continue;
                    }

                    Vector3 positionRoot = positions[i0] * baryA + positions[i1] * baryB + positions[i2] * baryC;
                    Vector3 normalRoot = normals[i0] * baryA + normals[i1] * baryB + normals[i2] * baryC;
                    Vector3 positionWorld = rootToWorld.MultiplyPoint3x4(positionRoot);
                    Vector3 normalWorld = (rootRotation * normalRoot).normalized;
                    float aoFactor = 1f;
                    if (vertexAo != null)
                    {
                        float ao = Mathf.Clamp01(vertexAo[i0] * baryA + vertexAo[i1] * baryB + vertexAo[i2] * baryC);
                        aoFactor = Mathf.Lerp(1f, ao, AoBakeStrength);
                    }

                    // A view must face the surface properly: sampling near-tangent views smears
                    // capture pixels sideways across the texel (decimation parallax), which reads
                    // as texture misalignment. Close-ups outrank body views only when they also
                    // face the surface — an oblique close-up is worse than a perpendicular body view.
                    int candidateCount = 0;
                    for (int v = 0; v < viewCount; v++)
                    {
                        ref CaptureView view = ref viewArray[v];
                        if (view.IsRegion && !view.ValidBoundsRoot.Contains(positionRoot))
                        {
                            continue;
                        }
                        float facing = Vector3.Dot(normalWorld, -view.DirectionWorld);
                        if (facing > MinViewFacing)
                        {
                            candidateOrder[candidateCount] = v;
                            candidateScore[candidateCount] = facing + (view.IsRegion && facing > RegionMinFacing ? RegionScoreBias : 0f);
                            candidateCount++;
                        }
                    }
                    if (candidateCount == 0)
                    {
                        // Crevice texel no view faces well — take anything above the old floor.
                        for (int v = 0; v < viewCount; v++)
                        {
                            ref CaptureView view = ref viewArray[v];
                            if (view.IsRegion && !view.ValidBoundsRoot.Contains(positionRoot))
                            {
                                continue;
                            }
                            float facing = Vector3.Dot(normalWorld, -view.DirectionWorld);
                            if (facing > 0.05f)
                            {
                                candidateOrder[candidateCount] = v;
                                candidateScore[candidateCount] = facing;
                                candidateCount++;
                            }
                        }
                    }
                    // insertion sort by score, best first (candidate counts are small)
                    for (int a = 1; a < candidateCount; a++)
                    {
                        int order = candidateOrder[a];
                        float score = candidateScore[a];
                        int b = a - 1;
                        while (b >= 0 && candidateScore[b] < score)
                        {
                            candidateOrder[b + 1] = candidateOrder[b];
                            candidateScore[b + 1] = candidateScore[b];
                            b--;
                        }
                        candidateOrder[b + 1] = order;
                        candidateScore[b + 1] = score;
                    }

                    bool sampled = false;
                    Color32 fallbackColor = default;
                    bool hasFallback = false;

                    int consider = Mathf.Min(candidateCount, 6);
                    for (int c = 0; c < consider && !sampled; c++)
                    {
                        ref CaptureView view = ref viewArray[candidateOrder[c]];
                        if (!TrySampleView(in view, positionWorld, allowedGroup0, allowedGroup1, allowedGroup2, out Color32 color, out float coverage))
                        {
                            continue;
                        }
                        if (!hasFallback && coverage >= FallbackCoverage)
                        {
                            fallbackColor = color;
                            hasFallback = true;
                        }
                        if (coverage < MinCoverage)
                        {
                            continue;
                        }
                        // Depth-validated samples don't need the raycast — the per-pixel depth
                        // match against the snapshot render already proves visibility. The
                        // collider raycast remains as the fallback when the mask pass is absent.
                        if (view.Depth16 == null)
                        {
                            Vector3 towardCamera = -view.DirectionWorld;
                            Vector3 origin = positionWorld + normalWorld * rayBias + towardCamera * rayBias;
                            if (Physics.Raycast(origin, towardCamera, radius * 3f, layerMask))
                            {
                                continue;
                            }
                        }
                        atlas[texelIndex] = ApplyAo(color, aoFactor);
                        texelQuality[texelIndex] = interior ? (byte)2 : (byte)1;
                        sampled = true;
                    }

                    if (!sampled && hasFallback)
                    {
                        atlas[texelIndex] = ApplyAo(fallbackColor, aoFactor);
                        texelQuality[texelIndex] = interior ? (byte)2 : (byte)1;
                        sampled = true;
                    }

                    if (!sampled)
                    {
                        // Rescue tier: nothing passed the strict gates (deep crevice). A
                        // same-part sample through a widened depth window still beats leaving
                        // the texel to flood fill.
                        for (int c = 0; c < candidateCount && !sampled; c++)
                        {
                            ref CaptureView rescueView = ref viewArray[candidateOrder[c]];
                            if (TrySampleView(in rescueView, positionWorld, allowedGroup0, allowedGroup1, allowedGroup2, out Color32 rescueColor, out float rescueCoverage, 3f)
                                && rescueCoverage >= 0.05f)
                            {
                                atlas[texelIndex] = ApplyAo(rescueColor, aoFactor);
                                texelQuality[texelIndex] = 1;
                                sampled = true;
                            }
                        }
                    }
                }
            }
        }
        }

        bool anyViewLacksDepth = false;
        for (int v = 0; v < viewCount; v++)
        {
            if (viewArray[v].Depth16 == null)
            {
                anyViewLacksDepth = true;
                break;
            }
        }

        if (anyViewLacksDepth)
        {
            ProjectRows(0, atlasSize - 1);
        }
        else
        {
            int bandHeight = Mathf.Max(16, atlasSize / (Mathf.Clamp(SystemInfo.processorCount, 1, 16) * 4));
            int bandCount = (atlasSize + bandHeight - 1) / bandHeight;
            System.Threading.Tasks.Parallel.For(0, bandCount, band =>
            {
                int bandStart = band * bandHeight;
                ProjectRows(bandStart, Mathf.Min(atlasSize - 1, bandStart + bandHeight - 1));
            });
        }

        // If projection wrote (almost) nothing the flood fill below would paint the whole
        // atlas a flat color — captures and the part mask disagreeing with the geometry (a
        // dead mask render rejects every sample). Shipping that is worse than shipping no
        // far avatar.
        long writtenTexels = 0;
        for (int Index = 0; Index < texelQuality.Length; Index++)
        {
            if (texelQuality[Index] > 0)
            {
                writtenTexels++;
            }
        }
        if (writtenTexels < texelCount / 100)
        {
            BasisFarLodGenerator.LastFailureReason = $"atlas projection wrote almost nothing: {writtenTexels} of {texelCount} texels";
            Debug.LogError($"[FarAvatar] Atlas projection wrote almost nothing ({writtenTexels} of {texelCount} texels) — skipping the far avatar instead of baking a flat atlas.");
            return null;
        }

        Dilate(atlas, texelQuality, atlasSize);
        return atlas;
    }

    /// <summary>
    /// Samples a strided set of front-facing vertices through the projection math against the
    /// captured coverage, upright vs Y-mirrored, and reports whether the capture rows came back
    /// top-down (platform/pipeline dependent). Guessing wrong reads as severe misalignment.
    /// </summary>
    private static bool DetectSampleFlip(List<CaptureView> views, Matrix4x4 rootToWorld, Quaternion rootRotation, Vector3[] positions, Vector3[] normals)
    {
        int upright = 0;
        int flipped = 0;
        int stride = Mathf.Max(1, positions.Length / 512);
        for (int v = 0; v < views.Count; v++)
        {
            CaptureView view = views[v];
            if (view.IsRegion)
            {
                continue;
            }
            for (int i = 0; i < positions.Length; i += stride)
            {
                Vector3 normalWorld = rootRotation * normals[i];
                if (Vector3.Dot(normalWorld, -view.DirectionWorld) < 0.5f)
                {
                    continue;
                }
                Vector3 pixel = view.WorldToPixel.MultiplyPoint(rootToWorld.MultiplyPoint3x4(positions[i]));
                int x = (int)pixel.x;
                int y = (int)pixel.y;
                if (x < 0 || y < 0 || x >= view.Size || y >= view.Size)
                {
                    continue;
                }
                if (view.Pixels[y * view.Size + x].a > 128)
                {
                    upright++;
                }
                if (view.Pixels[(view.Size - 1 - y) * view.Size + x].a > 128)
                {
                    flipped++;
                }
            }
        }
        return flipped > upright + upright / 2 && flipped > 64;
    }

    /// <summary>
    /// Bilinear, coverage-weighted sample — background texels are weighted out, and when a part
    /// mask is present, pixels belonging to another body group count as background too.
    /// </summary>
    private static bool TrySampleView(in CaptureView view, Vector3 positionWorld, byte allowed0, byte allowed1, byte allowed2, out Color32 color, out float coverage, float depthToleranceScale = 1f)
    {
        Vector3 pixel = view.WorldToPixel.MultiplyPoint(positionWorld);
        if (sFlipSampleY)
        {
            pixel.y = view.Size - pixel.y;
        }

        // Expected capture depth of this surface point — pixels whose recorded depth differs
        // belong to another surface along the same ray (front/back of the torso, arm in front
        // of chest) and count as background.
        bool hasDepth = view.Depth16 != null;
        float expectedDepth16 = 0f;
        float depthTolerance16 = 0f;
        if (hasDepth)
        {
            float depthRange = Mathf.Max(view.DepthFar - view.DepthNear, 1e-4f);
            float viewDepth = Vector3.Dot(positionWorld - view.CameraPositionWorld, view.DirectionWorld);
            expectedDepth16 = Mathf.Clamp01((viewDepth - view.DepthNear) / depthRange) * 65535f;
            depthTolerance16 = view.DepthToleranceMeters * depthToleranceScale / depthRange * 65535f;
        }

        float fx = pixel.x - 0.5f;
        float fy = pixel.y - 0.5f;
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        if (x0 < -1 || y0 < -1 || x0 >= view.Size || y0 >= view.Size)
        {
            color = default;
            coverage = 0f;
            return false;
        }
        float tx = fx - x0;
        float ty = fy - y0;

        float r = 0f, g = 0f, b = 0f, weightedCoverage = 0f, totalWeight = 0f;
        for (int dy = 0; dy <= 1; dy++)
        {
            int sy = y0 + dy;
            if (sy < 0 || sy >= view.Size)
            {
                continue;
            }
            float wy = dy == 0 ? 1f - ty : ty;
            for (int dx = 0; dx <= 1; dx++)
            {
                int sx = x0 + dx;
                if (sx < 0 || sx >= view.Size)
                {
                    continue;
                }
                float weight = wy * (dx == 0 ? 1f - tx : tx);
                if (weight <= 0f)
                {
                    continue;
                }
                int sampleIndex = sy * view.Size + sx;
                if (view.GroupIds != null && allowed0 != 255)
                {
                    byte pixelGroup = view.GroupIds[sampleIndex];
                    if (pixelGroup != allowed0 && pixelGroup != allowed1 && pixelGroup != allowed2)
                    {
                        totalWeight += weight;
                        continue;
                    }
                }
                if (hasDepth && Mathf.Abs(view.Depth16[sampleIndex] - expectedDepth16) > depthTolerance16)
                {
                    totalWeight += weight;
                    continue;
                }
                Color32 sample = view.Pixels[sampleIndex];
                float sampleCoverage = sample.a * (1f / 255f);
                float colorWeight = weight * sampleCoverage;
                r += sample.r * colorWeight;
                g += sample.g * colorWeight;
                b += sample.b * colorWeight;
                weightedCoverage += sampleCoverage * weight;
                totalWeight += weight;
            }
        }

        if (totalWeight <= 0f || weightedCoverage <= 1e-4f)
        {
            color = default;
            coverage = 0f;
            return false;
        }
        float inverseColorWeight = 1f / Mathf.Max(weightedCoverage, 1e-4f);
        color = new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(r * inverseColorWeight), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(g * inverseColorWeight), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(b * inverseColorWeight), 0, 255),
            255);
        coverage = weightedCoverage / totalWeight;
        return true;
    }

    private static void Dilate(Color32[] atlas, byte[] texelQuality, int atlasSize)
    {
        byte[] current = texelQuality;
        for (int pass = 0; pass < DilatePasses; pass++)
        {
            byte[] next = (byte[])current.Clone();
            bool any = false;
            for (int y = 0; y < atlasSize; y++)
            {
                for (int x = 0; x < atlasSize; x++)
                {
                    int index = y * atlasSize + x;
                    if (current[index] > 0)
                    {
                        continue;
                    }
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= atlasSize)
                        {
                            continue;
                        }
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= atlasSize)
                            {
                                continue;
                            }
                            int neighbor = ny * atlasSize + nx;
                            if (current[neighbor] == 0)
                            {
                                continue;
                            }
                            r += atlas[neighbor].r;
                            g += atlas[neighbor].g;
                            b += atlas[neighbor].b;
                            count++;
                        }
                    }
                    if (count > 0)
                    {
                        atlas[index] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
                        next[index] = 1;
                        any = true;
                    }
                }
            }
            current = next;
            if (!any)
            {
                break;
            }
        }

        // Flood the remaining texels from their nearest written neighbor — a flat global-average
        // patch in a crevice reads as a splotch the moment it becomes visible.
        Queue<int> frontier = new Queue<int>(4096);
        bool anyWritten = false;
        for (int i = 0; i < atlas.Length; i++)
        {
            if (current[i] > 0)
            {
                frontier.Enqueue(i);
                anyWritten = true;
            }
        }
        if (!anyWritten)
        {
            Color32 neutral = new Color32(128, 128, 128, 255);
            for (int i = 0; i < atlas.Length; i++)
            {
                atlas[i] = neutral;
            }
            return;
        }
        while (frontier.Count > 0)
        {
            int index = frontier.Dequeue();
            int x = index % atlasSize;
            int y = index / atlasSize;
            if (x > 0 && current[index - 1] == 0)
            {
                atlas[index - 1] = atlas[index];
                current[index - 1] = 1;
                frontier.Enqueue(index - 1);
            }
            if (x < atlasSize - 1 && current[index + 1] == 0)
            {
                atlas[index + 1] = atlas[index];
                current[index + 1] = 1;
                frontier.Enqueue(index + 1);
            }
            if (y > 0 && current[index - atlasSize] == 0)
            {
                atlas[index - atlasSize] = atlas[index];
                current[index - atlasSize] = 1;
                frontier.Enqueue(index - atlasSize);
            }
            if (y < atlasSize - 1 && current[index + atlasSize] == 0)
            {
                atlas[index + atlasSize] = atlas[index];
                current[index + atlasSize] = 1;
                frontier.Enqueue(index + atlasSize);
            }
        }
    }

    private static BasisFarLodPayload.FarLodTexture[] CompressAtlas(Color32[] atlas, int atlasSize)
    {
        Texture2D source = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        try
        {
            source.SetPixels32(atlas);
            source.Apply(true, false);

            List<BasisFarLodPayload.FarLodTexture> textures = new List<BasisFarLodPayload.FarLodTexture>(2);
            AppendCompressed(textures, source, TextureFormat.DXT1, BasisFarLodPayload.FarLodTextureFormat.BC1);
            AppendCompressed(textures, source, TextureFormat.ASTC_6x6, BasisFarLodPayload.FarLodTextureFormat.ASTC6x6);
            return textures.ToArray();
        }
        finally
        {
            Object.DestroyImmediate(source);
        }
    }

    private static void AppendCompressed(List<BasisFarLodPayload.FarLodTexture> textures, Texture2D source,
        TextureFormat format, BasisFarLodPayload.FarLodTextureFormat payloadFormat)
    {
        Texture2D copy = Object.Instantiate(source);
        copy.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            EditorUtility.CompressTexture(copy, format, TextureCompressionQuality.Normal);
            if (copy.format != format)
            {
                Debug.LogWarning($"Far avatar atlas compression to {format} was not applied on this platform; skipping that payload.");
                return;
            }
            textures.Add(new BasisFarLodPayload.FarLodTexture
            {
                Format = payloadFormat,
                Width = (ushort)copy.width,
                Height = (ushort)copy.height,
                MipCount = (byte)copy.mipmapCount,
                Data = copy.GetRawTextureData(),
            });
        }
        finally
        {
            Object.DestroyImmediate(copy);
        }
    }

    /// <summary>
    /// Overrides scene lighting for a neutral capture (flat white ambient, no lights, no fog,
    /// no reflections) and restores everything afterwards.
    /// </summary>
    private struct LightingScope
    {
        private UnityEngine.Rendering.AmbientMode _ambientMode;
        private Color _ambientLight;
        private float _reflectionIntensity;
        private bool _fog;
        private Light[] _disabledLights;

        public static LightingScope Push()
        {
            LightingScope scope = new LightingScope
            {
                _ambientMode = RenderSettings.ambientMode,
                _ambientLight = RenderSettings.ambientLight,
                _reflectionIntensity = RenderSettings.reflectionIntensity,
                _fog = RenderSettings.fog,
            };

            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            List<Light> disabled = new List<Light>(lights.Length);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].enabled)
                {
                    lights[i].enabled = false;
                    disabled.Add(lights[i]);
                }
            }
            scope._disabledLights = disabled.ToArray();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;
            return scope;
        }

        public void Pop()
        {
            RenderSettings.ambientMode = _ambientMode;
            RenderSettings.ambientLight = _ambientLight;
            RenderSettings.reflectionIntensity = _reflectionIntensity;
            RenderSettings.fog = _fog;
            if (_disabledLights != null)
            {
                for (int i = 0; i < _disabledLights.Length; i++)
                {
                    if (_disabledLights[i] != null)
                    {
                        _disabledLights[i].enabled = true;
                    }
                }
            }
        }
    }
}
