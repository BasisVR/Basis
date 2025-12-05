using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Handheld capture camera with preview, screenshotting (PNG/EXR),
/// post-processing integration (Tonemapping/DoF/Bloom/Color), and UI plumbing.
/// Extends <see cref="BasisHandHeldCameraInteractable"/> for pin/fly modes.
/// </summary>
public class BasisHandHeldCamera : BasisHandHeldCameraInteractable
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

    /// <summary>All handheld camera UI widgets and persistence (sliders/toggles/etc.).</summary>
    [SerializeField] public BasisHandHeldCameraUI HandHeld = new BasisHandHeldCameraUI();

    /// <summary>Handler to click-to-focus Depth of Field in the preview.</summary>
    [SerializeField] public BasisDepthOfFieldInteractionHandler BasisDOFInteractionHandler;

    /// <summary>Back-reference to the interactable (for UI hand-off).</summary>
    [SerializeField] private BasisHandHeldCameraInteractable interactable;

    [Header("Settings")]
    /// <summary>Output capture width (photo resolution).</summary>
    [Tooltip("Width of the captured photo")]
    public int captureWidth = 3840;

    /// <summary>Output capture height (photo resolution).</summary>
    [Tooltip("Height of the captured photo")]
    public int captureHeight = 2160;

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

    /// <summary>Instance identifier for multi-camera setups.</summary>
    [Tooltip("Instance ID for multi-camera setups")]
    public int InstanceID;

    [Header("Advanced/Debug")]
    /// <summary>If true and not on desktop, camera renders to display instead of RT.</summary>
    public bool enableRecordingView = false;

    /// <summary>Static metadata/presets and PP component references.</summary>
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();

    // --- private state ---

    /// <summary>Instantiated material assigned to the preview renderer.</summary>
    private Material actualMaterial;

    /// <summary>Current preview/capture render texture.</summary>
    private RenderTexture renderTexture;

    /// <summary>Last RT bound to material (to avoid redundant sets).</summary>
    private RenderTexture lastAssignedRenderTexture = null;

    /// <summary>Last material assigned to the renderer (to avoid redundant sets).</summary>
    private Material lastAssignedMaterial = null;

    /// <summary>Pooled CPU-side texture for async GPU readbacks.</summary>
    private Texture2D pooledScreenshot;

    /// <summary>Target preview frame interval (dynamic, approx. 30 FPS by default).</summary>
    private float previewUpdateInterval = 1f / 30f;

    /// <summary>Running preview coroutine handle.</summary>
    private Coroutine previewRoutine;

    /// <summary>Bitmask for the UI layer toggle in <see cref="Nameplates"/>.</summary>
    private int uiLayerMask;

    /// <summary>Shared “clear to color” material (Unlit/Color).</summary>
    private static Material clearMaterial;

    /// <summary>Shader path used to initialize <see cref="clearMaterial"/>.</summary>
    private const string CLEAR_SHADER_PATH = "Unlit/Color";

    /// <summary>Folder where screenshots are written (platform-dependent).</summary>
    private string picturesFolder;

    /// <summary>Whether the UI/nameplates are currently visible in the capture.</summary>
    private bool showUI = false;

    /// <summary>Last visibility state reported by the mesh renderer check.</summary>
    public bool LastVisibilityState = false;

    /// <summary>Renderer visibility observer.</summary>
    private BasisMeshRendererCheck basisMeshRendererCheck;

    /// <summary>
    /// Performs camera/PP/UI/material initialization, creates folders, saves initial settings,
    /// and starts the preview loop. Also hooks boot-mode changes.
    /// </summary>
    public new async void Awake()
    {
        InitializeCameraSettings();
        InitializeMaterial();
        InitializeMeshRendererCheck();
        await InitializeUI();
        InitializeTonemapping();
        InitializeDepthOfField();
        InitalizeVolumetrics();
        InitializeFolders();
        await HandHeld.SaveSettings();
        SetupUILayerMask();
        SetupClearMaterial();

        base.Awake();

        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low);
        captureCamera.targetTexture = renderTexture;
        captureCamera.gameObject.SetActive(true);

        StartPreviewLoop();
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
    }
    public void InitalizeVolumetrics()
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
        StopPreviewLoop();
        UnsubscribeMeshRendererCheck();
        ReleaseRenderTexture();

        if (HandHeld != null)
        {
            await HandHeld.SaveSettings();
        }

        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        OnPickupUse -= OnPickupUseCapture;

        base.OnDestroy();
    }

    /// <summary>
    /// Ensures preview RT is set when re-enabled and (re)starts the preview loop.
    /// </summary>
    private void OnEnable()
    {
        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low);
        BasisDebug.Log($"[HandHeldCamera] Preview reset to {PreviewCaptureWidth}x{PreviewCaptureHeight} @ {AntialiasingQuality.Low}");
        captureCamera.targetTexture = renderTexture;
        StartPreviewLoop();
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
    }

    /// <summary>Instantiates a unique material used for the preview mesh.</summary>
    private void InitializeMaterial()
    {
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
        picturesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Basis");
        if (!Directory.Exists(picturesFolder))
        {
            Directory.CreateDirectory(picturesFolder);
        }
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
        OnPickupUse += OnPickupUseCapture;
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
    public void SetResolution(int width, int height, AntialiasingQuality AQ, RenderTextureFormat RenderTextureFormat = RenderTextureFormat.ARGBFloat)
    {
        bool textureChanged = false;

        if (renderTexture == null || renderTexture.width != width || renderTexture.height != height || renderTexture.format != RenderTextureFormat)
        {
            if (renderTexture != null)
                renderTexture.Release();

            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat, depth)
            {
                msaaSamples = 2,
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
        SetResolution(captureWidth, captureHeight, AntialiasingQuality.High, Format);
        yield return new WaitForEndOfFrame();

        BasisLocalAvatarDriver.ScaleHeadToNormal();
        ToggleToneMapping(TonemappingMode.ACES);

        captureCamera.Render();

        EnsureTexturePool(renderTexture.width, renderTexture.height, TextureFormat);

        AsyncGPUReadback.Request(renderTexture, 0, request =>
        {
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
            SaveScreenshotAsync(pooledScreenshot);
        });
    }

    /// <summary>Ensures <see cref="pooledScreenshot"/> matches the required size/format.</summary>
    private void EnsureTexturePool(int width, int height, TextureFormat format)
    {
        if (pooledScreenshot == null || pooledScreenshot.width != width || pooledScreenshot.height != height || pooledScreenshot.format != format)
        {
            pooledScreenshot = new Texture2D(width, height, format, false);
        }
    }

    /// <summary>Lightweight preview loop that re-renders at a dynamic interval.</summary>
    private IEnumerator PreviewRenderLoop()
    {
        while (true)
        {
            if (captureCamera != null && captureCamera.targetTexture != null && captureCamera.enabled)
            {
                captureCamera.Render();
            }
            yield return new WaitForSecondsRealtime(previewUpdateInterval);
        }
    }

    /// <summary>Starts (or restarts) the preview loop at a fps based on current frame time.</summary>
    private void StartPreviewLoop()
    {
        float currentFPS = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        float halvedFPS = currentFPS * 0.5f;
        float roundedFPS = Mathf.Clamp(Mathf.Round(halvedFPS / 5f) * 5f, 5f, 60f);

        previewUpdateInterval = 1f / roundedFPS;
        BasisDebug.Log($"Camera Preview FPS: {roundedFPS}");

        if (previewRoutine == null)
        {
            previewRoutine = StartCoroutine(PreviewRenderLoop());
        }
    }

    /// <summary>Stops the preview coroutine if it’s running.</summary>
    private void StopPreviewLoop()
    {
        if (previewRoutine != null)
        {
            StopCoroutine(previewRoutine);
            previewRoutine = null;
        }
    }

    /// <summary>Starts a 5-second countdown and triggers a capture at the end.</summary>
    public void Timer()
    {
        StartCoroutine(DelayedAction(5));
    }

    /// <summary>Countdown coroutine that flashes “!” and then takes a screenshot.</summary>
    private IEnumerator DelayedAction(float delaySeconds)
    {
        for (int i = (int)delaySeconds; i > 0; i--)
        {
            countdownText.text = i.ToString();
            yield return new WaitForSeconds(1f);
        }

        countdownText.text = "!";
        yield return new WaitForSeconds(0.5f);

        // Choose formats based on captureFormat
        TextureFormat format;
        RenderTextureFormat renderFormat;
        if (captureFormat == "EXR")
        {
            format = TextureFormat.RGBAFloat;
            renderFormat = RenderTextureFormat.ARGBFloat;
        }
        else
        {
            format = TextureFormat.RGBA32;
            renderFormat = RenderTextureFormat.ARGB32;
        }

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

        showUI = !showUI;

        if (showUI)
            captureCamera.cullingMask |= uiLayerMask;
        else
            captureCamera.cullingMask &= ~uiLayerMask;
    }

    /// <summary>Immediate photo capture using the current format choice (EXR/PNG).</summary>
    public void CapturePhoto()
    {
        TextureFormat format;
        RenderTextureFormat renderFormat;

        if (captureFormat == "EXR")
        {
            format = TextureFormat.RGBAFloat;
            renderFormat = RenderTextureFormat.ARGBFloat;
        }
        else
        {
            format = TextureFormat.RGBA32;
            renderFormat = RenderTextureFormat.ARGB32;
        }

        StartCoroutine(TakeScreenshot(format, renderFormat));
    }

    /// <summary>
    /// When enabled and not on desktop, renders to the main display instead of the RT
    /// (and fills the RT with black). Otherwise restores RT output.
    /// </summary>
    public void OverrideDesktopOutput()
    {
        if (enableRecordingView && !BasisDeviceManagement.IsUserInDesktop())
        {
            captureCamera.targetTexture = null;
            captureCamera.depth = 1;
            captureCamera.targetDisplay = 0;
            FillRenderTextureWithColor(renderTexture, Color.black);
        }
        else
        {
            captureCamera.depth = -1;
            captureCamera.targetDisplay = 1;
            captureCamera.targetTexture = renderTexture;
        }
    }

    /// <summary>UI callback to toggle recording view and apply <see cref="OverrideDesktopOutput"/>.</summary>
    public void OnOverrideDesktopOutputButtonPress()
    {
        enableRecordingView = !enableRecordingView;
        OverrideDesktopOutput();
    }

    /// <summary>Clears a render texture to a solid color using an Unlit/Color blit.</summary>
    private void FillRenderTextureWithColor(RenderTexture rt, Color color)
    {
        if (clearMaterial == null)
        {
            BasisDebug.LogWarning("Clear material not initialized");
            return;
        }
        clearMaterial.color = color;
        Graphics.Blit(null, rt, clearMaterial);
    }

    /// <summary>
    /// Encodes and writes the screenshot to disk asynchronously using the selected format.
    /// </summary>
    /// <param name="screenshot">CPU-side texture to encode.</param>
    public async void SaveScreenshotAsync(Texture2D screenshot)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string extension = captureFormat == "EXR" ? "exr" : "png";
        string filename = $"Screenshot_{timestamp}_{captureWidth}x{captureHeight}.{extension}";
        string path = GetSavePath(filename);

        byte[] imageData = captureFormat == "EXR"
            ? screenshot.EncodeToEXR(Texture2D.EXRFlags.CompressZIP)
            : screenshot.EncodeToPNG();

        await File.WriteAllBytesAsync(path, imageData);
    }

    /// <summary>Builds a platform-appropriate save path for a screenshot filename.</summary>
    public string GetSavePath(string filename)
    {
#if UNITY_STANDALONE_WIN
        return Path.Combine(picturesFolder, filename);
#else
        return Path.Combine(Application.persistentDataPath, filename);
#endif
    }

    /// <summary>Applies one of the preset resolutions from <see cref="MetaData.resolutions"/>.</summary>
    public void ChangeResolution(int index)
    {
        if (index >= 0 && index < MetaData.resolutions.Length)
        {
            (captureWidth, captureHeight) = MetaData.resolutions[index];
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
        ToggleToneMapping(TonemappingMode.Neutral);
        BasisLocalAvatarDriver.ScaleheadToZero();
        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low);
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
            renderTexture.Release();
    }

    /// <summary>
    /// Called when the preview renderer enters/exits visibility; toggles camera.enabled accordingly.
    /// </summary>
    private void VisibilityFlag(bool isVisible)
    {
        if (!isVisible)
        {
            if (LastVisibilityState && BasisLocalPlayer.Instance != null)
            {
                captureCamera.enabled = false;
                LastVisibilityState = false;
            }
        }
        else
        {
            if (!LastVisibilityState && BasisLocalPlayer.Instance != null)
            {
                captureCamera.enabled = true;
                LastVisibilityState = true;
            }
        }
    }
}
