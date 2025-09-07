using System;
using UnityEngine;
using UnityEngine.Audio;
public class SMModuleAudio : BasisSettingsBase
{
    public AudioMixer Mixer;
    public AudioMixerGroup WorldDefaultMixer;
    public static SMModuleAudio Instance;
    public new void Awake()
    {
        Instance = this;
        base.Awake();
    }
    public static Action<float> MainVolume;
    public static Action<float> MenusVolume;
    public static Action<float> WorldVolume;
    public static Action<float> PlayerVolume;
    public static float ActiveMainVolume;
    public static float ActiveMenusVolume;
    public static float ActiveWorldVolume;
    public static float ActivePlayerVolume;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName)
        {
            case "main volume":
                if (SliderReadOption(optionValue, out float NewActiveMainVolume))
                {
                    BasisDebug.Log($"setting main Volume to {NewActiveMainVolume}");
                    ActiveMainVolume = NewActiveMainVolume / 100;
                    MainVolume?.Invoke(ActiveMainVolume);
                    AudioListener.volume = ActiveMainVolume;
                }
                break;
            case "menu volume":
                if (SliderReadOption(optionValue, out float NewActiveMenusVolume))
                {
                    BasisDebug.Log($"setting Menu Volume to {NewActiveMenusVolume}");
                    ActiveMenusVolume = ChangeVolume(NewActiveMenusVolume, "menu");
                    MenusVolume?.Invoke(ActiveMenusVolume);
                }
                break;
            case "world volume":
                if (SliderReadOption(optionValue, out float NewActiveWorldVolume))
                {
                    BasisDebug.Log($"setting world Volume to {NewActiveWorldVolume}");
                    ActiveWorldVolume = ChangeVolume(NewActiveWorldVolume, "world");
                    WorldVolume?.Invoke(ActiveWorldVolume);
                }
                break;
            case "player volume":
                if (SliderReadOption(optionValue, out float NewActivePlayerVolume))
                {
                    BasisDebug.Log($"setting player Volume to {NewActivePlayerVolume}");
                    ActivePlayerVolume = ChangeVolume(NewActivePlayerVolume, "player");
                    PlayerVolume?.Invoke(ActivePlayerVolume);
                }
                break;
            default:
                BasisDebug.LogError($"Missing Audio Settings for {matchedSettingName}");
                break;
        }
    }
    public float ChangeVolume(float value, string name)
    {
        // Convert 0–100 slider to 0.0001–1 (linear scale)
        float linear = Mathf.Clamp01(value / 100f);

        // Convert linear 0–1 to decibels (-80dB to 0dB)
        float dB = Mathf.Log10(Mathf.Max(linear, 0.0001f)) * 20f;
        Mixer.SetFloat(name, dB);
        return linear;
    }
}
