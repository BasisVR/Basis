using System.Collections.Generic;
using UnityEngine;

// Renderer/material sink for BasisMediaPlayer. Subscribes to the player's
// OnOutputTextureChanged and binds the current OutputTexture to one or more
// target Renderer material properties. The primary TargetRenderer/MaterialIndex
// plus every entry in AdditionalTargets are all driven from the same output, so
// one player can feed several screens or materials at once.
//
// Two binding strategies (applied to every target):
//   * UseSharedMaterial = false (default): assigns to Renderer.materials,
//     which clones the shared material the first time it's accessed. Safe for
//     per-instance video; avoids stomping other renderers sharing the asset.
//   * UseSharedMaterial = true: assigns to Renderer.sharedMaterials,
//     mutating the project asset. Use only when you intend every renderer
//     sharing this material to receive the same video texture.
//
// On disable, the original texture is restored on each target so editor scenes
// don't end up with the runtime video texture baked into the material.
[DisallowMultipleComponent]
public sealed class BasisVideoMaterialOutput : MonoBehaviour
{
    [System.Serializable]
    public sealed class MaterialTarget
    {
        [Tooltip("Renderer whose material receives the OutputTexture.")]
        public Renderer Renderer;

        [Tooltip("Index into Renderer.materials for multi-material renderers. 0 by default.")]
        [Min(0)] public int MaterialIndex = 0;

        [Tooltip("Shader texture property to bind. Leave empty to inherit the component-level Texture Property Name.")]
        public string TexturePropertyName = "";

        [System.NonSerialized] public Texture OriginalTexture;
        [System.NonSerialized] public Vector2 OriginalScale;
        [System.NonSerialized] public Vector2 OriginalOffset;
        [System.NonSerialized] public bool Captured;
        [System.NonSerialized] public int PropertyId;
        [System.NonSerialized] public string ActiveKeyword;
    }

    [Tooltip("Player to subscribe to. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
    public BasisMediaPlayer Player;

    [Tooltip("Renderer whose material receives the OutputTexture.")]
    public Renderer TargetRenderer;

    [Tooltip("Index into TargetRenderer.materials for multi-material renderers. 0 by default.")]
    [Min(0)] public int MaterialIndex = 0;

    [Tooltip("Shader texture property name to bind the frame texture to. URP uses _BaseMap; legacy BiRP uses _MainTex.")]
    public string TexturePropertyName = "_BaseMap";

    [Tooltip("If true, the renderer's sharedMaterial is mutated — every renderer using that material will see the video. Otherwise a per-instance copy is created via Renderer.material.")]
    public bool UseSharedMaterial = false;

    [Tooltip("Extra renderers/materials driven by the same OutputTexture. Each entry may override the Texture Property Name; leave it empty to inherit the one above. All entries share the projection/aspect/picture settings below.")]
    public List<MaterialTarget> AdditionalTargets = new List<MaterialTarget>();

    [Tooltip("Optional fallback bound when the player has no output texture (before first frame, after Stop). Leave empty to leave the material's prior texture in place.")]
    public Texture PlaceholderTexture;

    [Tooltip("If true, the placeholder is rebound whenever the player raises OnEnded.")]
    public bool RestorePlaceholderOnEnded = true;

    [Tooltip("Flip the video vertically. Per-GPU orientation differences are now corrected automatically (the backend reports its frame origin), so leave this OFF for normal content; enable it only if the source itself is encoded upside-down — which is consistent across all machines.")]
    public bool FlipVertically = false;

    [Tooltip("Flip the video horizontally. Use when the screen mesh's UV winding presents the video mirrored to the viewer.")]
    public bool FlipHorizontally = false;

    [Header("Projection")]
    [Tooltip("How the source frame is laid out. Mono/SBS/OU select the correct UV half via UV transform; Equirect360/VR180/Fisheye set a shader keyword (BASIS_PROJ_*) for compatible shaders.")]
    public BasisVideoProjectionMode ProjectionMode = BasisVideoProjectionMode.Mono;

    [Tooltip("Which half of a stereo SBS/OU stream to display on a non-VR or single-eye target. Stereo VR rendering on a per-eye material would use UnityStereoEyeIndex in the shader instead.")]
    public BasisVideoStereoEye StereoEye = BasisVideoStereoEye.Left;

    [Header("Aspect")]
    [Tooltip("Original = sample untransformed; Stretch = same; FitInside = letterbox (needs a shader that renders out-of-range UVs black, e.g. Basis/Media Player Video — on a clamp/repeat material it smears); FitOutside = crop to fill display (safe on any material); PixelPerfect = 1:1 source pixels at the chosen scale.")]
    public BasisVideoAspectMode AspectMode = BasisVideoAspectMode.Original;

    [Tooltip("Display aspect ratio for FitInside/FitOutside/PixelPerfect. Defaults to the renderer's local-bounds aspect when 0. Override for non-square quads or skewed targets.")]
    [Min(0f)] public float DisplayAspectOverride = 0f;

    [Header("Picture")]
    [Tooltip("Per-output picture controls. Shader must read _BasisBrightness/_BasisContrast/_BasisSaturation/_BasisGamma from the MaterialPropertyBlock; unsupported shaders just ignore them.")]
    public BasisVideoPicture Picture = BasisVideoPicture.Default;

    private static readonly int FallbackPropertyId = Shader.PropertyToID("_MainTex");
    private static readonly int PropBrightness = Shader.PropertyToID("_BasisBrightness");
    private static readonly int PropContrast = Shader.PropertyToID("_BasisContrast");
    private static readonly int PropSaturation = Shader.PropertyToID("_BasisSaturation");
    private static readonly int PropGamma = Shader.PropertyToID("_BasisGamma");

    private readonly List<MaterialTarget> activeTargets = new List<MaterialTarget>();
    private MaterialPropertyBlock mpb;

    private void Reset()
    {
        if (TargetRenderer == null) TargetRenderer = GetComponent<Renderer>();
        if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
    }

    private void OnEnable()
    {
        if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
        if (Player == null)
        {
            BasisDebug.LogWarning("BasisVideoMaterialOutput: no BasisMediaPlayer found in parents and Player field is empty.", BasisDebug.LogTag.Video);
            return;
        }

        RebuildActiveTargets();
        if (activeTargets.Count == 0)
        {
            BasisDebug.LogWarning("BasisVideoMaterialOutput: no target renderers assigned; cannot bind output texture.", BasisDebug.LogTag.Video);
            return;
        }

        for (int i = 0; i < activeTargets.Count; i++) CaptureOriginal(activeTargets[i]);

        Player.OnOutputTextureChanged += HandleTextureChanged;
        Player.OnEnded += HandleEnded;

        HandleTextureChanged(Player.OutputTexture);
    }

    private void OnDisable()
    {
        if (Player != null)
        {
            Player.OnOutputTextureChanged -= HandleTextureChanged;
            Player.OnEnded -= HandleEnded;
        }
        for (int i = 0; i < activeTargets.Count; i++) RestoreOriginal(activeTargets[i]);
        activeTargets.Clear();
    }

    private void RebuildActiveTargets()
    {
        activeTargets.Clear();
        int defaultPropertyId = !string.IsNullOrEmpty(TexturePropertyName)
            ? Shader.PropertyToID(TexturePropertyName)
            : FallbackPropertyId;

        if (TargetRenderer != null)
        {
            activeTargets.Add(new MaterialTarget
            {
                Renderer = TargetRenderer,
                MaterialIndex = MaterialIndex,
                PropertyId = defaultPropertyId
            });
        }

        if (AdditionalTargets != null)
        {
            for (int i = 0; i < AdditionalTargets.Count; i++)
            {
                var target = AdditionalTargets[i];
                if (target == null || target.Renderer == null) continue;
                target.PropertyId = !string.IsNullOrEmpty(target.TexturePropertyName)
                    ? Shader.PropertyToID(target.TexturePropertyName)
                    : defaultPropertyId;
                activeTargets.Add(target);
            }
        }
    }

    private void HandleTextureChanged(Texture texture)
    {
        if (texture == null) texture = PlaceholderTexture;
        for (int i = 0; i < activeTargets.Count; i++) SetTexture(activeTargets[i], texture);
    }

    private void HandleEnded()
    {
        if (!RestorePlaceholderOnEnded) return;
        for (int i = 0; i < activeTargets.Count; i++) SetTexture(activeTargets[i], PlaceholderTexture);
    }

    private void CaptureOriginal(MaterialTarget target)
    {
        if (target.Captured) return;
        var material = GetMaterial(target);
        if (material == null) return;
        if (material.HasProperty(target.PropertyId))
        {
            target.OriginalTexture = material.GetTexture(target.PropertyId);
            target.OriginalScale = material.GetTextureScale(target.PropertyId);
            target.OriginalOffset = material.GetTextureOffset(target.PropertyId);
            target.Captured = true;
        }
    }

    private void RestoreOriginal(MaterialTarget target)
    {
        if (!target.Captured) return;
        var material = GetMaterial(target);
        if (material != null && material.HasProperty(target.PropertyId))
        {
            material.SetTexture(target.PropertyId, target.OriginalTexture);
            material.SetTextureScale(target.PropertyId, target.OriginalScale);
            material.SetTextureOffset(target.PropertyId, target.OriginalOffset);
        }
        target.Captured = false;
        target.OriginalTexture = null;
    }

    private void SetTexture(MaterialTarget target, Texture texture)
    {
        var material = GetMaterial(target);
        if (material == null) return;
        if (!material.HasProperty(target.PropertyId)) return;

        material.SetTexture(target.PropertyId, texture);

        bool isLiveVideo = texture != null && texture != PlaceholderTexture;
        if (!isLiveVideo)
        {
            material.SetTextureScale(target.PropertyId, target.OriginalScale);
            material.SetTextureOffset(target.PropertyId, target.OriginalOffset);
            ApplyShaderKeyword(material, target, null);
            ClearPicture(target);
            return;
        }

        BasisVideoOutputMath.GetProjectionUv(ProjectionMode, StereoEye,
            out Vector2 projScale, out Vector2 projOffset, out string keyword);

        int vw = texture.width;
        int vh = texture.height;
        if (ProjectionMode == BasisVideoProjectionMode.SideBySideLR || ProjectionMode == BasisVideoProjectionMode.SideBySideRL) vw /= 2;
        else if (ProjectionMode == BasisVideoProjectionMode.OverUnderTB || ProjectionMode == BasisVideoProjectionMode.OverUnderBT) vh /= 2;

        float displayAspect = DisplayAspectOverride > 0f ? DisplayAspectOverride : EstimateRendererAspect(target.Renderer);
        BasisVideoOutputMath.GetAspectUv(AspectMode, vw, vh, displayAspect,
            out Vector2 aspScale, out Vector2 aspOffset);

        BasisVideoOutputMath.Compose(projScale, projOffset, aspScale, aspOffset,
            out Vector2 scale, out Vector2 offset);

        scale.x *= target.OriginalScale.x;
        scale.y *= target.OriginalScale.y;
        offset.x = target.OriginalOffset.x + target.OriginalScale.x * offset.x;
        offset.y = target.OriginalOffset.y + target.OriginalScale.y * offset.y;

        // The backend may publish the frame upside-down on GPUs that can't
        // normalize orientation natively (the Windows video-processor mirror is
        // driver-optional); fold that per-client correction into the authored flip
        // so one serialized FlipVertically value is correct on every machine.
        bool flipV = FlipVertically ^ (Player != null && Player.OutputFrameIsTopLeftOrigin);
        if (flipV && FlipHorizontally) BasisVideoOutputMath.ApplyBothFlip(ref scale, ref offset);
        else if (flipV) BasisVideoOutputMath.ApplyVerticalFlip(ref scale, ref offset);
        else if (FlipHorizontally) BasisVideoOutputMath.ApplyHorizontalFlip(ref scale, ref offset);

        material.SetTextureScale(target.PropertyId, scale);
        material.SetTextureOffset(target.PropertyId, offset);

        ApplyShaderKeyword(material, target, keyword);
        ApplyPicture(target);
    }

    private float EstimateRendererAspect(Renderer renderer)
    {
        if (renderer == null) return 1f;
        var b = renderer.localBounds.size;
        if (b.x > 0f && b.y > 0f) return b.x / b.y;
        return 1f;
    }

    private void ApplyShaderKeyword(Material material, MaterialTarget target, string keyword)
    {
        if (material == null) return;
        if (!string.IsNullOrEmpty(target.ActiveKeyword) && target.ActiveKeyword != keyword)
        {
            material.DisableKeyword(target.ActiveKeyword);
        }
        target.ActiveKeyword = keyword;
        if (!string.IsNullOrEmpty(keyword)) material.EnableKeyword(keyword);
    }

    private void ApplyPicture(MaterialTarget target)
    {
        if (target.Renderer == null) return;
        mpb ??= new MaterialPropertyBlock();
        target.Renderer.GetPropertyBlock(mpb, target.MaterialIndex);
        mpb.SetFloat(PropBrightness, Picture.Brightness);
        mpb.SetFloat(PropContrast, Picture.Contrast);
        mpb.SetFloat(PropSaturation, Picture.Saturation);
        mpb.SetFloat(PropGamma, Picture.Gamma <= 0f ? 1f : Picture.Gamma);
        target.Renderer.SetPropertyBlock(mpb, target.MaterialIndex);
    }

    private void ClearPicture(MaterialTarget target)
    {
        if (target.Renderer == null || mpb == null) return;
        target.Renderer.GetPropertyBlock(mpb, target.MaterialIndex);
        mpb.SetFloat(PropBrightness, 1f);
        mpb.SetFloat(PropContrast, 1f);
        mpb.SetFloat(PropSaturation, 1f);
        mpb.SetFloat(PropGamma, 1f);
        target.Renderer.SetPropertyBlock(mpb, target.MaterialIndex);
    }

    private void OnValidate()
    {
        if (Application.isPlaying && isActiveAndEnabled && Player != null)
            HandleTextureChanged(Player.OutputTexture);
    }

    private Material GetMaterial(MaterialTarget target)
    {
        var renderer = target.Renderer;
        if (renderer == null) return null;
        if (UseSharedMaterial)
        {
            var shared = renderer.sharedMaterials;
            if (target.MaterialIndex < 0 || target.MaterialIndex >= shared.Length) return null;
            return shared[target.MaterialIndex];
        }
        // Access .materials once to take ownership of a cloned array, then
        // index into it; accessing .materials repeatedly leaks instances.
        var instances = renderer.materials;
        if (target.MaterialIndex < 0 || target.MaterialIndex >= instances.Length) return null;
        return instances[target.MaterialIndex];
    }
}
