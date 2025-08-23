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
    /// <summary>
    /// 0 to 1 rest or 0 to 100
    /// </summary>
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
                    ActiveMainVolume = NewActiveMainVolume / 100;
                    MainVolume?.Invoke(ActiveMainVolume);
                    AudioListener.volume = ActiveMainVolume;
                }
                break;
            case "menu volume":
                if (SliderReadOption(optionValue, out float NewActiveMenusVolume))
                {
                    ActiveMenusVolume = NewActiveMenusVolume;
                    MenusVolume?.Invoke(ActiveMenusVolume);
                    ChangeVolume(ActiveMenusVolume, "menu");
                }
                break;
            case "world volume":
                if (SliderReadOption(optionValue, out float NewActiveWorldVolume))
                {
                    ActiveWorldVolume = NewActiveWorldVolume;
                    WorldVolume?.Invoke(ActiveWorldVolume);
                    ChangeVolume(ActiveWorldVolume, "world");
                }
                break;
            case "player volume":
                if (SliderReadOption(optionValue, out float NewActivePlayerVolume))
                {
                    ActivePlayerVolume = NewActivePlayerVolume;
                    PlayerVolume?.Invoke(ActivePlayerVolume);
                    ChangeVolume(ActivePlayerVolume, "player");
                }
                break;
        }
    }
    public void ChangeVolume(float value, string name)
    {
        // Convert 0–100 slider to 0.0001–1 (linear scale)
        float linear = Mathf.Clamp01(value / 100f);

        // Convert linear 0–1 to decibels (-80dB to 0dB)
        float dB = Mathf.Log10(Mathf.Max(linear, 0.0001f)) * 20f;

        // Debug & apply
        BasisDebug.Log($"{name} set to {value} (linear: {linear}, dB: {dB})");
        Mixer.SetFloat(name, dB);
    }
}
