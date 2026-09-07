using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Camera;
using RenderPipeline = UnityEngine.Rendering.RenderPipelineManager;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class BasisSDKMirror : MonoBehaviour
{
    public enum MirrorClearFlags
    {
        FromReferenceCamera = 0,
        Skybox = 1,
        Color = 2,
        Depth = 3,
        Nothing = 4,
    }

    [Header("Main Settings")]
    public Renderer Renderer;
    public Material MirrorsMaterial;
    [SerializeField] private LayerMask ReflectingLayers;
    [SerializeField] private MirrorClearFlags clearFlags = MirrorClearFlags.FromReferenceCamera;
    [SerializeField] private Color clearColor = Color.black;
    // 0.001 z-fights/flickers at grazing angles on mobile depth precision; classic planar-mirror
    // references use 0.05-0.07. Serialized 0.001 from older content is clamped up on Android.
    public float ClipPlaneOffset = 0.05f;
    public float nearClipLimit = 0.01f;
    public float FarClipPlane = 25f;
    public int XSize = 2048;
    public int YSize = 2048;
    public int depth = 24;
    public int Antialiasing = 2;

    [Header("Options")]
    [Tooltip("Ignored on Android: Quest multiview lets HMD tracking override the reflected camera pose.")]
    public bool allowXRRendering = true;
    public bool RenderPostProcessing = false;
    public bool OcclusionCulling = false;
    public bool renderShadows = false;

    [Header("Update Rate")]
    [Tooltip("Render the reflection every Nth frame (1 = every frame). Cheap lever for heavy worlds.")]
    public int UpdateEveryNthFrame = 1;
    [Tooltip("Standalone only: within this distance of the mirror surface it updates at full rate.")]
    public float FullRateDistance = 4f;
    [Tooltip("Standalone only: beyond FullRateDistance the mirror updates every 2nd frame; beyond this, every 4th.")]
    public float HalfRateDistance = 10f;
    [Tooltip("Standalone only: beyond this distance the mirror stops updating and keeps its last image.")]
    public float CullDistance = 25f;

    [Header("Secondary Viewers")]
    [Tooltip("Reflection resolution cap for secondary viewers (handheld cameras, Scene View)")]
    public int SecondaryViewerMaxSize = 1024;

    [Header("Debug / Runtime")]
    public bool IsActive;
    public bool IsAbleToRender;
    // Per-instance on purpose: a static flag leaked by one mirror's bad frame used to
    // permanently freeze every mirror in the world.
    [NonSerialized] public bool InsideRendering;
    [NonSerialized] private bool desktopPlayspaceYawInitialized;
    [NonSerialized] private float lastDesktopPlayspaceYaw;

    [Header("Cameras")]
    public Camera LeftCamera;
    public Camera RightCamera;
    public RenderTexture PortalTextureLeft;
    public RenderTexture PortalTextureRight;

    // Keep original event name (typo preserved) to avoid breaking external subscriptions.
    public Action OnCamerasRenderering;
    public Action OnCamerasFinished;

    public LayerMask ReflectionLayers
    {
        get => ReflectingLayers;
        set
        {
            ReflectingLayers = value;
            if (LeftCamera) LeftCamera.cullingMask = ReflectingLayers;
            if (RightCamera) RightCamera.cullingMask = ReflectingLayers;
        }
    }

    public MirrorClearFlags ClearFlags
    {
        get => clearFlags;
        set
        {
            clearFlags = value;
            Camera refCamera = BasisLocalCameraDriver.HasInstance ? BasisLocalCameraDriver.Instance.Camera : null;
            if (LeftCamera) updateCameraClearFlags(LeftCamera, refCamera);
            if (RightCamera) updateCameraClearFlags(RightCamera, refCamera);
        }
    }

    public int ReflectionWidth
    {
        get => XSize;
        set => SetTargetShape(value, YSize, depth, Antialiasing);
    }

    public int ReflectionHeight
    {
        get => YSize;
        set => SetTargetShape(XSize, value, depth, Antialiasing);
    }

    public int DepthBits
    {
        get => depth;
        set => SetTargetShape(XSize, YSize, value, Antialiasing);
    }

    public int MsaaSamples
    {
        get => Antialiasing;
        set => SetTargetShape(XSize, YSize, depth, value);
    }

    public int SecondaryViewerResolutionCap
    {
        get => SecondaryViewerMaxSize;
        set
        {
            int clamped = Mathf.Clamp(value, MinResolution, MaxResolution);
            if (clamped == SecondaryViewerMaxSize) return;
            SecondaryViewerMaxSize = clamped;
            ReleaseSecondaryViewerTextures();
        }
    }

    public float NearClip
    {
        get => nearClipLimit;
        set
        {
            nearClipLimit = Mathf.Clamp(value, MinNearClip, MaxNearClip);
            ApplyCameraOptions();
        }
    }

    public float FarClip
    {
        get => FarClipPlane;
        set
        {
            FarClipPlane = Mathf.Clamp(value, MinFarClip, MaxFarClip);
            ApplyCameraOptions();
        }
    }

    public float SurfaceClipOffset
    {
        get => ClipPlaneOffset;
        set => ClipPlaneOffset = Mathf.Clamp(value, MinClipPlaneOffset, MaxClipPlaneOffset);
    }

    public bool UsePostProcessing
    {
        get => RenderPostProcessing;
        set
        {
            RenderPostProcessing = value;
            ApplyCameraOptions();
        }
    }

    public bool UseOcclusionCulling
    {
        get => OcclusionCulling;
        set
        {
            OcclusionCulling = value;
            ApplyCameraOptions();
        }
    }

    public bool RenderShadows
    {
        get => renderShadows;
        set
        {
            renderShadows = value;
            ApplyCameraOptions();
        }
    }

    public const float MinSurfaceSize = 0.25f;
    public const float MaxSurfaceSize = 10f;

    [NonSerialized] private Vector3 baseScale = Vector3.one;
    [NonSerialized] private Vector2 baseSurfaceSize;
    [NonSerialized] private bool surfaceBaselineCaptured;

    /// <summary>
    /// Physical size of the mirror surface in metres. Driven by scaling this component's transform,
    /// so the surface, its frame and its collider stay in agreement — measured against the size the
    /// content author shipped rather than assuming a 1x1 quad at unit scale.
    /// </summary>
    public Vector2 SurfaceSize
    {
        get
        {
            if (!TryCaptureSurfaceBaseline()) return Vector2.zero;

            Vector3 scale = transform.localScale;
            return new Vector2(
                baseSurfaceSize.x * SafeRatio(scale.x, baseScale.x),
                baseSurfaceSize.y * SafeRatio(scale.y, baseScale.y));
        }
        set
        {
            if (!TryCaptureSurfaceBaseline()) return;
            if (baseSurfaceSize.x <= 0f || baseSurfaceSize.y <= 0f) return;

            float width = Mathf.Clamp(value.x, MinSurfaceSize, MaxSurfaceSize);
            float height = Mathf.Clamp(value.y, MinSurfaceSize, MaxSurfaceSize);

            Vector3 scale = transform.localScale;
            scale.x = baseScale.x * (width / baseSurfaceSize.x);
            scale.y = baseScale.y * (height / baseSurfaceSize.y);
            transform.localScale = scale;
        }
    }

    public float SurfaceWidth
    {
        get => SurfaceSize.x;
        set => SurfaceSize = new Vector2(value, SurfaceSize.y);
    }

    public float SurfaceHeight
    {
        get => SurfaceSize.y;
        set => SurfaceSize = new Vector2(SurfaceSize.x, value);
    }

    /// <summary>True once the authored scale and surface extents are known; false if there is no renderer yet.</summary>
    public bool HasSurfaceSize => TryCaptureSurfaceBaseline();

    private bool TryCaptureSurfaceBaseline()
    {
        if (surfaceBaselineCaptured) return true;
        if (Renderer == null) return false;

        baseScale = transform.localScale;
        if (Mathf.Approximately(baseScale.x, 0f)) baseScale.x = 1f;
        if (Mathf.Approximately(baseScale.y, 0f)) baseScale.y = 1f;

        // Local bounds are the untransformed mesh extents, so this stays correct under rotation
        // where world-space bounds would not. The surface lies in the renderer's local XY plane
        // (its local -Z is the reflection normal).
        Bounds local = Renderer.localBounds;
        Vector3 lossy = Renderer.transform.lossyScale;
        baseSurfaceSize = new Vector2(
            Mathf.Abs(local.size.x * lossy.x),
            Mathf.Abs(local.size.y * lossy.y));

        surfaceBaselineCaptured = true;
        return true;
    }

    private static float SafeRatio(float value, float baseline)
    {
        return Mathf.Approximately(baseline, 0f) ? 1f : value / baseline;
    }

    public const string CutoutShaderName = "BasisMirrorCutout";

    [NonSerialized] private Material cutoutMaterial;
    [NonSerialized] private bool cutoutEnabled;
    [NonSerialized] private MirrorClearFlags clearFlagsBeforeCutout;
    [NonSerialized] private Color clearColorBeforeCutout;

    public bool CutoutEnabled => cutoutEnabled;

    /// <summary>
    /// Clear flags/colour as the user configured them, ignoring the transparent clear the cutout
    /// imposes while it is on. Saving the live values instead would overwrite the real choice.
    /// </summary>
    public MirrorClearFlags ConfiguredClearFlags => cutoutEnabled ? clearFlagsBeforeCutout : clearFlags;
    public Color ConfiguredClearColor => cutoutEnabled ? clearColorBeforeCutout : clearColor;

    /// <summary>
    /// Swaps the surface to the transparent cutout shader and clears the reflection to fully
    /// transparent, so only opaque reflected geometry has alpha and the rest reads through — the
    /// calibration mirror's look. Returns the resulting state; false from a request to enable means
    /// the shader is missing or unsupported and the mirror was left as it was.
    /// </summary>
    public bool SetCutout(bool enabled)
    {
        if (enabled == cutoutEnabled) return cutoutEnabled;
        return enabled ? EnableCutout() : DisableCutout();
    }

    private bool EnableCutout()
    {
        if (Renderer == null) return false;

        Shader shader = Resources.Load<Shader>(CutoutShaderName);
        if (shader == null || !shader.isSupported) return false;

        clearFlagsBeforeCutout = clearFlags;
        clearColorBeforeCutout = clearColor;

        if (cutoutMaterial == null)
            cutoutMaterial = new Material(shader) { name = $"{name} Mirror Cutout" };

        SeedCutoutTextures();
        Renderer.sharedMaterial = cutoutMaterial;
        cutoutEnabled = true;

        ClearColor = new Color(0f, 0f, 0f, 0f);
        ClearFlags = MirrorClearFlags.Color;
        return true;
    }

    private bool DisableCutout()
    {
        cutoutEnabled = false;

        if (Renderer != null && MirrorsMaterial != null)
            Renderer.sharedMaterial = MirrorsMaterial;

        DestroyCutoutMaterial();

        ClearColor = clearColorBeforeCutout;
        ClearFlags = clearFlagsBeforeCutout;
        return false;
    }

    private void SeedCutoutTextures()
    {
        if (cutoutMaterial == null || PortalTextureLeft == null) return;

        cutoutMaterial.SetTexture(ReflectionTexLeftId, PortalTextureLeft);
        cutoutMaterial.SetTexture(ReflectionTexRightId,
            PortalTextureRight != null ? PortalTextureRight : PortalTextureLeft);
    }

    private void DestroyCutoutMaterial()
    {
        if (cutoutMaterial == null) return;

#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(cutoutMaterial);
        else Destroy(cutoutMaterial);
#else
        Destroy(cutoutMaterial);
#endif
        cutoutMaterial = null;
    }

    public int UpdateInterval
    {
        get => UpdateEveryNthFrame;
        set => UpdateEveryNthFrame = Mathf.Clamp(value, MinUpdateInterval, MaxUpdateInterval);
    }

    public float FullRateRange
    {
        get => FullRateDistance;
        set => FullRateDistance = Mathf.Clamp(value, 0f, MaxRateDistance);
    }

    public float HalfRateRange
    {
        get => HalfRateDistance;
        set => HalfRateDistance = Mathf.Clamp(value, 0f, MaxRateDistance);
    }

    public float CullRange
    {
        get => CullDistance;
        set => CullDistance = Mathf.Clamp(value, 0f, MaxRateDistance);
    }

    public string DisplayName => gameObject != null ? gameObject.name : "(destroyed)";

    public Vector2Int EffectiveResolution
    {
        get
        {
            GetEffectiveResolution(out int width, out int height);
            return new Vector2Int(width, height);
        }
    }

    public static bool ResolutionIsOverriddenGlobally =>
        BasisSettingsDefaults.UseMirrorQualityOverride.RawValue;

    public const int MinResolution = 64;
    public const int MaxResolution = 8192;
    public const int MinUpdateInterval = 1;
    public const int MaxUpdateInterval = 8;
    public const float MinNearClip = 0.001f;
    public const float MaxNearClip = 1f;
    public const float MinFarClip = 1f;
    public const float MaxFarClip = 1000f;
    public const float MinClipPlaneOffset = 0f;
    public const float MaxClipPlaneOffset = 0.5f;
    public const float MaxRateDistance = 200f;

    public void SetTargetShape(int width, int height, int depthBits, int msaa)
    {
        int newWidth = Mathf.Clamp(width, MinResolution, MaxResolution);
        int newHeight = Mathf.Clamp(height, MinResolution, MaxResolution);
        int newDepth = depthBits >= 24 ? 24 : depthBits >= 16 ? 16 : 0;
        int newMsaa = msaa >= 8 ? 8 : msaa >= 4 ? 4 : msaa >= 2 ? 2 : 1;

        if (newWidth == XSize && newHeight == YSize && newDepth == depth && newMsaa == Antialiasing) return;

        XSize = newWidth;
        YSize = newHeight;
        depth = newDepth;
        Antialiasing = newMsaa;
        RebuildReflectionTargets();
    }

    public void RebuildReflectionTargets()
    {
        if (!IsActive) return;

        ReplacePortalTexture(StereoscopicEye.Left, ref PortalTextureLeft, LeftCamera);
        ReplacePortalTexture(StereoscopicEye.Right, ref PortalTextureRight, RightCamera);
        ReleaseSecondaryViewerTextures();
        SeedCutoutTextures();

        BindReflectionTextures(PortalTextureLeft, PortalTextureRight);
        primaryBound = true;
    }

    private void ReplacePortalTexture(StereoscopicEye eye, ref RenderTexture texture, Camera portalCamera)
    {
        RenderTexture previous = texture;
        texture = CreatePortalTexture(eye);
        if (portalCamera) portalCamera.targetTexture = texture;
        DestroyTexture(previous);
    }

    private void ReleaseSecondaryViewerTextures()
    {
        foreach (KeyValuePair<Camera, SecondaryViewerState> pair in secondaryViewers)
        {
            ReleaseViewerTexture(pair.Value);
        }
    }

    private void ApplyCameraOptions()
    {
        ApplyCameraOptions(LeftCamera, leftCameraData);
        ApplyCameraOptions(RightCamera, rightCameraData);
    }

    private void ApplyCameraOptions(Camera camera, UniversalAdditionalCameraData cameraData)
    {
        if (camera == null) return;

        camera.nearClipPlane = Mathf.Max(0.001f, nearClipLimit);
        camera.farClipPlane = FarClipPlane;
        camera.cullingMask = ReflectingLayers;
        camera.useOcclusionCulling = OcclusionCulling;

        if (cameraData == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        cameraData.allowXRRendering = false;
#else
        cameraData.allowXRRendering = allowXRRendering;
#endif
        cameraData.renderPostProcessing = RenderPostProcessing;
        cameraData.renderShadows = renderShadows;
    }

    public Color ClearColor
    {
        get => clearColor;
        set
        {
            clearColor = value;
            if (clearFlags == MirrorClearFlags.Color)
            {
                if (LeftCamera) LeftCamera.backgroundColor = clearColor;
                if (RightCamera) RightCamera.backgroundColor = clearColor;
            }
        }
    }

    private BasisMeshRendererCheck basisMeshRendererCheck;
    private UniversalAdditionalCameraData leftCameraData;
    private UniversalAdditionalCameraData rightCameraData;
    private BasisGazeTarget gazeTarget;
    private Vector3 thisPosition;
    private Vector3 normal;
    private readonly Vector3 projectionDirection = -Vector3.forward;
    private static readonly Matrix4x4 xFlip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
    private int frameCounter;
    private bool materialApplied;
    [NonSerialized] public int TierIndex = -1;

    /// <summary>Per-viewer reflection state for cameras other than the primary (player) camera.</summary>
    private sealed class SecondaryViewerState
    {
        public RenderTexture Texture;
        public int LastCapturedFrame;
    }

    private readonly Dictionary<Camera, SecondaryViewerState> secondaryViewers = new Dictionary<Camera, SecondaryViewerState>();
    private MaterialPropertyBlock reflectionBlock;
    private bool primaryBound;
#if UNITY_EDITOR
    /// <summary>Scene View camera observed actually rendering, so hidden Scene Views don't cost a capture.</summary>
    private Camera lastSceneViewCamera;
    private int lastSceneViewRenderFrame = -1000;
#endif
    private readonly Plane[] frustumPlanes = new Plane[6];
    private static readonly List<Camera> ViewerScratch = new List<Camera>(4);
    private static readonly List<Camera> PruneScratch = new List<Camera>(4);
    private static readonly int ReflectionTexLeftId = Shader.PropertyToID("_ReflectionTexLeft");
    private static readonly int ReflectionTexRightId = Shader.PropertyToID("_ReflectionTexRight");
    /// <summary>Frames a secondary viewer may go uncaptured before its texture is released.</summary>
    private const int ViewerStaleFrames = 300;

    private void Start()
    {
        BasisMirrorSettingsStore.ApplyPersonalMirrorBehavior(this);
    }

    private void UpdatePersonalMirrorPlayspace()
    {
        if (!BasisMirrorSettingsStore.PersonalMirrorMovesWithPlayspace(this) ||
            !BasisDeviceManagement.IsUserInDesktop() ||
            BasisDesktopEye.Instance == null)
        {
            desktopPlayspaceYawInitialized = false;
            return;
        }

        float currentYaw = BasisDesktopEye.Instance.rotationYaw;
        if (!desktopPlayspaceYawInitialized)
        {
            lastDesktopPlayspaceYaw = currentYaw;
            desktopPlayspaceYawInitialized = true;
            return;
        }

        float deltaYaw = Mathf.DeltaAngle(lastDesktopPlayspaceYaw, currentYaw);
        lastDesktopPlayspaceYaw = currentYaw;
        if (Mathf.Approximately(deltaYaw, 0f)) return;

        Quaternion deltaRotation = Quaternion.AngleAxis(deltaYaw, Vector3.up);
        transform.localPosition = deltaRotation * transform.localPosition;
        transform.localRotation = deltaRotation * transform.localRotation;
    }

    private void OnEnable()
    {
        IsActive = false;
        IsAbleToRender = false;

        if (ReflectingLayers == 0)
        {
            int remoteLayer = LayerMask.NameToLayer("RemotePlayerAvatar");
            int localLayer = LayerMask.NameToLayer("LocalPlayerAvatar");
            int defaultLayer = LayerMask.NameToLayer("Default");

            if (remoteLayer < 0 || localLayer < 0 || defaultLayer < 0)
            {
                Debug.LogError("One or more required layers are missing (RemotePlayerAvatar / LocalPlayerAvatar / Default).");
            }
            else
            {
                ReflectingLayers = (1 << remoteLayer) | (1 << localLayer) | (1 << defaultLayer);
            }
        }

        if (Renderer == null || MirrorsMaterial == null)
        {
            Debug.LogError("Renderer or MirrorsMaterial not assigned.");
            return;
        }

        if (basisMeshRendererCheck == null)
            basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        basisMeshRendererCheck.Check += VisibilityFlag;

        BasisDeviceManagement.OnBootModeChanged += BootModeChanged;
        BasisLocalCameraDriver.InstanceExists += Initialize;
        BasisSettingsDefaults.MirrorQuality.OnChanged += OnMirrorQualityChanged;
        BasisSettingsDefaults.UseMirrorQualityOverride.OnChanged += OnMirrorQualityOverrideChanged;
        BasisSettingsDefaults.Antialiasing.OnChanged += OnAntialiasingChanged;

        BasisMirrorSettingsStore.ApplyTo(this);

        if (BasisLocalCameraDriver.HasInstance)
            Initialize();

        Application.onBeforeRender += OnBeforeRender;
        RenderPipeline.beginCameraRendering += OnBeginCameraRendering;

        BasisMirrorRegistry.Add(this);
    }

    private void OnDisable()
    {
        BasisMirrorRegistry.Remove(this);
        CleanUp();
    }

    private void OnDestroy()
    {
        BasisMirrorRegistry.Remove(this);
        DestroyCutoutMaterial();
        BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
        BasisSettingsDefaults.MirrorQuality.OnChanged -= OnMirrorQualityChanged;
        BasisSettingsDefaults.UseMirrorQualityOverride.OnChanged -= OnMirrorQualityOverrideChanged;
        BasisSettingsDefaults.Antialiasing.OnChanged -= OnAntialiasingChanged;
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipeline.beginCameraRendering -= OnBeginCameraRendering;
    }

    private void BootModeChanged(string _) => StartCoroutine(ResetMirror());
    private void OnMirrorQualityChanged(string _) => StartCoroutine(ResetMirror());
    private void OnMirrorQualityOverrideChanged(bool _) => StartCoroutine(ResetMirror());
    private void OnAntialiasingChanged(string _) => StartCoroutine(ResetMirror());

    private IEnumerator ResetMirror()
    {
        yield return null;
        CleanUp();
        OnEnable();
    }

    private void CleanUp()
    {
        BasisLocalCameraDriver.InstanceExists -= Initialize;
        desktopPlayspaceYawInitialized = false;

        if (basisMeshRendererCheck != null)
            basisMeshRendererCheck.Check -= VisibilityFlag;

        // Mirror every OnEnable subscription so ResetMirror's CleanUp()+OnEnable() cycle and
        // component toggling can't stack duplicate handlers (double mirror renders per frame).
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipeline.beginCameraRendering -= OnBeginCameraRendering;
        BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
        BasisSettingsDefaults.MirrorQuality.OnChanged -= OnMirrorQualityChanged;
        BasisSettingsDefaults.UseMirrorQualityOverride.OnChanged -= OnMirrorQualityOverrideChanged;
        BasisSettingsDefaults.Antialiasing.OnChanged -= OnAntialiasingChanged;

        BasisMirrorTierScheduler.Unregister(this);
        primaryBound = false;
        DisposePortalResources();

        if (gazeTarget != null)
            gazeTarget.enabled = false;

        IsActive = false;
        IsAbleToRender = false;
        InsideRendering = false;
    }

    private static void DestroyTexture(RenderTexture texture)
    {
        if (!texture) return;

        texture.Release();
#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(texture);
        else Destroy(texture);
#else
        Destroy(texture);
#endif
    }

    private void DisposePortalResources()
    {
        DestroyTexture(PortalTextureLeft);
        DestroyTexture(PortalTextureRight);

        BasisCullingCameraRegistry.Unregister(LeftCamera);
        if (LeftCamera) Destroy(LeftCamera.gameObject);
        if (RightCamera) Destroy(RightCamera.gameObject);

        PortalTextureLeft = null;
        PortalTextureRight = null;
        LeftCamera = RightCamera = null;
        leftCameraData = rightCameraData = null;

        foreach (KeyValuePair<Camera, SecondaryViewerState> pair in secondaryViewers)
        {
            ReleaseViewerTexture(pair.Value);
        }
        secondaryViewers.Clear();
    }

    private static void ReleaseViewerTexture(SecondaryViewerState state)
    {
        if (state.Texture != null)
        {
            state.Texture.Release();
            Destroy(state.Texture);
            state.Texture = null;
        }
    }

    private void GetEffectiveResolution(out int width, out int height)
    {
        if (BasisSettingsDefaults.UseMirrorQualityOverride.RawValue &&
            int.TryParse(BasisSettingsDefaults.MirrorQuality.RawValue, out int overrideRes) && overrideRes > 0)
        {
            // The user's explicit override wins outright, even past the standalone cap.
            width = overrideRes;
            height = overrideRes;
        }
        else
        {
            width = XSize;
            height = YSize;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Standalone ceiling: research consensus is 512-768 per eye; the 2048 world default is
            // a measured slideshow on mobile GPUs (two eyes, per mirror, per frame).
            width = Mathf.Min(width, StandaloneResolutionCap);
            height = Mathf.Min(height, StandaloneResolutionCap);
#endif
        }
    }

    private const int StandaloneResolutionCap = 768;

    private void Initialize()
    {
        if (IsActive) return;
        if (Renderer == null)
        {
            BasisDebug.LogError("BasisSDKMirror is missing its Renderer reference; mirror will not initialize.");
            return;
        }

        // Drop any stale cameras/textures from a prior init that didn't reach a clean teardown
        // (e.g. resources orphaned across a Play-Mode domain reload).
        DisposePortalResources();

        var mainCamera = BasisLocalCameraDriver.Instance.Camera;
        if (mainCamera == null)
        {
            BasisDebug.LogError("BasisSDKMirror could not initialize: the local camera is not available yet.");
            return;
        }

        CreatePortalCamera(mainCamera, StereoscopicEye.Left, ref LeftCamera, ref PortalTextureLeft, ref leftCameraData);
        CreatePortalCamera(mainCamera, StereoscopicEye.Right, ref RightCamera, ref PortalTextureRight, ref rightCameraData);
        BasisCullingCameraRegistry.Register(LeftCamera);

        // The reflection textures are bound per-renderer through a MaterialPropertyBlock
        // (OnBeginCameraRendering), never onto the material asset: mirrors sharing a material
        // asset used to hijack each other's reflections, and despawning one destroyed textures
        // still bound to the survivors. Only assign the material once so a runtime swap of the
        // surface material (e.g. the calibration cutout) survives a quality-change re-init.
        if (!materialApplied)
        {
            Renderer.sharedMaterial = MirrorsMaterial;
            materialApplied = true;
        }

        // A cutout enabled from saved settings is applied in OnEnable, before this runs, so the
        // first-init assignment above would drop it on a fresh spawn.
        SeedCutoutTextures();
        if (cutoutEnabled && cutoutMaterial != null)
            Renderer.sharedMaterial = cutoutMaterial;

        IsAbleToRender = Renderer.isVisible;
        IsActive = true;
        InsideRendering = false;
        primaryBound = false;
        BasisMirrorTierScheduler.Register(this);

        // Set up gaze target so the eye driver focuses on the player's reflection
        if (gazeTarget == null)
            gazeTarget = BasisHelpers.GetOrAddComponent<BasisGazeTarget>(gameObject);
        gazeTarget.Priority = 2f;
        gazeTarget.UseTransformPosition = false;
        gazeTarget.enabled = true;
    }
    private static Vector3 TransformPoint(Vector3 position, Quaternion rotation, Vector3 pointLocal)
    {
        return rotation * pointLocal + position;
    }
    private void OnBeforeRender()
    {
        UpdatePersonalMirrorPlayspace();

        // Self-heal after a Play-Mode domain reload (e.g. Test In Editor + reselect triggers
        // an AssetDatabase.Refresh()): the camera driver's serialized HasEvents flag persists
        // across the reload, so its InstanceExists event never re-fires for our subscription.
        if (!IsActive && BasisLocalCameraDriver.HasInstance)
            Initialize();

        if (!IsActive || !IsAbleToRender) return;

        Camera cam = null;
        if (BasisLocalCameraDriver.HasInstance)
            cam = BasisLocalCameraDriver.Instance.Camera;

#if UNITY_EDITOR
        // Optional SceneView support when testing
        if (cam == null && SceneView.lastActiveSceneView != null)
            cam = SceneView.lastActiveSceneView.camera;
#endif
        if (cam == null) return;

        frameCounter++;
        int rate = BasisMirrorTierScheduler.GetRate(this);
        if (rate == 0) return; // beyond CullDistance: keep the last image
        rate = Mathf.Max(rate, UpdateEveryNthFrame);
        if (rate > 1 && (frameCounter % rate) != 0) return;

        BasisLocalAvatarDriver.ScaleHeadToNormal();

        OnCamerasRenderering?.Invoke();

        thisPosition = Renderer.transform.position;
        normal = Renderer.transform.TransformDirection(projectionDirection).normalized;

        // Update gaze target: reflect the player's eye position across the mirror plane
        if (gazeTarget != null)
        {
            Vector3 eyePos = BasisLocalCameraDriver.Position;
            Renderer.transform.GetPositionAndRotation(out Vector3 planePosWS, out Quaternion planeRotWS);
            Vector3 eyeLocal = InverseTransformPoint(planePosWS, planeRotWS, eyePos);
            Vector3 reflLocal = Vector3.Reflect(eyeLocal, Vector3.forward);
            gazeTarget.FocusPoint = TransformPoint(planePosWS, planeRotWS, reflLocal);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        float lodBiasWas = QualitySettings.lodBias;
        QualitySettings.lodBias = lodBiasWas * 0.75f;
#endif
        try
        {
            RenderBothEyes(cam);

            CaptureSecondaryViewers(cam);
        }
        catch (Exception e)
        {
            // One bad frame skips THIS mirror's frame only — no shared state, no world-wide freeze.
            BasisDebug.LogWarning($"BasisSDKMirror '{name}' skipped a frame: {e.Message}");
        }
        finally
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            QualitySettings.lodBias = lodBiasWas;
#endif
            InsideRendering = false;

            OnCamerasFinished?.Invoke();

            BasisLocalAvatarDriver.ScaleHeadToZero();
        }

        if ((Time.frameCount & 127) == 0)
            PruneStaleViewers();
    }

    /// <summary>
    /// Renders a mono reflection for every registered viewer camera (handheld capture,
    /// Scene View) that can currently see the mirror, each into its own texture. Render
    /// requests cannot be submitted from inside the SRP render loop, so this must happen
    /// here in onBeforeRender; OnBeginCameraRendering later binds the matching texture
    /// for whichever camera is about to draw the mirror.
    /// </summary>
    private void CaptureSecondaryViewers(Camera primary)
    {
        ViewerScratch.Clear();
        BasisMirrorViewerRegistry.CollectInto(ViewerScratch);
#if UNITY_EDITOR
        if (lastSceneViewCamera != null && Time.frameCount - lastSceneViewRenderFrame <= 4)
            ViewerScratch.Add(lastSceneViewCamera);
#endif
        int count = ViewerScratch.Count;
        if (count == 0 || InsideRendering) return;
        InsideRendering = true;

        for (int Index = 0; Index < count; Index++)
        {
            Camera viewer = ViewerScratch[Index];
            if (viewer == null || ReferenceEquals(viewer, primary)) continue;
            if (BasisMirrorReflectionCamera.IsReflectionCamera(viewer)) continue;
            if (!CanSeeMirror(viewer)) continue;

            SecondaryViewerState state = GetOrCreateViewerState(viewer);
            viewer.transform.GetPositionAndRotation(out Vector3 viewerPos, out Quaternion viewerRot);
            RenderEye(viewer, MonoOrStereoscopicEye.Mono, viewerPos, viewerRot, state.Texture);
            state.LastCapturedFrame = Time.frameCount;
        }

        InsideRendering = false;
    }

    private bool CanSeeMirror(Camera viewer)
    {
        if (Vector3.Dot(normal, viewer.transform.position - thisPosition) < 0f)
            return false;

        Matrix4x4 worldToProjection = viewer.projectionMatrix * viewer.worldToCameraMatrix;
        GeometryUtility.CalculateFrustumPlanes(worldToProjection, frustumPlanes);
        return GeometryUtility.TestPlanesAABB(frustumPlanes, Renderer.bounds);
    }

    private SecondaryViewerState GetOrCreateViewerState(Camera viewer)
    {
        if (!secondaryViewers.TryGetValue(viewer, out SecondaryViewerState state))
        {
            state = new SecondaryViewerState();
            secondaryViewers[viewer] = state;
        }
        if (state.Texture == null)
        {
            GetEffectiveResolution(out int width, out int height);
            var desc = new RenderTextureDescriptor(
                Mathf.Min(width, SecondaryViewerMaxSize), Mathf.Min(height, SecondaryViewerMaxSize),
                RenderTextureFormat.Default, depth)
            {
                msaaSamples = 1,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                useMipMap = false,
                autoGenerateMips = false,
                vrUsage = VRTextureUsage.None,
                dimension = TextureDimension.Tex2D
            };
            state.Texture = new RenderTexture(desc)
            {
                name = $"__MirrorReflectionViewer_{GetEntityId()}",
                anisoLevel = 0
            };
            state.Texture.Create();
        }
        return state;
    }

    private void PruneStaleViewers()
    {
        if (secondaryViewers.Count == 0) return;

        PruneScratch.Clear();
        foreach (KeyValuePair<Camera, SecondaryViewerState> pair in secondaryViewers)
        {
            if (pair.Key == null || Time.frameCount - pair.Value.LastCapturedFrame > ViewerStaleFrames)
                PruneScratch.Add(pair.Key);
        }

        int count = PruneScratch.Count;
        for (int Index = 0; Index < count; Index++)
        {
            if (secondaryViewers.TryGetValue(PruneScratch[Index], out SecondaryViewerState state))
            {
                ReleaseViewerTexture(state);
                secondaryViewers.Remove(PruneScratch[Index]);
            }
        }
    }

    private void RenderBothEyes(Camera camera)
    {
        if (InsideRendering) return; // avoid recursion in SRP
        InsideRendering = true;

        camera.transform.GetPositionAndRotation(out Vector3 srcPos, out Quaternion srcRot);

        if (camera.stereoEnabled)
        {
            RenderEye(camera, MonoOrStereoscopicEye.Left, srcPos, srcRot, PortalTextureLeft);
            RenderEye(camera, MonoOrStereoscopicEye.Right, srcPos, srcRot, PortalTextureRight);
        }
        else
        {
            RenderEye(camera, MonoOrStereoscopicEye.Mono, srcPos, srcRot, PortalTextureLeft);
        }

        InsideRendering = false;
    }

    private void RenderEye(Camera sourceCamera, MonoOrStereoscopicEye eye, Vector3 srcPos, Quaternion srcRot, RenderTexture destination)
    {
        Camera portalCamera = (eye == MonoOrStereoscopicEye.Right) ? RightCamera : LeftCamera;
        if (!portalCamera) return;
        if (destination == null) return;

        // Portal cameras are shared by every viewer now, so per-render state must be refreshed.
        updateCameraClearFlags(portalCamera, sourceCamera);
        UpdateAttachmentRequirement((eye == MonoOrStereoscopicEye.Right) ? rightCameraData : leftCameraData, destination);

        // --- Eye pose/projection from source camera ---
        Vector3 eyeOriginWS;
        Matrix4x4 proj;

        if (eye == MonoOrStereoscopicEye.Mono)
        {
            eyeOriginWS = srcPos;
            proj = sourceCamera.projectionMatrix;
        }
        else
        {
            var e = (StereoscopicEye)eye;
            eyeOriginWS = sourceCamera.GetStereoViewMatrix(e).inverse.MultiplyPoint(Vector3.zero);
            proj = sourceCamera.GetStereoProjectionMatrix(e);
            // Stereo projections are identity for the first frames until the XR display subsystem
            // is up (documented Unity behavior); a real perspective matrix always has m33 == 0.
            if (proj.m33 != 0f) return;
        }
        if (float.IsNaN(eyeOriginWS.x) || float.IsNaN(eyeOriginWS.y) || float.IsNaN(eyeOriginWS.z)) return;

        // The SURFACE renderer's transform is the plane: reflection, grazing handling and the
        // oblique clip all derive from it, sign-agnostic, so they cannot disagree. Deriving the
        // reflection from the component's transform used to render the ceiling on world mirrors
        // whose component sits on a differently-oriented object than the surface quad.
        Renderer.transform.GetPositionAndRotation(out Vector3 planePosWS, out Quaternion planeRotWS);
        Vector3 planeNormal = planeRotWS * Vector3.forward;

        Vector3 fwd = srcRot * Vector3.forward;
        Vector3 up = srcRot * Vector3.up;

        Vector3 reflPosWS = eyeOriginWS - 2f * Vector3.Dot(eyeOriginWS - planePosWS, planeNormal) * planeNormal;
        Vector3 reflFwdWS = fwd - 2f * Vector3.Dot(fwd, planeNormal) * planeNormal;
        Vector3 reflUpWS = up - 2f * Vector3.Dot(up, planeNormal) * planeNormal;
        if (reflFwdWS.sqrMagnitude < 1e-6f || reflUpWS.sqrMagnitude < 1e-6f) return;

        portalCamera.transform.SetPositionAndRotation(reflPosWS, Quaternion.LookRotation(reflFwdWS, reflUpWS));

        // Clamp near/far
        if (BasisSettingsDefaults.UseCameraClipOverride.RawValue)
        {
            portalCamera.nearClipPlane = Mathf.Max(0.001f, BasisSettingsDefaults.CameraClipNear.RawValue);
            portalCamera.farClipPlane = BasisSettingsDefaults.CameraClipFar.RawValue;
        }
        else
        {
            portalCamera.nearClipPlane = Mathf.Max(0.001f, nearClipLimit);
            portalCamera.farClipPlane = FarClipPlane;
        }

        Matrix4x4 worldToCam = portalCamera.worldToCameraMatrix;

        // Culling ALWAYS uses the plain (non-oblique) projection: oblique frustum planes tilt at
        // steep view angles and wrongly cull renderers that are actually visible in the mirror.
        portalCamera.cullingMatrix = xFlip * proj * xFlip * worldToCam;

        // Skyboxes are infinitely distant and must not receive the mirror-plane oblique clip.
        // Preserve the clean reflected projection so URP can render the skybox with the same
        // reflected view as geometry but without the clip-plane distortion.
        portalCamera.nonJitteredProjectionMatrix = xFlip * proj * xFlip;

        // Oblique clip to avoid "behind mirror"; clip normal chosen toward the eye.
        Vector3 clipNormal = planeNormal * Mathf.Sign(Vector3.Dot(eyeOriginWS - planePosWS, planeNormal));
        Vector4 clipPlaneCamSpace = BasisHelpers.CameraSpacePlane(
            worldToCam, planePosWS, clipNormal, EffectiveClipPlaneOffset());

        clipPlaneCamSpace.x *= -1f; // compensate for x-flip
        CalculateObliqueMatrix(ref proj, clipPlaneCamSpace);

        // Keep triangle winding after reflection
        portalCamera.projectionMatrix = xFlip * proj * xFlip;

        SubmitRenderRequest(portalCamera, destination);
    }

    private float EffectiveClipPlaneOffset()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Serialized 0.001 from older content z-fights at grazing angles on mobile depth precision.
        return ClipPlaneOffset < 0.02f ? 0.05f : ClipPlaneOffset;
#else
        return ClipPlaneOffset;
#endif
    }

    /// <summary>
    /// URP callback before each camera render: binds the reflection captured for that
    /// camera's viewpoint, so mirrors stay correct when drawn by handheld cameras or the
    /// Scene View instead of the player camera. The shader samples in screen space, so a
    /// texture is only valid for the camera whose pose produced it.
    /// </summary>
    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
#if UNITY_EDITOR
        if (renderingCamera.cameraType == CameraType.SceneView)
        {
            lastSceneViewCamera = renderingCamera;
            lastSceneViewRenderFrame = Time.frameCount;
        }
#endif
        if (!IsActive || Renderer == null) return;

        if (secondaryViewers.Count != 0 &&
            secondaryViewers.TryGetValue(renderingCamera, out SecondaryViewerState state) &&
            state.Texture != null)
        {
            BindReflectionTextures(state.Texture, state.Texture);
            primaryBound = false;
        }
        else if (!primaryBound)
        {
            BindReflectionTextures(PortalTextureLeft, PortalTextureRight);
            primaryBound = true;
        }
    }

    private void BindReflectionTextures(RenderTexture left, RenderTexture right)
    {
        if (left == null || right == null) return;

        if (reflectionBlock == null)
            reflectionBlock = new MaterialPropertyBlock();

        reflectionBlock.SetTexture(ReflectionTexLeftId, left);
        reflectionBlock.SetTexture(ReflectionTexRightId, right);
        Renderer.SetPropertyBlock(reflectionBlock);
    }

    public void SubmitRenderRequest(Camera camera, RenderTexture texture2D)
    {
        if (!camera || !texture2D) return;

        var request = new UniversalRenderPipeline.SingleCameraRequest
        {
            destination = texture2D,
            mipLevel = 0,
            slice = 0,
            face = CubemapFace.Unknown
        };

        if (UniversalRenderPipeline.SupportsRenderRequest(camera, request))
        {
            UniversalRenderPipeline.SubmitRenderRequest(camera, request);
        }
        // else: active RP doesn’t support this request type; safely skip
    }


    private static Vector3 InverseTransformPoint(Vector3 position, Quaternion rotation, Vector3 point)
    {
        return Quaternion.Inverse(rotation) * (point - position);
    }

    /// <summary>|clipPlane · q| below this leaves the projection un-clipped instead of exploding it.</summary>
    public const float ObliqueDotEpsilon = 0.05f;

    /// <summary>
    /// Calculates an oblique projection matrix. Gated on the dot product's own conditioning, not
    /// eye-to-plane distance: near-grazing/view-parallel geometry collapses the dot term and the
    /// matrix explodes (Adreno GPU hangs). An Approximately(0) check misses near-zero-but-not-zero,
    /// so anything inside the epsilon renders without the clip — objects behind the plane can peek
    /// for a frame at extreme grazing angles, which beats a device freeze.
    /// </summary>
    public static void CalculateObliqueMatrix(ref Matrix4x4 projection, float4 clipPlane)
    {
        float4 q = projection.inverse * new float4(math.sign(clipPlane.x), math.sign(clipPlane.y), 1.0f, 1.0f);
        float dot = math.dot(clipPlane, q);
        if (math.abs(dot) < ObliqueDotEpsilon) return;

        float4 c = clipPlane * (2.0f / dot);
        projection[2] = c.x - projection[3];
        projection[6] = c.y - projection[7];
        projection[10] = c.z - projection[11];
        projection[14] = c.w - projection[15];
    }

    private RenderTexture CreatePortalTexture(StereoscopicEye eye)
    {
        GetEffectiveResolution(out int effectiveWidth, out int effectiveHeight);
#if UNITY_ANDROID && !UNITY_EDITOR
        // 16-bit depth is plenty for a 25 m far plane at half the tile bandwidth; 4x MSAA resolves
        // on-tile on Adreno (Meta-recommended) and keeps edges clean at the reduced resolution.
        int effectiveDepth = 16;
        int effectiveMsaa = Mathf.Max(Antialiasing, 4);
#else
        int effectiveDepth = depth;
        int effectiveMsaa = Mathf.Max(1, Antialiasing);
#endif
        effectiveMsaa = BasisCameraTargetMsaa.Clamp(effectiveMsaa);

        var desc = new RenderTextureDescriptor(effectiveWidth, effectiveHeight, RenderTextureFormat.Default, effectiveDepth)
        {
            msaaSamples = effectiveMsaa,
            sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
            useMipMap = false,
            autoGenerateMips = false,
            vrUsage = VRTextureUsage.None,
            dimension = TextureDimension.Tex2D
        };

        var texture = new RenderTexture(desc)
        {
            name = $"__MirrorReflection{eye}_{GetEntityId()}",
            anisoLevel = 0
        };
        texture.Create();
        return texture;
    }

    private void CreatePortalCamera(Camera sourceCamera, StereoscopicEye eye, ref Camera portalCamera, ref RenderTexture portalTexture, ref UniversalAdditionalCameraData portalCameraData)
    {
        portalTexture = CreatePortalTexture(eye);

        CreateNewCamera(sourceCamera, out portalCamera, out portalCameraData);
        portalCamera.targetTexture = portalTexture;
    }

    private void CreateNewCamera(Camera sourceCamera, out Camera newCamera, out UniversalAdditionalCameraData cameraData)
    {
        // Built bare on purpose — CopyFrom(mainCamera) inherits stereoTargetEye = Both and the XR
        // flags that let HMD tracking override the computed reflected pose on Quest multiview
        // ("the mirror is a handheld camera tilting around the room"). Pose and projection are set
        // explicitly every frame, so nothing else needs copying.
        GameObject camObj = new GameObject($"MirrorCam_{GetEntityId()}_{sourceCamera.GetEntityId()}", typeof(Camera), typeof(BasisMirrorReflectionCamera));
        camObj.TryGetComponent<Camera>(out newCamera);
        newCamera.enabled = false;

        newCamera.depth = 2;
        newCamera.allowHDR = false;
        newCamera.allowMSAA = true;
        updateCameraClearFlags(newCamera, sourceCamera);

        cameraData = newCamera.GetUniversalAdditionalCameraData();
        cameraData.isMirrorReflectionCamera = true;
        ApplyCameraOptions(newCamera, cameraData);

        if (cameraData != null)
        {
            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off; // refreshed per render below
        }
    }

    /// <summary>
    /// With post-processing off and both textures overridden Off, a multisampled target is the only
    /// thing keeping URP's intermediate colour/depth attachments alive for this camera. Antialiasing
    /// Off (and the always-single-sampled secondary viewer textures) drops it to rendering straight
    /// into the shared reflection texture, which comes back holding nothing but URP's background
    /// clear. Asking for the depth texture restores the attachments — the same graph the mirror runs
    /// at every MSAA level — for one depth copy on the renders that would otherwise be empty.
    /// </summary>
    private static void UpdateAttachmentRequirement(UniversalAdditionalCameraData cameraData, RenderTexture destination)
    {
        if (cameraData == null) return;

        cameraData.requiresDepthOption = BasisCameraTargetMsaa.ClampsToSingleSample(destination.antiAliasing)
            ? CameraOverrideOption.On
            : CameraOverrideOption.Off;
    }

    private void VisibilityFlag(bool isVisible)
    {
        IsAbleToRender = isVisible;
    }

    private void updateCameraClearFlags(Camera camera, Camera refCamera)
    {
        switch (clearFlags)
        {
            case MirrorClearFlags.Skybox:
                camera.clearFlags = CameraClearFlags.Skybox;
                break;
            case MirrorClearFlags.Color:
                camera.backgroundColor = clearColor;
                camera.clearFlags = CameraClearFlags.Color;
                break;
            case MirrorClearFlags.Depth:
                camera.clearFlags = CameraClearFlags.Depth;
                break;
            case MirrorClearFlags.Nothing:
                camera.clearFlags = CameraClearFlags.Nothing;
                break;
            case MirrorClearFlags.FromReferenceCamera:
            default:
                if (refCamera == null)
                {
                    return;
                }
                camera.backgroundColor = refCamera.backgroundColor;
                camera.clearFlags = refCamera.clearFlags;
                break;
        }
    }
}
