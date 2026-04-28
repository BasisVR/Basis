using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
using System.Collections;
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
    public float ClipPlaneOffset = 0.001f;
    public float nearClipLimit = 0.01f;
    public float FarClipPlane = 25f;
    public int XSize = 2048;
    public int YSize = 2048;
    public int depth = 24;
    public int Antialiasing = 2;

    [Header("Options")]
    public bool allowXRRendering = true;
    public bool RenderPostProcessing = false;
    public bool OcclusionCulling = false;
    public bool renderShadows = false;

    [Header("Debug / Runtime")]
    public bool IsActive;
    public bool IsAbleToRender;
    public static bool InsideRendering;

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
    private Matrix4x4 xFlip;

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

        if (LeftCamera) Destroy(LeftCamera.gameObject);
        if (RightCamera) Destroy(RightCamera.gameObject);

        PortalTextureLeft = null;
        PortalTextureRight = null;
        LeftCamera = RightCamera = null;
    }

    private void GetEffectiveResolution(out int width, out int height)
    {
        if (BasisSettingsDefaults.UseMirrorQualityOverride.RawValue &&
            int.TryParse(BasisSettingsDefaults.MirrorQuality.RawValue, out int overrideRes) && overrideRes > 0)
        {
            width = overrideRes;
            height = overrideRes;
        }
        else
        {
            width = XSize;
            height = YSize;
        }
    }

    private void Initialize()
    {
        if (IsActive) return;

        // Drop any stale cameras/textures from a prior init that didn't reach a clean teardown
        // (e.g. resources orphaned across a Play-Mode domain reload).
        DisposePortalResources();

        xFlip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));

        var mainCamera = BasisLocalCameraDriver.Instance.Camera;

        CreatePortalCamera(mainCamera, StereoscopicEye.Left, ref LeftCamera, ref PortalTextureLeft);
        CreatePortalCamera(mainCamera, StereoscopicEye.Right, ref RightCamera, ref PortalTextureRight);

        // Bind textures to the mirror material
        Renderer.material = MirrorsMaterial;
        Renderer.sharedMaterial.SetTexture("_ReflectionTexLeft", PortalTextureLeft);
        Renderer.sharedMaterial.SetTexture("_ReflectionTexRight", PortalTextureRight);

        IsAbleToRender = Renderer.isVisible;
        IsActive = true;
        InsideRendering = false;

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
    private static Vector3 TransformDirection(Quaternion rotation, Vector3 directionLocal)
    {
        return rotation * directionLocal;
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

        BasisLocalAvatarDriver.ScaleHeadToNormal();

        OnCamerasRenderering?.Invoke();

        thisPosition = Renderer.transform.position;
        normal = Renderer.transform.TransformDirection(projectionDirection).normalized;

        // Update gaze target: reflect the player's eye position across the mirror plane
        if (gazeTarget != null)
        {
            Vector3 eyePos = BasisLocalCameraDriver.Position;
            transform.GetPositionAndRotation(out Vector3 planePosWS, out Quaternion planeRotWS);
            Vector3 eyeLocal = InverseTransformPoint(planePosWS, planeRotWS, eyePos);
            Vector3 reflLocal = Vector3.Reflect(eyeLocal, Vector3.forward);
            gazeTarget.FocusPoint = TransformPoint(planePosWS, planeRotWS, reflLocal);
        }

        RenderBothEyes(cam);

        OnCamerasFinished?.Invoke();

        BasisLocalAvatarDriver.ScaleheadToZero();
    }

    private void RenderBothEyes(Camera camera)
    {
        if (InsideRendering) return; // avoid recursion in SRP
        InsideRendering = true;

        camera.transform.GetPositionAndRotation(out Vector3 srcPos, out Quaternion srcRot);

        if (camera.stereoEnabled)
        {
            RenderEye(camera, MonoOrStereoscopicEye.Left, srcPos, srcRot);
            RenderEye(camera, MonoOrStereoscopicEye.Right, srcPos, srcRot);
        }
        else
        {
            RenderEye(camera, MonoOrStereoscopicEye.Mono, srcPos, srcRot);
        }

        InsideRendering = false;
    }

    private void RenderEye(Camera sourceCamera, MonoOrStereoscopicEye eye, Vector3 srcPos, Quaternion srcRot)
    {
        Camera portalCamera = (eye == MonoOrStereoscopicEye.Right) ? RightCamera : LeftCamera;
        if (!portalCamera) return;

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
        }

        // Mirror plane (world)
        transform.GetPositionAndRotation(out Vector3 planePosWS, out Quaternion planeRotWS);

        // World -> mirror-local (TR only)
        Vector3 eyeLocal = InverseTransformPoint(planePosWS, planeRotWS, eyeOriginWS);
        Vector3 fwdLocal = InverseTransformDirection(planeRotWS, srcRot * Vector3.forward);
        Vector3 upLocal = InverseTransformDirection(planeRotWS, srcRot * Vector3.up);

        // Reflect across local +Z plane
        Vector3 reflPosLocal = Vector3.Reflect(eyeLocal, Vector3.forward);
        Vector3 reflFwdLocal = Vector3.Reflect(fwdLocal, Vector3.forward);
        Vector3 reflUpLocal = Vector3.Reflect(upLocal, Vector3.forward);

        // Back to world (TR only) and set camera **world** pose
        Vector3 reflPosWS = TransformPoint(planePosWS, planeRotWS, reflPosLocal);
        Vector3 reflFwdWS = TransformDirection(planeRotWS, reflFwdLocal);
        Vector3 reflUpWS = TransformDirection(planeRotWS, reflUpLocal);
        Quaternion reflRotWS = Quaternion.LookRotation(reflFwdWS, reflUpWS);

        portalCamera.transform.SetPositionAndRotation(reflPosWS, reflRotWS);

        // Oblique clip to avoid "behind mirror"
        Vector4 clipPlaneCamSpace = BasisHelpers.CameraSpacePlane(
            portalCamera.worldToCameraMatrix, planePosWS, normal, ClipPlaneOffset);

        clipPlaneCamSpace.x *= -1f; // compensate for x-flip
        CalculateObliqueMatrix(ref proj, clipPlaneCamSpace);

        // Keep triangle winding after reflection
        portalCamera.projectionMatrix = xFlip * proj * xFlip;

        // Keep culling in sync with that projection
        portalCamera.cullingMatrix = portalCamera.projectionMatrix * portalCamera.worldToCameraMatrix;

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

        SubmitRenderRequest(portalCamera, portalCamera.targetTexture);
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

    private static Vector3 InverseTransformDirection(Quaternion rotation, Vector3 direction)
    {
        return Quaternion.Inverse(rotation) * direction;
    }

    private static Vector3 InverseTransformPoint(Vector3 position, Quaternion rotation, Vector3 point)
    {
        return Quaternion.Inverse(rotation) * (point - position);
    }

    /// <summary>
    /// Calculates an oblique projection matrix.
    /// </summary>
    public static void CalculateObliqueMatrix(ref Matrix4x4 projection, float4 clipPlane)
    {
        float4 q = projection.inverse * new float4(math.sign(clipPlane.x), math.sign(clipPlane.y), 1.0f, 1.0f);
        float dot = math.dot(clipPlane, q);
        if (Mathf.Approximately(dot, 0f)) return;

        float4 c = clipPlane * (2.0f / dot);
        projection[2] = c.x - projection[3];
        projection[6] = c.y - projection[7];
        projection[10] = c.z - projection[11];
        projection[14] = c.w - projection[15];
    }

    private void CreatePortalCamera(Camera sourceCamera, StereoscopicEye eye, ref Camera portalCamera, ref RenderTexture portalTexture)
    {
        GetEffectiveResolution(out int effectiveWidth, out int effectiveHeight);
        var desc = new RenderTextureDescriptor(effectiveWidth, effectiveHeight, RenderTextureFormat.Default, depth)
        {
            msaaSamples = Mathf.Max(1, Antialiasing),
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

        // Bind to material with side-specific names as well (handy if your shader expects these).
        // If your shader uses different property names, update accordingly.
        if (eye == StereoscopicEye.Left)
            Renderer.sharedMaterial.SetTexture("_ReflectionTexLeft", portalTexture);
        else
            Renderer.sharedMaterial.SetTexture("_ReflectionTexRight", portalTexture);
    }

    private void CreateNewCamera(Camera sourceCamera, out Camera newCamera)
    {
        GameObject camObj = new GameObject($"MirrorCam_{GetEntityId()}_{sourceCamera.GetEntityId()}", typeof(Camera));
        camObj.TryGetComponent<Camera>(out  newCamera);
        newCamera.enabled = false;
        newCamera.CopyFrom(sourceCamera);

        newCamera.depth = 2;
        newCamera.farClipPlane = FarClipPlane;
        newCamera.cullingMask = ReflectingLayers;
        newCamera.useOcclusionCulling = OcclusionCulling;
        updateCameraClearFlags(newCamera, sourceCamera);

        if (newCamera.TryGetComponent(out UniversalAdditionalCameraData cameraData))
        {
            cameraData.allowXRRendering = allowXRRendering;
            cameraData.renderPostProcessing = RenderPostProcessing;
            cameraData.renderShadows = renderShadows;
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
