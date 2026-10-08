using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using System;
using UnityEngine;

[AutoStaticsCleanup]
public partial class SMModuleDistanceBasedReductions : BasisSettingsBase
{
    private static float _microphoneRange = 25f * 25f;
    private static float _hearingRange = 25f * 25f;
    private static float _microphoneRangeCapMeters;
    private static float _hearingRangeCapMeters;
    private static float _avatarRange = 25f * 25f;
    private static float _meshLod = 25f;
    private static int _maxVisibleAvatars = 0;
    private static bool _UsemaxVisibleAvatars = false;
    private static int _maxAudioSources = 0;
    private static bool _useMaxAudioSources = false;

    /// <summary>
    /// Per-LOD base skip rates. Multiplied by PoseLODBias to produce the actual skip counts.
    /// Index = LOD level (0-3). LOD 0 always updates every frame.
    /// </summary>
    [NoAutoStaticsCleanup] private static readonly float[] PoseSkipBase = { 0f, 0.25f, 0.75f, 1f };

    /// <summary>
    /// Computed skip rates. Updated when PoseLODBias changes.
    /// Index = LOD level (0-3). Value = frames to skip between pose updates.
    /// </summary>
    [NoAutoStaticsCleanup] public static readonly byte[] PoseSkipByLod = { 0, 0, 0, 0 };

    private static float _poseLODBias = 0f;

    /// <summary>
    /// Controls how aggressively distant players skip pose updates.
    /// 0 = off (all players update every frame).
    /// Higher = more frames skipped for distant LOD levels.
    /// The slider value multiplies the per-LOD base rates.
    /// </summary>
    public static float PoseLODBias
    {
        get => _poseLODBias;
        private set
        {
            _poseLODBias = value;
            // Recompute skip rates: LOD 0 always 0, others scale with bias.
            // Ceil, not round — rounding sent every fractional rate below 0.5 back to zero, so
            // LOD 1 (base 0.25) skipped nothing until bias 3 and the slider looked inert.
            for (int i = 0; i < 4; i++)
            {
                PoseSkipByLod[i] = (byte)Mathf.Clamp(Mathf.CeilToInt(PoseSkipBase[i] * value), 0, 255);
            }
            OnPoseLODChanged?.Invoke(value);
        }
    }
    public static event Action<float> OnPoseLODChanged;
    private static string K_POSE_LOD => BasisSettingsDefaults.PoseLOD.BindingKey;
    private static string K_MIC_RANGE => BasisSettingsDefaults.MicrophoneRange.BindingKey;   // "microphonerange"
    private static string K_HEARING_RANGE => BasisSettingsDefaults.HearingRange.BindingKey;     // "hearingrange"
    private static string K_AVATAR_RANGE => BasisSettingsDefaults.AvatarRange.BindingKey;      // "avatarrange"
    private static string K_AVATAR_MESH_LOD => BasisSettingsDefaults.AvatarMeshLOD.BindingKey;    // "avatarmeshlod"
    private static string K_GLOBAL_MESH_LOD => BasisSettingsDefaults.GlobalMeshLOD.BindingKey;    // "global meshlod" (note space!)
    private static string K_MAX_VISIBLE_AVATARS => BasisSettingsDefaults.MaxVisibleAvatars.BindingKey; // "maxvisibleavatars"
    private static string K_USEMAX_VISIBLE_AVATARS => BasisSettingsDefaults.UseMaxVisibleAvatars.BindingKey; // "usemaxvisibleavatars"
    private static string K_MAX_AUDIO_SOURCES => BasisSettingsDefaults.MaxAudioSources.BindingKey; // "maxaudiosources"
    private static string K_USEMAX_AUDIO_SOURCES => BasisSettingsDefaults.UseMaxAudioSources.BindingKey; // "usemaxaudiosources"
    public static event Action<float> OnMicrophoneRangeChanged;
    public static event Action<float> OnHearingRangeChanged;
    public static event Action<float> OnAvatarRangeChanged;
    public static event Action<float> OnMeshLodChanged;
    public static event Action<int> OnMaxVisibleAvatarsChanged;
    public static event Action<bool> OnUseMaxVisibleAvatarsChanged;
    public static event Action<int> OnMaxAudioSourcesChanged;
    public static event Action<bool> OnUseMaxAudioSourcesChanged;
    public static float MicrophoneRange
    {
        get => _microphoneRange;
        private set => SetAndNotify(ref _microphoneRange, value, OnMicrophoneRangeChanged);
    }
    public static float HearingRange
    {
        get => _hearingRange;
        private set => SetAndNotify(ref _hearingRange, value, OnHearingRangeChanged);
    }
    public static void SetMicrophoneRangeCapMeters(float meters)
    {
        _microphoneRangeCapMeters = meters < 0f ? 0f : meters;
        ReapplyMicrophoneRange();
    }
    public static void SetHearingRangeCapMeters(float meters)
    {
        _hearingRangeCapMeters = meters < 0f ? 0f : meters;
        ReapplyHearingRange();
    }
    private static float CapMeters(float rawMeters, float capMeters)
        => capMeters > 0f && rawMeters > capMeters ? capMeters : rawMeters;
    private static void ReapplyMicrophoneRange()
    {
        float raw = CapMeters(BasisSettingsDefaults.MicrophoneRange.RawValue, _microphoneRangeCapMeters);
        MicrophoneRange = SquaredDistance(raw);
    }
    private static void ReapplyHearingRange()
    {
#if UNITY_SERVER
        HearingRange = 0f;
#else
        float raw = CapMeters(BasisSettingsDefaults.HearingRange.RawValue, _hearingRangeCapMeters);
        HearingRange = SquaredDistance(raw);
#endif
    }
    public static float AvatarRange
    {
        get => _avatarRange;
        private set => SetAndNotify(ref _avatarRange, value, OnAvatarRangeChanged);
    }
    public static float MeshLod
    {
        get => _meshLod;
        private set => SetAndNotify(ref _meshLod, value, OnMeshLodChanged);
    }
    public static bool UseMaxVisibleAvatars
    {
        get => _UsemaxVisibleAvatars;
        private set
        {
            if (_UsemaxVisibleAvatars != value)
            {
                _UsemaxVisibleAvatars = value;
                OnUseMaxVisibleAvatarsChanged?.Invoke(value);
            }
        }
    }
    /// <summary>
    /// Maximum number of remote avatars allowed to show their real model.
    /// 0 = unlimited. Players beyond this cap use the fallback avatar.
    /// </summary>
    public static int MaxVisibleAvatars
    {
        get => _maxVisibleAvatars;
        private set
        {
            if (_maxVisibleAvatars != value)
            {
                _maxVisibleAvatars = value;
                OnMaxVisibleAvatarsChanged?.Invoke(value);
            }
        }
    }
    public static bool UseMaxAudioSources
    {
        get => _useMaxAudioSources;
        private set
        {
            if (_useMaxAudioSources != value)
            {
                _useMaxAudioSources = value;
                OnUseMaxAudioSourcesChanged?.Invoke(value);
            }
        }
    }
    /// <summary>
    /// Maximum number of remote players allowed to have active audio sources.
    /// 0 = unlimited. Players beyond this cap lose their audio source.
    /// Closest players get priority; currently-active sources are sticky to prevent popping.
    /// </summary>
    public static int MaxAudioSources
    {
        get => _maxAudioSources;
        private set
        {
            if (_maxAudioSources != value)
            {
                _maxAudioSources = value;
                OnMaxAudioSourcesChanged?.Invoke(value);
            }
        }
    }
    private static void SetAndNotify(ref float field, float value, Action<float> changedEvent)
    {
        field = value;
        changedEvent?.Invoke(value);
    }
    private static bool TryReadSlider(string optionValue, out float raw) => StaticSliderReadOption(optionValue, out raw);
#if UNITY_SERVER
    private static float ServerSafeDistance(float _) => 0f;
#endif
    private static float SquaredDistance(float v) => v * v;
    private static void LogDistanceSetting(string label, float value) => BasisDebug.Log($"{label} {value}");
    public override void ChangedSettings()
    {
        // Intentionally left blank (base contract).
    }
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName)
        {
            case var s when s == K_MIC_RANGE:
                ApplyDistanceSetting(optionValue, "MicrophoneRange", v => MicrophoneRange = v, false, _microphoneRangeCapMeters);
                break;

            case var s when s == K_HEARING_RANGE:
                ApplyDistanceSetting(optionValue, "HearingRange", v => HearingRange = v, true, _hearingRangeCapMeters);
                break;

            case var s when s == K_AVATAR_RANGE:
                ApplyDistanceSetting(optionValue, "AvatarRange", v => AvatarRange = v, true);
                break;

            case var s when s == K_AVATAR_MESH_LOD:
                ApplyDistanceSetting(optionValue, "MeshLod", v => MeshLod = v, true);
                break;

            case var s when s == K_GLOBAL_MESH_LOD:
                if (TryReadSlider(optionValue, out var globalLod))
                {
                    QualitySettings.meshLodThreshold = globalLod;
                    LogDistanceSetting("Global Mesh LOD", globalLod);
                }
                break;

            case var s when s == K_MAX_VISIBLE_AVATARS:
                if (TryReadSlider(optionValue, out var maxAv))
                {
                    MaxVisibleAvatars = (int)maxAv;
                    LogDistanceSetting("MaxVisibleAvatars", maxAv);
                }
                break;
            case var s when s == K_USEMAX_VISIBLE_AVATARS:
                if (bool.TryParse(optionValue, out bool usemax))
                {
                    UseMaxVisibleAvatars = usemax;
                    BasisDebug.Log($"Use Max Visible Avatars {usemax}");
                }
                break;
            case var s when s == K_MAX_AUDIO_SOURCES:
                if (TryReadSlider(optionValue, out var maxAudio))
                {
                    MaxAudioSources = (int)maxAudio;
                    LogDistanceSetting("MaxAudioSources", maxAudio);
                }
                break;
            case var s when s == K_USEMAX_AUDIO_SOURCES:
                if (bool.TryParse(optionValue, out bool useMaxAudio))
                {
                    UseMaxAudioSources = useMaxAudio;
                    BasisDebug.Log($"Use Max Audio Sources {useMaxAudio}");
                }
                break;
            case var s when s == K_POSE_LOD:
                if (TryReadSlider(optionValue, out var poseLodBias))
                {
                    PoseLODBias = poseLodBias;
                    BasisDebug.Log($"Pose LOD Bias {poseLodBias} -> skip rates [{PoseSkipByLod[0]},{PoseSkipByLod[1]},{PoseSkipByLod[2]},{PoseSkipByLod[3]}]");
                }
                break;
        }
    }

    private static void ApplyDistanceSetting(string optionValue, string label, Action<float> assign, bool IfServerUseZero, float capMeters = 0f)
    {
        if (!TryReadSlider(optionValue, out var raw))
        {
            return;
        }

        raw = CapMeters(raw, capMeters);
#if UNITY_SERVER
        if (IfServerUseZero)
        {
            assign(ServerSafeDistance(raw));
            LogDistanceSetting(label, 0f);
        }
        else
        {
            var squared = SquaredDistance(raw);
            assign(squared);
            LogDistanceSetting(label, squared);
        }
#else
        var squared = SquaredDistance(raw);
        assign(squared);
        LogDistanceSetting(label, squared);
#endif
    }
}
