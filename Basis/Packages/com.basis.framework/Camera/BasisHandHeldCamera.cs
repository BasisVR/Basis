using System.IO;
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine.Rendering;
using TMPro;
using UnityEngine.XR;
using Basis.Scripts.Device_Management;
public class BasisHandHeldCamera : BasisHandHeldCameraInteractable
{
    public UniversalAdditionalCameraData CameraData;
    public Camera captureCamera;
    public RenderTexture renderTexture;
    public TextMeshProUGUI countdownText;
    public int captureWidth = 3840;
    public int captureHeight = 2160;
    [Space(10)]
    public int PreviewCaptureWidth = 1920;
    public int PreviewCaptureHeight = 1080;
    [Space(10)]
    private bool showUI = false;
    [Space(10)]
    public BasisMeshRendererCheck BasisMeshRendererCheck;
    public MeshRenderer Renderer;
    public Material Material;
    public Material actualMaterial;
    public string captureFormat = "EXR";
    public string picturesFolder;
    public int InstanceID;
    public int depth = 24;
    [Space(10)]
    public bool enableRecordingView;
    [Space(10)]
    private int uiLayerMask;
    private static Material clearMaterial;
    private const string CLEAR_SHADER_PATH = "Unlit/Color";
    private const float RaycastMaxDistance = 1000f;
    private Texture2D pooledScreenshot;
    [Space(10)]
    [SerializeField]
    public BasisHandHeldCameraUI HandHeld = new BasisHandHeldCameraUI();
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();
    [Space(10)]
    private float previewUpdateInterval = 1f / 30f; // Target 30 FPS
    private Coroutine previewRoutine;

    public new async void Awake()
    {
        captureCamera.forceIntoRenderTexture = true;
        captureCamera.allowHDR = true;
        captureCamera.allowMSAA = true;
        captureCamera.useOcclusionCulling = true;
        captureCamera.usePhysicalProperties = true;
        captureCamera.targetTexture = renderTexture;
        captureCamera.targetDisplay = 1;
        actualMaterial = Instantiate(Material);
        if (BasisLocalCameraDriver.HasInstance)
        {
            Camera PlayerCamera = BasisLocalCameraDriver.Instance.Camera;
            if (PlayerCamera != null)
            {
                captureCamera.backgroundColor = PlayerCamera.backgroundColor;
                captureCamera.clearFlags = PlayerCamera.clearFlags;
            }
        }

        await HandHeld.Initialize(this);

        if (MetaData.Profile.TryGet(out MetaData.tonemapping))
        {
            ToggleToneMapping(TonemappingMode.Neutral);
        }

        SetResolution(captureWidth, captureHeight, AntialiasingQuality.Low);
        CameraData.allowHDROutput = true;
        CameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        CameraData.antialiasingQuality = AntialiasingQuality.High;

        picturesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Basis");
        if (!Directory.Exists(picturesFolder))
        {
            Directory.CreateDirectory(picturesFolder);
        }

        await HandHeld.SaveSettings();
        base.Awake();
        captureCamera.gameObject.SetActive(true);
        StartPreviewLoop();
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;

        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            BasisDebug.LogWarning("UI Layer not found.");
        }
        else
        {
            uiLayerMask = 1 << uiLayer;
        }

        if (clearMaterial == null)
        {
            Shader shader = Shader.Find(CLEAR_SHADER_PATH);
            if (shader != null)
            {
                clearMaterial = new Material(shader);
            }
        }
    }

    public void SetResolution(int width, int height, AntialiasingQuality AQ, RenderTextureFormat RenderTextureFormat = RenderTextureFormat.ARGBFloat)
    {
        if (renderTexture == null || renderTexture.width != width || renderTexture.height != height || renderTexture.format != RenderTextureFormat)
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
            }

            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat, depth)
            {
                msaaSamples = 2,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = true
            };
            renderTexture = new RenderTexture(descriptor);
            renderTexture.Create();
        }

        captureCamera.targetTexture = renderTexture;
        CameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        CameraData.antialiasingQuality = AQ;
        actualMaterial.SetTexture("_MainTex", renderTexture);
        actualMaterial.mainTexture = renderTexture;
        Renderer.sharedMaterial = actualMaterial;
    }
    private void EnsureTexturePool(int width, int height, TextureFormat format)
    {
        if (pooledScreenshot == null || pooledScreenshot.width != width || pooledScreenshot.height != height || pooledScreenshot.format != format)
        {
            pooledScreenshot = new Texture2D(width, height, format, false);
        }
    }

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

    private void OnBootModeChanged(string obj)
    {
        OverrideDesktopOutput();
    }

    public new async void OnDestroy()
    {
        StopPreviewLoop();
        if (BasisMeshRendererCheck != null)
        {
            BasisMeshRendererCheck.Check -= VisibilityFlag;
        }
        if (renderTexture != null)
        {
            renderTexture.Release();
        }
        if (HandHeld != null)
        {
            await HandHeld.SaveSettings();
        }
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        base.OnDestroy();
    }
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
        TextureFormat Format;
        RenderTextureFormat RenderFormat;

        if (captureFormat == "EXR")
        {
            Format = TextureFormat.RGBAFloat;
            RenderFormat = RenderTextureFormat.ARGBFloat;
        }
        else
        {
            Format = TextureFormat.RGBA32;
            RenderFormat = RenderTextureFormat.ARGB32;
        }

        StartCoroutine(TakeScreenshot(Format, RenderFormat));

        // Reset the countdown text back to "5" after triggering
        countdownText.text = ((int)delaySeconds).ToString();
    }
    public void OverrideDesktopOutput()
    {
        if (enableRecordingView && BasisDeviceManagement.IsUserInDesktop() == false)
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
            captureCamera.cullingMask |= uiLayerMask; // Enable UI layer
        }
        else
        {
            captureCamera.cullingMask &= ~uiLayerMask; // Disable UI layer
        }
    }
    public void SetFocusFromRay(Ray ray)
    {
        if (captureCamera == null || MetaData.depthOfField == null)
        {
            BasisDebug.LogWarning("Cannot set DOF: Camera or DOF is missing.");
            return;
        }

        if (Physics.Raycast(ray, out RaycastHit hit, RaycastMaxDistance))
        {
            if (hit.collider != null && hit.collider.transform.IsChildOf(transform))
            {
                BasisDebug.Log("[DOF] Raycast hit self — skipping.");
                return;
            }

            float distance = Vector3.Distance(ray.origin, hit.point);
            MetaData.depthOfField.focusDistance.value = distance;

            if (HandHeld != null)
            {
                HandHeld.DepthFocusDistanceSlider.SetValueWithoutNotify(distance);
                HandHeld.DOFFocusOutput.text = distance.ToString("F2");
            }

            BasisDebug.Log($"[DOF] Focus distance set to {distance:F2} units (hit {hit.collider.name})");
        }
        else
        {
            BasisDebug.Log("[DOF] Raycast did not hit anything.");
        }
    }

    public void CapturePhoto()
    {
        TextureFormat Format;
        RenderTextureFormat RenderFormat;
        if (captureFormat == "EXR")
        {
            Format = TextureFormat.RGBAFloat;
            RenderFormat = RenderTextureFormat.ARGBFloat;
        }
        else
        {
            Format = TextureFormat.RGBA32;
            RenderFormat = RenderTextureFormat.ARGB32;
        }

        StartCoroutine(TakeScreenshot(Format, RenderFormat));
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
    public async void SaveScreenshotAsync(Texture2D screenshot)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string extension = captureFormat == "EXR" ? "exr" : "png";
        string filename = $"Screenshot_{timestamp}_{captureWidth}x{captureHeight}.{extension}";
        string path = GetSavePath(filename);


        // Encode the screenshot (biggest performance cost)
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

    public void ToggleToneMapping(TonemappingMode MappingMode)
    {
        MetaData.tonemapping.mode.value = MappingMode;
    }
    public bool LastVisibilityState = false;
    private void VisibilityFlag(bool IsVisible)
    {
        if (IsVisible)
        {
            if (LastVisibilityState != IsVisible)
            {
                if (BasisLocalPlayer.Instance != null)
                {
                    captureCamera.enabled = true;
                    LastVisibilityState = true;
                    BasisLocalPlayer.Instance.LocalAvatarDriver.TryActiveMatrixOverride(InstanceID);
                }
            }
        }
        else
        {
            if (LastVisibilityState != IsVisible)
            {
                if (BasisLocalPlayer.Instance != null)
                {
                    captureCamera.enabled = false;
                    LastVisibilityState = false;
                    BasisLocalPlayer.Instance.LocalAvatarDriver.RemoveActiveMatrixOverride(InstanceID);
                }
            }
        }
    }
}