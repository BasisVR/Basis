using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>How much of the frame the brightness meter listens to.</summary>
public enum BasisCameraMeteringMode
{
    /// <summary>The whole frame, weighted evenly. Steadiest, and the easiest to predict.</summary>
    Average = 0,

    /// <summary>The whole frame, but the middle counts for far more — the usual stills-camera meter.</summary>
    CentreWeighted = 1,

    /// <summary>Only a small circle at the centre, so a subject can be exposed against any background.</summary>
    Spot = 2,
}

/// <summary>
/// Auto brightness. The frame that was just shown is metered, and the camera's post-exposure is
/// moved until the picture sits at the target level — a closed loop, so it measures the result of
/// its own correction and every non-linearity between the two (the tonemapper above all) is
/// absorbed rather than modelled.
/// <para>
/// The manual exposure control keeps working while this is on and becomes exposure compensation:
/// the two are summed, which is the same division of labour a stills camera makes between its meter
/// and its ±EV dial.
/// </para>
/// </summary>
public partial class BasisHandHeldCamera
{
    /// <summary>Whether the camera meters the scene and sets its own exposure.</summary>
    public bool autoBrightnessEnabled;

    /// <summary>Level the metered picture is driven to, 0 to 1 as the display shows it.</summary>
    public float autoBrightnessTarget = DefaultBrightnessTarget;

    /// <summary>How quickly exposure catches up, as the rate of an exponential approach.</summary>
    public float autoBrightnessSpeed = DefaultBrightnessSpeed;

    /// <summary>Which part of the frame is metered, as <see cref="BasisCameraMeteringMode"/>.</summary>
    public int autoBrightnessMetering;

    /// <summary>Furthest the meter may move exposure on its own, in stops either way.</summary>
    public float autoBrightnessRange = DefaultBrightnessRange;

    public const float DefaultBrightnessTarget = 0.45f;
    public const float DefaultBrightnessSpeed = 2f;
    public const float DefaultBrightnessRange = 3f;

    public const float MinBrightnessTarget = 0.05f;
    public const float MaxBrightnessTarget = 0.95f;

    /// <summary>
    /// Response rate limits. The loop measures a frame that has already been shown, so its own
    /// correction reaches it late; past this the lag turns the approach into a ring, and the picture
    /// hunts up and down instead of settling.
    /// </summary>
    public const float MinBrightnessSpeed = 0.1f;
    public const float MaxBrightnessSpeed = 8f;

    public const float MinBrightnessRange = 0.5f;
    public const float MaxBrightnessRange = 6f;

    /// <summary>
    /// Meter resolution. Not an average of the frame so much as a 4096-point sample of it, which is
    /// what a camera's own meter is — the sampling noise that costs is well under the temporal
    /// smoothing, and the readback is 16KB.
    /// </summary>
    private const int MeterSize = 64;

    /// <summary>Meters a second. The approach runs every frame regardless, so this only sets how often it is aimed.</summary>
    private const float MeterRate = 12f;

    /// <summary>Darkest the picture is allowed to read as. One 8-bit step; below it the log blows up.</summary>
    private const float MinMeasurable = 1f / 255f;

    /// <summary>The stops auto brightness is currently adding, before the manual compensation.</summary>
    public float AutoBrightnessStops { get; private set; }

    /// <summary>What auto brightness contributes to the exposure, or zero while it is off.</summary>
    public float AutoBrightnessOffset => autoBrightnessEnabled ? AutoBrightnessStops : 0f;

    /// <summary>The level the last metered frame came out at, 0 to 1, or -1 before the first one lands.</summary>
    public float MeasuredBrightness { get; private set; } = -1f;

    /// <summary>True once a frame has been metered, so the panel can say "metering…" until then.</summary>
    public bool HasMeasuredBrightness => MeasuredBrightness >= 0f;

    private RenderTexture meterTexture;
    private bool meterRequestInFlight;
    private bool meterReleasePending;
    private float meterCountdown;

    /// <summary>Where the approach is heading. Re-aimed only when a fresh reading lands.</summary>
    private float autoBrightnessGoal;

    /// <summary>
    /// The exposure the metered frame was rendered with. The error a reading carries is relative to
    /// that, not to wherever the approach has since moved — reading it live instead is what turns a
    /// proportional loop into an integrating one, and the exposure walks away rather than settling.
    /// </summary>
    private float meterStopsAtRequest;

    /// <summary>
    /// Freezes the metering loop without switching auto brightness off. <see
    /// cref="AutoBrightnessOffset"/> keeps returning whatever it last converged to — no new
    /// reading is taken and the approach stops moving — rather than a real "off", which would drop
    /// the offset to zero and visibly change the shot (see that property). For a caller that needs
    /// a stable exposure across many frames, a photogrammetry capture above all: inconsistent
    /// brightness between shots of the same place reads as noise to whatever trains on them.
    /// </summary>
    private bool autoBrightnessMeteringHeld;

    public void SetAutoBrightnessMeteringHeld(bool held) => autoBrightnessMeteringHeld = held;

    public void SetAutoBrightnessEnabled(bool enabled)
    {
        if (autoBrightnessEnabled == enabled) return;
        autoBrightnessEnabled = enabled;

        if (enabled)
        {
            // From wherever the picture is now rather than from a stale reading, so switching it on
            // does not first jump to the exposure some other room needed.
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

    public void SetAutoBrightnessTarget(float target) =>
        autoBrightnessTarget = Mathf.Clamp(target, MinBrightnessTarget, MaxBrightnessTarget);

    public void SetAutoBrightnessSpeed(float speed) =>
        autoBrightnessSpeed = Mathf.Clamp(speed, MinBrightnessSpeed, MaxBrightnessSpeed);

    public void SetAutoBrightnessMetering(int mode) =>
        autoBrightnessMetering = System.Enum.IsDefined(typeof(BasisCameraMeteringMode), mode)
            ? mode
            : (int)BasisCameraMeteringMode.CentreWeighted;

    public void SetAutoBrightnessRange(float stops) =>
        autoBrightnessRange = Mathf.Clamp(stops, MinBrightnessRange, MaxBrightnessRange);

    /// <summary>
    /// How much a point in the frame counts towards the reading, for a metering mode. Pure, and the
    /// whole difference between the three modes lives here.
    /// </summary>
    public static float MeteringWeight(BasisCameraMeteringMode mode, float u, float v)
    {
        switch (mode)
        {
            case BasisCameraMeteringMode.Spot:
            {
                float radius = Radius(u, v);
                return radius <= SpotRadius ? 1f : 0f;
            }

            case BasisCameraMeteringMode.CentreWeighted:
                return Mathf.Lerp(1f, EdgeWeight, Mathf.Clamp01(Radius(u, v)));

            default:
                return 1f;
        }
    }

    /// <summary>Distance from the centre of the frame, 0 at the centre and 1 at the middle of an edge.</summary>
    private static float Radius(float u, float v) =>
        Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 2f;

    /// <summary>What a point at the edge of the frame counts for under centre weighting.</summary>
    private const float EdgeWeight = 0.15f;

    /// <summary>Spot radius, as a share of the half-frame. 0.2 is about 3% of the picture.</summary>
    private const float SpotRadius = 0.2f;

    /// <summary>
    /// Averages a metered frame's brightness under the chosen weighting. Rows arrive bottom-up,
    /// which does not matter to a weighting that is symmetric about the centre, and is why this
    /// takes the buffer rather than caring which way up it was.
    /// </summary>
    public static float MeasureBrightness(NativeArray<Color32> pixels, int width, int height, BasisCameraMeteringMode mode)
    {
        if (!pixels.IsCreated || width <= 0 || height <= 0 || pixels.Length < width * height) return -1f;

        double total = 0d;
        double weightSum = 0d;

        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float weight = MeteringWeight(mode, (x + 0.5f) / width, v);
                if (weight <= 0f) continue;

                Color32 pixel = pixels[y * width + x];

                // Rec. 709 luma on the display-encoded bytes, not on linearised ones: a meter is
                // asking how bright the picture looks, and the target is read off the same scale.
                float luma = (0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b) / 255f;

                total += luma * weight;
                weightSum += weight;
            }
        }

        return weightSum > 0d ? (float)(total / weightSum) : -1f;
    }

    /// <summary>
    /// The exposure a reading asks for, in stops. Separated out so the loop's one piece of real
    /// arithmetic can be tested without a GPU.
    /// </summary>
    public static float GoalStops(float stopsWhenMetered, float measured, float target, float range)
    {
        float safeMeasured = Mathf.Max(measured, MinMeasurable);
        float safeTarget = Mathf.Clamp(target, MinBrightnessTarget, MaxBrightnessTarget);
        float error = Mathf.Log(safeTarget / safeMeasured, 2f);
        return Mathf.Clamp(stopsWhenMetered + error, -Mathf.Abs(range), Mathf.Abs(range));
    }

    /// <summary>
    /// Per-frame half of the loop: approach the exposure the last reading asked for, and take a new
    /// reading when one is due. Run from the camera's render-phase tick, ahead of the render, so
    /// the exposure it sets is the one this frame is shot at.
    /// </summary>
    private void TickAutoBrightness()
    {
        if (!autoBrightnessEnabled || autoBrightnessMeteringHeld)
        {
            return;
        }

        float deltaTime = Time.unscaledDeltaTime;
        if (deltaTime > 0f && !Mathf.Approximately(AutoBrightnessStops, autoBrightnessGoal))
        {
            float rate = Mathf.Clamp(autoBrightnessSpeed, MinBrightnessSpeed, MaxBrightnessSpeed);
            AutoBrightnessStops = Mathf.Lerp(AutoBrightnessStops, autoBrightnessGoal,
                1f - Mathf.Exp(-rate * deltaTime));
            HandHeld?.ApplyPostExposure();
        }

        // A capture owns the feed at its own size and format for those frames, and a camera that is
        // not rendering has nothing new to read — metering either would be measuring a frame that
        // is not the one on screen.
        if (captureInFlight || renderTexture == null || captureCamera == null || !captureCamera.enabled) return;

        meterCountdown -= deltaTime;
        if (meterCountdown > 0f || meterRequestInFlight) return;
        meterCountdown = 1f / MeterRate;

        if (!EnsureMeterTexture()) return;

        Graphics.Blit(renderTexture, meterTexture);

        meterRequestInFlight = true;
        meterStopsAtRequest = AutoBrightnessStops;
        AsyncGPUReadback.Request(meterTexture, 0, TextureFormat.RGBA32, OnMeterReadback);
    }

    private void OnMeterReadback(AsyncGPUReadbackRequest request)
    {
        meterRequestInFlight = false;

        // Freeing was asked for while this was in flight; the texture had to outlive the read.
        if (meterReleasePending)
        {
            meterReleasePending = false;
            ReleaseMeterTexture();
            return;
        }

        // The camera can be put away between the request and its answer.
        if (this == null || request.hasError || !autoBrightnessEnabled) return;

        NativeArray<Color32> pixels = request.GetData<Color32>();
        float measured = MeasureBrightness(pixels, MeterSize, MeterSize,
            (BasisCameraMeteringMode)Mathf.Clamp(autoBrightnessMetering, 0, 2));
        if (measured < 0f) return;

        MeasuredBrightness = measured;
        autoBrightnessGoal = GoalStops(meterStopsAtRequest, measured, autoBrightnessTarget, autoBrightnessRange);
    }

    private bool EnsureMeterTexture()
    {
        if (meterTexture != null) return true;

        var descriptor = new RenderTextureDescriptor(MeterSize, MeterSize, RenderTextureFormat.ARGB32, 0)
        {
            msaaSamples = 1,
            useMipMap = false,
            autoGenerateMips = false,
            sRGB = true,
        };

        meterTexture = new RenderTexture(descriptor) { name = "BasisBrightnessMeter" };
        meterTexture.Create();
        return true;
    }

    private void ReleaseMeterTexture()
    {
        if (meterTexture == null) return;

        // A readback holds the texture until it lands, so freeing it here would be reading freed
        // memory. Hand the job to the callback instead.
        if (meterRequestInFlight)
        {
            meterReleasePending = true;
            return;
        }

        meterTexture.Release();
        Destroy(meterTexture);
        meterTexture = null;
    }

    private void ReleaseAutoBrightness()
    {
        MeasuredBrightness = -1f;
        ReleaseMeterTexture();
    }
}
