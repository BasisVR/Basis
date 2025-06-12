using BattlePhaze.SettingsManager;
using System;
public class SMModuleDistanceBasedReductions : SettingsManagerOption
{
    private static float _microphoneRange;
    private static float _hearingRange;
    private static float _AvatarRange;

    public static event Action<float> OnMicrophoneRangeChanged;
    public static event Action<float> OnHearingRangeChanged;
    public static event Action<float> OnAvatarRangeChanged;
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

    /// <summary>
    /// microphone range
    /// hearing range
    /// maximum avatars
    /// </summary>
    /// <param name="Option"></param>
    /// <param name="Manager"></param>
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            if (SliderReadOption(Option, Manager, out var newMicrophoneRange))
            {
                MicrophoneRange = newMicrophoneRange * newMicrophoneRange;
            }
        }
        else if (NameReturn(1, Option))
        {
            if (SliderReadOption(Option, Manager, out var newHearingRange))
            {
                HearingRange = newHearingRange * newHearingRange;
            }
        }
        else if (NameReturn(2, Option))
        {
            if (SliderReadOption(Option, Manager, out var LoadRange))
            {
                AvatarRange = LoadRange * LoadRange;
            }
        }
    }
}
