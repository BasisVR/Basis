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
    public static Action<float> VoiceVolume;
    public static Action<float> VideoVolume;

    public static Action<float> AvatarVolume;
    public static Action<float> PropVolume;

    public static float ActiveMainVolume = 1f;
    public static float ActiveMenusVolume;
    public static float ActiveWorldVolume;
    public static float ActiveVoiceVolume;
    public static float ActiveVideoVolume;

    public static float ActiveAvatarVolume;
    public static float ActivePropVolume;

    // --- Binding names (single source of truth) ---
    private static string K_MAIN_VOLUME => BasisSettingsDefaults.MainVolume.BindingKey;
    private static string K_MENU_VOLUME => BasisSettingsDefaults.MenuVolume.BindingKey;
    private static string K_WORLD_VOLUME => BasisSettingsDefaults.WorldVolume.BindingKey;
    private static string K_VOICE_VOLUME => BasisSettingsDefaults.VoiceVolume.BindingKey;
    private static string K_MEDIA_VOLUME => BasisSettingsDefaults.MediaVolume.BindingKey;
    private static string K_AVATAR_VOLUME => BasisSettingsDefaults.AvatarVolume.BindingKey;
    private static string K_PROP_VOLUME => BasisSettingsDefaults.PropVolume.BindingKey;

    // If your mixer parameter names differ, change these strings.
    private const string MIXER_MENU = "menu";
    private const string MIXER_WORLD = "world";
    private const string MIXER_VOICE = "player";
    private const string MIXER_AVATAR = "avatar";
    private const string MIXER_PROP = "prop";

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
                if (SliderReadOption(optionValue, out float newMain)) ApplyMainVolume(newMain);
                break;
            case var s when s == K_MENU_VOLUME:
                if (SliderReadOption(optionValue, out float newMenus)) ApplyMenuVolume(newMenus);
                break;
            case var s when s == K_WORLD_VOLUME:
                if (SliderReadOption(optionValue, out float newWorld)) ApplyWorldVolume(newWorld);
                break;
            case var s when s == K_VOICE_VOLUME:
                if (SliderReadOption(optionValue, out float newVoice)) ApplyVoiceVolume(newVoice);
                break;
            case var s when s == K_AVATAR_VOLUME:
                if (SliderReadOption(optionValue, out float newAvatar)) ApplyAvatarVolume(newAvatar);
                break;
            case var s when s == K_PROP_VOLUME:
                if (SliderReadOption(optionValue, out float newProp)) ApplyPropVolume(newProp);
                break;
            case var s when s == K_MEDIA_VOLUME:
                if (SliderReadOption(optionValue, out float newVideo)) ApplyMediaVolume(newVideo);
                break;
        }
    }

    public static void ApplyMainVolume(float sliderPercent)
    {
        ActiveMainVolume = Mathf.Clamp01(sliderPercent / 100f);
        AudioListener.volume = ActiveMainVolume;
        MainVolume?.Invoke(ActiveMainVolume);
    }

    public static void ApplyMenuVolume(float sliderPercent)
    {
        if (Instance == null) return;
        ActiveMenusVolume = Instance.ChangeVolume(sliderPercent, MIXER_MENU);
        MenusVolume?.Invoke(ActiveMenusVolume);
    }

    public static void ApplyWorldVolume(float sliderPercent)
    {
        if (Instance == null) return;
        ActiveWorldVolume = Instance.ChangeVolume(sliderPercent, MIXER_WORLD);
        WorldVolume?.Invoke(ActiveWorldVolume);
    }

    public static void ApplyVoiceVolume(float sliderPercent)
    {
        if (Instance == null) return;
        ActiveVoiceVolume = Instance.ChangeVolume(sliderPercent, MIXER_VOICE);
        VoiceVolume?.Invoke(ActiveVoiceVolume);
    }

    public static void ApplyAvatarVolume(float sliderPercent)
    {
        if (Instance == null) return;
        ActiveAvatarVolume = Instance.ChangeVolume(sliderPercent, MIXER_AVATAR);
        AvatarVolume?.Invoke(ActiveAvatarVolume);
    }

    public static void ApplyPropVolume(float sliderPercent)
    {
        if (Instance == null) return;
        ActivePropVolume = Instance.ChangeVolume(sliderPercent, MIXER_PROP);
        PropVolume?.Invoke(ActivePropVolume);
    }

    public static void ApplyMediaVolume(float sliderPercent)
    {
        ActiveVideoVolume = Mathf.Clamp01(sliderPercent / 100f);
        VideoVolume?.Invoke(ActiveVideoVolume);
    }

    public override void ChangedSettings()
    {
        // Optional: re-apply Active* if your system needs a "push current state" pass.
        // Example:
        // AudioListener.volume = ActiveMainVolume;
        // ChangeVolume(ActiveMenusVolume * 100f, MIXER_MENU); ... etc
    }

    public float ChangeVolume(float value, string mixerParamName)
    {
        float linear = Mathf.Clamp01(value / 100f);

        // Convert linear 0..1 -> dB. Use -80dB floor for "silent".
        float clamped = Mathf.Max(linear, 0.0001f);
        float dB = Mathf.Log10(clamped) * 20f;

        if (Mixer != null)
        {
            Mixer.SetFloat(mixerParamName, dB);
        }
        else
        {
            BasisDebug.LogWarning($"AudioMixer is null; cannot set '{mixerParamName}' to {dB} dB.");
        }

        return linear;
    }
}
