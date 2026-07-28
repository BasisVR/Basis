using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Basis.Scripts.Audio;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public partial class BasisHandHeldCamera
{
    [Header("360 Capture")]
    /// <summary>When true, the shutter (Take Photo / Timer / trigger) captures a 360 instead of a flat photo.</summary>
    public bool capture360Enabled = false;

    /// <summary>When true, captures over/under stereo (ODS); otherwise a single monoscopic equirect.</summary>
    public bool capture360Stereo = false;

    /// <summary>Cube face size used for the intermediate render. 0 derives it from the capture resolution.</summary>
    public int pano360CubeFaceSize = 0;

    /// <summary>Eye separation (metres) used for stereo ODS capture.</summary>
    public float pano360StereoSeparation = 0.064f;

    private const int Pano360MaxFaceSize = 4096;

    /// <summary>Immediate 360 capture using the current format choice (EXR/PNG).</summary>
    public void Capture360Photo()
    {
        bool exr = captureFormat == "EXR";

        if (BasisDeviceManagement.Instance.CameraShutterSound != null)
        {
            BasisUISounds.PlayAt(BasisUISoundEvent.CameraShutter, BasisDeviceManagement.Instance.CameraShutterSound, captureCamera.transform.position, SMModuleAudio.ActivePropVolume);
        }

        if (BasisNetworkConnection.LocalPlayerPeer != null)
        {
            BasisNetworkPIPCameraDriver.SendShutterSound();
        }

        StartCoroutine(TakeScreenshot360(exr));
    }

    private IEnumerator TakeScreenshot360(bool exr)
    {
        int perEyeWidth = Mathf.Max(16, captureWidth);
        int perEyeHeight = perEyeWidth / 2;
        bool stereo = capture360Stereo;
        int fullHeight = stereo ? perEyeWidth : perEyeHeight;

        int faceSize = pano360CubeFaceSize > 0
            ? pano360CubeFaceSize
            : Mathf.NextPowerOfTwo(Mathf.Max(16, perEyeWidth / 2));
        faceSize = Mathf.Min(faceSize, Pano360MaxFaceSize);

        yield return new WaitForEndOfFrame();

        BasisLocalAvatarDriver.ScaleHeadToNormal();

        float headingDegrees = captureCamera.transform.eulerAngles.y;

        BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata =
            BasisHandHeldCameraPhotoMetadata.CollectMetadata(captureCamera, transform, true);

        float bakeExposure = 1f, bakeContrast = 1f, bakeSaturation = 1f;
        if (MetaData.colorAdjustments != null)
        {
            bakeExposure = Mathf.Pow(2f, MetaData.colorAdjustments.postExposure.value);
            bakeContrast = 1f + MetaData.colorAdjustments.contrast.value / 100f;
            bakeSaturation = 1f + MetaData.colorAdjustments.saturation.value / 100f;
        }

        bool savedPhysical = captureCamera.usePhysicalProperties;
        RenderTexture savedTarget = captureCamera.targetTexture;
        float savedSeparation = captureCamera.stereoSeparation;
        bool savedEnabled = captureCamera.enabled;

        bool savedBloom = MetaData.bloom != null && MetaData.bloom.active;
        bool savedDof = MetaData.depthOfField != null && MetaData.depthOfField.active;
        bool savedColor = MetaData.colorAdjustments != null && MetaData.colorAdjustments.active;
        TonemappingMode savedTone = MetaData.tonemapping != null ? MetaData.tonemapping.mode.value : TonemappingMode.None;

        if (MetaData.bloom != null) MetaData.bloom.active = false;
        if (MetaData.depthOfField != null) MetaData.depthOfField.active = false;
        if (MetaData.colorAdjustments != null) MetaData.colorAdjustments.active = false;
        if (MetaData.tonemapping != null) MetaData.tonemapping.mode.value = TonemappingMode.None;

        captureCamera.usePhysicalProperties = false;
        captureCamera.targetTexture = null;
        captureCamera.stereoSeparation = pano360StereoSeparation;
        captureCamera.enabled = true;

        RenderTexture equirect = NewEquirectRT(perEyeWidth, fullHeight);
        RenderTexture cubeLeft = NewCubeRT(faceSize);
        RenderTexture cubeRight = stereo ? NewCubeRT(faceSize) : null;

        bool rendered;
        if (stereo)
        {
            rendered = captureCamera.RenderToCubemap(cubeLeft, 63, Camera.MonoOrStereoscopicEye.Left)
                && captureCamera.RenderToCubemap(cubeRight, 63, Camera.MonoOrStereoscopicEye.Right);
            if (rendered)
            {
                cubeLeft.ConvertToEquirect(equirect, Camera.MonoOrStereoscopicEye.Left);
                cubeRight.ConvertToEquirect(equirect, Camera.MonoOrStereoscopicEye.Right);
            }
        }
        else
        {
            rendered = captureCamera.RenderToCubemap(cubeLeft, 63, Camera.MonoOrStereoscopicEye.Mono);
            if (rendered)
                cubeLeft.ConvertToEquirect(equirect, Camera.MonoOrStereoscopicEye.Mono);
        }

        captureCamera.usePhysicalProperties = savedPhysical;
        captureCamera.targetTexture = savedTarget;
        captureCamera.stereoSeparation = savedSeparation;
        captureCamera.enabled = savedEnabled;

        if (MetaData.bloom != null) MetaData.bloom.active = savedBloom;
        if (MetaData.depthOfField != null) MetaData.depthOfField.active = savedDof;
        if (MetaData.colorAdjustments != null) MetaData.colorAdjustments.active = savedColor;
        if (MetaData.tonemapping != null) MetaData.tonemapping.mode.value = savedTone;

        ReleaseRT(cubeLeft);
        ReleaseRT(cubeRight);

        if (!rendered)
        {
            BasisDebug.LogError("[HandHeldCamera] RenderToCubemap failed; 360 capture aborted.");
            ReleaseRT(equirect);
            BasisLocalAvatarDriver.ScaleHeadToZero();
            yield break;
        }

        RenderTexture readbackRT = equirect;

        AsyncGPUReadback.Request(readbackRT, 0, request =>
        {
            int width = readbackRT.width;
            int height = readbackRT.height;

            if (request.hasError)
            {
                BasisDebug.LogError("360 GPU Readback failed.");
                ReleaseRT(readbackRT);
                BasisLocalAvatarDriver.ScaleHeadToZero();
                return;
            }

            byte[] raw = request.GetData<byte>().ToArray();
            ReleaseRT(readbackRT);
            BasisLocalAvatarDriver.ScaleHeadToZero();

            bool anyNonZero = false;
            for (int i = 0; i < raw.Length; i += 3988)
            {
                if (raw[i] != 0) { anyNonZero = true; break; }
            }
            if (!anyNonZero)
                BasisDebug.LogError($"[360] Rendered image is fully black — RenderToCubemap drew nothing (stereo={stereo}, {width}x{height}). URP likely isn't drawing the scene via RenderToCubemap in this context.");
            else
                BasisDebug.Log($"[360] Captured {width}x{height} (stereo={stereo}).");

            Process360AndSave(raw, width, height, exr, photoMetadata, perEyeWidth, fullHeight, stereo, headingDegrees, bakeExposure, bakeContrast, bakeSaturation);
        });
    }

    private async void Process360AndSave(byte[] raw, int width, int height, bool exr, BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata, int perEyeWidth, int fullHeight, bool stereo, float headingDegrees, float exposure, float contrast, float saturation)
    {
        byte[] imageData;

        if (exr)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
            tex.LoadRawTextureData(raw);
            tex.Apply(false);
            imageData = tex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);
            Destroy(tex);
        }
        else
        {
            byte[] rgba = await Task.Run(() => TonemapEquirectToRgba32(raw, width, height, exposure, contrast, saturation));
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(rgba);
            tex.Apply(false);
            imageData = tex.EncodeToPNG();
            Destroy(tex);
        }

        if (photoMetadata != null)
        {
            photoMetadata.HasPano = true;
            photoMetadata.PanoStereoTopBottom = stereo;
            photoMetadata.PanoFullWidth = perEyeWidth;
            photoMetadata.PanoFullHeight = fullHeight;
            photoMetadata.PanoPerEyeHeight = perEyeWidth / 2;
            photoMetadata.PanoHeadingDegrees = headingDegrees;

            imageData = BasisHandHeldCameraPhotoMetadata.Embed(imageData, exr ? "EXR" : "PNG", photoMetadata, width, height);
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string extension = exr ? "exr" : "png";
        string layout = stereo ? "Stereo" : "Mono";
        string filename = $"Screenshot360_{layout}_{timestamp}_{width}x{height}.{extension}";
        string path = GetSavePath(filename);

        await File.WriteAllBytesAsync(path, imageData);
    }

    private static byte[] TonemapEquirectToRgba32(byte[] linearFloatRgba, int width, int height, float exposure, float contrast, float saturation)
    {
        int pixelCount = width * height;
        byte[] output = new byte[pixelCount * 4];

        for (int p = 0; p < pixelCount; p++)
        {
            int fi = p * 16;
            float r = BitConverter.ToSingle(linearFloatRgba, fi);
            float g = BitConverter.ToSingle(linearFloatRgba, fi + 4);
            float b = BitConverter.ToSingle(linearFloatRgba, fi + 8);

            r *= exposure;
            g *= exposure;
            b *= exposure;

            AcesFitted(ref r, ref g, ref b);

            r = (r - 0.5f) * contrast + 0.5f;
            g = (g - 0.5f) * contrast + 0.5f;
            b = (b - 0.5f) * contrast + 0.5f;

            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = luma + (r - luma) * saturation;
            g = luma + (g - luma) * saturation;
            b = luma + (b - luma) * saturation;

            int oi = p * 4;
            output[oi] = ToSrgbByte(r);
            output[oi + 1] = ToSrgbByte(g);
            output[oi + 2] = ToSrgbByte(b);
            output[oi + 3] = 255;
        }

        return output;
    }

    private static void AcesFitted(ref float r, ref float g, ref float b)
    {
        float ir = 0.59719f * r + 0.35458f * g + 0.04823f * b;
        float ig = 0.07600f * r + 0.90834f * g + 0.01566f * b;
        float ib = 0.02840f * r + 0.13383f * g + 0.83777f * b;

        ir = RRTAndODTFit(ir);
        ig = RRTAndODTFit(ig);
        ib = RRTAndODTFit(ib);

        r = Mathf.Clamp01(1.60475f * ir - 0.53108f * ig - 0.07367f * ib);
        g = Mathf.Clamp01(-0.10208f * ir + 1.10813f * ig - 0.00605f * ib);
        b = Mathf.Clamp01(-0.00327f * ir - 0.07276f * ig + 1.07602f * ib);
    }

    private static float RRTAndODTFit(float v)
    {
        float a = v * (v + 0.0245786f) - 0.000090537f;
        float b = v * (0.983729f * v + 0.432951f) + 0.238081f;
        return a / b;
    }

    private static byte ToSrgbByte(float linear)
    {
        if (linear <= 0f) return 0;
        if (linear >= 1f) return 255;
        float srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * Mathf.Pow(linear, 1f / 2.4f) - 0.055f;
        int value = (int)(srgb * 255f + 0.5f);
        return (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));
    }

    private RenderTexture NewEquirectRT(int width, int height)
    {
        var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
        {
            msaaSamples = 1,
            useMipMap = false,
            autoGenerateMips = false,
            sRGB = false
        };
        var rt = new RenderTexture(descriptor);
        rt.Create();
        return rt;
    }

    private RenderTexture NewCubeRT(int faceSize)
    {
        var rt = new RenderTexture(faceSize, faceSize, depth, RenderTextureFormat.ARGBFloat)
        {
            dimension = TextureDimension.Cube,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        return rt;
    }

    private void ReleaseRT(RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        Destroy(rt);
    }
}
