using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Rendering;
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
public partial class BasisHandHeldCamera
{
    private Texture2D pooledScreenshot;
    private RenderTexture srgbResolveTexture;
    private Coroutine countdownRoutine;
    private Vector2Int lastResizeNoticeFor;
    public int CountdownRemaining { get; private set; }
    public bool IsCountingDown => CountdownRemaining > 0;
    public string LastPhotoPath { get; private set; }
    public string LastPhotoFileName => LastPhotoPath == null ? null : Path.GetFileName(LastPhotoPath);
    public string LastPhotoFailure { get; private set; }
    public bool RevealLastPhoto() => BasisCameraPhotoFolder.Reveal(LastPhotoPath);
    public void CapturePhoto()
    {
        if (BasisCameraShutter.CaptureBlocked("CapturePhoto") || !TryTakeFrame()) return;

        BasisCameraShutter.Announce(captureCamera);
        StartCapture();
    }
    public void Timer()
    {
        if (IsCountingDown)
        {
            CancelTimer();
            return;
        }

        if (BasisCameraShutter.CaptureBlocked("Timer")) return;

        if (BasisNetworkConnection.LocalPlayerPeer != null) BasisNetworkPIPCameraDriver.SendCountdown(5);
        countdownRoutine = StartCoroutine(DelayedAction(5));
    }
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
    public IEnumerator TakeScreenshot(TextureFormat textureFormat, RenderTextureFormat renderFormat = RenderTextureFormat.ARGBFloat)
    {
        captureInFlight = true;
        SetResolution(captureWidth, captureHeight, AntialiasingQuality.High, renderFormat);
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
        bool resolved = BasisCameraCaptureFormats.NeedsSrgbResolve(textureFormat, renderTexture);
        if (resolved)
        {
            BasisCameraRenderTargets.Release(ref srgbResolveTexture);
            srgbResolveTexture = BasisCameraCaptureFormats.ResolveToSrgb(renderTexture);
        }
        RenderTexture readbackSource = resolved ? srgbResolveTexture : renderTexture;

        BasisCameraRenderTargets.EnsureReadback(ref pooledScreenshot, readbackSource.width, readbackSource.height, textureFormat);

        AsyncGPUReadback.Request(readbackSource, 0, request =>
        {
            if (resolved) BasisCameraRenderTargets.Release(ref srgbResolveTexture);

            if (request.hasError)
            {
                BasisDebug.LogError("GPU Readback failed.");
                SetNormalAfterCapture();
                return;
            }

            pooledScreenshot.LoadRawTextureData(request.GetData<byte>());
            pooledScreenshot.Apply(false);

            Texture2D finished = FinishPicture(pooledScreenshot);

            SetNormalAfterCapture();
            SaveScreenshotAsync(finished, photoMetadata);
        });
    }
    public async void SaveScreenshotAsync(Texture2D screenshot, BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata)
    {
        int savedWidth = screenshot != null ? screenshot.width : captureWidth, savedHeight = screenshot != null ? screenshot.height : captureHeight;
        string path = BasisCameraPhotoFolder.PathFor($"Screenshot_{BasisCameraPhotoFolder.Timestamp()}_{savedWidth}x{savedHeight}.{BasisCameraCaptureFormats.Extension(captureFormat)}");
        BasisCameraPrintResize.PrintCopy printCopy = default;

        try
        {
            int width = screenshot.width, height = screenshot.height;
            var pixelFormat = screenshot.graphicsFormat;
            bool exr = BasisCameraCaptureFormats.IsExr(captureFormat), printable = printPhotoEnabled && !exr && screenshot.format == TextureFormat.RGBA32;
            string format = captureFormat;
            Unity.Collections.NativeArray<byte> raw = screenshot.GetRawTextureData<byte>();
            byte[] pixels = new byte[raw.Length];
            Unity.Collections.NativeArray<byte>.Copy(raw, pixels, raw.Length);

            (byte[] imageData, BasisCameraPrintResize.PrintCopy print) = await Task.Run(() =>
            {
                byte[] encoded = exr ? ImageConversion.EncodeArrayToEXR(pixels, pixelFormat, (uint)width, (uint)height, 0, Texture2D.EXRFlags.CompressZIP) : ImageConversion.EncodeArrayToPNG(pixels, pixelFormat, (uint)width, (uint)height, 0);
                if (photoMetadata != null) encoded = BasisHandHeldCameraPhotoMetadata.Embed(encoded, format, photoMetadata, width, height);
                return (encoded, printable ? BasisCameraPrintPhoto.BuildCopy(pixels, width, height, encoded.LongLength) : default);
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
    public void ChangeResolution(int index)
    {
        if (index < 0 || index >= MetaData.resolutions.Length) return;
        (captureWidth, captureHeight) = MetaData.resolutions[index];
        ApplyViewfinderCrop();
    }
    public void SetNormalAfterCapture()
    {
        captureInFlight = false;
        ToggleToneMapping(PreviewTonemapping);
        ApplyPreviewResolution();
    }
    private IEnumerator DelayedAction(float delaySeconds)
    {
        for (int i = (int)delaySeconds; i > 0; i--)
        {
            CountdownRemaining = i;
            countdownText.text = i.ToString();
            BasisCameraShutter.PlayCountdownTick(captureCamera);
            yield return new WaitForSeconds(1f);
        }

        CountdownRemaining = 0;
        countdownText.text = "!";
        yield return new WaitForSeconds(0.5f);

        countdownRoutine = null;

        if (BasisCameraShutter.CaptureBlocked("Timer capture") || !TryTakeFrame())
        {
            countdownText.text = string.Empty;
            yield break;
        }

        BasisCameraShutter.PlayShutter(captureCamera);
        StartCapture();
        countdownText.text = ((int)delaySeconds).ToString();
    }
    private void StartCapture()
    {
        if (capture360Enabled)
        {
            StartCoroutine(TakeScreenshot360(BasisCameraCaptureFormats.IsExr(captureFormat)));
            return;
        }

        BasisCameraCaptureFormats.Get(captureFormat, out TextureFormat textureFormat, out RenderTextureFormat renderFormat);
        StartCoroutine(TakeScreenshot(textureFormat, renderFormat));
    }
    private void PrintPhotoIfEnabled(string path, BasisCameraPrintResize.PrintCopy printCopy)
    {
        if (printPhotoEnabled) BasisCameraPrintPhoto.Spawn(path, printCopy, ref lastResizeNoticeFor);
    }
    private void RecordPhotoSaved(string path)
    {
        LastPhotoPath = path;
        LastPhotoFailure = null;
    }
    private void RecordPhotoFailed(Exception e)
    {
        LastPhotoFailure = $"{e.GetType().Name}: {e.Message}";
        BasisDebug.LogError($"Could not save photo: {LastPhotoFailure}", BasisDebug.LogTag.Camera);
    }
}
