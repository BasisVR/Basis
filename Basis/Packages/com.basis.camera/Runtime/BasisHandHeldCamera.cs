using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
public partial class BasisHandHeldCamera : BasisHandHeldCameraInteractable
{
    [Header("Camera Components")]
    public UniversalAdditionalCameraData CameraData;
    public Camera captureCamera;
    public MeshRenderer Renderer;
    public Material Material;
    [Header("UI Components")]
    public TextMeshProUGUI countdownText;
    [SerializeField] public BasisHandHeldCameraUI HandHeld = new BasisHandHeldCameraUI();
    [SerializeField] public BasisDepthOfFieldInteractionHandler BasisDOFInteractionHandler;
    [SerializeField] private BasisHandHeldCameraInteractable interactable;
    [Header("Settings")]
    [Tooltip("Width of the captured photo")]
    public int captureWidth = 1920;
    [Tooltip("Height of the captured photo")]
    public int captureHeight = 1080;
    [Tooltip("Preview resolution width")]
    public int PreviewCaptureWidth = 1920;
    [Tooltip("Preview resolution height")]
    public int PreviewCaptureHeight = 1080;
    [Tooltip("Capture format (EXR/PNG)")]
    public string captureFormat = "EXR";
    [Tooltip("Also spawn each saved photo in the world as an image pickup")]
    public bool printPhotoEnabled = false;
    [Tooltip("Depth buffer bits for render texture")]
    public int depth = 24;
    [Tooltip("MSAA samples on the capture render texture")]
    public int msaaSamples = 2;
    [Header("Advanced/Debug")]
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();
#if Basis_VOLUMETRIC_SUPPORTED
    public VolumetricFogCameraSource VolumetricFogSource;
#endif
    private const int SimulateLatePriority = 204;
    public BasisHandHeldCameraGizmos DebugGizmos { get; } = new BasisHandHeldCameraGizmos();
    public new async void Awake()
    {
        BasisHandHeldCameraReticle.Acquire();
        BasisHandHeldCameraRegistry.Add(this);

        InitializeCameraSettings();
        InitializePostProcessingVolume();
        InitializeMaterial();
        InitializeMeshRendererCheck();
        await InitializeUI();

        if (this == null) return;

        InitializeTonemapping();
        InitializeDepthOfField();
        InitializeVolumetrics();
        BasisCameraPhotoFolder.Ensure();
        await HandHeld.SaveSettings();

        if (this == null) return;

        base.Awake();

        ApplyPreviewResolution();
        captureCamera.targetTexture = renderTexture;
        captureCamera.gameObject.SetActive(true);

        BasisLocalPlayer.AfterSimulateOnRender.AddAction(SimulateLatePriority, SimulateLate);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        BasisLocalCameraDriver.RenderSettingsApplied += SyncBackgroundFromMainCamera;

        if (BasisLocalCameraDriver.HasInstance) BasisLocalCameraDriver.Instance.ExitThirdPerson();

        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            GetNetworkedMarkerPose(out Vector3 pipPos, out Quaternion pipRot);
            BasisNetworkPIPCameraDriver.SendPIPState(true, pipPos, pipRot);
        }
    }
    public new void Start()
    {
        base.Start();
        OnPickupUse.AddListener(OnPickupUseCapture);
    }
    public new async void OnDestroy()
    {
#if BASIS_HAS_GI && !UNITY_ANDROID
        SMModuleGlobalIlluminationURP.UnregisterCamera(captureCamera);
#endif
        if (BasisNetworkConnection.LocalPlayerPeer != null) BasisNetworkPIPCameraDriver.SendPIPState(false, Vector3.zero, Quaternion.identity);

        BasisHandHeldCameraReticle.Release();
        BasisHandHeldCameraRegistry.Remove(this);

        UnRegisterLoadedNetID(gameObject.name);

        StopWebStream();
        StopVideoOutput();
        ShutdownGifRecorder();
        ShutdownVideoRecorder();
        ShutdownPhotogrammetry();
        BasisHandHeldCameraAudioListener.Set(this, false);
        DespawnFollowPip();
        DestroyDetachedGizmo();
        DespawnPuckPreview();
        ShutdownLookAtPointer();
        DebugGizmos.Shutdown();

        UnsubscribeMeshRendererCheck();
        BasisCullingCameraRegistry.Unregister(captureCamera);
        BasisMirrorViewerRegistry.Unregister(captureCamera);
        ShutdownDirectToScreen();
        BasisCameraRenderTargets.Release(ref renderTexture);
        ReleaseFocusPeaking();
        ReleaseViewfinderGrid();
        ReleaseAutoBrightness();
        BasisCameraRenderTargets.DestroyAndClear(ref pooledScreenshot);
        ReleasePrintSheet();
        BasisCameraRenderTargets.Release(ref srgbResolveTexture);
        BasisCameraRenderTargets.DestroyAndClear(ref actualMaterial);

        if (HandHeld != null) HandHeld.ReleaseUILock();

        BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(SimulateLatePriority, SimulateLate);

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        BasisLocalCameraDriver.RenderSettingsApplied -= SyncBackgroundFromMainCamera;
        OnPickupUse.RemoveListener(OnPickupUseCapture);

        base.OnDestroy();

        if (HandHeld != null) await HandHeld.SaveSettings();
    }
    public void OnPickupUseCapture(BasisPickUpUseMode mode)
    {
        if (mode == BasisPickUpUseMode.OnPickUpUseDown) CapturePhoto();
    }
    private void OnEnable()
    {
        ApplyPreviewResolution();
        if (renderTexture != null) BasisDebug.Log($"[HandHeldCamera] Preview reset to {renderTexture.width}x{renderTexture.height} @ {AntialiasingQuality.Low}");
        captureCamera.targetTexture = renderTexture;
        BasisCullingCameraRegistry.Register(captureCamera);
        BasisMirrorViewerRegistry.Register(captureCamera);
    }
    private void InitializeCameraSettings()
    {
        captureCamera.forceIntoRenderTexture = true;
        captureCamera.allowHDR = true;
        captureCamera.allowMSAA = true;
        captureCamera.useOcclusionCulling = true;
        captureCamera.usePhysicalProperties = true;
        captureCamera.targetTexture = renderTexture;
        captureCamera.targetDisplay = 1;
        SyncBackgroundFromMainCamera();
    }
    private async System.Threading.Tasks.Task InitializeUI()
    {
        await HandHeld.Initialize(this);
        interactable.SetCameraUI(HandHeld);
        CacheOnPropUI();
    }
    private void SimulateLate()
    {
        TickDirectToScreen();
        UpdateRenderGate();
        TickBody();
        TickAutoBrightness();
        TickFocusPeaking();
        TickViewfinderGrid();
        TickVideoOutput();
        TickGifRecorder();
        TickVideoRecorder();
        TickPhotogrammetry();
        UpdateOnPropUIVisibility();
        TickFocusRack();
        UpdateAutoFocus();
        UpdateFollowPip();
        UpdatePuckPreview();
        TickLookAtPointer();
        DebugGizmos.Tick(this);

        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            GetNetworkedMarkerPose(out Vector3 pos, out Quaternion rot);
            BasisNetworkPIPCameraDriver.SendPIPPosition(pos, rot);
        }
    }
    private new void OnBootModeChanged(string obj) => RefreshDirectToScreen();
    private static async void UnRegisterLoadedNetID(string loadedNetId)
    {
        if (string.IsNullOrEmpty(loadedNetId)) return;
        if (!BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(loadedNetId, out var go) || !go) return;

        if (await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(loadedNetId)) BasisDebug.Log($"successfully removed item = {loadedNetId} from registry");
        else BasisDebug.LogError($"failed to remove item = {loadedNetId} from registry");
    }
}
