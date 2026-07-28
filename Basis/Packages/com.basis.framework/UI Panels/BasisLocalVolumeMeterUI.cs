#if !BASIS_DISABLE_MICROPHONE
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class BasisLocalVolumeMeterUI : MonoBehaviour
{
    [Header("UI (assign in Inspector)")]
    [Tooltip("Image set to Filled/Horizontal. Acts as the main bar.")]
    public Image fill;
    [Tooltip("Thin Image used as a peak-hold tick (optional).")]
    public Image peakTick;
    [Tooltip("Gradient from quiet→loud (e.g., green→yellow→red).")]
    public Gradient colorByLevel;

    [Header("Meter mapping")]
    [Tooltip("Convert RMS to dB and map from [minDb..maxDb] to 0..1.")]
    public bool useDecibels = true;
    [Tooltip("Lower dB bound mapped to 0. Typical noise floor.")]
    public float minDb = -60f;
    [Tooltip("Upper dB bound mapped to 1. Full-scale ~ 0 dBFS.")]
    public float maxDb = 0f;
    [Tooltip("dB offset after calibration (fine-tune).")]
    public float gainDb = 0f;

    [Header("Dynamics (feel)")]
    [Tooltip("Seconds to rise toward louder levels.")]
    public float attack = 0.06f;
    [Tooltip("Seconds to fall toward quieter levels.")]
    public float release = 0.20f;
    [Tooltip("How long to hold the peak before it starts falling.")]
    public float peakHoldTime = 0.6f;
    [Tooltip("How fast the peak tick falls (normalized units / sec).")]
    public float peakFallPerSecond = 1.5f;

    float smoothed;     // normalized 0..1
    float peak;         // normalized 0..1
    float peakTimer;    // seconds

    // A live meter is the classic whole-canvas killer: the exponential smoothing never
    // exactly settles, so an unguarded write path re-dirties the canvas every frame
    // forever — even fully muted. Quantize to visually-identical steps and only touch
    // the UI when a step actually changes; at rest the meter writes nothing at all.
    const float WriteStep = 1f / 256f;
    float lastWrittenFill = -1f;
    float lastWrittenPeak = -1f;
    bool colorRouted;
    bool tickAnchorsInitialized;

    void Update()
    {
        // Read the rolling RMS computed by your driver.
        float rms = BasisLocalMicrophoneDriver.averageRms;
        if (BasisLocalMicrophoneDriver.isPaused) rms = 0f;

        // Map to 0..1
        float target = useDecibels ? RmsToUnit(rms) : Mathf.Clamp01(rms);

        float UnscaledDeltaTime = Time.unscaledDeltaTime;
        // Smooth (faster up than down feels right for meters)
        float tau = (target > smoothed) ? attack : release;
        float coeff = 1f - Mathf.Exp(-UnscaledDeltaTime / Mathf.Max(0.0001f, tau));
        smoothed = Mathf.Lerp(smoothed, target, coeff);
        // Snap the asymptotic tail to rest so silence stops producing writes.
        if (target <= 0f && smoothed < WriteStep) smoothed = 0f;

        float quantizedFill = Mathf.Round(smoothed / WriteStep) * WriteStep;
        if (quantizedFill != lastWrittenFill)
        {
            lastWrittenFill = quantizedFill;
            fill.fillAmount = quantizedFill;
            // Color rides the canvasRenderer (a render-time multiply) instead of
            // Graphic.color, which would rebuild the mesh on every gradient step.
            if (!colorRouted)
            {
                colorRouted = true;
                fill.color = Color.white;
            }
            fill.canvasRenderer.SetColor(colorByLevel.Evaluate(quantizedFill));
        }

        // Peak-hold tick
        if (smoothed > peak)
        {
            peak = smoothed;
            peakTimer = peakHoldTime;
        }
        else
        {
            if (peakTimer > 0f) peakTimer -= UnscaledDeltaTime;
            else peak = Mathf.Max(0f, peak - peakFallPerSecond * UnscaledDeltaTime);
        }

        float quantizedPeak = Mathf.Round(peak / WriteStep) * WriteStep;
        if (quantizedPeak != lastWrittenPeak)
        {
            lastWrittenPeak = quantizedPeak;
            var rt = peakTick.rectTransform;
            // Moved by position, not by rewriting anchors: anchor writes fire
            // OnRectTransformDimensionsChange into every parent LayoutGroup, which
            // put the whole layout pass on the per-frame path.
            if (!tickAnchorsInitialized)
            {
                tickAnchorsInitialized = true;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 1f);
            }
            RectTransform parentRect = rt.parent as RectTransform;
            float width = parentRect != null ? parentRect.rect.width : 0f;
            rt.anchoredPosition = new Vector2(quantizedPeak * width, 0f);
        }
    }
    float RmsToUnit(float rms)
    {
        // Convert to dB and remap into 0..1 range
        float db = 20f * Mathf.Log10(Mathf.Max(1e-7f, rms)) + gainDb;
        return Mathf.InverseLerp(minDb, maxDb, db);
    }
}

#endif
