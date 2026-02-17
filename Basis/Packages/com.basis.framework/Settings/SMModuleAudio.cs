using System;
using UnityEngine;
using UnityEngine.Audio;
using Basis.BasisUI;

public class SMModuleAudio : BasisSettingsBase
{
    public AudioMixer Mixer;
    public AudioMixerGroup WorldDefaultMixer;

    public static SMModuleAudio Instance;

    public static Action<float> MainVolume;
    public static Action<float> MenusVolume;
    public static Action<float> WorldVolume;
    public static Action<float> PlayerVolume;

    public static float ActiveMainVolume;
    public static float ActiveMenusVolume;
    public static float ActiveWorldVolume;
    public static float ActivePlayerVolume;

    // --- Binding names (single source of truth) ---
    private static string K_MAIN_VOLUME => BasisSettingsDefaults.MainVolume.BindingKey;
    private static string K_MENU_VOLUME => BasisSettingsDefaults.MenuVolume.BindingKey;
    private static string K_WORLD_VOLUME => BasisSettingsDefaults.WorldVolume.BindingKey;
    private static string K_PLAYER_VOLUME => BasisSettingsDefaults.PlayerVolume.BindingKey;

    public new void Awake()
    {
        Instance = this;
        base.Awake();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName)
        {
            case var s when s == K_MAIN_VOLUME:
                if (SliderReadOption(optionValue, out float newMain))
                {
                    BasisDebug.Log($"setting main Volume to {newMain}");
                    ActiveMainVolume = newMain / 100f;
                    MainVolume?.Invoke(ActiveMainVolume);
                    AudioListener.volume = ActiveMainVolume;
                }
                break;

            case var s when s == K_MENU_VOLUME:
                if (SliderReadOption(optionValue, out float newMenus))
                {
                    BasisDebug.Log($"setting Menu Volume to {newMenus}");
                    ActiveMenusVolume = ChangeVolume(newMenus, "menu");
                    MenusVolume?.Invoke(ActiveMenusVolume);
                }
                break;

            case var s when s == K_WORLD_VOLUME:
                if (SliderReadOption(optionValue, out float newWorld))
                {
                    BasisDebug.Log($"setting world Volume to {newWorld}");
                    ActiveWorldVolume = ChangeVolume(newWorld, "world");
                    WorldVolume?.Invoke(ActiveWorldVolume);
                }
                break;

            case var s when s == K_PLAYER_VOLUME:
                if (SliderReadOption(optionValue, out float newPlayer))
                {
                    BasisDebug.Log($"setting player Volume to {newPlayer}");
                    ActivePlayerVolume = ChangeVolume(newPlayer, "player");
                    PlayerVolume?.Invoke(ActivePlayerVolume);
                }
                break;
        }
    }

    public override void ChangedSettings()
    {
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
