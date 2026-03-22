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

    public static float ActiveMainVolume;
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
    private const string MIXER_MASTER = "Master";
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
                {
                    if (SliderReadOption(optionValue, out float newMain))
                    {
                        BasisDebug.Log($"setting main volume to {newMain}");
                        ActiveMainVolume = ChangeVolume(newMain, MIXER_MASTER);
                        MainVolume?.Invoke(ActiveMainVolume);
                    }
                    break;
                }

            case var s when s == K_MENU_VOLUME:
                {
                    if (SliderReadOption(optionValue, out float newMenus))
                    {
                        BasisDebug.Log($"setting menu volume to {newMenus}");
                        ActiveMenusVolume = ChangeVolume(newMenus, MIXER_MENU);
                        MenusVolume?.Invoke(ActiveMenusVolume);
                    }
                    break;
                }

            case var s when s == K_WORLD_VOLUME:
                {
                    if (SliderReadOption(optionValue, out float newWorld))
                    {
                        BasisDebug.Log($"setting world volume to {newWorld}");
                        ActiveWorldVolume = ChangeVolume(newWorld, MIXER_WORLD);
                        WorldVolume?.Invoke(ActiveWorldVolume);
                    }
                    break;
                }

            case var s when s == K_VOICE_VOLUME:
                {
                    if (SliderReadOption(optionValue, out float newPlayer))
                    {
                        BasisDebug.Log($"setting VOICE volume to {newPlayer}");
                        ActiveVoiceVolume = ChangeVolume(newPlayer, MIXER_VOICE);
                        VoiceVolume?.Invoke(ActiveVoiceVolume);
                    }
                    break;
                }

            case var s when s == K_AVATAR_VOLUME:
                {
                    if (SliderReadOption(optionValue, out float newAvatar))
                    {
                        BasisDebug.Log($"setting AVATAR volume to {newAvatar}");
                        ActiveAvatarVolume = ChangeVolume(newAvatar, MIXER_AVATAR);
                        AvatarVolume?.Invoke(ActiveAvatarVolume);
                    }
                    break;
                }

            case var s when s == K_PROP_VOLUME:
                {
                    if (SliderReadOption(optionValue, out float newProp))
                    {
                        BasisDebug.Log($"setting PROP volume to {newProp}");
                        ActivePropVolume = ChangeVolume(newProp, MIXER_PROP);
                        PropVolume?.Invoke(ActivePropVolume);
                    }
                    break;
                }

            case var s when s == K_MEDIA_VOLUME:
                {
                    if (SliderReadOption(optionValue, out float newVideo))
                    {
                        BasisDebug.Log($"setting media volume to {newVideo}");
                        ActiveVideoVolume = Mathf.Clamp01(newVideo / 100f);
                        VideoVolume?.Invoke(ActiveVideoVolume);
                    }
                    break;
                }
        }
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
