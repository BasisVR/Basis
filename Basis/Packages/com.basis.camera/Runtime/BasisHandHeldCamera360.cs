using Basis.Scripts.Drivers;
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
public partial class BasisHandHeldCamera
{
    [Header("360 Capture")]
    public bool capture360Enabled = false;
    public bool capture360Stereo = false;
    public int pano360CubeFaceSize = 0;
    public float pano360StereoSeparation = 0.064f;
    private IEnumerator TakeScreenshot360(bool exr)
    {
        int perEyeWidth = Mathf.Max(16, captureWidth);
        bool stereo = capture360Stereo;
        int fullHeight = stereo ? perEyeWidth : perEyeWidth / 2, faceSize = BasisCamera360.FaceSize(pano360CubeFaceSize, perEyeWidth);

        yield return new WaitForEndOfFrame();

        bool headWasNormal = BasisLocalAvatarDriver.IsNormalHead;
        BasisLocalAvatarDriver.ScaleHeadToNormal();

        float headingDegrees = captureCamera.transform.eulerAngles.y;
        BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata = BasisHandHeldCameraPhotoMetadata.CollectMetadata(captureCamera, transform, true);
        BasisCamera360.Bake(MetaData, out float bakeExposure, out float bakeContrast, out float bakeSaturation);

        bool savedPhysical = captureCamera.usePhysicalProperties, savedEnabled = captureCamera.enabled;
        RenderTexture savedTarget = captureCamera.targetTexture;
        float savedSeparation = captureCamera.stereoSeparation;
        BasisCamera360.EffectState savedEffects = BasisCamera360.SuspendEffects(MetaData);

        captureCamera.usePhysicalProperties = false;
        captureCamera.targetTexture = null;
        captureCamera.stereoSeparation = pano360StereoSeparation;
        captureCamera.enabled = true;

        RenderTexture equirect = BasisCameraRenderTargets.Create(perEyeWidth, fullHeight, RenderTextureFormat.ARGBFloat, 0, 1, false);
        RenderTexture cubeLeft = BasisCameraRenderTargets.CreateCube(faceSize, depth), cubeRight = stereo ? BasisCameraRenderTargets.CreateCube(faceSize, depth) : null;
#if BASIS_HAS_GI && !UNITY_ANDROID
        SMModuleGlobalIlluminationURP.SuspendCamera(captureCamera, true);
#endif
        bool rendered;
        try
        {
            if (stereo)
            {
                rendered = captureCamera.RenderToCubemap(cubeLeft, 63, Camera.MonoOrStereoscopicEye.Left) && captureCamera.RenderToCubemap(cubeRight, 63, Camera.MonoOrStereoscopicEye.Right);
                if (rendered)
                {
                    cubeLeft.ConvertToEquirect(equirect, Camera.MonoOrStereoscopicEye.Left);
                    cubeRight.ConvertToEquirect(equirect, Camera.MonoOrStereoscopicEye.Right);
                }
            }
            else
            {
                rendered = captureCamera.RenderToCubemap(cubeLeft, 63, Camera.MonoOrStereoscopicEye.Mono);
                if (rendered) cubeLeft.ConvertToEquirect(equirect, Camera.MonoOrStereoscopicEye.Mono);
            }
        }
        finally
        {
#if BASIS_HAS_GI && !UNITY_ANDROID
            SMModuleGlobalIlluminationURP.SuspendCamera(captureCamera, false);
#endif
            if (!headWasNormal) BasisLocalAvatarDriver.ScaleHeadToZero();
        }

        captureCamera.usePhysicalProperties = savedPhysical;
        captureCamera.targetTexture = savedTarget;
        captureCamera.stereoSeparation = savedSeparation;
        captureCamera.enabled = savedEnabled;
        BasisCamera360.RestoreEffects(MetaData, savedEffects);

        BasisCameraRenderTargets.Release(ref cubeLeft);
        BasisCameraRenderTargets.Release(ref cubeRight);

        if (!rendered)
        {
            BasisDebug.LogError("[HandHeldCamera] RenderToCubemap failed; 360 capture aborted.");
            BasisCameraRenderTargets.Release(ref equirect);
            yield break;
        }

        AsyncGPUReadback.Request(equirect, 0, request =>
        {
            int width = equirect.width, height = equirect.height;

            if (request.hasError)
            {
                BasisDebug.LogError("360 GPU Readback failed.");
                BasisCameraRenderTargets.Release(ref equirect);
                return;
            }

            byte[] raw = request.GetData<byte>().ToArray();
            BasisCameraRenderTargets.Release(ref equirect);

            if (BasisCamera360.IsBlack(raw)) BasisDebug.LogError($"[360] Rendered image is fully black — RenderToCubemap drew nothing (stereo={stereo}, {width}x{height}). URP likely isn't drawing the scene via RenderToCubemap in this context.");
            else BasisDebug.Log($"[360] Captured {width}x{height} (stereo={stereo}).");

            BasisCamera360.TagPano(photoMetadata, stereo, perEyeWidth, fullHeight, headingDegrees);
            Process360AndSave(raw, width, height, exr, photoMetadata, stereo, bakeExposure, bakeContrast, bakeSaturation);
        });
    }
    private async void Process360AndSave(byte[] raw, int width, int height, bool exr, BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata, bool stereo, float exposure, float contrast, float saturation)
    {
        byte[] imageData;
        BasisCameraPrintResize.PrintCopy printCopy = default;
        bool printable = printPhotoEnabled && !exr;

        if (exr)
        {
            imageData = await Task.Run(() =>
            {
                byte[] encoded = ImageConversion.EncodeArrayToEXR(raw, GraphicsFormat.R32G32B32A32_SFloat, (uint)width, (uint)height, 0, Texture2D.EXRFlags.CompressZIP);
                return photoMetadata != null ? BasisHandHeldCameraPhotoMetadata.Embed(encoded, "EXR", photoMetadata, width, height) : encoded;
            });
        }
        else
        {
            (imageData, printCopy) = await Task.Run(() =>
            {
                byte[] rgba = BasisCamera360.Tonemap(raw, width, height, exposure, contrast, saturation);
                byte[] encoded = ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0);
                if (photoMetadata != null) encoded = BasisHandHeldCameraPhotoMetadata.Embed(encoded, "PNG", photoMetadata, width, height);
                return (encoded, printable ? BasisCameraPrintPhoto.BuildCopy(rgba, width, height, encoded.LongLength) : default);
            });
        }

        string path = BasisCameraPhotoFolder.PathFor($"Screenshot360_{(stereo ? "Stereo" : "Mono")}_{BasisCameraPhotoFolder.Timestamp()}_{width}x{height}.{(exr ? "exr" : "png")}");

        try
        {
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
}
