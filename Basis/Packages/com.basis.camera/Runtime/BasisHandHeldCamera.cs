using Basis;
using Basis.BasisUI;
using Basis.ImagePickup;
using Basis.Scripts.Audio;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Rendering;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// Handheld capture camera with preview, screenshotting (PNG/EXR),
/// post-processing integration (Tonemapping/DoF/Bloom/Color), and UI plumbing.
/// Extends <see cref="BasisHandHeldCameraInteractable"/> for pin/fly modes.
/// </summary>
public partial class BasisHandHeldCamera : BasisHandHeldCameraInteractable
{
    [Header("Camera Components")]
    /// <summary>URP camera data (AA, stack, etc.).</summary>
    public UniversalAdditionalCameraData CameraData;

    /// <summary>The actual capture camera (physical properties enabled).</summary>
    public Camera captureCamera;

    /// <summary>Preview mesh renderer that displays the render texture.</summary>
    public MeshRenderer Renderer;

    /// <summary>Base material used to show the preview texture on <see cref="Renderer"/>.</summary>
    public Material Material;

    [Header("UI Components")]
    /// <summary>Countdown text for timer captures (e.g., “3…2…1…!”).</summary>
    public TextMeshProUGUI countdownText;

    /// <summary>Seconds left on a timer capture, or 0 when no countdown is running.</summary>
    public int CountdownRemaining { get; private set; }

    /// <summary>True while a timer capture is counting down.</summary>
    public bool IsCountingDown => CountdownRemaining > 0;

    /// <summary>All handheld camera UI widgets and persistence (sliders/toggles/etc.).</summary>
    [SerializeField] public BasisHandHeldCameraUI HandHeld = new BasisHandHeldCameraUI();

    /// <summary>Handler to click-to-focus Depth of Field in the preview.</summary>
    [SerializeField] public BasisDepthOfFieldInteractionHandler BasisDOFInteractionHandler;

    /// <summary>Back-reference to the interactable (for UI hand-off).</summary>
    [SerializeField] private BasisHandHeldCameraInteractable interactable;

    [Header("Settings")]
    /// <summary>Output capture width (photo resolution).</summary>
    [Tooltip("Width of the captured photo")]
    public int captureWidth = 1920;

    /// <summary>Output capture height (photo resolution).</summary>
    [Tooltip("Height of the captured photo")]
    public int captureHeight = 1080;

    /// <summary>Preview RT width.</summary>
    [Tooltip("Preview resolution width")]
    public int PreviewCaptureWidth = 1920;

    /// <summary>Preview RT height.</summary>
    [Tooltip("Preview resolution height")]
    public int PreviewCaptureHeight = 1080;

    /// <summary>“EXR” or “PNG” (affects RT format and encoding).</summary>
    [Tooltip("Capture format (EXR/PNG)")]
    public string captureFormat = "EXR";

    /// <summary>
    /// When on, every photo saved to disk is also printed into the world as a shared image
    /// pickup — the same card a file drag-and-dropped onto the window makes.
    /// </summary>
    [Tooltip("Also spawn each saved photo in the world as an image pickup")]
    public bool printPhotoEnabled = false;

    /// <summary>Depth buffer bits for the render texture (e.g., 24).</summary>
    [Tooltip("Depth buffer bits for render texture")]
    public int depth = 24;

    /// <summary>MSAA sample count on the capture render texture (1 = off, else 2/4/8).</summary>
    [Tooltip("MSAA samples on the capture render texture")]
    public int msaaSamples = 2;

    /// <summary>Instance identifier for multi-camera setups.</summary>
    [Tooltip("Instance ID for multi-camera setups")]
    public int InstanceID;

    [Header("Advanced/Debug")]

    /// <summary>Static metadata/presets and PP component references.</summary>
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();

#if Basis_VOLUMETRIC_SUPPORTED
    public VolumetricFogCameraSource VolumetricFogSource;
#endif

    /// <summary>World-space debug representations of this camera, toggled from the settings panel.</summary>
    public BasisHandHeldCameraGizmos DebugGizmos { get; } = new BasisHandHeldCameraGizmos();

    // --- private state ---

    /// <summary>Instantiated material assigned to the preview renderer.</summary>
    private Material actualMaterial;

    /// <summary>Current preview/capture render texture.</summary>
    private RenderTexture renderTexture;

    public RenderTexture PreviewTexture => renderTexture;

    /// <summary>
    /// True from the moment a still capture takes the RT to its capture size until the readback
    /// has landed. The RT is freed and rebuilt on every resize, so anything that sizes the feed
    /// on its own schedule — Direct To Screen follows the window — has to stand off for that
    /// window or it destroys the texture the readback is reading.
    /// </summary>
    private bool captureInFlight;

    /// <summary>Last RT bound to material (to avoid redundant sets).</summary>
    private RenderTexture lastAssignedRenderTexture = null;

    /// <summary>Last material assigned to the renderer (to avoid redundant sets).</summary>
    private Material lastAssignedMaterial = null;

    /// <summary>Pooled CPU-side texture for async GPU readbacks.</summary>
    private Texture2D pooledScreenshot;

    /// <summary>8-bit sRGB target the HDR capture frame is resolved into before readback.</summary>
    private RenderTexture srgbResolveTexture;

    /// <summary>Bitmask for the UI layer toggle in <see cref="Nameplates"/>.</summary>
    private int uiLayerMask;

    /// <summary>Shared “clear to color” material (Unlit/Color).</summary>
    private static Material clearMaterial;

    /// <summary>Shader path used to initialize <see cref="clearMaterial"/>.</summary>
    private const string CLEAR_SHADER_PATH = "Unlit/Color";

    /// <summary>Number of handheld cameras currently out. The desktop reticle is suppressed
    /// while this is greater than zero and restored when the last camera closes.</summary>
    private static int _activeHandHeldCount;

    /// <summary>Folder where screenshots are written (platform-dependent).</summary>
    private string picturesFolder;

    /// <summary>Last visibility state reported by the mesh renderer check.</summary>
    public bool LastVisibilityState = false;

    /// <summary>
    /// Whether the prop's viewfinder mesh is in some camera's view. Starts true: a renderer that
    /// has never been culled has never reported either way, and a camera that has just spawned
    /// should be rendering rather than waiting to be looked at.
    /// </summary>
    private bool rendererVisible = true;

    /// <summary>True while the settings panel is bound to this camera and showing its preview.</summary>
    private bool panelPreviewActive;

    /// <summary>Renderer visibility observer.</summary>
    private BasisMeshRendererCheck basisMeshRendererCheck;

    /// <summary>The prop's own HUD canvas, hidden while the main-menu camera panel drives this camera instead.</summary>
    private Canvas onPropUICanvas;
    private GraphicRaycaster onPropUIRaycaster;
    private Collider onPropUICollider;
    private bool onPropUIHidden;

    /// <summary>Every renderer on the prop, so the whole thing can go without stopping it.</summary>
    private Renderer[] cameraBodyRenderers;

    /// <summary>True while the prop is hidden but still live.</summary>
    private bool cameraHidden;

    /// <summary>True when the camera is running but invisible — "closed" without being destroyed.</summary>
    public bool IsCameraHidden => cameraHidden;

    /// <summary>True when the camera was dismissed via Close (kept alive) rather than merely hidden from the panel.</summary>
    private bool dismissed;

    /// <summary>
    /// True only when the camera was closed with "Close Hides Instead" — the state the panel shows
    /// the Bring Back banner for. Just hiding the visuals from the Hide Camera toggle is not this,
    /// so the settings stay put while you keep adjusting a hidden camera.
    /// </summary>
    public bool IsClosedHidden => dismissed && cameraHidden;

    /// <summary>Closes the camera to a hidden-but-running state, marking it for the panel's Bring Back flow.</summary>
    public void CloseToHidden()
    {
        dismissed = true;
        SetCameraHidden(true);
    }

    /// <summary>The camera that currently owns the scene audio listener, or null. Only one at a time — there is one listener.</summary>
    private static BasisHandHeldCamera audioListenerOwner;

    /// <summary>True while this camera is the audio listener, so the world is heard from here.</summary>
    public bool IsAudioListener => audioListenerOwner == this;

    /// <summary>
    /// Routes the scene audio listener to this camera's pose, or releases it. Because there is
    /// a single listener, taking it hands it over from whichever camera held it. Released
    /// automatically on hide-to-destroy so a gone camera can't strand the listener in space.
    /// </summary>
    public void SetAudioListener(bool enabled)
    {
        if (enabled)
        {
            audioListenerOwner = this;
            // Feed the driver this camera's live pose each frame it asks; the null guard hands
            // control back the instant a different camera claims it or this one releases.
            BasisLocalCameraDriver.AudioListenerPoseOverride = GetAudioListenerPose;
        }
        else if (audioListenerOwner == this)
        {
            audioListenerOwner = null;
            BasisLocalCameraDriver.AudioListenerPoseOverride = null;
        }
    }

    private (Vector3 position, Quaternion rotation)? GetAudioListenerPose()
    {
        if (audioListenerOwner != this || captureCamera == null) return null;
        captureCamera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        return (position, rotation);
    }

    /// <summary>
    /// Performs camera/PP/UI/material initialization, creates folders, saves initial settings,
    /// and starts the preview loop. Also hooks boot-mode changes.
    /// </summary>
    public new async void Awake()
    {
        // Take the desktop reticle down immediately on bring-out — before the awaits below —
        // destroyed rather than hidden, and restored when the last camera closes. Ref-counted
        // for multi-camera setups; no-op in VR. Done synchronously so it can't race OnDestroy.
        _activeHandHeldCount++;
        ApplyReticleSuppression();
        BasisHandHeldCameraRegistry.Add(this);

        InitializeCameraSettings();
        InitializePostProcessingVolume();
        InitializeMaterial();
        InitializeMeshRendererCheck();
        await InitializeUI();

        // Destroyed while an await above was running — closed straight away, or the scene went.
        // OnDestroy has already run its teardown, so continuing would re-register the handlers
        // below onto a dead object and leave SimulateLate firing every frame forever.
        if (this == null) return;

        InitializeTonemapping();
        InitializeDepthOfField();
        InitializeVolumetrics();
        InitializeFolders();
        await HandHeld.SaveSettings();

        if (this == null) return;

        SetupUILayerMask();
        SetupClearMaterial();

        base.Awake();

        ApplyPreviewResolution();
        captureCamera.targetTexture = renderTexture;
        captureCamera.gameObject.SetActive(true);

        // Ordered render phase instead of Unity's LateUpdate, so this always runs after the camera
        // has been moved for the frame rather than racing it.
        BasisLocalPlayer.AfterSimulateOnRender.AddAction(SimulateLatePriority, SimulateLate);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        BasisLocalCameraDriver.RenderSettingsApplied += SyncBackgroundFromMainCamera;

        if (BasisLocalCameraDriver.HasInstance)
        {
            BasisLocalCameraDriver.Instance.ExitThirdPerson();
        }

        // Notify network that PIP camera was created
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            GetNetworkedMarkerPose(out Vector3 pipPos, out Quaternion pipRot);
            BasisNetworkPIPCameraDriver.SendPIPState(true, pipPos, pipRot);
        }
    }
    public void InitializeVolumetrics()
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (MetaData.VolumetricFogVolume == null)
        {
            MetaData.Profile.TryGet(out MetaData.VolumetricFogVolume);
        }

        if (captureCamera != null && VolumetricFogSource != null)
        {
            VolumetricFogSource.Initialize(captureCamera);

            int defaultLayer = LayerMask.NameToLayer("Default");
            VolumetricFogSource.WorldVolumeLayerMask = defaultLayer >= 0 ? 1 << defaultLayer : 1;
            UpdateVolumetricFogSource();
        }
#endif
    }

    /// <summary>True when this camera's own fog override replaces the world's volumetric fog.</summary>
    public bool OverrideVolumetricFog
    {
        get
        {
#if Basis_VOLUMETRIC_SUPPORTED
            return MetaData.VolumetricFogVolume != null && MetaData.VolumetricFogVolume.active;
#else
            return false;
#endif
        }
    }

    public void SetOverrideVolumetricFog(bool enabled)
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (MetaData.VolumetricFogVolume != null)
        {
            MetaData.VolumetricFogVolume.active = enabled;
        }
        UpdateVolumetricFogSource();
#endif
    }

    private void UpdateVolumetricFogSource()
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (VolumetricFogSource == null) return;

        bool useCameraOverride = OverrideVolumetricFog;
        bool worldIsInShot = backgroundMode == BasisCameraBackgroundMode.World || backgroundKeepsWorld;

        VolumetricFogSource.SuppressFog = !useCameraOverride && !worldIsInShot;
        VolumetricFogSource.UseWorldFog = !useCameraOverride && worldIsInShot;
#endif
    }
    /// <summary>
    /// Stops preview, saves settings, releases resources, unsubscribes events,
    /// and returns this object to the Addressables pool.
    /// </summary>
    public new async void OnDestroy()
    {
#if BASIS_HAS_GI && !UNITY_ANDROID
        SMModuleGlobalIlluminationURP.UnregisterCamera(captureCamera);
#endif
        // Notify network that PIP camera was destroyed
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            BasisNetworkPIPCameraDriver.SendPIPState(false, Vector3.zero, Quaternion.identity);
        }

        // Camera is closing: drop the ref count and lift reticle suppression once the
        // last camera is gone, so it returns if the user still wants it.
        _activeHandHeldCount = Mathf.Max(0, _activeHandHeldCount - 1);
        ApplyReticleSuppression();
        BasisHandHeldCameraRegistry.Remove(this);

        string myLoadedNetId = gameObject.name;
        UnRegisterLoadedNetID(myLoadedNetId);

        // Neither of these unwinds itself. The web stream owns a socket and a thread that
        // Unity knows nothing about, so they would outlive the camera and hold the port for
        // the rest of the session; the Spout path leaks its claimed sender name, which makes
        // the next camera come up as "Basis Camera 2".
        StopWebStream();
        StopVideoOutput();
        ShutdownGifRecorder();
        ShutdownVideoRecorder();
        ShutdownPhotogrammetry();
        SetAudioListener(false);
        DespawnFollowPip();
        DestroyDetachedGizmo();
        DespawnPuckPreview();
        ShutdownLookAtPointer();

        DebugGizmos.Shutdown();

        UnsubscribeMeshRendererCheck();
        BasisCullingCameraRegistry.Unregister(captureCamera);
        BasisMirrorViewerRegistry.Unregister(captureCamera);
        ShutdownDirectToScreen();
        ReleaseRenderTexture();
        ReleaseFocusPeaking();
        ReleaseViewfinderGrid();
        ReleaseAutoBrightness();
        if (pooledScreenshot != null) { Destroy(pooledScreenshot); pooledScreenshot = null; }
        ReleasePrintSheet();
        ReleaseSrgbResolveTarget();
        if (actualMaterial != null) { Destroy(actualMaterial); actualMaterial = null; }

        if (HandHeld != null)
        {
            HandHeld.ReleaseUILock(); // we should release locks if for whatever reason we get destroyed
        
        }
        

        BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(SimulateLatePriority, SimulateLate);

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        BasisLocalCameraDriver.RenderSettingsApplied -= SyncBackgroundFromMainCamera;
        OnPickupUse.RemoveListener( OnPickupUseCapture );

        base.OnDestroy();

        if (HandHeld != null)
        {
            await HandHeld.SaveSettings();
        }
    }

    /// <summary>
    /// Ensures preview RT is set when re-enabled and (re)starts the preview loop.
    /// </summary>
    private void OnEnable()
    {
        ApplyPreviewResolution();
        if (renderTexture != null)
        {
            BasisDebug.Log($"[HandHeldCamera] Preview reset to {renderTexture.width}x{renderTexture.height} @ {AntialiasingQuality.Low}");
        }
        captureCamera.targetTexture = renderTexture;
        BasisCullingCameraRegistry.Register(captureCamera);
        BasisMirrorViewerRegistry.Register(captureCamera);
    }

    /// <summary>
    /// Suppresses (destroys) or restores the desktop reticle based on how many handheld
    /// cameras are currently out. No-op in VR, where there is no desktop eye/reticle.
    /// </summary>
    private static void ApplyReticleSuppression()
    {
        if (BasisDesktopEye.Instance != null)
        {
            BasisDesktopEye.Instance.Reticle?.SetSuppressed(_activeHandHeldCount > 0);
        }
    }

    /// <summary>Initializes base camera properties (HDR, MSAA, physical cam, targets).</summary>
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

    public void InitializePostProcessingVolume()
    {
        if (captureCamera == null) return;
        if (CameraData == null) CameraData = captureCamera.GetUniversalAdditionalCameraData();

        // Both of these are about the CAMERA, not about the volume, and they used to sit past the early
        // return below - so a camera that found no post processing volume silently rendered no global
        // illumination. Nothing connects the two: the allow-list only needs to know this camera is one of
        // ours, and the bounce is not part of the post stack at all (it composites before transparents, off
        // the depth buffer). The one thing that DID connect them was the renderer's own gate, which refuses
        // any camera with post processing off - so leaving both behind a volume lookup meant one missing
        // component turned the effect off twice over.
        CameraData.renderPostProcessing = true;
#if BASIS_HAS_GI && !UNITY_ANDROID
        SMModuleGlobalIlluminationURP.RegisterCamera(captureCamera);
#endif

        Volume volume = FindPostProcessingVolume();
        if (volume == null) return;

        if (MetaData.Profile == null) MetaData.Profile = volume.sharedProfile;
        else if (volume.sharedProfile != MetaData.Profile) volume.sharedProfile = MetaData.Profile;

        CameraData.volumeLayerMask = 1 << volume.gameObject.layer;
        CameraData.volumeTrigger = volume.transform;
    }

    private Volume FindPostProcessingVolume()
    {
        Volume[] volumes = GetComponentsInChildren<Volume>(true);
        for (int Index = 0; Index < volumes.Length; Index++)
        {
            if (MetaData.Profile != null && volumes[Index].sharedProfile == MetaData.Profile) return volumes[Index];
        }
        return volumes.Length > 0 ? volumes[0] : null;
    }

    private void SyncBackgroundFromMainCamera()
    {
        if (BasisLocalCameraDriver.Instance == null) return;
        Camera main = BasisLocalCameraDriver.Instance.Camera;
        if (main == null) return;

        captureCamera.clearFlags = main.clearFlags;
        captureCamera.backgroundColor = main.backgroundColor;

        bool hasMainSky = main.TryGetComponent(out Skybox mainSky) && mainSky.material != null;
        bool hasCapSky = captureCamera.TryGetComponent(out Skybox capSky);
        if (hasMainSky)
        {
            if (!hasCapSky) capSky = captureCamera.gameObject.AddComponent<Skybox>();
            capSky.material = mainSky.material;
        }
        else if (hasCapSky)
        {
            capSky.material = null;
        }
    }

    /// <summary>Instantiates a unique material used for the preview mesh.</summary>
    private void InitializeMaterial()
    {
        if (actualMaterial != null) Destroy(actualMaterial);
        actualMaterial = Instantiate(Material);
    }

    /// <summary>Attaches a renderer visibility checker and subscribes its event.</summary>
    private void InitializeMeshRendererCheck()
    {
        basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        basisMeshRendererCheck.Check += VisibilityFlag;
    }

    /// <summary>Builds UI, binds it to this camera, and registers for orientation updates.</summary>
    private async System.Threading.Tasks.Task InitializeUI()
    {
        basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        basisMeshRendererCheck.Check += VisibilityFlag;
        await HandHeld.Initialize(this);
        interactable.SetCameraUI(HandHeld);
        CacheOnPropUI();
    }

    /// <summary>
    /// Caches the prop's single HUD canvas. Grabbed once at init, before the preview
    /// screen can exist, so the search can only land on the prop's own UI.
    /// </summary>
    private void CacheOnPropUI()
    {
        cameraBodyRenderers = GetComponentsInChildren<Renderer>(true);

        onPropUICanvas = GetComponentInChildren<Canvas>(true);
        if (onPropUICanvas == null) return;
        onPropUICanvas.TryGetComponent(out onPropUIRaycaster);
        onPropUICanvas.TryGetComponent(out onPropUICollider);
    }

    /// <summary>
    /// Hides the prop without stopping it. Everything downstream keeps running — capture,
    /// streaming, auto-follow, the panel preview — only the visuals go, which is what lets a
    /// "closed" camera stay alive and be brought back rather than respawned.
    /// </summary>
    public void SetCameraHidden(bool hidden)
    {
        if (cameraHidden == hidden) return;
        cameraHidden = hidden;

        // Shown again means it is no longer a dismissed camera awaiting bring-back.
        if (!hidden) dismissed = false;

        if (cameraBodyRenderers != null)
        {
            for (int Index = 0; Index < cameraBodyRenderers.Length; Index++)
            {
                if (cameraBodyRenderers[Index] != null) cameraBodyRenderers[Index].enabled = !hidden;
            }
        }

        // Hiding the preview mesh drops Renderer.isVisible, which would otherwise cull the
        // capture camera and stop the feed the moment the prop went invisible.
        UpdateRenderGate();
        UpdateOnPropUIVisibility();
        UpdateHiddenInputLocks();
    }

    private void UpdateHiddenInputLocks()
    {
        if (!BasisDeviceManagement.IsUserInDesktop()) return;
        if (IsFlying) return;

        if (cameraHidden)
        {
            ReleasePlayerLocks();
            ReleaseCursorLock();
            return;
        }

        AcquireCursorLock();
    }

    /// <summary>
    /// Brings a hidden camera back as though it had just been spawned: visible again, out of
    /// world-space follow, and returned to the hand. Everything it was doing while hidden —
    /// streaming, settings, the session — carries over, which is the point of hiding rather
    /// than destroying.
    /// </summary>
    public void RevealAsFreshSpawn()
    {
        SetCameraHidden(false);
        ClearModifiers();
        PinSpace = CameraPinSpace.HandHeld;
        AcquireCursorLock();
    }

    /// <summary>Forward distance the camera spawns at, matching the Photo Camera catalog offset (0,0,0.5).</summary>
    private const float SpawnForwardOffset = 0.5f;

    /// <summary>
    /// Teleports the camera to where it would spawn — in front of the player at head height,
    /// facing them — using the same placement math as a fresh spawn (SpawnInFrontOfPlayer),
    /// just without instantiating a new one. Used to retrieve a camera that has flown off.
    /// Drops auto-follow and world-pins it there so it holds the pose and can be grabbed.
    /// </summary>
    public void TeleportInFrontOfPlayer()
    {
        if (!BasisLocalCameraDriver.HasInstance || captureCamera == null) return;

        SetCameraHidden(false);

        Vector3 headPos = BasisLocalCameraDriver.HeadPosition;
        Vector3 forward = BasisLocalCameraDriver.HeadForward();
        forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;

        Vector3 position = headPos + forward * SpawnForwardOffset;
        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
        PlaceWorldPinned(position, rotation);
    }

    /// <summary>
    /// Hides or restores the prop's own HUD. Used while the main-menu camera panel is
    /// open, so the same controls aren't fighting for the same screen space on desktop.
    /// Toggles the canvas rather than the GameObject so nothing under it re-runs
    /// OnEnable, and drops the pointer targets so hidden buttons can't be clicked.
    /// </summary>
    /// <summary>
    /// On desktop the main menu and the prop's own HUD share one flat screen, so the HUD steps
    /// aside for as long as any menu is open. In VR they occupy different space and it stays put.
    /// </summary>
    private void UpdateOnPropUIVisibility()
    {
        SetOnPropUIHidden(cameraHidden
            || (BasisDeviceManagement.IsUserInDesktop() && BasisMainMenu.Instance != null));
    }

    public void SetOnPropUIHidden(bool hidden)
    {
        if (onPropUIHidden == hidden || onPropUICanvas == null) return;
        onPropUIHidden = hidden;
        onPropUICanvas.enabled = !hidden;
        if (onPropUIRaycaster != null) onPropUIRaycaster.enabled = !hidden;
        if (onPropUICollider != null) onPropUICollider.enabled = !hidden;
    }

    /// <summary>
    /// Layers the render-layers UI must not expose, because the camera manages them itself.
    /// OverlayUI carries the camera's own world markers — both detached markers (follow-PIP
    /// puck and wireframe gizmo) and the dolly waypoints — which
    /// would leak the rig into every shot.
    /// The UI layer (players' nameplates) is exposed there as its own toggle, so there is no
    /// separate "Show Nameplates" control, and HandHeldCameraUI (the prop's HUD) is exposed
    /// as its own toggle too, off by default.
    /// </summary>
    private static readonly string[] ManagedCaptureLayers = { "OverlayUI" };

    /// <summary>Whether a given layer is one the user may toggle for this camera's captures.</summary>
    public static bool IsCaptureLayerUserTogglable(int layer)
    {
        if (layer < 0 || layer > 31) return false;
        string name = LayerMask.LayerToName(layer);
        if (string.IsNullOrEmpty(name)) return false;
        for (int Index = 0; Index < ManagedCaptureLayers.Length; Index++)
        {
            if (name == ManagedCaptureLayers[Index]) return false;
        }
        return true;
    }

    /// <summary>Reads whether a layer is currently rendered by the capture camera.</summary>
    public bool IsCaptureLayerEnabled(int layer)
    {
        if (captureCamera == null || layer < 0 || layer > 31) return false;
        return (WorldCullingMask & (1 << layer)) != 0;
    }

    /// <summary>
    /// Shows or hides a whole layer in this camera's captures by editing its culling mask.
    /// Refuses the layers the camera drives itself, so this can't undermine the nameplate
    /// toggle or leak the prop HUD into a shot.
    /// </summary>
    public void SetCaptureLayerEnabled(int layer, bool enabled)
    {
        if (captureCamera == null || !IsCaptureLayerUserTogglable(layer)) return;
        if (enabled) WorldCullingMask |= 1 << layer;
        else WorldCullingMask &= ~(1 << layer);
    }

    /// <summary>Fetches Tonemapping from the profile and sets default mode.</summary>
    private void InitializeTonemapping()
    {
        if (MetaData.Profile.TryGet(out MetaData.tonemapping))
        {
            ToggleToneMapping(PreviewTonemapping);
        }
    }

    /// <summary>Validates Depth of Field is present; logs details.</summary>
    private void InitializeDepthOfField()
    {
        if (!MetaData.Profile.TryGet(out MetaData.depthOfField))
        {
            BasisDebug.LogError("DoF profile not found!");
        }
        else
        {
            BasisDebug.Log($"DoF is loaded. FocusDistance: {MetaData.depthOfField.focusDistance.value}");
        }
    }

    /// <summary>Creates/ensures a “Basis” pictures folder for screenshots.</summary>
    private void InitializeFolders()
    {
        picturesFolder = PhotosDirectory;
        if (!Directory.Exists(picturesFolder))
        {
            Directory.CreateDirectory(picturesFolder);
        }
    }

    /// <summary>
    /// The one folder screenshots are written to, resolved per platform. Windows gets a
    /// browsable Pictures/Basis; the mobile and other-desktop paths fall back to
    /// persistentDataPath, which is the only writable location that always exists.
    /// </summary>
    public static string PhotosDirectory
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        get => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Basis");
#else
        get => Application.persistentDataPath;
#endif
    }

    /// <summary>True where a file manager can be launched: the three desktop platforms.</summary>
    public static bool CanOpenPhotosFolder =>
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
        true;
#else
        false;
#endif

    /// <summary>
    /// Opens the screenshot folder in the OS file browser. Desktop only; the path is built
    /// internally, never from user input, so it cannot be steered elsewhere.
    /// </summary>
    public static bool OpenPhotosFolder()
    {
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
        try
        {
            string folder = PhotosDirectory;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return BasisFileBrowserUtility.Reveal(folder);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Could not open photos folder: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>Full path of the last photo this camera wrote, or null until one lands.</summary>
    public string LastPhotoPath { get; private set; }

    /// <summary>File name of the last photo this camera wrote, or null.</summary>
    public string LastPhotoFileName => LastPhotoPath == null ? null : Path.GetFileName(LastPhotoPath);

    /// <summary>Why the last save failed, or null. Cleared by the next successful save.</summary>
    public string LastPhotoFailure { get; private set; }

    /// <summary>
    /// Opens the OS file browser on the photo this camera saved most recently, with the file
    /// itself highlighted, so "where did that shot go" is one click rather than a hunt through
    /// Pictures. Falls back to the plain folder when nothing has been shot yet, when the file has
    /// since been moved, or when the highlighting launcher is refused — some Windows setups block
    /// the explorer.exe spawn that selecting a file requires, and the folder still opens there.
    /// </summary>
    public bool RevealLastPhoto()
    {
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
        string path = LastPhotoPath;
        if (!string.IsNullOrEmpty(path) && BasisFileBrowserUtility.Reveal(path, true)) return true;
        return OpenPhotosFolder();
#else
        return false;
#endif
    }

    /// <summary>
    /// Records where a photo just landed. The single write point for both the flat and the 360
    /// save paths, so the panel has one place to read regardless of which one took the shot.
    /// </summary>
    private void RecordPhotoSaved(string path)
    {
        LastPhotoPath = path;
        LastPhotoFailure = null;
    }

    /// <summary>
    /// Records a failed write. The previous photo's path is deliberately kept — it is still on
    /// disk and still worth revealing — so only the failure text changes.
    /// </summary>
    private void RecordPhotoFailed(Exception e)
    {
        LastPhotoFailure = $"{e.GetType().Name}: {e.Message}";
        BasisDebug.LogError($"Could not save photo: {LastPhotoFailure}", BasisDebug.LogTag.Camera);
    }

    /// <summary>Stores the UI layer bit as a culling mask for toggling nameplates.</summary>
    private void SetupUILayerMask()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            BasisDebug.LogError("UI Layer not found.");
        }
        else
        {
            uiLayerMask = 1 << uiLayer;
        }
    }

    /// <summary>Initializes a shared “clear to color” material lazily.</summary>
    private void SetupClearMaterial()
    {
        if (clearMaterial == null)
        {
            Shader shader = Shader.Find(CLEAR_SHADER_PATH);
            if (shader != null)
            {
                clearMaterial = new Material(shader);
            }
        }
    }

    /// <summary>Registers input callbacks (e.g., pickup “use” → capture) after base start.</summary>
    public new void Start()
    {
        base.Start();
        OnPickupUse.AddListener( OnPickupUseCapture );
    }

    /// <summary>Pickup “use” callback that triggers a capture on press down.</summary>
    /// <param name="mode">Pickup use mode.</param>
    public void OnPickupUseCapture(BasisPickUpUseMode mode)
    {
        if (mode == BasisPickUpUseMode.OnPickUpUseDown)
        {
            CapturePhoto();
        }
    }

    /// <summary>
    /// (Re)creates a render texture for preview/capture and applies AA mode/quality.
    /// Automatically updates the preview material when the RT changes.
    /// </summary>
    /// <param name="width">RT width.</param>
    /// <param name="height">RT height.</param>
    /// <param name="AQ">URP SMAA quality.</param>
    /// <param name="RenderTextureFormat">Render texture format (ARGBFloat for EXR).</param>
    /// <summary>
    /// Continuously points depth of field at the follow subject so they stay sharp while moving.
    /// Reads the camera's already-positioned transform, so it runs a frame behind the move — below
    /// perception, and it avoids re-solving the subject before the camera itself has settled.
    /// <para>
    /// Only drives the focus distance; it must not switch DoF on. The DoF toggle on the prop is
    /// the single owner of <c>depthOfField.active</c>, and force-enabling it here made the effect
    /// come on while that toggle read off.
    /// </para>
    /// </summary>
    private void UpdateAutoFocus()
    {
        if (!autoFocusFollowSubject || MetaData.depthOfField == null || captureCamera == null) return;
        if (!MetaData.depthOfField.active) return;
        if (!CanAutoFocusOnFollowSubject) return;
        if (!TryGetFollowFocusPoint(out Vector3 point)) return;
        if (!TryGetFocusDepth(point, out float depth)) return;

        float current = MetaData.depthOfField.focusDistance.value;
        float focus = Mathf.Abs(depth - current) > AutoFocusSnapDistance
            ? depth
            : Mathf.Lerp(current, depth, 1f - Mathf.Exp(-AutoFocusPullRate * Time.deltaTime));

        ApplyFocusDistance(focus);
    }

    private const float FocusFocalLengthMargin = 1.15f;
    private const float AutoFocusPullRate = 8f;
    private const float AutoFocusSnapDistance = 8f;
    private const float GaussianFalloffRatio = 2.5f;

    /// <summary>
    /// True when the follow subject is somewhere the camera could actually be pointed. Follow
    /// resolves to the local player whenever no remote is targeted, and while the camera is in
    /// hand that point sits behind the lens, so focusing on it blurs the whole shot.
    /// <para>
    /// A fitted modifier only counts while the Subject slot names somebody: with the slot on None
    /// the stack is driving the camera at nothing in particular, and the fallback is again your own
    /// head — the same shot-wide blur, arrived at from the other direction.
    /// </para>
    /// </summary>
    public bool CanAutoFocusOnFollowSubject =>
        IsFollowingRemotePlayer || (IsModifierDriven && Modifiers.ResolvesSubject);

    /// <summary>
    /// True when Follow Subject focus is selected and there is nobody for it to keep sharp, which
    /// is a state the operator cannot otherwise see: the focus mode reads Follow Subject, the
    /// manual slider is quietly still in charge, and who the camera films is set on another page.
    /// </summary>
    public bool AutoFocusHasNoSubject => autoFocusFollowSubject && !CanAutoFocusOnFollowSubject;

    /// <summary>
    /// Shortest focus distance the blur solver can take, in metres. Its circle of confusion is
    /// <c>(f/N · f)/(P − f)</c>, so a focus distance at or inside the lens focal length divides by
    /// zero and then inverts, swapping near and far blur.
    /// </summary>
    public float MinimumFocusDistance =>
        MetaData != null && MetaData.depthOfField != null
            ? Mathf.Max(0.1f, MetaData.depthOfField.focalLength.value * 0.001f * FocusFocalLengthMargin)
            : 0.1f;

    /// <summary>
    /// Depth of a world point along the capture camera's view axis, which is what the blur solver
    /// compares the focus distance against. Fails when the point is behind the lens or inside
    /// <see cref="MinimumFocusDistance"/>, in which case focus should be left where it is.
    /// </summary>
    public bool TryGetFocusDepth(Vector3 worldPoint, out float depth)
    {
        depth = 0f;
        if (captureCamera == null) return false;

        captureCamera.transform.GetPositionAndRotation(out Vector3 eye, out Quaternion rotation);
        depth = Vector3.Dot(worldPoint - eye, rotation * Vector3.forward);
        return depth > MinimumFocusDistance;
    }

    /// <summary>
    /// The one place a focus distance reaches the effect. Clamps clear of the lens focal length,
    /// and in Gaussian mode — which has no focus distance of its own, only a far-blur ramp — places
    /// that ramp to begin at the focus plane so the control still does something.
    /// </summary>
    public void ApplyFocusDistance(float metres)
    {
        focusRacking = false;
        SetFocusDistance(metres);
    }

    public void RefreshFocusDistance()
    {
        if (MetaData == null || MetaData.depthOfField == null) return;
        SetFocusDistance(MetaData.depthOfField.focusDistance.value);
    }

    private void SetFocusDistance(float metres)
    {
        if (MetaData == null || MetaData.depthOfField == null) return;

        float focus = Mathf.Max(MinimumFocusDistance, metres);

        MetaData.depthOfField.focusDistance.overrideState = true;
        MetaData.depthOfField.focusDistance.value = focus;

        if (MetaData.depthOfField.mode.value == DepthOfFieldMode.Gaussian)
        {
            MetaData.depthOfField.gaussianStart.overrideState = true;
            MetaData.depthOfField.gaussianStart.value = focus;
            MetaData.depthOfField.gaussianEnd.overrideState = true;
            MetaData.depthOfField.gaussianEnd.value = focus * GaussianFalloffRatio;
        }
    }

    [SerializeField] public float focusRackSeconds = 0.5f;

    private float focusRackFrom, focusRackTo, focusRackElapsed;
    private bool focusRacking;

    public bool IsRackingFocus => focusRacking;

    public float FocusRackTarget => focusRacking
        ? focusRackTo
        : (MetaData != null && MetaData.depthOfField != null ? MetaData.depthOfField.focusDistance.value : 0f);

    public void RackFocusTo(float metres)
    {
        if (MetaData == null || MetaData.depthOfField == null) return;

        float target = Mathf.Max(MinimumFocusDistance, metres);
        float current = Mathf.Max(MinimumFocusDistance, MetaData.depthOfField.focusDistance.value);

        if (focusRackSeconds <= 0f || Mathf.Abs(target - current) <= FocusRackEpsilon)
        {
            focusRacking = false;
            SetFocusDistance(target);
            HandHeld?.SyncFocusReadout();
            return;
        }

        focusRackFrom = current;
        focusRackTo = target;
        focusRackElapsed = 0f;
        focusRacking = true;
    }

    private void TickFocusRack()
    {
        if (!focusRacking) return;
        if (MetaData == null || MetaData.depthOfField == null)
        {
            focusRacking = false;
            return;
        }

        focusRackElapsed += Time.deltaTime;
        float t = focusRackSeconds > 0f ? Mathf.Clamp01(focusRackElapsed / focusRackSeconds) : 1f;

        SetFocusDistance(SampleFocusRack(focusRackFrom, focusRackTo, t));
        HandHeld?.SyncFocusReadout();

        if (t >= 1f) focusRacking = false;
    }

    private const float FocusRackEpsilon = 0.001f;

    public static float SampleFocusRack(float from, float to, float t)
    {
        float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
        float near = 1f / Mathf.Max(from, 1e-4f);
        float far = 1f / Mathf.Max(to, 1e-4f);
        return 1f / Mathf.Lerp(near, far, eased);
    }

    /// <summary>Clamps an arbitrary sample count to a value the GPU accepts (1/2/4/8).</summary>
    private static int SanitizeMsaaSamples(int requested)
    {
        if (requested >= 8) return 8;
        if (requested >= 4) return 4;
        if (requested >= 2) return 2;
        return 1;
    }

    /// <summary>Sets MSAA samples on the capture RT and rebuilds the live preview to match.</summary>
    public void SetMsaaSamples(int samples)
    {
        msaaSamples = SanitizeMsaaSamples(samples);
        if (renderTexture != null)
        {
            SetResolution(renderTexture.width, renderTexture.height, CameraData.antialiasingQuality, renderTexture.format);
        }
    }

    /// <summary>
    /// Live-preview RT format. SDR + sRGB (unlike the ARGBFloat capture RT) so the quad displays
    /// the same gamma the camera renders to the screen in Direct To Screen mode — an ARGBFloat RT
    /// silently ignores the descriptor's sRGB flag (float formats have no hardware sRGB), which
    /// made the preview look darker/off versus the actual on-screen render. EXR capture still
    /// switches to ARGBFloat for that one frame.
    /// </summary>
    private const RenderTextureFormat PreviewRenderTextureFormat = RenderTextureFormat.Default;

    public void SetResolution(int width, int height, AntialiasingQuality AQ, RenderTextureFormat RenderTextureFormat = RenderTextureFormat.ARGBFloat)
    {
        bool textureChanged = false;

        int samples = BasisCameraTargetMsaa.Clamp(SanitizeMsaaSamples(msaaSamples));

        if (renderTexture == null || renderTexture.width != width || renderTexture.height != height || renderTexture.format != RenderTextureFormat || renderTexture.antiAliasing != samples)
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
            }

            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat, depth)
            {
                msaaSamples = samples,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = true
            };
            renderTexture = new RenderTexture(descriptor);
            renderTexture.Create();
            textureChanged = true;
        }

        if (captureCamera.targetTexture != renderTexture)
            captureCamera.targetTexture = renderTexture;

        if (CameraData.antialiasing != AntialiasingMode.SubpixelMorphologicalAntiAliasing)
            CameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

        if (CameraData.antialiasingQuality != AQ)
            CameraData.antialiasingQuality = AQ;

        BindViewfinderFeed(textureChanged);
        if (textureChanged && backgroundMode == BasisCameraBackgroundMode.Transparent && CanPreserveVideoOutputAlpha())
        {
            PrepareTransparentVideoOutputResources(renderTexture);
        }
    }

    /// <summary>
    /// Points the prop's viewfinder mesh at whatever is currently being shown — the feed, or the
    /// focus-peaking overlay of it. Change-gated, since it is called from the per-frame tick as
    /// well as from every resize.
    /// </summary>
    private void BindViewfinderFeed(bool force = false)
    {
        if (actualMaterial == null) return;

        RenderTexture feed = ViewfinderTexture;
        if (!force && actualMaterial == lastAssignedMaterial && feed == lastAssignedRenderTexture) return;

        actualMaterial.SetTexture("_MainTex", feed);
        actualMaterial.mainTexture = feed;
        if (Renderer != null) Renderer.sharedMaterial = actualMaterial;
        lastAssignedMaterial = actualMaterial;
        lastAssignedRenderTexture = feed;
        ApplyViewfinderCrop();
    }

    /// <summary>
    /// Sizes the feed for how it is currently being shown: the screen's shape while Direct To
    /// Screen is presenting it, the authored preview size otherwise. Every path that leaves the RT
    /// at some other size — a capture, a re-enable, toggling the mode — comes back through here
    /// instead of assuming the preview size, which would put the letterbox bars back for as long
    /// as the mode stayed on.
    /// </summary>
    private void ApplyPreviewResolution()
    {
        if (captureInFlight) return;

        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low, PreviewRenderTextureFormat);
    }

    /// <summary>
    /// Keeps the prop's viewfinder undistorted. The quad is a fixed shape, so a feed that is not
    /// the capture aspect — which is what Direct To Screen produces, since the feed then follows
    /// the screen — gets squashed onto it. Showing the middle of the feed instead keeps faces the
    /// right width; the full frame is still there on the menu panel, which sizes itself to the
    /// feed. Identity whenever the feed and the capture aspect agree, which is every case except
    /// that mode.
    /// </summary>
    private void ApplyViewfinderCrop()
    {
        if (actualMaterial == null) return;

        Vector2 scale = Vector2.one;
        Vector2 offset = Vector2.zero;

        if (renderTexture != null && renderTexture.height > 0 && captureWidth > 0 && captureHeight > 0)
        {
            float feedAspect = (float)renderTexture.width / renderTexture.height;
            float shotAspect = (float)captureWidth / captureHeight;

            if (feedAspect > shotAspect)
            {
                scale.x = shotAspect / feedAspect;
                offset.x = (1f - scale.x) * 0.5f;
            }
            else if (feedAspect < shotAspect)
            {
                scale.y = feedAspect / shotAspect;
                offset.y = (1f - scale.y) * 0.5f;
            }
        }

        // Both, because the preview material's main texture is _MainTex on some shaders and
        // _BaseMap on the URP ones — the same reason the texture itself is assigned twice above.
        actualMaterial.mainTextureScale = scale;
        actualMaterial.mainTextureOffset = offset;
        if (actualMaterial.HasProperty("_MainTex"))
        {
            actualMaterial.SetTextureScale("_MainTex", scale);
            actualMaterial.SetTextureOffset("_MainTex", offset);
        }
    }

    /// <summary>
    /// Captures a still image from the camera using the current resolution/format.
    /// Uses AsyncGPUReadback and saves on completion.
    /// </summary>
    /// <param name="TextureFormat">Texture format for CPU-side buffer.</param>
    /// <param name="Format">RT format for rendering the frame.</param>
    public IEnumerator TakeScreenshot(TextureFormat TextureFormat, RenderTextureFormat Format = RenderTextureFormat.ARGBFloat)
    {
        captureInFlight = true;
        SetResolution(captureWidth, captureHeight, AntialiasingQuality.High, Format);
        yield return new WaitForEndOfFrame();

        bool headWasNormal = BasisLocalAvatarDriver.IsNormalHead;
        BasisLocalAvatarDriver.ScaleHeadToNormal();
        ToggleToneMapping(CaptureTonemapping);

#if BASIS_HAS_GI && !UNITY_ANDROID
        SMModuleGlobalIlluminationURP.BeginCapture(captureCamera, OverrideGlobalIllumination ? GlobalIlluminationOverride : (BasisGlobalIlluminationCaptureOverride?)null);
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
        BasisRTAOIntegration.BeginCapture(captureCamera, OverrideRTAO ? RTAOOverride : (BasisRTAOCaptureOverride?)null);
#endif
        try
        {
            captureCamera.Render();
        }
        finally
        {
#if BASIS_HAS_GI && !UNITY_ANDROID
            SMModuleGlobalIlluminationURP.EndCapture();
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            BasisRTAOIntegration.EndCapture();
#endif
            if (!headWasNormal) BasisLocalAvatarDriver.ScaleHeadToZero();
        }

        BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata = BasisHandHeldCameraPhotoMetadata.CollectMetadata(captureCamera, transform);

        bool resolved = NeedsSrgbResolve(TextureFormat, renderTexture);
        RenderTexture readbackSource = resolved ? ResolveToSrgb(renderTexture) : renderTexture;

        EnsureTexturePool(readbackSource.width, readbackSource.height, TextureFormat);

        AsyncGPUReadback.Request(readbackSource, 0, request =>
        {
            if (resolved) ReleaseSrgbResolveTarget();

            if (request.hasError)
            {
                BasisDebug.LogError("GPU Readback failed.");
                SetNormalAfterCapture();
                return;
            }

            Unity.Collections.NativeArray<byte> data = request.GetData<byte>();
            pooledScreenshot.LoadRawTextureData(data);
            pooledScreenshot.Apply(false);

            // After the readback and before the save, so what the body does to a picture — the
            // fog on the ends of a roll, the date a databack burned in, the sheet a print is
            // mounted on — is in the file rather than only on screen, and every path that writes
            // the picture out carries it, including the print-to-world one.
            //
            // The result is saved rather than the buffer, because a print is a bigger sheet with
            // the photograph placed on it and is not the texture that was handed in.
            Texture2D finished = FinishPicture(pooledScreenshot);

            SetNormalAfterCapture();
            SaveScreenshotAsync(finished, photoMetadata);
        });
    }

    /// <summary>Ensures <see cref="pooledScreenshot"/> matches the required size/format.</summary>
    private void EnsureTexturePool(int width, int height, TextureFormat format)
    {
        if (pooledScreenshot == null || pooledScreenshot.width != width || pooledScreenshot.height != height || pooledScreenshot.format != format)
        {
            if (pooledScreenshot != null)
                Destroy(pooledScreenshot);
            pooledScreenshot = new Texture2D(width, height, format, false);
        }
    }

    /// <summary>
    /// HDR render texture format for still capture. URP takes an external target's format as its
    /// own internal colour buffer format — see the note in <c>CreateRenderTextureDescriptor</c> —
    /// so an 8-bit target clamps every shading result to 1.0 before tonemapping runs. Per-channel
    /// clamping is what shifts hue in bright scenes: a highlight of (3.0, 1.4, 0.5) lands as
    /// (1, 1, 0.5) and reads yellow rather than orange, and ACES then has no headroom left to roll
    /// off. Half float matches the pipeline asset's own 64-bit HDR buffer precision.
    /// </summary>
    private static RenderTextureFormat CaptureHdrRenderTextureFormat =>
        SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
            ? RenderTextureFormat.ARGBHalf
            : RenderTextureFormat.ARGB32;

    /// <summary>
    /// True when the capture frame still has to be display encoded before it can be read into an
    /// 8-bit buffer. Float render textures have no hardware sRGB write, so an HDR target holds
    /// linear values whatever the descriptor's sRGB flag says; writing those bytes straight into a
    /// PNG is what makes an HDR capture come out dark and over-contrasty.
    /// </summary>
    private static bool NeedsSrgbResolve(TextureFormat format, RenderTexture source) =>
        format == TextureFormat.RGBA32
        && source != null
        && !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);

    /// <summary>
    /// Blits the HDR capture frame into an 8-bit sRGB target and returns it for readback. The
    /// hardware sRGB write on that target is what performs the linear-to-display encode. Freed
    /// again as soon as the readback lands — at the 8K preset it is 133MB that nothing else needs.
    /// </summary>
    private RenderTexture ResolveToSrgb(RenderTexture source)
    {
        ReleaseSrgbResolveTarget();

        var descriptor = new RenderTextureDescriptor(source.width, source.height, RenderTextureFormat.ARGB32, 0)
        {
            msaaSamples = 1,
            useMipMap = false,
            autoGenerateMips = false,
            sRGB = true
        };
        srgbResolveTexture = new RenderTexture(descriptor) { name = "BasisCaptureSrgbResolve" };
        srgbResolveTexture.Create();

        bool previousSrgbWrite = GL.sRGBWrite;
        GL.sRGBWrite = true;
        Graphics.Blit(source, srgbResolveTexture);
        GL.sRGBWrite = previousSrgbWrite;

        return srgbResolveTexture;
    }

    /// <summary>Frees the sRGB resolve target.</summary>
    private void ReleaseSrgbResolveTarget()
    {
        if (srgbResolveTexture == null) return;
        srgbResolveTexture.Release();
        Destroy(srgbResolveTexture);
        srgbResolveTexture = null;
    }

    /// <summary>
    /// Render and readback formats for a still capture. PNG renders HDR and is resolved to sRGB
    /// before readback; EXR keeps the float frame linear, which is what the format wants.
    /// </summary>
    private void GetCaptureFormats(out TextureFormat textureFormat, out RenderTextureFormat renderFormat)
    {
        if (captureFormat == "EXR")
        {
            textureFormat = TextureFormat.RGBAFloat;
            renderFormat = RenderTextureFormat.ARGBFloat;
        }
        else
        {
            textureFormat = TextureFormat.RGBA32;
            renderFormat = CaptureHdrRenderTextureFormat;
        }
    }
    /// <summary>Starts a 5-second countdown and triggers a capture at the end.</summary>
    /// <summary>Running countdown coroutine, held so a second press can cancel it.</summary>
    private Coroutine countdownRoutine;

    /// <summary>
    /// Starts the self-timer, or cancels it if one is already counting down — the timer button
    /// is a toggle. Cancelling only stops the local capture; a remote that already received the
    /// countdown will still play its tick/shutter sounds, since the countdown network message
    /// is fire-and-forget with no cancel path.
    /// </summary>
    public void Timer()
    {
        if (IsCountingDown)
        {
            CancelTimer();
            return;
        }

        // Same gate CapturePhoto applies: the timer is just a delayed capture, so a locked client
        // must not start one — and must not broadcast the countdown remotes replay.
        if (BasisNetworkModeration.CameraCaptureBlockedLocally)
        {
            BasisDebug.LogWarning("Timer blocked: camera capture is locked by an admin.", BasisDebug.LogTag.Camera);
            return;
        }

        // Notify remote clients so they replay the same tick/shutter timing
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            BasisNetworkPIPCameraDriver.SendCountdown(5);
        }
        countdownRoutine = StartCoroutine(DelayedAction(5));
    }

    /// <summary>Stops a running self-timer and clears the countdown display.</summary>
    public void CancelTimer()
    {
        if (countdownRoutine != null)
        {
            StopCoroutine(countdownRoutine);
            countdownRoutine = null;
        }
        CountdownRemaining = 0;
        if (countdownText != null) countdownText.text = string.Empty;
    }

    /// <summary>Countdown coroutine that flashes “!” and then takes a screenshot.</summary>
    private IEnumerator DelayedAction(float delaySeconds)
    {
        for (int i = (int)delaySeconds; i > 0; i--)
        {
            CountdownRemaining = i;
            countdownText.text = i.ToString();

            if (BasisDeviceManagement.Instance.CameraCountdownTickSound != null)
            {
                BasisUISounds.PlayAt(BasisUISoundEvent.CameraCountdownTick, BasisDeviceManagement.Instance.CameraCountdownTickSound, captureCamera.transform.position, SMModuleAudio.ActivePropVolume);
            }

            yield return new WaitForSeconds(1f);
        }

        CountdownRemaining = 0;
        countdownText.text = "!";
        yield return new WaitForSeconds(0.5f);

        countdownRoutine = null;

        // Re-checked here because an admin can lock capture during the countdown, and before the
        // shutter sound for the same reason CapturePhoto checks early: a refusal must not sound
        // like a photo was taken.
        if (BasisNetworkModeration.CameraCaptureBlockedLocally)
        {
            BasisDebug.LogWarning("Timer capture blocked: camera capture is locked by an admin.", BasisDebug.LogTag.Camera);
            countdownText.text = string.Empty;
            yield break;
        }

        // Re-checked here too, and for the same reason: five seconds is long enough for the last
        // frame of a pack to have been spent by the shutter button while this was counting.
        if (!TryTakeFrame())
        {
            countdownText.text = string.Empty;
            yield break;
        }

        // Choose formats based on captureFormat
        GetCaptureFormats(out TextureFormat format, out RenderTextureFormat renderFormat);

        // Play shutter sound locally (network was already notified via SendCountdown)
        if (BasisDeviceManagement.Instance.CameraShutterSound != null)
        {
            BasisUISounds.PlayAt(BasisUISoundEvent.CameraShutter, BasisDeviceManagement.Instance.CameraShutterSound, captureCamera.transform.position, SMModuleAudio.ActivePropVolume);
        }

        if (capture360Enabled)
            StartCoroutine(TakeScreenshot360(captureFormat == "EXR"));
        else
            StartCoroutine(TakeScreenshot(format, renderFormat));
        countdownText.text = ((int)delaySeconds).ToString();
    }

    /// <summary>Toggles UI/nameplates in/out of the capture via the UI layer bit.</summary>
    public void Nameplates()
    {
        if (uiLayerMask == 0)
        {
            BasisDebug.LogWarning("UI Layer Mask was not initialized properly.");
            return;
        }

        if ((WorldCullingMask & uiLayerMask) != 0)
            WorldCullingMask &= ~uiLayerMask;
        else
            WorldCullingMask |= uiLayerMask;
    }

    /// <summary>Immediate photo capture using the current format choice (EXR/PNG).</summary>
    public void CapturePhoto()
    {
        // Refused before the shutter sound so a locked capture doesn't look like it worked.
        if (BasisNetworkModeration.CameraCaptureBlockedLocally)
        {
            BasisDebug.LogWarning("CapturePhoto blocked: camera capture is locked by an admin.", BasisDebug.LogTag.Camera);
            return;
        }

        // The film, the wind-on and the flash, in one call — and before the shutter sound for the
        // same reason the moderation check is: a camera with nothing left in it must not sound like
        // it took a picture. A digital body always says yes.
        if (!TryTakeFrame()) return;

        GetCaptureFormats(out TextureFormat format, out RenderTextureFormat renderFormat);

        // Play shutter sound locally at the camera position
        if (BasisDeviceManagement.Instance.CameraShutterSound != null)
        {
            BasisUISounds.PlayAt(BasisUISoundEvent.CameraShutter, BasisDeviceManagement.Instance.CameraShutterSound, captureCamera.transform.position, SMModuleAudio.ActivePropVolume);
        }

        // Send shutter sound event over the network
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            BasisNetworkPIPCameraDriver.SendShutterSound();
        }

        if (capture360Enabled)
        {
            StartCoroutine(TakeScreenshot360(captureFormat == "EXR"));
            return;
        }

        StartCoroutine(TakeScreenshot(format, renderFormat));
    }
    private BasisRenderRateLimiter renderRateLimiter;

    public const float MinHandHeldRenderHz = 1f;
    public const float MaxHandHeldRenderHz = 120f;

    /// <summary>Render-phase priority: after the camera has been moved (202).</summary>
    private const int SimulateLatePriority = 204;

    /// <summary>
    /// Per-frame camera upkeep, run from <see cref="BasisLocalPlayer.AfterSimulateOnRender"/> rather
    /// than a Unity LateUpdate.
    /// <para>
    /// Everything here reads the capture camera's pose — the detached marker and the networked
    /// PIP position. The camera is moved by UpdateCamera at priority 202 in the
    /// same render phase, so a plain LateUpdate raced it: with no script execution order set, this
    /// could run either side of the move and would intermittently publish and place things from the
    /// previous frame's pose. That inconsistency read as jitter.
    /// </para>
    /// </summary>
    private void SimulateLate()
    {
        // Before the gate, so the frame the monitor is being given — or no longer is — is the
        // one the gate decides for.
        TickDirectToScreen();
        UpdateRenderGate();

        // Wind-on, develop and the flash all count down here rather than in an Update, so the lamp
        // is put out on a frame boundary instead of somewhere inside a capture.
        TickBody();

        // Ahead of the render, so the exposure the meter settles on is the one this frame is shot at.
        TickAutoBrightness();

        // Before every surface that binds a feed, so they are pointed at the overlay for the frame
        // it was produced in rather than the frame after.
        TickFocusPeaking();

        // After the peaks, so the grid lies over them: it is the thing being aligned against.
        TickViewfinderGrid();

        TickVideoOutput();
        TickGifRecorder();
        TickVideoRecorder();
        TickPhotogrammetry();
        UpdateOnPropUIVisibility();
        TickFocusRack();
        UpdateAutoFocus();
        UpdateFollowPip();
        // After the marker, so the puck and the screen parked past it are placed from one pose.
        UpdatePuckPreview();

        // After everything that moves the camera, so the reticle is drawn against the pose the
        // frame actually ended on rather than the one it started from.
        TickLookAtPointer();

        DebugGizmos.Tick(this);

        // Send PIP camera position to network
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            GetNetworkedMarkerPose(out Vector3 pos, out Quaternion rot);
            BasisNetworkPIPCameraDriver.SendPIPPosition(pos, rot);
        }
    }
    /// <summary>
    /// Encodes and writes the screenshot to disk asynchronously using the selected format.
    /// </summary>
    /// <param name="screenshot">CPU-side texture to encode.</param>
    public void SaveScreenshotAsync(Texture2D screenshot) => SaveScreenshotAsync(screenshot, null);

    public async void SaveScreenshotAsync(Texture2D screenshot, BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string extension = captureFormat == "EXR" ? "exr" : "png";
        // The texture's own size rather than the capture size: a body that mounts its picture in
        // a border writes a bigger file than the frame it rendered, and a name that reported the
        // frame would disagree with the image it is on.
        int savedWidth = screenshot != null ? screenshot.width : captureWidth;
        int savedHeight = screenshot != null ? screenshot.height : captureHeight;
        string filename = $"Screenshot_{timestamp}_{savedWidth}x{savedHeight}.{extension}";
        string path = GetSavePath(filename);
        BasisCameraPrintResize.PrintCopy printCopy = default;

        // async void: anything thrown out of here surfaces as an unhandled exception rather than
        // as something the shooter can act on, so encode-and-write is captured and reported on
        // the panel instead — a full disk or a locked file is a normal thing to hit.
        try
        {
            // Copied out before any await: the readback texture is pooled and the next shutter
            // press overwrites it, so nothing past this line may touch the Texture2D.
            int width = screenshot.width;
            int height = screenshot.height;
            var pixelFormat = screenshot.graphicsFormat;
            bool exr = captureFormat == "EXR";
            bool printable = printPhotoEnabled && !exr && screenshot.format == TextureFormat.RGBA32;
            string format = captureFormat;
            Unity.Collections.NativeArray<byte> raw = screenshot.GetRawTextureData<byte>();
            byte[] pixels = new byte[raw.Length];
            Unity.Collections.NativeArray<byte>.Copy(raw, pixels, raw.Length);

            (byte[] imageData, BasisCameraPrintResize.PrintCopy print) = await Task.Run(() =>
            {
                byte[] encoded = exr
                    ? ImageConversion.EncodeArrayToEXR(pixels, pixelFormat, (uint)width, (uint)height, 0, Texture2D.EXRFlags.CompressZIP)
                    : ImageConversion.EncodeArrayToPNG(pixels, pixelFormat, (uint)width, (uint)height, 0);

                if (photoMetadata != null)
                    encoded = BasisHandHeldCameraPhotoMetadata.Embed(encoded, format, photoMetadata, width, height);

                // Caught on its own rather than under the save's handler: a resize that fails costs
                // a card, and must never be the reason a photograph that encoded perfectly well is
                // reported to the shooter as unsaved.
                BasisCameraPrintResize.PrintCopy builtPrint = default;
                if (printable)
                {
                    try
                    {
                        builtPrint = BasisCameraPrintResize.Build(pixels, width, height, encoded.LongLength);
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogWarning(
                            $"Print Photo could not resize the shot to fit the image pickup limits: {e.GetType().Name}: {e.Message}",
                            BasisDebug.LogTag.Camera);
                    }
                }
                return (encoded, builtPrint);
            });
            printCopy = print;

            await File.WriteAllBytesAsync(path, imageData);
        }
        catch (Exception e)
        {
            RecordPhotoFailed(e);
            return;
        }

        RecordPhotoSaved(path);
        PrintPhotoIfEnabled(path, printCopy);
    }

    /// <summary>
    /// Hands a photo that just landed on disk to the image pickup service, spawning it in front
    /// of the player as the same shareable, replicated card a drag-and-dropped image file makes.
    /// PNG only: EXR is a float format the pickup pipeline cannot decode, so those saves stay on
    /// disk rather than raising a rejection popup for every shot.
    ///
    /// <para>A shot past what the service imports is shared as the resized copy
    /// <see cref="BasisCameraPrintResize.Build(byte[], int, int, long)"/> made of it, and the shooter is told once that it happened.
    /// The file on disk is untouched either way — it is still the full-size photograph.</para>
    /// </summary>
    private void PrintPhotoIfEnabled(string path, BasisCameraPrintResize.PrintCopy printCopy)
    {
        if (!printPhotoEnabled) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            BasisDebug.Log("Print Photo skipped: only PNG photos can become image pickups.", BasisDebug.LogTag.Camera);
            return;
        }

        if (!printCopy.Exists)
        {
            BasisImagePickupManager.SpawnFromFile(path);
            return;
        }

        // False means the service refused the spawn for a reason of its own — an admin lock, or
        // the per-player image limit — and has already told the shooter why. A second popup
        // about a resize that no longer matters would only bury that one.
        if (!BasisImagePickupManager.SpawnFromImageData(printCopy.Png, Path.GetFileName(path))) return;

        BasisDebug.Log(
            $"Print Photo resized {printCopy.SourceWidth}x{printCopy.SourceHeight} to "
                + $"{printCopy.Width}x{printCopy.Height} to fit the image pickup limits.",
            BasisDebug.LogTag.Camera);
        ShowPrintResizedNotice(printCopy);
    }

    /// <summary>
    /// The capture size the resize notice was last shown for. A shooter working at 8K takes a
    /// roll of them, and a modal dialogue between every shutter press would be worse than the
    /// rejection this replaced; the notice is worth showing once per size, not once per photo.
    /// </summary>
    private Vector2Int lastResizeNoticeFor;

    /// <summary>
    /// Tells the shooter that the card in front of them is a smaller copy, and that the photo
    /// they shot is on disk at full size. Diverted into the notification centre when the user has
    /// asked for popups to go there, like every other non-blocking notice.
    /// </summary>
    private void ShowPrintResizedNotice(BasisCameraPrintResize.PrintCopy printCopy)
    {
        var shotAt = new Vector2Int(printCopy.SourceWidth, printCopy.SourceHeight);
        if (lastResizeNoticeFor == shotAt) return;
        lastResizeNoticeFor = shotAt;

        string title = BasisLocalization.Get("camera.printPhoto.resized.title");
        string body = BasisLocalization.Get("camera.printPhoto.resized.description",
            printCopy.SourceWidth, printCopy.SourceHeight, printCopy.Width, printCopy.Height);
        string accept = BasisLocalization.Get("ui.ok");

        // Unsolicited, so under do-not-disturb this belongs in the notification bell rather than
        // in front of someone mid-roll — CreateNew makes that call itself. Only the branch that
        // actually draws a panel needs a menu, and a null Instance just means it is closed.
        if (!BasisNotificationCenter.RouteToNotifications && !BasisMainMenu.Instance)
        {
            BasisMainMenu.Open();
        }

        BasisMenuDialoguePanel.CreateNew(title, body, accept, (Action<bool>)null, true, BasisPanelSeverity.Calm, BasisNotificationCategory.Content);
    }

    /// <summary>Builds a platform-appropriate save path for a screenshot filename.</summary>
    public string GetSavePath(string filename) => Path.Combine(PhotosDirectory, filename);

    /// <summary>Applies one of the preset resolutions from <see cref="MetaData.resolutions"/>.</summary>
    public void ChangeResolution(int index)
    {
        if (index >= 0 && index < MetaData.resolutions.Length)
        {
            (captureWidth, captureHeight) = MetaData.resolutions[index];
            // The viewfinder crop is framed against the capture aspect, so a new preset re-frames it.
            ApplyViewfinderCrop();
        }
    }

    /// <summary>Switches between formats in <see cref="MetaData.formats"/> and logs the change.</summary>
    public void ChangeFormat(int index)
    {
        captureFormat = MetaData.formats[index];
        BasisDebug.Log($"Capture format changed to {captureFormat}");
    }

    /// <summary>
    /// Restores tonemapping and returns preview RT settings after capture.
    /// </summary>
    public void SetNormalAfterCapture()
    {
        captureInFlight = false;
        ToggleToneMapping(PreviewTonemapping);
        ApplyPreviewResolution();
    }

    /// <summary>Sets the URP tonemapping mode on the active profile.</summary>
    public void ToggleToneMapping(TonemappingMode mappingMode)
    {
        if (MetaData.tonemapping == null) return;
        MetaData.tonemapping.mode.value = mappingMode;
    }

    /// <summary>
    /// What the viewfinder is graded with. Fixed: the preview is rendered at a different resolution
    /// and exposure to the still, and a viewfinder whose grade moved under the operator would make
    /// the two harder to compare rather than easier.
    /// </summary>
    public const TonemappingMode PreviewTonemapping = TonemappingMode.Neutral;

    /// <summary>
    /// Which tonemapper the saved photo is graded with. ACES by default, which is what the capture
    /// path always used before this was a choice.
    /// </summary>
    public TonemappingMode CaptureTonemapping { get; private set; } = TonemappingMode.ACES;

    /// <summary>Sets the still's grade from a persisted <see cref="TonemappingMode"/> value.</summary>
    public void SetCaptureTonemapping(int mode)
    {
        CaptureTonemapping = System.Enum.IsDefined(typeof(TonemappingMode), mode)
            ? (TonemappingMode)mode
            : TonemappingMode.ACES;
    }

#if BASIS_HAS_GI && !UNITY_ANDROID
    /// <summary>
    /// The per-photo Global Illumination substitute this camera applies when
    /// <see cref="OverrideGlobalIllumination"/> is on. Inert otherwise — like
    /// <see cref="CaptureTonemapping"/>, there is nothing here to keep continuously previewed, so
    /// it only ever reaches the renderer inside <see cref="TakeScreenshot"/>. Defaulted to match
    /// the player's own live Global Illumination settings, so turning the override on for the
    /// first time does not jar against what the live preview already looks like.
    /// </summary>
    private BasisGlobalIlluminationCaptureOverride giOverride = new BasisGlobalIlluminationCaptureOverride
    {
        Mode = SMModuleGlobalIlluminationURP.ModeOptions[0],
        SkinnedMeshes = SMModuleGlobalIlluminationURP.SkinnedMeshesOptions[1],
        Layers = SMModuleGlobalIlluminationURP.LayersOptions[2],
        Quality = SMModuleGlobalIlluminationURP.QualityOptions[1],
        Fallback = SMModuleGlobalIlluminationURP.FallbackOptions[2],
        IgnoreBakedEmission = false,
        Intensity = 1f,
        Saturation = 1f,
        Obscurance = 0.5f,
        RayLength = 16f,
        Smoothing = 1f,
        WideBlur = true,
        RayReuse = true,
        Emitters = true,
        EmitterIntensity = 3f,
        Specular = false,
        ObscuranceRadius = 0.5f,
        FadeDistance = 120f,
        NormalBias = 0.02f,
        DistanceBias = 0.0015f,
        BounceThreshold = 0.02f,
        FireflyClamp = 6f,
        ReflectionProbes = false,
        Mirrors = true,
    };

    /// <summary>Whether <see cref="GlobalIlluminationOverride"/> substitutes into this camera's own captures. Off by default, so a fresh camera's photos match the player's live settings exactly.</summary>
    public bool OverrideGlobalIllumination { get; private set; }

    public BasisGlobalIlluminationCaptureOverride GlobalIlluminationOverride => giOverride;

    public void SetOverrideGlobalIllumination(bool enabled) => OverrideGlobalIllumination = enabled;
    public void SetGlobalIlluminationOverrideMode(int index) => giOverride.Mode = ClampedGiOption(SMModuleGlobalIlluminationURP.ModeOptions, index);
    public void SetGlobalIlluminationOverrideSkinnedMeshes(int index) => giOverride.SkinnedMeshes = ClampedGiOption(SMModuleGlobalIlluminationURP.SkinnedMeshesOptions, index);
    public void SetGlobalIlluminationOverrideLayers(int index) => giOverride.Layers = ClampedGiOption(SMModuleGlobalIlluminationURP.LayersOptions, index);
    public void SetGlobalIlluminationOverrideQuality(int index) => giOverride.Quality = ClampedGiOption(SMModuleGlobalIlluminationURP.QualityOptions, index);
    public void SetGlobalIlluminationOverrideFallback(int index) => giOverride.Fallback = ClampedGiOption(SMModuleGlobalIlluminationURP.FallbackOptions, index);
    public void SetGlobalIlluminationOverrideIgnoreBakedEmission(bool value) => giOverride.IgnoreBakedEmission = value;
    public void SetGlobalIlluminationOverrideIntensity(float value) => giOverride.Intensity = value;
    public void SetGlobalIlluminationOverrideSaturation(float value) => giOverride.Saturation = value;
    public void SetGlobalIlluminationOverrideObscurance(float value) => giOverride.Obscurance = value;
    public void SetGlobalIlluminationOverrideRayLength(float value) => giOverride.RayLength = value;
    public void SetGlobalIlluminationOverrideSmoothing(float value) => giOverride.Smoothing = value;
    public void SetGlobalIlluminationOverrideWideBlur(bool value) => giOverride.WideBlur = value;
    public void SetGlobalIlluminationOverrideRayReuse(bool value) => giOverride.RayReuse = value;
    public void SetGlobalIlluminationOverrideEmitters(bool value) => giOverride.Emitters = value;
    public void SetGlobalIlluminationOverrideEmitterIntensity(float value) => giOverride.EmitterIntensity = value;
    public void SetGlobalIlluminationOverrideSpecular(bool value) => giOverride.Specular = value;
    public void SetGlobalIlluminationOverrideObscuranceRadius(float value) => giOverride.ObscuranceRadius = value;
    public void SetGlobalIlluminationOverrideFadeDistance(float value) => giOverride.FadeDistance = value;
    public void SetGlobalIlluminationOverrideNormalBias(float value) => giOverride.NormalBias = value;
    public void SetGlobalIlluminationOverrideDistanceBias(float value) => giOverride.DistanceBias = value;
    public void SetGlobalIlluminationOverrideBounceThreshold(float value) => giOverride.BounceThreshold = value;
    public void SetGlobalIlluminationOverrideFireflyClamp(float value) => giOverride.FireflyClamp = value;
    public void SetGlobalIlluminationOverrideReflectionProbes(bool value) => giOverride.ReflectionProbes = value;
    public void SetGlobalIlluminationOverrideMirrors(bool value) => giOverride.Mirrors = value;

    private static string ClampedGiOption(string[] options, int index) => options[Mathf.Clamp(index, 0, options.Length - 1)];
#endif

#if BASIS_HAS_RTAO && !UNITY_ANDROID
    /// <summary>
    /// The per-photo ambient occlusion substitute this camera applies when
    /// <see cref="OverrideRTAO"/> is on. Inert otherwise, and only ever reaches the renderer inside
    /// <see cref="TakeScreenshot"/> - see <see cref="giOverride"/>, which this mirrors. Defaulted to
    /// match the player's own live settings.
    /// </summary>
    private BasisRTAOCaptureOverride rtaoOverride = new BasisRTAOCaptureOverride
    {
        Mode = BasisRTAOIntegration.ModeScreenSpace,
        Intensity = 1f,
        Radius = 0.02f,
        ApplyMode = BasisRTAOIntegration.ApplyLighting,
        DenoisePasses = 2,
        DirectStrength = 0.5f,
        Layers = "Avatars",
        SkinnedMeshes = "Proxy",
        NormalBias = 0.005f,
        DistanceBias = 0.0005f,
        Falloff = 1f,
        Power = 1f,
        FadeStart = 40f,
        FadeEnd = 60f,
        SpecularRelief = 0f,
    };

    /// <summary>Whether <see cref="RTAOOverride"/> substitutes into this camera's own captures. Off by default, so a fresh camera's photos match the player's live settings exactly.</summary>
    public bool OverrideRTAO { get; private set; }

    public BasisRTAOCaptureOverride RTAOOverride => rtaoOverride;

    public void SetOverrideRTAO(bool enabled) => OverrideRTAO = enabled;
    public void SetRTAOOverrideMode(int index) => rtaoOverride.Mode = index == 1 ? BasisRTAOIntegration.ModeRayTraced : BasisRTAOIntegration.ModeScreenSpace;
    public void SetRTAOOverrideIntensity(float value) => rtaoOverride.Intensity = value;
    public void SetRTAOOverrideRadius(float value) => rtaoOverride.Radius = value;
    public void SetRTAOOverrideApplyMode(int index) => rtaoOverride.ApplyMode = index == 1 ? BasisRTAOIntegration.ApplyFinalImage : BasisRTAOIntegration.ApplyLighting;
    public void SetRTAOOverrideDenoisePasses(int passes) => rtaoOverride.DenoisePasses = Mathf.Clamp(passes, 0, 3);
    public void SetRTAOOverrideDirectStrength(float value) => rtaoOverride.DirectStrength = value;
    public void SetRTAOOverrideLayers(int index) => rtaoOverride.Layers = index switch { 1 => "World", 2 => "World And Avatars", _ => "Avatars" };
    public void SetRTAOOverrideSkinnedMeshes(int index) => rtaoOverride.SkinnedMeshes = index == 1 ? "Proxy" : "Off";
    public void SetRTAOOverrideNormalBias(float value) => rtaoOverride.NormalBias = value;
    public void SetRTAOOverrideDistanceBias(float value) => rtaoOverride.DistanceBias = value;
    public void SetRTAOOverrideFalloff(float value) => rtaoOverride.Falloff = value;
    public void SetRTAOOverridePower(float value) => rtaoOverride.Power = value;
    public void SetRTAOOverrideFadeStart(float value) => rtaoOverride.FadeStart = value;
    public void SetRTAOOverrideFadeEnd(float value) => rtaoOverride.FadeEnd = value;
    public void SetRTAOOverrideSpecularRelief(float value) => rtaoOverride.SpecularRelief = value;
#endif

    /// <summary>Boot-mode swap handler.</summary>
    private new void OnBootModeChanged(string obj)
    {
        // A switch to desktop hands the window back; a switch into VR takes it over again if
        // the mode is still on. The setting itself is not touched either way.
        RefreshDirectToScreen();
    }

    /// <summary>Unhooks visibility observer from the preview renderer.</summary>
    private void UnsubscribeMeshRendererCheck()
    {
        if (basisMeshRendererCheck != null)
            basisMeshRendererCheck.Check -= VisibilityFlag;
    }

    /// <summary>Releases the current render texture (if any).</summary>
    private void ReleaseRenderTexture()
    {
        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
            renderTexture = null;
        }
    }

    private async void UnRegisterLoadedNetID(string myLoadedNetId)
    {
        if (string.IsNullOrEmpty(myLoadedNetId))
            return;

        if (BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(myLoadedNetId, out var go) && go)
        {
            bool success = await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(myLoadedNetId);
            if (success)
            {
                BasisDebug.Log($"successfully removed item = {myLoadedNetId} from registry");
            }
            else
            {
                BasisDebug.LogError($"failed to remove item = {myLoadedNetId} from registry");
            }
        }
    }

    /// <summary>
    /// True while something other than the prop's own viewfinder is showing this camera's feed:
    /// the settings panel's preview, the look-at preview a detached camera turned on you puts up,
    /// the desktop output, or a live video stream. Each draws the render texture somewhere the
    /// prop's own visibility says
    /// nothing about, so each has to keep the camera rendering on its own account — otherwise it
    /// freezes on whatever frame the prop was last on screen for.
    /// </summary>
    private bool HasOffPropFeedConsumer =>
        IsAnyVideoOutputActive || IsGifRecording || IsVideoRecording || IsPhotogrammetryActive || panelPreviewActive
        || IsPuckPreviewVisible || IsDirectToScreenPresenting;

    /// <summary>
    /// Told by the settings panel while it is open on this camera. Its preview is a second window
    /// onto the same feed, drawn wherever the menu is rather than on the prop, so the camera has
    /// to keep rendering for it even when the prop itself is nowhere in view.
    /// </summary>
    public void SetPanelPreviewActive(bool active)
    {
        if (panelPreviewActive == active) return;
        panelPreviewActive = active;
        UpdateRenderGate();
    }

    /// <summary>
    /// Decides whether the capture camera renders this frame and how often: off entirely when
    /// nothing is showing the feed, otherwise gated to the developer render-rate override
    /// (0 = uncapped), and never throttled while it is driving the desktop output.
    /// <para>
    /// Re-evaluated every frame from <see cref="SimulateLate"/> rather than only when the prop's
    /// visibility changes. A viewer can arrive or leave while the prop is off screen — opening the
    /// settings panel on a camera you have flown away is the obvious one — and a gate that ran
    /// only on visibility transitions would not hear about it until the prop was next looked at.
    /// </para>
    /// </summary>
    private void UpdateRenderGate()
    {
        if (captureCamera == null) return;

        // Hiding the prop drops its renderer's visibility, which must not stop the camera: a
        // hidden camera is still live and everything downstream of it keeps running.
        bool shouldRender = rendererVisible || cameraHidden || HasOffPropFeedConsumer;
        LastVisibilityState = shouldRender;
        if (!shouldRender)
        {
            captureCamera.enabled = false;
            return;
        }

        float targetHz = BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue;
        bool limitEnabled = BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue;

        // A live video stream is the RT's real consumer: publishing at 30fps off a camera
        // the user limited to 10fps would send the same frame three times. Floor the render
        // rate at the stream rate for as long as the stream is up.
        if (IsAnyVideoOutputActive && VideoOutputSettings.FrameRate > 0f)
        {
            targetHz = Mathf.Max(targetHz, VideoOutputSettings.FrameRate);
        }

        // A recording is a consumer the same way: capturing 15 distinct frames a second needs
        // the camera rendering at least that often.
        if (IsGifRecording && gifRecorder.FrameRate > 0)
        {
            targetHz = Mathf.Max(targetHz, gifRecorder.FrameRate);
        }
        if (IsVideoRecording && videoRecorder.FrameRate > 0)
        {
            targetHz = Mathf.Max(targetHz, videoRecorder.FrameRate);
        }

        // The monitor is a consumer that wants every frame: a picture that stutters where the
        // headset mirror used to be smooth reads as broken, not as saving work. The cap is lifted
        // rather than raised, since a window has no rate of its own but the display's.
        if (IsDirectToScreenPresenting) limitEnabled = false;

        captureCamera.enabled = renderRateLimiter.AllowThisFrame(Time.unscaledDeltaTime, targetHz, limitEnabled);
    }

    /// <summary>
    /// URP callback before each camera render: shows the local head for this camera's renders.
    /// The live preview goes through the normal camera loop (unlike photos, which bracket an
    /// explicit Render call), and it renders before the main camera has set any head state —
    /// so it must not rely on leftovers: a mirror's onBeforeRender pass leaves the head zeroed.
    /// </summary>
    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (ReferenceEquals(renderingCamera, captureCamera))
        {
            BasisLocalAvatarDriver.ScaleHeadToNormal();
        }
    }

    /// <summary>
    /// URP callback after each camera render: the render texture now holds a picture nothing has
    /// published yet. Taken from the pipeline rather than inferred from the render gate, because
    /// the gate only says whether the automatic render was allowed — the transparent output and
    /// the photo path both drive <see cref="Camera.Render"/> themselves, and those frames are just
    /// as fresh.
    /// </summary>
    private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (ReferenceEquals(renderingCamera, captureCamera))
        {
            MarkStreamFrameFresh();
        }
    }

    /// <summary>
    /// Called when the prop's viewfinder mesh enters or leaves every camera's view. It only
    /// records the state: whether that stops the capture camera is <see cref="UpdateRenderGate"/>'s
    /// call, since the viewfinder is not the only surface that can be showing the feed.
    /// </summary>
    private void VisibilityFlag(bool isVisible)
    {
        if (BasisLocalPlayer.Instance == null)
            return;

        rendererVisible = isVisible;
        UpdateRenderGate();
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>
    /// Test-only stand-in for the prop's viewfinder entering or leaving view, which otherwise
    /// only arrives from Unity's culling through <see cref="BasisMeshRendererCheck"/>.
    /// </summary>
    public void SetRendererVisibleForTest(bool visible)
    {
        rendererVisible = visible;
        UpdateRenderGate();
    }
#endif
}
