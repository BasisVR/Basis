using UnityEngine;
using UnityEngine.Rendering;
public partial class BasisHandHeldCamera
{
    public bool autoBrightnessEnabled;
    public float autoBrightnessTarget = BasisCameraMetering.DefaultTarget, autoBrightnessSpeed = BasisCameraMetering.DefaultSpeed;
    public float autoBrightnessRange = BasisCameraMetering.DefaultRange;
    public int autoBrightnessMetering;
    private RenderTexture meterTexture;
    private bool meterRequestInFlight, meterReleasePending, autoBrightnessMeteringHeld;
    private float meterCountdown, autoBrightnessGoal, meterStopsAtRequest;
    public float AutoBrightnessStops { get; private set; }
    public float MeasuredBrightness { get; private set; } = -1f;
    public float AutoBrightnessOffset => autoBrightnessEnabled ? AutoBrightnessStops : 0f;
    public bool HasMeasuredBrightness => MeasuredBrightness >= 0f;
    public void SetAutoBrightnessMeteringHeld(bool held) => autoBrightnessMeteringHeld = held;
    public void SetAutoBrightnessEnabled(bool enabled)
    {
        if (autoBrightnessEnabled == enabled) return;
        autoBrightnessEnabled = enabled;

        if (enabled)
        {
            AutoBrightnessStops = 0f;
            autoBrightnessGoal = 0f;
            MeasuredBrightness = -1f;
            meterCountdown = 0f;
        }
        else
        {
            ReleaseMeterTexture();
        }

        HandHeld?.ApplyPostExposure();
    }
    public void SetAutoBrightnessTarget(float target) => autoBrightnessTarget = Mathf.Clamp(target, BasisCameraMetering.MinTarget, BasisCameraMetering.MaxTarget);
    public void SetAutoBrightnessSpeed(float speed) => autoBrightnessSpeed = Mathf.Clamp(speed, BasisCameraMetering.MinSpeed, BasisCameraMetering.MaxSpeed);
    public void SetAutoBrightnessMetering(int mode) => autoBrightnessMetering = BasisCameraMetering.SanitizeMode(mode);
    public void SetAutoBrightnessRange(float stops) => autoBrightnessRange = Mathf.Clamp(stops, BasisCameraMetering.MinRange, BasisCameraMetering.MaxRange);
    private void TickAutoBrightness()
    {
        if (!autoBrightnessEnabled || autoBrightnessMeteringHeld) return;

        float deltaTime = Time.unscaledDeltaTime;
        if (deltaTime > 0f && !Mathf.Approximately(AutoBrightnessStops, autoBrightnessGoal))
        {
            AutoBrightnessStops = BasisCameraMetering.Approach(AutoBrightnessStops, autoBrightnessGoal, autoBrightnessSpeed, deltaTime);
            HandHeld?.ApplyPostExposure();
        }

        if (captureInFlight || renderTexture == null || captureCamera == null || !captureCamera.enabled) return;

        meterCountdown -= deltaTime;
        if (meterCountdown > 0f || meterRequestInFlight) return;
        meterCountdown = 1f / BasisCameraMetering.Rate;

        if (meterTexture == null) meterTexture = BasisCameraRenderTargets.Create(BasisCameraMetering.Size, BasisCameraMetering.Size, RenderTextureFormat.ARGB32, 0, 1, true, "BasisBrightnessMeter");

        Graphics.Blit(renderTexture, meterTexture);

        meterRequestInFlight = true;
        meterStopsAtRequest = AutoBrightnessStops;
        AsyncGPUReadback.Request(meterTexture, 0, TextureFormat.RGBA32, OnMeterReadback);
    }
    private void OnMeterReadback(AsyncGPUReadbackRequest request)
    {
        meterRequestInFlight = false;

        if (meterReleasePending)
        {
            meterReleasePending = false;
            ReleaseMeterTexture();
            return;
        }

        if (this == null || request.hasError || !autoBrightnessEnabled) return;

        float measured = BasisCameraMetering.Measure(request.GetData<Color32>(), BasisCameraMetering.Size, BasisCameraMetering.Size, (BasisCameraMeteringMode)Mathf.Clamp(autoBrightnessMetering, 0, 2));
        if (measured < 0f) return;

        MeasuredBrightness = measured;
        autoBrightnessGoal = BasisCameraMetering.GoalStops(meterStopsAtRequest, measured, autoBrightnessTarget, autoBrightnessRange);
    }
    private void ReleaseMeterTexture()
    {
        if (meterTexture == null) return;

        if (meterRequestInFlight)
        {
            meterReleasePending = true;
            return;
        }

        BasisCameraRenderTargets.Release(ref meterTexture);
    }
    private void ReleaseAutoBrightness()
    {
        MeasuredBrightness = -1f;
        ReleaseMeterTexture();
    }
}
