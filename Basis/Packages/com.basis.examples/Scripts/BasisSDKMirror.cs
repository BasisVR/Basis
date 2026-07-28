using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
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
            Camera refCamera = BasisLocalCameraDriver.Instance.Camera;
            if (LeftCamera) updateCameraClearFlags(LeftCamera, refCamera);
            if (RightCamera) updateCameraClearFlags(RightCamera, refCamera);
        }
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

        if (BasisLocalCameraDriver.HasInstance)
            Initialize();

        Application.onBeforeRender += OnBeforeRender;
        RenderPipeline.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        CleanUp();
    }

    private void OnDestroy()
    {
        BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
        BasisSettingsDefaults.MirrorQuality.OnChanged -= OnMirrorQualityChanged;
        BasisSettingsDefaults.UseMirrorQualityOverride.OnChanged -= OnMirrorQualityOverrideChanged;
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipeline.beginCameraRendering -= OnBeginCameraRendering;
    }

    private void BootModeChanged(string _) => StartCoroutine(ResetMirror());
    private void OnMirrorQualityChanged(string _) => StartCoroutine(ResetMirror());
    private void OnMirrorQualityOverrideChanged(bool _) => StartCoroutine(ResetMirror());

    private IEnumerator ResetMirror()
    {
        yield return null;
        CleanUp();
        OnEnable();
    }

    private void CleanUp()
    {
        BasisLocalCameraDriver.InstanceExists -= Initialize;

        if (basisMeshRendererCheck != null)
            basisMeshRendererCheck.Check -= VisibilityFlag;

        // Mirror every OnEnable subscription so ResetMirror's CleanUp()+OnEnable() cycle and
        // component toggling can't stack duplicate handlers (double mirror renders per frame).
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipeline.beginCameraRendering -= OnBeginCameraRendering;
        BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
        BasisSettingsDefaults.MirrorQuality.OnChanged -= OnMirrorQualityChanged;
        BasisSettingsDefaults.UseMirrorQualityOverride.OnChanged -= OnMirrorQualityOverrideChanged;

        BasisMirrorTierScheduler.Unregister(this);
        primaryBound = false;
        DisposePortalResources();

        if (gazeTarget != null)
            gazeTarget.enabled = false;

        IsActive = false;
        IsAbleToRender = false;
        InsideRendering = false;
    }

    private void DisposePortalResources()
    {
        if (PortalTextureLeft)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(PortalTextureLeft);
            else Destroy(PortalTextureLeft);
#else
            Destroy(PortalTextureLeft);
#endif
        }
        if (PortalTextureRight)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(PortalTextureRight);
            else Destroy(PortalTextureRight);
#else
            Destroy(PortalTextureRight);
#endif
        }

        BasisCullingCameraRegistry.Unregister(LeftCamera);
        if (LeftCamera) Destroy(LeftCamera.gameObject);
        if (RightCamera) Destroy(RightCamera.gameObject);

        PortalTextureLeft = null;
        PortalTextureRight = null;
        LeftCamera = RightCamera = null;

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

        CreatePortalCamera(mainCamera, StereoscopicEye.Left, ref LeftCamera, ref PortalTextureLeft);
        CreatePortalCamera(mainCamera, StereoscopicEye.Right, ref RightCamera, ref PortalTextureRight);
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

        // Portal cameras are shared by every viewer now, so per-render state must be refreshed.
        updateCameraClearFlags(portalCamera, sourceCamera);

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
            portalCamera.nearClipPlane = Mathf.Max(nearClipLimit, portalCamera.nearClipPlane);
            portalCamera.farClipPlane = FarClipPlane;
        }

        Matrix4x4 worldToCam = portalCamera.worldToCameraMatrix;

        // Culling ALWAYS uses the plain (non-oblique) projection: oblique frustum planes tilt at
        // steep view angles and wrongly cull renderers that are actually visible in the mirror.
        portalCamera.cullingMatrix = xFlip * proj * xFlip * worldToCam;

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

    private void CreatePortalCamera(Camera sourceCamera, StereoscopicEye eye, ref Camera portalCamera, ref RenderTexture portalTexture)
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
        var desc = new RenderTextureDescriptor(effectiveWidth, effectiveHeight, RenderTextureFormat.Default, effectiveDepth)
        {
            msaaSamples = effectiveMsaa,
            sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
            useMipMap = false,
            autoGenerateMips = false,
            vrUsage = VRTextureUsage.None,
            dimension = TextureDimension.Tex2D
        };

        portalTexture = new RenderTexture(desc)
        {
            name = $"__MirrorReflection{eye}_{GetEntityId()}",
            anisoLevel = 0
        };
        portalTexture.Create();

        CreateNewCamera(sourceCamera, out portalCamera);
        portalCamera.targetTexture = portalTexture;
    }

    private void CreateNewCamera(Camera sourceCamera, out Camera newCamera)
    {
        // Built bare on purpose — CopyFrom(mainCamera) inherits stereoTargetEye = Both and the XR
        // flags that let HMD tracking override the computed reflected pose on Quest multiview
        // ("the mirror is a handheld camera tilting around the room"). Pose and projection are set
        // explicitly every frame, so nothing else needs copying.
        GameObject camObj = new GameObject($"MirrorCam_{GetEntityId()}_{sourceCamera.GetEntityId()}", typeof(Camera), typeof(BasisMirrorReflectionCamera));
        camObj.TryGetComponent<Camera>(out newCamera);
        newCamera.enabled = false;

        newCamera.depth = 2;
        newCamera.nearClipPlane = Mathf.Max(0.01f, nearClipLimit);
        newCamera.farClipPlane = FarClipPlane;
        newCamera.cullingMask = ReflectingLayers;
        newCamera.useOcclusionCulling = OcclusionCulling;
        newCamera.allowHDR = false;
        newCamera.allowMSAA = true;
        newCamera.stereoTargetEye = StereoTargetEyeMask.None;
        updateCameraClearFlags(newCamera, sourceCamera);

        UniversalAdditionalCameraData cameraData = newCamera.GetUniversalAdditionalCameraData();
        if (cameraData != null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            cameraData.allowXRRendering = false;
#else
            cameraData.allowXRRendering = allowXRRendering;
#endif
            cameraData.renderPostProcessing = RenderPostProcessing;
            cameraData.renderShadows = renderShadows;
            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off;
        }
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
