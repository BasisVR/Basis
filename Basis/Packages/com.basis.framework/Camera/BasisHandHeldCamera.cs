using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering;
using System.Collections;
using TMPro;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.BasisSdk.Helpers;

public class BasisHandHeldCamera : BasisHandHeldCameraInteractable
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
    [SerializeField] private BasisHandHeldCameraUI cameraUIReference;

    [Header("Settings")]
    [Tooltip("Width of the captured photo")]
    public int captureWidth = 3840;
    [Tooltip("Height of the captured photo")]
    public int captureHeight = 2160;
    [Tooltip("Preview resolution width")]
    public int PreviewCaptureWidth = 1920;
    [Tooltip("Preview resolution height")]
    public int PreviewCaptureHeight = 1080;
    [Tooltip("Capture format (EXR/PNG)")]
    public string captureFormat = "EXR";
    [Tooltip("Depth buffer bits for render texture")]
    public int depth = 24;
    [Tooltip("Instance ID for multi-camera setups")]
    public int InstanceID;

    [Header("Advanced/Debug")]
    public bool enableRecordingView = false;
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();

    private Material actualMaterial;
    private RenderTexture renderTexture;
    private RenderTexture lastAssignedRenderTexture = null;
    private Material lastAssignedMaterial = null;
    private Texture2D pooledScreenshot;
    private float previewUpdateInterval = 1f / 30f; // Target 30 FPS
    private Coroutine previewRoutine;
    private int uiLayerMask;
    private static Material clearMaterial;
    private const string CLEAR_SHADER_PATH = "Unlit/Color";
    private string picturesFolder;
    private bool showUI = false;
    public bool LastVisibilityState = false;
    private BasisMeshRendererCheck basisMeshRendererCheck;
    /// <summary>
    /// Performs camera, UI, folder, and material initialization.
    /// </summary>
    public new async void Awake()
    {
        InitializeCameraSettings();
        InitializeMaterial();
        InitializeMeshRendererCheck();
        await InitializeUI();
        InitializeTonemapping();
        InitializeDepthOfField();
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
    /// <summary>
    /// Releases resources and unsubscribes from events.
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
    private void OnEnable()
    {
        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low);
        BasisDebug.Log($"[HandHeldCamera] Preview reset to {PreviewCaptureWidth}x{PreviewCaptureHeight} @ {AntialiasingQuality.Low}");
        captureCamera.targetTexture = renderTexture;
        StartPreviewLoop();
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
    }

    private void InitializeMaterial()
    {
        actualMaterial = Instantiate(Material);
    }

    private void InitializeMeshRendererCheck()
    {
        basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        basisMeshRendererCheck.Check += VisibilityFlag;
    }

    private async System.Threading.Tasks.Task InitializeUI()
    {
        basisMeshRendererCheck = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Renderer.gameObject);
        basisMeshRendererCheck.Check += VisibilityFlag;
        await HandHeld.Initialize(this);
        interactable.SetCameraUI(HandHeld);
    }

    private void InitializeTonemapping()
    {
        if (MetaData.Profile.TryGet(out MetaData.tonemapping))
        {
            ToggleToneMapping(TonemappingMode.Neutral);
        }
    }
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

    private void InitializeFolders()
    {
        picturesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Basis");
        if (!Directory.Exists(picturesFolder))
        {
            Directory.CreateDirectory(picturesFolder);
        }
    }

    private void SetupUILayerMask()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            BasisDebug.LogWarning("UI Layer not found.");
        }
        else
        {
            uiLayerMask = 1 << uiLayer;
        }
    }

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
    public new void Start()
    {
        base.Start();

        OnPickupUse += OnPickupUseCapture;
    }

    public void OnPickupUseCapture(PickUpUseMode mode)
    {
        if (mode == PickUpUseMode.OnPickUpUseDown)
        {
            CapturePhoto();
        }
    }
    /// <summary>
    /// Changes the render resolution and anti-aliasing settings.
    /// </summary>
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
    /// Coroutine to take a screenshot using the current settings.
    /// </summary>
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

    private void EnsureTexturePool(int width, int height, TextureFormat format)
    {
        if (pooledScreenshot == null || pooledScreenshot.width != width || pooledScreenshot.height != height || pooledScreenshot.format != format)
        {
            pooledScreenshot = new Texture2D(width, height, format, false);
        }
    }

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

    private void StopPreviewLoop()
    {
        if (previewRoutine != null)
        {
            StopCoroutine(previewRoutine);
            previewRoutine = null;
        }
    }

    // TODO parent class destroys self on boot mode swap
    // private void OnBootModeChanged(string obj)
    // {
    //     OverrideDesktopOutput();
    // }
    public void Timer()
    {
        StartCoroutine(DelayedAction(5)); // Countdown from 5 seconds
    }

    private IEnumerator DelayedAction(float delaySeconds)
    {
        for (int i = (int)delaySeconds; i > 0; i--)
        {
            countdownText.text = i.ToString();
            yield return new WaitForSeconds(1f);
        }
        // Flash "!" before capture
        countdownText.text = "!";
        yield return new WaitForSeconds(0.5f);

        // Prepare format
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

    public void Nameplates()
    {
        if (uiLayerMask == 0)
        {
            BasisDebug.LogWarning("UI Layer Mask was not initialized properly.");
            return;
        }

        showUI = !showUI;

        if (showUI)
        {
            captureCamera.cullingMask |= uiLayerMask;
        }
        else
        {
            captureCamera.cullingMask &= ~uiLayerMask;
        }
    }

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

    public void OnOverrideDesktopOutputButtonPress()
    {
        enableRecordingView = !enableRecordingView;
        OverrideDesktopOutput();
    }

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

    public string GetSavePath(string filename)
    {
#if UNITY_STANDALONE_WIN
        return Path.Combine(picturesFolder, filename);
#else
        return Path.Combine(Application.persistentDataPath, filename);
#endif
    }

    public void ChangeResolution(int index)
    {
        if (index >= 0 && index < MetaData.resolutions.Length)
        {
            (captureWidth, captureHeight) = MetaData.resolutions[index];
        }
    }

    public void ChangeFormat(int index)
    {
        captureFormat = MetaData.formats[index];
        BasisDebug.Log($"Capture format changed to {captureFormat}");
    }

    public void SetNormalAfterCapture()
    {
        ToggleToneMapping(TonemappingMode.Neutral);
        BasisLocalAvatarDriver.ScaleheadToZero();
        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low);
    }

    public void ToggleToneMapping(TonemappingMode mappingMode)
    {
        MetaData.tonemapping.mode.value = mappingMode;
    }

    private new void OnBootModeChanged(string obj)
    {
        OverrideDesktopOutput();

       // base.OnBootModeChanged(obj);
    }

    private void UnsubscribeMeshRendererCheck()
    {
        if (basisMeshRendererCheck != null)
            basisMeshRendererCheck.Check -= VisibilityFlag;
    }

    private void ReleaseRenderTexture()
    {
        if (renderTexture != null)
            renderTexture.Release();
    }

    private void VisibilityFlag(bool isVisible)
    {
        if (!isVisible)
        {
            if (LastVisibilityState && BasisLocalPlayer.Instance != null)
            {
                captureCamera.enabled = false;
                LastVisibilityState = false;
                BasisLocalPlayer.Instance.LocalAvatarDriver.RemoveActiveMatrixOverride(InstanceID);
            }
        }
        else
        {
            if (!LastVisibilityState && BasisLocalPlayer.Instance != null)
            {
                captureCamera.enabled = true;
                LastVisibilityState = true;
                BasisLocalPlayer.Instance.LocalAvatarDriver.TryActiveMatrixOverride(InstanceID);
            }
        }
    }
}
