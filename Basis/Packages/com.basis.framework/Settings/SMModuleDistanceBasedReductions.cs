using System;
using UnityEngine;
using Basis.BasisUI;

public class SMModuleDistanceBasedReductions : BasisSettingsBase
{
    private static float _microphoneRange = 25f;
    private static float _hearingRange = 25f;
    private static float _avatarRange = 25f;
    private static float _meshLod = 25f;
    private static string K_MIC_RANGE => BasisSettingsDefaults.MicrophoneRange.BindingKey;   // "microphonerange"
    private static string K_HEARING_RANGE => BasisSettingsDefaults.HearingRange.BindingKey;     // "hearingrange"
    private static string K_AVATAR_RANGE => BasisSettingsDefaults.AvatarRange.BindingKey;      // "avatarrange"
    private static string K_AVATAR_MESH_LOD => BasisSettingsDefaults.AvatarMeshLOD.BindingKey;    // "avatarmeshlod"
    private static string K_GLOBAL_MESH_LOD => BasisSettingsDefaults.GlobalMeshLOD.BindingKey;    // "global meshlod" (note space!)
    public static event Action<float> OnMicrophoneRangeChanged;
    public static event Action<float> OnHearingRangeChanged;
    public static event Action<float> OnAvatarRangeChanged;
    public static event Action<float> OnMeshLodChanged;
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
    private static void SetAndNotify(ref float field, float value, Action<float> changedEvent)
    {
        field = value;
        changedEvent?.Invoke(value);
    }
    private static bool TryReadSlider(string optionValue, out float raw) => StaticSliderReadOption(optionValue, out raw);
#if UNITY_SERVER
    private static float ServerSafeDistance(float _) => 0f;
#else
    private static float SquaredDistance(float v) => v * v;
#endif
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
                ApplyDistanceSetting(optionValue, "MicrophoneRange", v => MicrophoneRange = v);
                break;

            case var s when s == K_HEARING_RANGE:
                ApplyDistanceSetting(optionValue, "HearingRange", v => HearingRange = v);
                break;

            case var s when s == K_AVATAR_RANGE:
                ApplyDistanceSetting(optionValue, "AvatarRange", v => AvatarRange = v);
                break;

            case var s when s == K_AVATAR_MESH_LOD:
                ApplyDistanceSetting(optionValue, "MeshLod", v => MeshLod = v);
                break;

            case var s when s == K_GLOBAL_MESH_LOD:
                if (TryReadSlider(optionValue, out var globalLod))
                {
                    QualitySettings.meshLodThreshold = globalLod;
                    LogDistanceSetting("Global Mesh LOD", globalLod);
                }
                break;
        }
    }

    private static void ApplyDistanceSetting(string optionValue, string label, Action<float> assign)
    {
        if (!TryReadSlider(optionValue, out var raw))
        {
            return;
        }

#if UNITY_SERVER
        assign(ServerSafeDistance(raw));
        LogDistanceSetting(label, 0f);
#else
        var squared = SquaredDistance(raw);
        assign(squared);
        LogDistanceSetting(label, squared);
#endif
    }
}
