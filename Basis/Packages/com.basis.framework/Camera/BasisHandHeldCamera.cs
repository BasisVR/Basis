using System.IO;
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine.Rendering;
using TMPro;
public class BasisHandHeldCamera : BasisHandHeldCameraInteractable
{
    public UniversalAdditionalCameraData CameraData;
    public Camera captureCamera;
    public RenderTexture renderTexture;
    public TextMeshProUGUI countdownText;
    public int captureWidth = 3840;
    public int captureHeight = 2160;

    public int PreviewCaptureWidth = 1920;
    public int PreviewCaptureHeight = 1080;

    private bool showUI = false;

    public BasisMeshRendererCheck BasisMeshRendererCheck;
    public MeshRenderer Renderer;
    public Material Material;
    public Material actualMaterial;
    public string captureFormat = "EXR";
    public string picturesFolder;
    public int InstanceID;
    public int depth = 24;

    [SerializeField]
    public BasisHandHeldCameraUI HandHeld = new BasisHandHeldCameraUI();
    public BasisHandHeldCameraMetaData MetaData = new BasisHandHeldCameraMetaData();
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
        await HandHeld.Initalize(this);
        if (MetaData.Profile.TryGet(out MetaData.tonemapping))
        {
            ToggleToneMapping(TonemappingMode.Neutral);
        }
        SetResolution(PreviewCaptureWidth, PreviewCaptureHeight, AntialiasingQuality.Low);
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
    }
    public new void OnDestroy()
    {
        if (BasisMeshRendererCheck != null)
        {
            BasisMeshRendererCheck.Check -= VisibilityFlag;
        }
        if (renderTexture != null)
        {
            renderTexture.Release();
        }
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
    public void Nameplates()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            Debug.LogWarning("UI Layer not found.");
            return;
        }

        int uiLayerMask = 1 << uiLayer;

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

    public void SetResolution(int width, int height, AntialiasingQuality AQ, RenderTextureFormat RenderTextureFormat = RenderTextureFormat.ARGBFloat)
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
        captureCamera.targetTexture = renderTexture;
        CameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        CameraData.antialiasingQuality = AQ;
        actualMaterial.SetTexture("_MainTex", renderTexture);
        actualMaterial.mainTexture = renderTexture;
        Renderer.sharedMaterial = actualMaterial;
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

    public IEnumerator TakeScreenshot(TextureFormat TextureFormat, RenderTextureFormat Format = RenderTextureFormat.ARGBFloat)
    {
        SetResolution(captureWidth, captureHeight, AntialiasingQuality.High, Format);
        yield return new WaitForEndOfFrame();
        BasisLocalAvatarDriver.ScaleHeadToNormal();
        ToggleToneMapping(TonemappingMode.ACES);
        captureCamera.Render();
        yield return new WaitForEndOfFrame();

        Texture2D screenshot = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat, false);
        AsyncGPUReadback.Request(renderTexture, 0, request =>
        {
            if (request.hasError)
            {
                BasisDebug.LogError("GPU Readback failed.");
                SetNormalAfterCapture();
                return;
            }

            Unity.Collections.NativeArray<byte> data = request.GetData<byte>();
            screenshot.LoadRawTextureData(data);
            screenshot.Apply(false);

            SetNormalAfterCapture();
            SaveScreenshotAsync(screenshot);
        });
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
