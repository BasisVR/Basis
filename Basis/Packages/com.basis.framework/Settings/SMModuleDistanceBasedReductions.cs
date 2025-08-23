using System;

public class SMModuleDistanceBasedReductions : BasisSettingsBase
{
    private static float _microphoneRange;
    private static float _hearingRange;
    private static float _AvatarRange;
    private static float _meshLod;

    public static event Action<float> OnMicrophoneRangeChanged;
    public static event Action<float> OnHearingRangeChanged;
    public static event Action<float> OnAvatarRangeChanged;
    public static event Action<float> OnMeshLodChanged; 

    /// <summary>
    /// will be value * value returned pre-squared
    /// </summary>
    public static float MicrophoneRange
    {
        get => _microphoneRange;
        set
        {
            _microphoneRange = value;
            OnMicrophoneRangeChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// will be value * value returned pre-squared
    /// </summary>
    public static float HearingRange
    {
        get => _hearingRange;
        set
        {
            _hearingRange = value;
            OnHearingRangeChanged?.Invoke(value);
        }
    }

    public static float AvatarRange
    {
        get => _AvatarRange;
        set
        {
            _AvatarRange = value;
            OnAvatarRangeChanged?.Invoke(value);
        }
    }

    public static float MeshLod
    {
        get => _meshLod;
        set
        {
            _meshLod = value;
            OnMeshLodChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// microphone range
    /// hearing range
    /// maximum avatars
    /// mesh LOD
    /// </summary>
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        string key = matchedSettingName.ToLower();

        switch (key)
        {
            case "microphonerange":
                if (SliderReadOption(optionValue, out float newMicrophoneRange))
                {
#if UNITY_SERVER
                    MicrophoneRange = 0;
#else
                    MicrophoneRange = newMicrophoneRange * newMicrophoneRange;
#endif
                }
                break;

            case "hearingrange":
                if (SliderReadOption(optionValue, out float newHearingRange))
                {
#if UNITY_SERVER
                    HearingRange = 0;
#else
                    HearingRange = newHearingRange * newHearingRange;
#endif
                }
                break;

            case "avatarrange":
                if (SliderReadOption(optionValue, out float loadRange))
                {
#if UNITY_SERVER
                    AvatarRange = 0;
#else
                    AvatarRange = loadRange * loadRange;
#endif
                }
                break;

            case "meshlod":
                if (SliderReadOption(optionValue, out float lod))
                {
#if UNITY_SERVER
                    MeshLod = 0;
#else
                    MeshLod = lod * lod;
#endif
                }
                break;

            default:
                // Optionally handle unknown settings
                break;
        }
    }
}
