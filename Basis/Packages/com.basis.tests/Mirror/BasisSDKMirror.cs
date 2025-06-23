using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using RenderPipeline = UnityEngine.Rendering.RenderPipelineManager;
using static UnityEngine.Camera;
using Basis.Scripts.Drivers;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.BasisSdk.Players;
using System;
using System.Collections;
using Unity.Mathematics;
public class BasisSDKMirror : MonoBehaviour
{
    [Header("Main Settings")]
    public Renderer Renderer;
    public Material MirrorsMaterial;
    [SerializeField]
    private LayerMask ReflectingLayers;
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

    public Action OnCamerasRenderering;
    public Action OnCamerasFinished;

    private BasisMeshRendererCheck basisMeshRendererCheck;
    private Vector3 thisPosition;
    private Vector3 normal;
    private Vector3 projectionDirection = -Vector3.forward;
    private Matrix4x4 scaledMatrix;
    private int instanceID;
    private void OnEnable()
    {
        IsActive = false;
        IsAbleToRender = false;
        if (BasisLocalCameraDriver.HasInstance)
        {
            Initialize();
        }
        instanceID = gameObject.GetInstanceID();
        if (ReflectingLayers == 0)
        {
            int remoteLayer = LayerMask.NameToLayer("RemotePlayerAvatar");
            int localLayer = LayerMask.NameToLayer("LocalPlayerAvatar");
            int defaultLayer = LayerMask.NameToLayer("Default");

            if (remoteLayer < 0 || localLayer < 0 || defaultLayer < 0)
            {
                Debug.LogError("One or more required layers are missing.");
            }
            else
            {
                ReflectingLayers = (1 << remoteLayer) | (1 << localLayer) | (1 << defaultLayer);
            }
        }

        if (basisMeshRendererCheck == null)
        {
            basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        }
        basisMeshRendererCheck.Check += VisibilityFlag;

        BasisDeviceManagement.OnBootModeChanged += BootModeChanged;
        BasisLocalCameraDriver.InstanceExists += Initialize;
        RenderPipeline.beginCameraRendering += UpdateCamera;
    }
    private void OnDisable()
    {
        CleanUp();
    }
    private void OnDestroy()
    {
        BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
    }
    private void BootModeChanged(string obj) => StartCoroutine(ResetMirror());
    private IEnumerator ResetMirror()
    {
        yield return null;
        CleanUp();
        OnEnable();
    }
    private void CleanUp()
    {
        if (PortalTextureLeft) DestroyImmediate(PortalTextureLeft);
        if (PortalTextureRight) DestroyImmediate(PortalTextureRight);
        if (LeftCamera) Destroy(LeftCamera.gameObject);
        if (RightCamera) Destroy(RightCamera.gameObject);

        BasisLocalCameraDriver.InstanceExists -= Initialize;
        RenderPipeline.beginCameraRendering -= UpdateCamera;
        BasisLocalPlayer.Instance.LocalAvatarDriver.RemoveActiveMatrixOverride(instanceID);
        basisMeshRendererCheck.Check -= VisibilityFlag;
    }
    private void Initialize()
    {
        scaledMatrix = Matrix4x4.Scale(new Vector3(-1, 1, 1));

        Camera mainCamera = BasisLocalCameraDriver.Instance.Camera;
        CreatePortalCamera(mainCamera, StereoscopicEye.Left, ref LeftCamera, ref PortalTextureLeft);
        CreatePortalCamera(mainCamera, StereoscopicEye.Right, ref RightCamera, ref PortalTextureRight);

        IsAbleToRender = Renderer.isVisible;
        IsActive = true;
        InsideRendering = false;
    }
    private void UpdateCamera(ScriptableRenderContext context, Camera camera)
    {
        if (!IsAbleToRender || !IsActive) return;
        if (!IsCameraAble(camera)) return;

        OnCamerasRenderering?.Invoke();
        BasisLocalAvatarDriver.ScaleHeadToNormal();
        if (BasisLocalAvatarDriver.Instance != null)
        {
            BasisLocalAvatarDriver.Instance.TryActiveMatrixOverride(instanceID);
        }

        thisPosition = Renderer.transform.position;
        normal = Renderer.transform.TransformDirection(projectionDirection);
        UpdateCameraState(context, camera);

        OnCamerasFinished?.Invoke();
        BasisLocalAvatarDriver.ScaleHeadToZero();
    }
    private bool IsCameraAble(Camera camera)
    {
#if UNITY_EDITOR
        if (camera.cameraType == CameraType.SceneView)
            return true;
#endif
        return camera.GetInstanceID() == BasisLocalCameraDriver.CameraInstanceID;
    }
    private void UpdateCameraState(ScriptableRenderContext context, Camera camera)
    {
        if (InsideRendering) return;

        InsideRendering = true;
        camera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        if (camera.stereoEnabled)
        {
            RenderCamera(camera, MonoOrStereoscopicEye.Left, context, position, rotation);
            RenderCamera(camera, MonoOrStereoscopicEye.Right, context, position, rotation);
        }
        else
        {
            RenderCamera(camera, MonoOrStereoscopicEye.Mono, context, position, rotation);
        }
        InsideRendering = false;
    }
    private void RenderCamera(Camera sourceCamera, MonoOrStereoscopicEye eye, ScriptableRenderContext context, Vector3 srcPosition, Quaternion srcRotation)
    {
        Camera portalCamera = (eye == MonoOrStereoscopicEye.Right) ? RightCamera : LeftCamera;
        Vector3 eyeOffset;
        Matrix4x4 projMatrix;
        if (eye == MonoOrStereoscopicEye.Mono)
        {
            eyeOffset = srcPosition;
            projMatrix = sourceCamera.projectionMatrix;
        }
        else
        {
            StereoscopicEye Eye = (StereoscopicEye)eye;
            eyeOffset = sourceCamera.GetStereoViewMatrix(Eye).inverse.MultiplyPoint(Vector3.zero);
            projMatrix = sourceCamera.GetStereoProjectionMatrix(Eye);
        }
        transform.GetPositionAndRotation(out Vector3 TransformPosition, out Quaternion Rotation);
        Vector3 localEyeOffset = InverseTransformPointCustom(TransformPosition, Rotation, eyeOffset);
        Vector3 reflectedForward = Vector3.Reflect(InverseTransformDirectionCustom(Rotation, srcRotation * Vector3.forward), Vector3.forward);
        Vector3 reflectedUp = Vector3.Reflect(InverseTransformDirectionCustom(Rotation, srcRotation * Vector3.up), Vector3.forward);
        Vector3 reflectedPos = Vector3.Reflect(localEyeOffset, Vector3.forward);

        Quaternion reflectedRotation = Quaternion.LookRotation(reflectedForward, reflectedUp);

        portalCamera.transform.SetLocalPositionAndRotation(reflectedPos, reflectedRotation);

        Vector4 clipPlane = BasisHelpers.CameraSpacePlane(portalCamera.worldToCameraMatrix, thisPosition, normal, ClipPlaneOffset);
        clipPlane.x *= -1;

        CalculateObliqueMatrix(ref projMatrix, clipPlane);
        portalCamera.projectionMatrix = scaledMatrix * projMatrix * scaledMatrix;
#pragma warning disable CS0618
        UniversalRenderPipeline.RenderSingleCamera(context, portalCamera);
#pragma warning restore CS0618
    }

    private Vector3 InverseTransformDirectionCustom(Quaternion rotation, Vector3 direction)
    {
        // Inverse transform the direction by the rotation only (ignore position)
        return Quaternion.Inverse(rotation) * direction;
    }
    private Vector3 InverseTransformPointCustom(Vector3 position, Quaternion rotation, Vector3 point)
    {
        // Subtract the position, then remove rotation
        return Quaternion.Inverse(rotation) * (point - position);
    }
    /// <summary>
    /// Calculates an oblique projection matrix
    /// </summary>
    public static void CalculateObliqueMatrix(ref Matrix4x4 projection, float4 clipPlane)
    {
        // Compute the clip-space corner point opposite the clipping plane
        float4 q = projection.inverse * new float4(math.sign(clipPlane.x), math.sign(clipPlane.y), 1.0f, 1.0f);

        // Calculate the scaled plane vector
        float dot = math.dot(clipPlane, q);
        if (dot == 0.0f)
        {
            return; // avoid divide-by-zero just in case
        }
        float4 c = clipPlane * (2.0f / dot);

        // Replace the third row of the projection matrix
        projection[2] = c.x - projection[3];
        projection[6] = c.y - projection[7];
        projection[10] = c.z - projection[11];
        projection[14] = c.w - projection[15];
    }
    private void CreatePortalCamera(Camera sourceCamera, StereoscopicEye eye, ref Camera portalCamera, ref RenderTexture portalTexture)
    {
        portalTexture = new RenderTexture(XSize, YSize, 0, RenderTextureFormat.Default)
        {
            name = $"__MirrorReflection{eye}{GetInstanceID()}",
            isPowerOfTwo = true,
            antiAliasing = 1,
            depth = depth
        };

        Renderer.material = MirrorsMaterial;
        Renderer.sharedMaterial.SetTexture($"_ReflectionTex{eye}", portalTexture);

        CreateNewCamera(sourceCamera, out portalCamera);
        portalCamera.targetTexture = portalTexture;
    }
    private void CreateNewCamera(Camera sourceCamera, out Camera newCamera)
    {
        GameObject camObj = new GameObject($"MirrorCam_{GetInstanceID()}_{sourceCamera.GetInstanceID()}", typeof(Camera));
        camObj.transform.SetParent(transform);
        newCamera = camObj.GetComponent<Camera>();
        newCamera.enabled = false;
        newCamera.CopyFrom(sourceCamera);
        newCamera.depth = 2;
        newCamera.farClipPlane = FarClipPlane;
        newCamera.cullingMask = ReflectingLayers;
        newCamera.useOcclusionCulling = OcclusionCulling;
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
}
