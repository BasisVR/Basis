using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using System;
using System.Globalization;
using UnityEngine;

public class SMDMicrophone : BasisSettingsBase
{
    public static string[] MicrophoneDevices;

    public enum BasisMicrophoneMode { OnActivation = 0, PushToTalk = 1 }

    [Serializable]
    public struct MicSettings
    {
        public string Microphone;
        public float Volume01;

        public bool UseDenoiser;

        public float LimitThreshold;
        public float LimitKnee;

        public float DenoiseMakeupDb;
        public float DenoiseWet;

        public bool UseAGC;
        public float AgcTargetRms;
        public float AgcMaxGainDb;
        public float AgcAttack;
        public float AgcRelease;

        public BasisMicrophoneMode TalkMode;
    }

    // ONE EVENT
    public static event Action<MicSettings> OnMicrophoneSettingsChanged;

    // Current (active-mode) snapshot
    public static MicSettings Current { get; private set; }

    public static string CurrentMode { get; private set; }

    // Consistent prefs key namespace
    private static string P(string mode, string key) => $"{mode}_Mic_{key}";

    private const string K_MIC = "Microphone";
    private const string K_VOL = "Volume01";
    private const string K_DENOISER = "Denoiser";
    private const string K_LIMIT_TH = "LimitThreshold";
    private const string K_LIMIT_KNEE = "LimitKnee";
    private const string K_DN_MK = "DenoiseMakeupDb";
    private const string K_DN_WET = "DenoiseWet";
    private const string K_AGC_ON = "UseAGC";
    private const string K_AGC_TR = "AgcTargetRms";
    private const string K_AGC_MG = "AgcMaxGainDb";
    private const string K_AGC_AT = "AgcAttack";
    private const string K_AGC_RL = "AgcRelease";
    private const string K_TALK = "TalkMode";

    private static MicSettings Defaults()
    {
        string defaultMic = (MicrophoneDevices != null && MicrophoneDevices.Length > 0) ? MicrophoneDevices[0] : "";
        return new MicSettings
        {
            Microphone = defaultMic,
            Volume01 = 1f,
            UseDenoiser = false,
            LimitThreshold = 0.95f,
            LimitKnee = 0.05f,
            DenoiseMakeupDb = 3f,
            DenoiseWet = 1f,
            UseAGC = false,
            AgcTargetRms = 0.06f,
            AgcMaxGainDb = 8f,
            AgcAttack = 0.10f,
            AgcRelease = 0.01f,
            TalkMode = BasisMicrophoneMode.OnActivation
        };
    }

    private static void ClampAndValidate(ref MicSettings s)
    {
        s.Volume01 = Mathf.Clamp01(s.Volume01);
        s.LimitThreshold = Mathf.Clamp01(s.LimitThreshold);
        s.LimitKnee = Mathf.Clamp01(s.LimitKnee);
        s.DenoiseWet = Mathf.Clamp01(s.DenoiseWet);
        s.AgcTargetRms = Mathf.Max(1e-6f, s.AgcTargetRms);
        s.AgcAttack = Mathf.Clamp01(s.AgcAttack);
        s.AgcRelease = Mathf.Clamp01(s.AgcRelease);

        // Validate mic exists
        if (MicrophoneDevices != null && MicrophoneDevices.Length > 0)
        {
            if (string.IsNullOrEmpty(s.Microphone))
                s.Microphone = MicrophoneDevices[0];

            bool exists = false;
            foreach (var d in MicrophoneDevices)
            {
                if (d == s.Microphone) { exists = true; break; }
            }
            if (!exists) s.Microphone = MicrophoneDevices[0];
        }
        else
        {
            s.Microphone = "";
        }
    }

    private static void Emit()
    {
        OnMicrophoneSettingsChanged?.Invoke(Current);
    }

    // Load active mode (sets Current and emits once)
    public static void LoadInMicrophoneData(string mode)
    {
        MicrophoneDevices = Microphone.devices;

        if (string.IsNullOrEmpty(mode))
        {
            BasisDebug.LogError("Missing Device Mode!");
            return;
        }

        CurrentMode = mode;

        var s = Defaults();

        s.Microphone = PlayerPrefs.GetString(P(mode, K_MIC), s.Microphone);
        s.Volume01 = PlayerPrefs.GetFloat(P(mode, K_VOL), s.Volume01);

        s.UseDenoiser = PlayerPrefs.GetInt(P(mode, K_DENOISER), s.UseDenoiser ? 1 : 0) == 1;

        s.LimitThreshold = PlayerPrefs.GetFloat(P(mode, K_LIMIT_TH), s.LimitThreshold);
        s.LimitKnee = PlayerPrefs.GetFloat(P(mode, K_LIMIT_KNEE), s.LimitKnee);

        s.DenoiseMakeupDb = PlayerPrefs.GetFloat(P(mode, K_DN_MK), s.DenoiseMakeupDb);
        s.DenoiseWet = PlayerPrefs.GetFloat(P(mode, K_DN_WET), s.DenoiseWet);

        s.UseAGC = PlayerPrefs.GetInt(P(mode, K_AGC_ON), s.UseAGC ? 1 : 0) == 1;
        s.AgcTargetRms = PlayerPrefs.GetFloat(P(mode, K_AGC_TR), s.AgcTargetRms);
        s.AgcMaxGainDb = PlayerPrefs.GetFloat(P(mode, K_AGC_MG), s.AgcMaxGainDb);
        s.AgcAttack = PlayerPrefs.GetFloat(P(mode, K_AGC_AT), s.AgcAttack);
        s.AgcRelease = PlayerPrefs.GetFloat(P(mode, K_AGC_RL), s.AgcRelease);

        s.TalkMode = (BasisMicrophoneMode)PlayerPrefs.GetInt(P(mode, K_TALK), (int)s.TalkMode);

        ClampAndValidate(ref s);
        Current = s;

        Emit();
    }

    // Save helper: writes Current to prefs and emits once
    private static void SaveCurrent()
    {
        string mode = CurrentMode;
        if (string.IsNullOrEmpty(mode))
        {
            BasisDebug.LogError("Missing Device Mode!");
            return;
        }

        var s = Current;
        ClampAndValidate(ref s);
        Current = s;

        PlayerPrefs.SetString(P(mode, K_MIC), s.Microphone);
        PlayerPrefs.SetFloat(P(mode, K_VOL), s.Volume01);

        PlayerPrefs.SetInt(P(mode, K_DENOISER), s.UseDenoiser ? 1 : 0);

        PlayerPrefs.SetFloat(P(mode, K_LIMIT_TH), s.LimitThreshold);
        PlayerPrefs.SetFloat(P(mode, K_LIMIT_KNEE), s.LimitKnee);

        PlayerPrefs.SetFloat(P(mode, K_DN_MK), s.DenoiseMakeupDb);
        PlayerPrefs.SetFloat(P(mode, K_DN_WET), s.DenoiseWet);

        PlayerPrefs.SetInt(P(mode, K_AGC_ON), s.UseAGC ? 1 : 0);
        PlayerPrefs.SetFloat(P(mode, K_AGC_TR), s.AgcTargetRms);
        PlayerPrefs.SetFloat(P(mode, K_AGC_MG), s.AgcMaxGainDb);
        PlayerPrefs.SetFloat(P(mode, K_AGC_AT), s.AgcAttack);
        PlayerPrefs.SetFloat(P(mode, K_AGC_RL), s.AgcRelease);

        PlayerPrefs.SetInt(P(mode, K_TALK), (int)s.TalkMode);

        PlayerPrefs.Save();

        Emit();
    }

    // Public “setters” mutate Current then SaveCurrent()

    public static void SetMicrophone(string mic)
    {
        var s = Current;
        s.Microphone = mic;
        Current = s;
        SaveCurrent();
    }

    public static void SetVolume(float volume01)
    {
        var s = Current;
        s.Volume01 = volume01;
        Current = s;
        SaveCurrent();
    }

    public static void SetDenoiser(bool enabled)
    {
        var s = Current;
        s.UseDenoiser = enabled;
        Current = s;
        SaveCurrent();
    }

    public static void SetLimiter(float threshold, float knee)
    {
        var s = Current;
        s.LimitThreshold = threshold;
        s.LimitKnee = knee;
        Current = s;
        SaveCurrent();
    }

    public static void SetDenoiseParams(float makeupDb, float wet)
    {
        var s = Current;
        s.DenoiseMakeupDb = makeupDb;
        s.DenoiseWet = wet;
        Current = s;
        SaveCurrent();
    }

    public static void SetAgcEnabled(bool enabled)
    {
        var s = Current;
        s.UseAGC = enabled;
        Current = s;
        SaveCurrent();
    }

    public static void SetAgcParams(float targetRms, float maxGainDb, float attack, float release)
    {
        var s = Current;
        s.AgcTargetRms = targetRms;
        s.AgcMaxGainDb = maxGainDb;
        s.AgcAttack = attack;
        s.AgcRelease = release;
        Current = s;
        SaveCurrent();
    }

    public static void SetTalkMode(BasisMicrophoneMode mode)
    {
        var s = Current;
        s.TalkMode = mode;
        Current = s;
        SaveCurrent();
    }

    // ---- Hook to your settings system (BindingKey mapping stays the same) ----
    private static string B_LIMIT_THRESHOLD => BasisSettingsDefaults.LimitThreshold.BindingKey;
    private static string B_LIMIT_KNEE => BasisSettingsDefaults.LimitKnee.BindingKey;
    private static string B_DENOISE_MAKEUP => BasisSettingsDefaults.DenoiseMakeupDb.BindingKey;
    private static string B_DENOISE_WET => BasisSettingsDefaults.DenoiseWet.BindingKey;

    private static string B_AGC => BasisSettingsDefaults.UseAutomaticGain.BindingKey;
    private static string B_AGC_TARGET => BasisSettingsDefaults.AgcTargetRms.BindingKey;
    private static string B_AGC_MAXGAIN => BasisSettingsDefaults.AgcMaxGainDb.BindingKey;
    private static string B_AGC_ATTACK => BasisSettingsDefaults.AgcAttack.BindingKey;
    private static string B_AGC_RELEASE => BasisSettingsDefaults.AgcRelease.BindingKey;

    private static string B_DENOISER => BasisSettingsDefaults.MicrophoneDenoiser.BindingKey;
    private static string B_MIC_MODE => BasisSettingsDefaults.MicrophoneMode.BindingKey;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        string mode = BasisDeviceManagement.StaticCurrentMode;
        if (string.IsNullOrEmpty(mode))
        {
            BasisDebug.LogError("Missing Device Mode!");
            return;
        }

        // Make sure CurrentMode/Current are initialized for this mode
        if (CurrentMode != mode) LoadInMicrophoneData(mode);

        var st = NumberStyles.Float | NumberStyles.AllowThousands;
        var ci = CultureInfo.InvariantCulture;

        try
        {
            switch (matchedSettingName)
            {
                case var s when s == B_DENOISER:
                    if (bool.TryParse(optionValue, out bool den)) SetDenoiser(den);
                    break;

                case var s when s == B_LIMIT_THRESHOLD:
                    if (float.TryParse(optionValue, st, ci, out float th)) SetLimiter(th, Current.LimitKnee);
                    break;

                case var s when s == B_LIMIT_KNEE:
                    if (float.TryParse(optionValue, st, ci, out float kn)) SetLimiter(Current.LimitThreshold, kn);
                    break;

                case var s when s == B_DENOISE_MAKEUP:
                    if (float.TryParse(optionValue, st, ci, out float mk)) SetDenoiseParams(mk, Current.DenoiseWet);
                    break;

                case var s when s == B_DENOISE_WET:
                    if (float.TryParse(optionValue, st, ci, out float wet)) SetDenoiseParams(Current.DenoiseMakeupDb, wet);
                    break;

                case var s when s == B_AGC:
                    if (bool.TryParse(optionValue, out bool agcOn)) SetAgcEnabled(agcOn);
                    break;

                case var s when s == B_AGC_TARGET:
                    if (float.TryParse(optionValue, st, ci, out float tr))
                        SetAgcParams(tr, Current.AgcMaxGainDb, Current.AgcAttack, Current.AgcRelease);
                    break;

                case var s when s == B_AGC_MAXGAIN:
                    if (float.TryParse(optionValue, st, ci, out float mg))
                        SetAgcParams(Current.AgcTargetRms, mg, Current.AgcAttack, Current.AgcRelease);
                    break;

                case var s when s == B_AGC_ATTACK:
                    if (float.TryParse(optionValue, st, ci, out float att))
                        SetAgcParams(Current.AgcTargetRms, Current.AgcMaxGainDb, att, Current.AgcRelease);
                    break;

                case var s when s == B_AGC_RELEASE:
                    if (float.TryParse(optionValue, st, ci, out float rel))
                        SetAgcParams(Current.AgcTargetRms, Current.AgcMaxGainDb, Current.AgcAttack, rel);
                    break;

                case var s when s == B_MIC_MODE:
                    if (Enum.TryParse<BasisMicrophoneMode>(optionValue.Replace(" ", ""), true, out var m))
                        SetTalkMode(m);
                    break;
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"ValidSettingsChange error for '{matchedSettingName}': {ex}");
        }
    }

    public override void ChangedSettings() { }
}
