using System;
using System.Collections.Generic;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    public const int FullRoll = -1;
    private readonly List<RectInt> stampGlyphs = new List<RectInt>();
    private Light flashLight;
    private Texture2D pooledPrint;
    private byte[] printBorderRow;
    private float flashHoldRemaining;
    private bool frameCountShowing;
    public BasisCameraBodyKind Body { get; private set; } = BasisCameraBodyKind.Digital;
    public int ExposuresRemaining { get; private set; }
    public bool FlashEnabled { get; private set; }
    public float WindOnRemaining { get; private set; }
    public float DevelopRemaining { get; private set; }
    public float FlashRecycleRemaining { get; private set; }
    public BasisCameraBodyTraits BodyTraits => BasisCameraBodies.Get(Body);
    public bool FlashReady => BodyTraits.HasFlash && FlashEnabled && FlashRecycleRemaining <= 0f;
    public bool BodyAllowsLiveFeed => BodyTraits.LivePreview;
    public void ReloadFilm()
    {
        BasisCameraBodyTraits traits = BodyTraits;
        if (!traits.HasFilm) return;

        ExposuresRemaining = traits.Exposures;
        WindOnRemaining = 0f;
        DevelopRemaining = 0f;

        ClearFrameCount();
    }
    public void SetFlashEnabled(bool enabled)
    {
        if (BodyTraits.HasFlash && FlashEnabled != enabled) FlashEnabled = enabled;
    }
    public BasisCameraShutterState EvaluateShutter()
    {
        if (DevelopRemaining > 0f) return BasisCameraShutterState.Developing;
        if (WindOnRemaining > 0f) return BasisCameraShutterState.WindingOn;
        return BasisCameraShutterState.Ready;
    }
    internal void SetBody(BasisCameraBodyKind kind, bool freshLoad)
    {
        BasisCameraBodyTraits previous = BodyTraits;
        bool moved = Body != kind;
        Body = kind;

        BasisCameraBodyTraits traits = BasisCameraBodies.Get(kind);

        if (moved || freshLoad)
        {
            ExposuresRemaining = traits.Exposures;
            FlashEnabled = traits.HasFlash;
            WindOnRemaining = 0f;
            DevelopRemaining = 0f;
            FlashRecycleRemaining = 0f;
        }
        else
        {
            ExposuresRemaining = traits.HasFilm ? Mathf.Clamp(ExposuresRemaining, 0, traits.Exposures) : 0;
        }

        if (!traits.HasFlash) ReleaseFlash();
        ClearFrameCount();

        ApplyBodyCaptureSize(previous, traits);
        ApplyBodyLiveFeed(previous, traits);
    }
    internal void RestoreBody(int kind, int exposuresRemaining, bool flashEnabled)
    {
        SetBody(BasisCameraBodies.Sanitize(kind), freshLoad: true);

        BasisCameraBodyTraits traits = BodyTraits;
        if (traits.HasFilm && exposuresRemaining != FullRoll) ExposuresRemaining = Mathf.Clamp(exposuresRemaining, 0, traits.Exposures);

        FlashEnabled = traits.HasFlash && flashEnabled;
    }
    private bool TryTakeFrame()
    {
        BasisCameraBodyTraits loaded = BodyTraits;
        if (loaded.HasFilm && ExposuresRemaining <= 0) ExposuresRemaining = loaded.Exposures;

        BasisCameraShutterState state = EvaluateShutter();
        if (state != BasisCameraShutterState.Ready)
        {
            BasisDebug.Log($"Shutter refused: {state}.", BasisDebug.LogTag.Camera);
            return false;
        }

        BasisCameraBodyTraits traits = BodyTraits;
        if (traits.HasFilm) ExposuresRemaining--;

        WindOnRemaining = traits.WindOnSeconds;
        DevelopRemaining = traits.DevelopSeconds;

        FireFlash(traits);
        ShowFrameCount(traits);
        return true;
    }
    private void TickBody()
    {
        float delta = Time.deltaTime;

        if (WindOnRemaining > 0f) WindOnRemaining = Mathf.Max(0f, WindOnRemaining - delta);
        if (DevelopRemaining > 0f) DevelopRemaining = Mathf.Max(0f, DevelopRemaining - delta);
        if (FlashRecycleRemaining > 0f) FlashRecycleRemaining = Mathf.Max(0f, FlashRecycleRemaining - delta);

        if (flashHoldRemaining > 0f)
        {
            flashHoldRemaining -= delta;
            if (flashHoldRemaining <= 0f && flashLight != null) flashLight.enabled = false;
        }

        if (frameCountShowing && WindOnRemaining <= 0f && DevelopRemaining <= 0f) ClearFrameCount();
    }
    private void ShowFrameCount(BasisCameraBodyTraits traits)
    {
        if (!traits.HasFilm || countdownText == null || countdownRoutine != null) return;

        countdownText.text = ExposuresRemaining.ToString();
        frameCountShowing = true;
    }
    private void ClearFrameCount()
    {
        if (!frameCountShowing) return;
        frameCountShowing = false;

        if (countdownText != null && countdownRoutine == null) countdownText.text = string.Empty;
    }
    private void FireFlash(BasisCameraBodyTraits traits)
    {
        if (!traits.HasFlash || !FlashEnabled || FlashRecycleRemaining > 0f) return;

        FlashRecycleRemaining = traits.FlashRecycleSeconds;

        if (!EnsureFlash(traits)) return;

        flashLight.enabled = true;
        flashHoldRemaining = traits.FlashSeconds;
    }
    private bool EnsureFlash(BasisCameraBodyTraits traits)
    {
        if (captureCamera == null) return false;

        if (flashLight == null)
        {
            GameObject go = new GameObject("BasisCameraFlash");
            go.transform.SetParent(captureCamera.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            flashLight = go.AddComponent<Light>();
            flashLight.type = LightType.Spot;
            flashLight.shadows = LightShadows.None;
            flashLight.enabled = false;
#if Basis_VOLUMETRIC_SUPPORTED
            go.AddComponent<VolumetricFogLight>().Multiplier = 0f;
#endif
        }

        flashLight.intensity = traits.FlashIntensity;
        flashLight.range = traits.FlashRange;
        flashLight.spotAngle = traits.FlashAngle;
        flashLight.color = traits.FlashColour;
        return true;
    }
    private void ReleaseFlash()
    {
        flashHoldRemaining = 0f;
        if (flashLight == null) return;

        Destroy(flashLight.gameObject);
        flashLight = null;
    }
    private void ApplyBodyCaptureSize(BasisCameraBodyTraits previous, BasisCameraBodyTraits traits)
    {
        if (captureCamera != null && traits.SensorSize.x > 0f && traits.SensorSize.y > 0f)
        {
            float fieldOfView = captureCamera.fieldOfView;
            captureCamera.sensorSize = traits.SensorSize;
            captureCamera.gateFit = Camera.GateFitMode.Vertical;
            captureCamera.fieldOfView = fieldOfView;
        }

        if (traits.CaptureSize.x > 0 && traits.CaptureSize.y > 0)
        {
            captureWidth = traits.CaptureSize.x;
            captureHeight = traits.CaptureSize.y;
            ApplyViewfinderCrop();
            return;
        }

        if (previous.CaptureSize.x > 0) HandHeld?.RestoreCaptureResolution();
    }
    private void ApplyBodyLiveFeed(BasisCameraBodyTraits previous, BasisCameraBodyTraits traits)
    {
        if (previous.LivePreview == traits.LivePreview) return;
        if (!traits.LivePreview) StopVideoOutput();
        RefreshDirectToScreen();
    }
    private Texture2D FinishPicture(Texture2D picture)
    {
        if (picture == null || picture.format != TextureFormat.RGBA32) return picture;

        BurnLightLeak(picture);
        BurnStamp(picture);
        return MountPrint(picture);
    }
    private void BurnLightLeak(Texture2D picture)
    {
        BasisCameraBodyTraits traits = BodyTraits;
        if (!traits.LeaksLight || !traits.HasFilm || !BasisCameraPrintFinish.ShouldLeak(ExposuresRemaining, traits.Exposures)) return;
        if (!BasisCameraPrintFinish.TryGetLeak(ExposuresRemaining, picture.width, picture.height, out int edge, out int leakDepth, out float strength)) return;

        BasisCameraFilmPixels.BurnLeak(picture.GetRawTextureData<byte>(), picture.width, picture.height, edge, leakDepth, strength, BasisCameraPrintFinish.LeakColour);
        picture.Apply(false);
    }
    private void BurnStamp(Texture2D picture)
    {
        BasisCameraStamp stamp = BodyTraits.Stamp;
        if (stamp == BasisCameraStamp.None || picture == null || picture.format != TextureFormat.RGBA32) return;

        stampGlyphs.Clear();
        if (!BasisCameraStampPainter.BuildGlyphs(BasisCameraStampPainter.Compose(stamp, DateTime.Now), picture.width, picture.height, stampGlyphs)) return;

        BasisCameraFilmPixels.FillGlyphs(picture.GetRawTextureData<byte>(), picture.width, picture.height, stampGlyphs, BasisCameraStampPainter.ColourOf(stamp));
        picture.Apply(false);
    }
    private Texture2D MountPrint(Texture2D picture)
    {
        if (!BasisCameraPrintFinish.TryGetMount(BodyTraits.PrintBorder, picture.width, picture.height, out RectInt window, out int printWidth, out int printHeight)) return picture;

        BasisCameraRenderTargets.EnsureReadback(ref pooledPrint, printWidth, printHeight, TextureFormat.RGBA32);
        BasisCameraFilmPixels.Mount(picture.GetRawTextureData<byte>(), pooledPrint.GetRawTextureData<byte>(), window, printWidth, printHeight, ref printBorderRow);
        pooledPrint.Apply(false);
        return pooledPrint;
    }
    private void ReleasePrintSheet() => BasisCameraRenderTargets.DestroyAndClear(ref pooledPrint);
#if UNITY_INCLUDE_TESTS
    public bool TryTakeFrameForTest() => TryTakeFrame();
    public void AdvanceBodyForTest(float seconds)
    {
        WindOnRemaining = Mathf.Max(0f, WindOnRemaining - seconds);
        DevelopRemaining = Mathf.Max(0f, DevelopRemaining - seconds);
        FlashRecycleRemaining = Mathf.Max(0f, FlashRecycleRemaining - seconds);
    }
    public void RestoreBodyForTest(int kind, int exposuresRemaining, bool flashEnabled) => RestoreBody(kind, exposuresRemaining, flashEnabled);
#endif
}
