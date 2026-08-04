using Basis;
using Basis.BasisUI;
using Basis.Scripts.Audio;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>If true and not on desktop, camera renders to display instead of RT.</summary>
    public bool enableRecordingView = false;

    /// <summary>Static metadata/presets and PP component references.</summary>
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();

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

    /// <summary>
    /// Whether the UI (nameplate) layer is in the capture. Derived from the capture camera's own
    /// culling mask so it is the single source of truth — the Render Layers "Nameplates" toggle
    /// and this can never disagree.
    /// </summary>
    public bool ShowUIInCapture => captureCamera != null && uiLayerMask != 0 && (WorldCullingMask & uiLayerMask) != 0;

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

        SubscribePreviewScreen();

        // Ordered render phase instead of Unity's LateUpdate, so this always runs after the camera
        // has been moved for the frame rather than racing it.
        BasisLocalPlayer.AfterSimulateOnRender.AddAction(SimulateLatePriority, SimulateLate);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        BasisLocalCameraDriver.RenderSettingsApplied += SyncBackgroundFromMainCamera;

        if (BasisLocalCameraDriver.HasInstance)
        {
            BasisLocalCameraDriver.Instance.ExitThirdPerson();
        }

        // Notify network that PIP camera was created
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            captureCamera.transform.GetPositionAndRotation(out Vector3 pipPos, out Quaternion pipRot);
            BasisNetworkPIPCameraDriver.SendPIPState(true, pipPos, pipRot);
        }
    }
    public void InitializeVolumetrics()
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (MetaData.Profile.TryGet(out MetaData.VolumetricFogVolume))
        {

        }
#endif
    }
    /// <summary>
    /// Stops preview, saves settings, releases resources, unsubscribes events,
    /// and returns this object to the Addressables pool.
    /// </summary>
    public new async void OnDestroy()
    {
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

        UnsubscribePreviewScreen();
        DespawnPreviewScreen();

        string myLoadedNetId = gameObject.name;
        UnRegisterLoadedNetID(myLoadedNetId);

        // Neither of these unwinds itself. The web stream owns a socket and a thread that
        // Unity knows nothing about, so they would outlive the camera and hold the port for
        // the rest of the session; the Spout path leaks its claimed sender name, which makes
        // the next camera come up as "Basis Camera 2".
        StopWebStream();
        StopVideoOutput();
        SetAudioListener(false);
        DespawnFollowPip();
        DestroyDetachedGizmo();
        DespawnDirectToScreenOverlay();

        DebugGizmos.Shutdown();

        UnsubscribeMeshRendererCheck();
        BasisCullingCameraRegistry.Unregister(captureCamera);
        BasisMirrorViewerRegistry.Unregister(captureCamera);
        ReleaseRenderTexture();
        if (pooledScreenshot != null) { Destroy(pooledScreenshot); pooledScreenshot = null; }
        ReleaseSrgbResolveTarget();
        if (actualMaterial != null) { Destroy(actualMaterial); actualMaterial = null; }

        if (HandHeld != null)
        {
            HandHeld.ReleaseUILock(); // we should release locks if for whatever reason we get destroyed
        
        }
        

        BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(SimulateLatePriority, SimulateLate);

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
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
        // Grabbed once at init, before the preview screen can exist, so hiding the prop can
        // never reach the separately-rooted screen the user may have placed in the world.
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
        SetAutoFollowEnabled(false);
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
    /// OverlayUI carries the camera's own world markers — the detached preview screen, the
    /// follow-PIP puck and the dolly waypoints — which would leak the rig into every shot.
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
            ToggleToneMapping(TonemappingMode.Neutral);
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

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // explorer wants backslashes and does not accept a file:// URI.
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folder.Replace('/', '\\')}\"");
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            System.Diagnostics.Process.Start("open", $"\"{folder}\"");
#else
            System.Diagnostics.Process.Start("xdg-open", $"\"{folder}\"");
#endif
            return true;
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
    /// </summary>
    public bool CanAutoFocusOnFollowSubject => IsAutoFollowing || IsFollowingRemotePlayer;

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

        if (actualMaterial != lastAssignedMaterial || renderTexture != lastAssignedRenderTexture || textureChanged)
        {
            actualMaterial.SetTexture("_MainTex", renderTexture);
            actualMaterial.mainTexture = renderTexture;
            Renderer.sharedMaterial = actualMaterial;
            lastAssignedMaterial = actualMaterial;
            lastAssignedRenderTexture = renderTexture;
            ApplyViewfinderCrop();
        }
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

        if (IsOverridingDesktopView)
        {
            // A window that reports nothing to fill — minimised — leaves the feed where it is.
            // Falling back to the preview size would rebuild the RT twice per restore.
            if (TryGetDirectToScreenFeedSize(out int screenWidth, out int screenHeight))
            {
                SetResolution(screenWidth, screenHeight, AntialiasingQuality.Low, PreviewRenderTextureFormat);
            }
            return;
        }

        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low, PreviewRenderTextureFormat);
    }

    /// <summary>
    /// Keeps the prop's viewfinder undistorted. The quad is a fixed shape, so a feed that is not
    /// the capture aspect — which is what Direct To Screen produces, since the feed then follows
    /// the screen — gets squashed onto it. Showing the middle of the feed instead keeps faces the
    /// right width; the full frame is still there on the floating preview screen and the menu
    /// panel, both of which size themselves to the feed. Identity whenever the feed and the
    /// capture aspect agree, which is every case except that mode.
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

        BasisLocalAvatarDriver.ScaleHeadToNormal();
        ToggleToneMapping(TonemappingMode.ACES);

        captureCamera.Render();

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

            SetNormalAfterCapture();
            SaveScreenshotAsync(pooledScreenshot, photoMetadata);
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

        // Choose formats based on captureFormat
        GetCaptureFormats(out TextureFormat format, out RenderTextureFormat renderFormat);

        // Play shutter sound locally (network was already notified via SendCountdown)
        if (BasisDeviceManagement.Instance.CameraShutterSound != null)
        {
            BasisUISounds.PlayAt(BasisUISoundEvent.CameraShutter, BasisDeviceManagement.Instance.CameraShutterSound, captureCamera.transform.position, SMModuleAudio.ActivePropVolume);
        }

        countdownRoutine = null;

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
    bool IsOverridingDesktopView = false;

    /// <summary>
    /// True while the camera's feed is also presented on the main screen (Direct To Screen). The
    /// camera still renders into its own RT — the mode only adds a fullscreen overlay showing it —
    /// so post-processing, MSAA and colour are the same as when it is off.
    /// </summary>
    public bool IsDirectToScreen => IsOverridingDesktopView;
    private BasisRenderRateLimiter renderRateLimiter;

    /// <summary>Render-phase priority: after the camera has been moved (202).</summary>
    private const int SimulateLatePriority = 204;

    /// <summary>
    /// Per-frame camera upkeep, run from <see cref="BasisLocalPlayer.AfterSimulateOnRender"/> rather
    /// than a Unity LateUpdate.
    /// <para>
    /// Everything here reads the capture camera's pose — the preview screen, the detached marker,
    /// and the networked PIP position. The camera is moved by UpdateCamera at priority 202 in the
    /// same render phase, so a plain LateUpdate raced it: with no script execution order set, this
    /// could run either side of the move and would intermittently publish and place things from the
    /// previous frame's pose. That inconsistency read as jitter.
    /// </para>
    /// </summary>
    private void SimulateLate()
    {
        UpdateRenderGate();

        if (IsOverridingDesktopView)
        {
            UpdateDirectToScreenTexture();
        }

        UpdatePreviewScreenTexture();
        TickVideoOutput();
        UpdateOnPropUIVisibility();
        UpdateAutoFocus();
        UpdateFollowPip();
        DebugGizmos.Tick(this);

        // Send PIP camera position to network
        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
            BasisNetworkPIPCameraDriver.SendPIPPosition(pos, rot);
        }
    }
    /// <summary>
    /// When enabled and not on desktop, renders to the main display instead of the RT
    /// (and fills the RT with black). Otherwise restores RT output.
    /// </summary>
    public void OverrideDesktopOutput()
    {
        IsOverridingDesktopView = enableRecordingView && !BasisDeviceManagement.IsUserInDesktop();

        // ONE render path. The camera always renders into its own RT, so post-processing, MSAA and
        // colour are identical whether or not Direct To Screen is on; the mode only changes where
        // that RT is presented. Re-targeting the camera at the backbuffer (what this used to do)
        // was the root cause of PP dropping out, MSAA falling back and the mismatched look.
        captureCamera.depth = -1;
        captureCamera.targetDisplay = 0;
        captureCamera.targetTexture = renderTexture;
        actualMaterial.mainTexture = renderTexture;
        actualMaterial.SetTexture("_MainTex", renderTexture);

        SetDirectToScreenOverlayActive(IsOverridingDesktopView);

        UpdateRenderGate();
        UpdatePreviewScreen();
    }

    /// <summary>UI callback to toggle recording view and apply <see cref="OverrideDesktopOutput"/>.</summary>
    public void OnOverrideDesktopOutputButtonPress()
    {
        enableRecordingView = !enableRecordingView;
        OverrideDesktopOutput();
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
        string filename = $"Screenshot_{timestamp}_{captureWidth}x{captureHeight}.{extension}";
        string path = GetSavePath(filename);

        byte[] imageData = captureFormat == "EXR"
            ? screenshot.EncodeToEXR(Texture2D.EXRFlags.CompressZIP)
            : screenshot.EncodeToPNG();

        if (photoMetadata != null)
            imageData = BasisHandHeldCameraPhotoMetadata.Embed(imageData, captureFormat, photoMetadata, screenshot.width, screenshot.height);

        await File.WriteAllBytesAsync(path, imageData);
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
    /// Restores tonemapping, hides local head mesh, and returns preview RT settings after capture.
    /// </summary>
    public void SetNormalAfterCapture()
    {
        captureInFlight = false;
        ToggleToneMapping(TonemappingMode.Neutral);
        BasisLocalAvatarDriver.ScaleHeadToZero();
        ApplyPreviewResolution();
    }

    /// <summary>Sets the URP tonemapping mode on the active profile.</summary>
    public void ToggleToneMapping(TonemappingMode mappingMode)
    {
        MetaData.tonemapping.mode.value = mappingMode;
    }

    /// <summary>Boot-mode swap handler (keeps overrides in sync).</summary>
    private new void OnBootModeChanged(string obj)
    {
        OverrideDesktopOutput();
        // base.OnBootModeChanged(obj);
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
    /// the settings panel's preview, the detached preview screen, the desktop output, or a live
    /// video stream. Each draws the render texture somewhere the prop's own visibility says
    /// nothing about, so each has to keep the camera rendering on its own account — otherwise it
    /// freezes on whatever frame the prop was last on screen for.
    /// </summary>
    private bool HasOffPropFeedConsumer =>
        IsOverridingDesktopView || IsAnyVideoOutputActive || panelPreviewActive || IsPreviewScreenVisible;

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

        if (IsOverridingDesktopView)
        {
            captureCamera.enabled = true;
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
