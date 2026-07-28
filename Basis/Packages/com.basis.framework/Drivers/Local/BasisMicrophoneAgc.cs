using UnityEngine;

/// <summary>
/// Minimum-statistics noise floor: the quietest frame seen across a sliding window of
/// blocks. Fast to fall, effectively immune to being dragged up by sustained speech.
/// Shared by the AGC's speech gate and the microphone noise gate, which needs its own
/// instance fed from PRE-gate audio — feeding it post-gate closes a loop where a shut
/// gate collapses the floor, which reopens the gate.
/// </summary>
public sealed class BasisNoiseFloorTracker
{
    public const float MinNoiseFloor = 1e-5f;

    private const int BlockCount = 6;
    private const float BlockSeconds = 0.4f;

    private readonly float[] _blocks = new float[BlockCount];
    private int _blockIndex;
    private int _blocksFilled;
    private float _blockMin;
    private float _blockTimer;

    public float NoiseFloor { get; private set; }

    public BasisNoiseFloorTracker()
    {
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < BlockCount; i++)
        {
            _blocks[i] = 0f;
        }

        _blockIndex = 0;
        _blocksFilled = 0;
        _blockMin = float.MaxValue;
        _blockTimer = 0f;
        NoiseFloor = MinNoiseFloor;
    }

    public float Update(float frameRms, float frameSeconds)
    {
        if (frameRms < _blockMin)
        {
            _blockMin = frameRms;
        }

        _blockTimer += frameSeconds;

        if (_blockTimer >= BlockSeconds)
        {
            _blockTimer = 0f;
            _blocks[_blockIndex] = _blockMin;
            _blockIndex = (_blockIndex + 1) % BlockCount;

            if (_blocksFilled < BlockCount)
            {
                _blocksFilled++;
            }

            _blockMin = float.MaxValue;
        }

        float floor = _blockMin;

        for (int i = 0; i < _blocksFilled; i++)
        {
            if (_blocks[i] < floor)
            {
                floor = _blocks[i];
            }
        }

        NoiseFloor = Mathf.Max(MinNoiseFloor, floor);
        return NoiseFloor;
    }
}

/// <summary>
/// Speech-level normaliser for the local microphone chain. Estimates the level of the
/// talker's voice (not the level of the frame) and holds a gain that lands that voice on
/// <see cref="Settings.TargetRms"/>, so two people with very different microphones and
/// speaking volumes arrive at the same loudness for everyone listening.
/// </summary>
public sealed class BasisMicrophoneAgc
{
    public struct Settings
    {
        public float TargetRms;
        public float MaxBoostDb;
        public float Attack01;
        public float Release01;
        public float Headroom;
    }

    /// <summary>
    /// Target speech RMS, deliberately NOT user-tunable: it is the one number every client
    /// has to agree on for "everyone the same loudness" to mean anything. The receive-side
    /// normaliser uses it too.
    /// </summary>
    public const float DefaultTargetRms = 0.1f;

    public const float MaxCutDb = 24f;
    public const float SpeechOverNoise = 4f;
    public const float MinSpeechRms = 1e-4f;

    private const float LevelRiseSeconds = 0.08f;
    private const float LevelFallSeconds = 0.6f;
    private const float HangoverSeconds = 0.4f;

    private const float AttackSlowSeconds = 0.30f;
    private const float AttackFastSeconds = 0.010f;
    private const float ReleaseSlowSeconds = 4f;
    private const float ReleaseFastSeconds = 0.15f;

    private const float SettleSeconds = 0.5f;
    private const float SettleAttackSeconds = 0.04f;
    private const float SettleReleaseSeconds = 0.10f;
    private const float HoldSeconds = 0.4f;

    private readonly BasisNoiseFloorTracker _noiseFloor = new BasisNoiseFloorTracker();

    private float _gainDb;
    private float _speechLevel;
    private float _holdTimer;
    private float _settleTimer;
    private float _hangoverTimer;
    private bool _hasSpeech;

    public float GainDb => _gainDb;
    public float Gain => DbToAmp(_gainDb);
    public float SpeechLevel => _speechLevel;
    public float NoiseFloor => _noiseFloor.NoiseFloor;
    public bool HasSpeechEstimate => _hasSpeech;

    public BasisMicrophoneAgc()
    {
        Reset();
    }

    /// <summary>
    /// Drops every estimate. Call when the capture device changes or the chain restarts —
    /// the held gain belongs to the old microphone's sensitivity.
    /// </summary>
    public void Reset()
    {
        _noiseFloor.Reset();

        _gainDb = 0f;
        _speechLevel = 0f;
        _holdTimer = 0f;
        _settleTimer = 0f;
        _hangoverTimer = 0f;
        _hasSpeech = false;
    }

    /// <summary>
    /// Advances one frame and returns the linear gain to apply to it. The gain is held
    /// across pauses and only ever moves while the talker is actually speaking, so an
    /// utterance starts at the same level it ended at.
    /// </summary>
    public float Process(float frameRms, float framePeak, in Settings settings, float frameSeconds)
    {
        if (frameSeconds <= 0f)
        {
            frameSeconds = 0.02f;
        }

        float target = Mathf.Max(1e-6f, settings.TargetRms);
        float maxBoostDb = Mathf.Max(0f, settings.MaxBoostDb);
        float headroom = settings.Headroom > 0f ? settings.Headroom : 1f;

        float noiseFloor = _noiseFloor.Update(frameRms, frameSeconds);

        if (_holdTimer > 0f)
        {
            _holdTimer -= frameSeconds;
        }

        if (_settleTimer > 0f)
        {
            _settleTimer -= frameSeconds;
        }

        bool aboveNoise = frameRms > MinSpeechRms && frameRms > noiseFloor * SpeechOverNoise;

        if (aboveNoise)
        {
            _hangoverTimer = HangoverSeconds;
        }
        else if (_hangoverTimer > 0f)
        {
            _hangoverTimer -= frameSeconds;
        }

        if (aboveNoise || _hangoverTimer > 0f)
        {
            if (!_hasSpeech)
            {
                _hasSpeech = true;
                _speechLevel = frameRms;
                _settleTimer = SettleSeconds;
                _gainDb = DesiredDb(target, _speechLevel, maxBoostDb);
            }
            else
            {
                float levelSeconds = frameRms > _speechLevel ? LevelRiseSeconds : LevelFallSeconds;
                _speechLevel = Mathf.Lerp(_speechLevel, frameRms, Coeff(levelSeconds, frameSeconds));

                float desiredDb = DesiredDb(target, _speechLevel, maxBoostDb);
                bool settling = _settleTimer > 0f;

                if (desiredDb < _gainDb)
                {
                    float attackSeconds = settling
                        ? SettleAttackSeconds
                        : Mathf.Lerp(AttackSlowSeconds, AttackFastSeconds, Mathf.Clamp01(settings.Attack01));

                    _gainDb = Mathf.Lerp(_gainDb, desiredDb, Coeff(attackSeconds, frameSeconds));
                    _holdTimer = HoldSeconds;
                }
                else if (settling || _holdTimer <= 0f)
                {
                    float releaseSeconds = settling
                        ? SettleReleaseSeconds
                        : Mathf.Lerp(ReleaseSlowSeconds, ReleaseFastSeconds, Mathf.Clamp01(settings.Release01));

                    _gainDb = Mathf.Lerp(_gainDb, desiredDb, Coeff(releaseSeconds, frameSeconds));
                }
            }
        }

        float amp = DbToAmp(_gainDb);

        if (framePeak > 1e-6f && framePeak * amp > headroom)
        {
            amp = headroom / framePeak;
            _gainDb = 20f * Mathf.Log10(Mathf.Max(1e-6f, amp));
            _holdTimer = HoldSeconds;
        }

        return amp;
    }

    private static float DesiredDb(float target, float level, float maxBoostDb)
    {
        float db = 20f * Mathf.Log10(target / Mathf.Max(1e-6f, level));
        return Mathf.Clamp(db, -MaxCutDb, maxBoostDb);
    }

    private static float Coeff(float tauSeconds, float frameSeconds)
    {
        if (tauSeconds <= 0f)
        {
            return 1f;
        }

        return 1f - Mathf.Exp(-frameSeconds / tauSeconds);
    }

    private static float DbToAmp(float db) => Mathf.Pow(10f, db / 20f);
}
