using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using UnityEngine.UI;
public class BasisUIVolumeSampler : MonoBehaviour
{
    [Header("Remote source (set via Initialize)")]
    public BasisRemotePlayer RemotePlayer;


    [Tooltip("Optional thin Image used as a peak-hold tick.")]
    public Image peakTick;
    [Tooltip("Gradient from quiet→loud (e.g., green→yellow→red).")]
    public Gradient colorByLevel;
    [Header("Level mapping")]
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

    [Header("Look")]
    [Tooltip("Color for inactive segments.")]
    public Color inactiveColor = new Color(0.4f, 0.4f, 0.4f, 1f);
    [Tooltip("Color override when signal exceeds maxDb (overdrive).")]
    public Color overdriveColor = Color.red;
    [Tooltip("Image set to Filled/Horizontal. Acts as the main bar.")]
    public Image fill;

    bool subscribed;

    // raw measurements derived from audio callback
    volatile float instantaneousRms;      // linear 0..+ (not clamped)
    volatile float instantaneousPeak;     // linear 0..+

    // UI-driving state (normalized 0..1 unless noted)
    float smoothed; // meter fill (0..1)
    float peakNorm; // 0..1 peak-hold position
    float peakTimer;


    [Header("References")]
    public Slider slider;
    public Image bandRecommended;
    public Image bandOverdrive;
    public Image defaultTick; // thin vertical image

    [Header("Semantics (in slider units)")]
    [Tooltip("Lower bound of 'recommended' range, e.g., 0.6 (60%).")]
    public float recommendedMin = 1f;
    [Tooltip("Default/standard reference, typically 1.0 (100%).")]
    public float defaultValue = 1.0f;
    public void Initalize(BasisRemotePlayer remotePlayer)
    {
        RemotePlayer = remotePlayer;

        TryUnsubscribe(); // in case we were already wired

        if (RemotePlayer == null ||RemotePlayer.NetworkReceiver == null ||
            RemotePlayer.NetworkReceiver.AudioReceiverModule == null ||
            RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver == null)
        {
            // Remote stream not available (yet).
            return;
        }
        // Drive UI
        fill.fillAmount = smoothed;
        fill.color = colorByLevel.Evaluate(smoothed);

        RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver.AudioData += OnAudio;
        subscribed = true;

        float span = Mathf.Max(0.0001f, slider.maxValue - slider.minValue);

        // Helper to set a child Image’s anchors in normalized [0..1] space along X.
        void SetBand(Image img, float xMin, float xMax)
        {
            if (!img) return;
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(Mathf.Clamp01(xMin), 0f);
            rt.anchorMax = new Vector2(Mathf.Clamp01(xMax), 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        bandRecommended.color = new Color(0f, 0.8f, 0.4f, 0.4f); // semi-transparent green
        bandOverdrive.color = new Color(0.9f, 0f, 0f, 0.4f); // semi-transparent red

        float recMinN = (recommendedMin - slider.minValue) / span;
        float defN = (defaultValue - slider.minValue) / span;
        float overMinN = defN;
        float overMaxN = 1f; // up to max

        SetBand(bandRecommended, recMinN, defN);
        SetBand(bandOverdrive, overMinN, overMaxN);

        if (defaultTick)
        {
            var rt = defaultTick.rectTransform;
            rt.anchorMin = new Vector2(defN, 0f);
            rt.anchorMax = new Vector2(defN, 1f);
            rt.anchoredPosition = Vector2.zero;
            // Make sure DefaultTick has a LayoutElement or fixed width so it’s visible.
        }
    }
    void OnDisable() => TryUnsubscribe();
    void OnDestroy() => TryUnsubscribe();
    void OnAudio(float[] data, int channels)
    {
        if (data == null || data.Length == 0)
            return;

        // Compute linear RMS and peak without GC
        // Treat buffer as interleaved; we just consider absolute sample values overall
        double sumSq = 0.0;
        float peak = 0f;

        // Clamp pass (just in case) and collect stats
        for (int i = 0; i < data.Length; i++)
        {
            float s = data[i];

            // Purge NaN/Inf defensively (cheap)
            if (!float.IsFinite(s)) s = 0f;

            float abs = (s >= 0f) ? s : -s;
            if (abs > peak) peak = abs;

            // Use double for numerical headroom
            sumSq += (double)s * (double)s;
        }

        float rms = Mathf.Sqrt((float)(sumSq / Mathf.Max(1, data.Length)));

        instantaneousRms = rms;
        instantaneousPeak = peak;
    }
    void Update()
    {
        // Map current measurement to target normalized 0..1 (can exceed 1 before clamp for overdrive detection)
        float targetUnit = RmsToUnit(instantaneousRms);
        float targetClamped = Mathf.Clamp01(targetUnit);

        // Smooth with different time constants for rise/fall
        float dt = Time.unscaledDeltaTime;
        float tau = (targetClamped > smoothed) ? Mathf.Max(0.0001f, attack) : Mathf.Max(0.0001f, release);
        float coeff = 1f - Mathf.Exp(-dt / tau);
        smoothed = Mathf.Lerp(smoothed, targetClamped, coeff);

        // Drive UI
        fill.fillAmount = smoothed;
        fill.color = colorByLevel.Evaluate(smoothed);

        // Peak-hold tick (optional)
        if (peakTick)
        {
            // Calculate current normalized peak from instantaneousPeak
            float peakUnit = RmsToUnit(instantaneousPeak);
            float peakClamped = Mathf.Clamp01(peakUnit);

            if (peakClamped > peakNorm)
            {
                peakNorm = peakClamped;
                peakTimer = peakHoldTime;
            }
            else
            {
                if (peakTimer > 0f) peakTimer -= dt;
                else peakNorm = Mathf.Max(0f, peakNorm - peakFallPerSecond * dt);
            }

            // Place tick at X = peakNorm
            var rt = peakTick.rectTransform;
            rt.anchorMin = new Vector2(peakNorm, 0f);
            rt.anchorMax = new Vector2(peakNorm, 1f);
            rt.anchoredPosition = Vector2.zero;

            // Color tick: red on overdrive, otherwise follow gradient at that position
            peakTick.color = (targetUnit > 1f) ? overdriveColor : colorByLevel.Evaluate(peakNorm);
        }
    }
    float RmsToUnit(float rms)
    {
        // Convert to dB, then map to 0..1 across [minDb..maxDb].
        float db = 20f * Mathf.Log10(Mathf.Max(1e-7f, rms)) + gainDb;
        return Mathf.InverseLerp(minDb, maxDb, db);
    }
    void TryUnsubscribe()
    {
        if (!subscribed) return;

        // The stream can vanish mid-session; guard every hop.
        if (RemotePlayer != null &&
            RemotePlayer.NetworkReceiver != null &&
            RemotePlayer.NetworkReceiver.AudioReceiverModule != null &&
            RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver != null)
        {
            RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver.AudioData -= OnAudio;
        }

        subscribed = false;
    }
}
