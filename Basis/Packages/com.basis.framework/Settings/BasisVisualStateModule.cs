using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

[AutoStaticsCleanup]
public partial class BasisVisualStateModule : BasisSettingsBase
{
    public static string AdaptiveCircleId = "Adaptive Circle Display.prefab";

    private static readonly Color AvatarColor = new Color(1f, 0.84f, 0.2f, 0.45f);
    private static readonly Color HearingColor = new Color(0.25f, 0.7f, 1f, 0.45f);
    private const float MicrophoneAlpha = 0.45f;

    private const float AvatarYOffset = 0.01f;
    private const float HearingYOffset = 0.02f;
    private const float MicrophoneYOffset = 0.03f;

    private class RangeCircle
    {
        public GameObject Instance;
        public BasisAdaptiveCircle Circle;
        public AsyncOperationHandle<GameObject> Handle;
        public float Radius;
        public Color Color;

        public void ApplyNow()
        {
            if (Circle != null)
            {
                Circle.Apply(Radius, Color);
            }
        }
    }

    private static RangeCircle _avatar;
    private static RangeCircle _hearing;
    private static RangeCircle _microphone;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (!bool.TryParse(optionValue, out bool on))
        {
            return;
        }

        if (matchedSettingName == BasisSettingsDefaults.AvatarRangeIndicator.BindingKey)
        {
            SetAvatar(on);
        }
        else if (matchedSettingName == BasisSettingsDefaults.HearingRangeIndicator.BindingKey)
        {
            SetHearing(on);
        }
        else if (matchedSettingName == BasisSettingsDefaults.MicrophoneRangeIndicator.BindingKey)
        {
            SetMicrophone(on);
        }
    }

    public override void ChangedSettings()
    {
    }

    private static void SetAvatar(bool on)
    {
        SMModuleDistanceBasedReductions.OnAvatarRangeChanged -= OnAvatarRangeChanged;
        if (on)
        {
            _avatar = Show(_avatar, Radius(SMModuleDistanceBasedReductions.AvatarRange), AvatarColor, AvatarYOffset);
            if (_avatar != null)
            {
                SMModuleDistanceBasedReductions.OnAvatarRangeChanged += OnAvatarRangeChanged;
            }
        }
        else
        {
            Hide(ref _avatar);
        }
    }

    private static void SetHearing(bool on)
    {
        SMModuleDistanceBasedReductions.OnHearingRangeChanged -= OnHearingRangeChanged;
        if (on)
        {
            _hearing = Show(_hearing, Radius(SMModuleDistanceBasedReductions.HearingRange), HearingColor, HearingYOffset);
            if (_hearing != null)
            {
                SMModuleDistanceBasedReductions.OnHearingRangeChanged += OnHearingRangeChanged;
            }
        }
        else
        {
            Hide(ref _hearing);
        }
    }

    private static void SetMicrophone(bool on)
    {
        SMModuleDistanceBasedReductions.OnMicrophoneRangeChanged -= OnMicrophoneRangeChanged;
#if !BASIS_DISABLE_MICROPHONE
        BasisLocalMicrophoneIconDriver.OnColorChanged -= OnMicrophoneColorChanged;
#endif
        if (on)
        {
            _microphone = Show(_microphone, Radius(SMModuleDistanceBasedReductions.MicrophoneRange), CurrentMicrophoneColor(), MicrophoneYOffset);
            if (_microphone != null)
            {
                SMModuleDistanceBasedReductions.OnMicrophoneRangeChanged += OnMicrophoneRangeChanged;
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneIconDriver.OnColorChanged += OnMicrophoneColorChanged;
#endif
            }
        }
        else
        {
            Hide(ref _microphone);
        }
    }

    private static void OnAvatarRangeChanged(float squared)
    {
        if (_avatar != null)
        {
            _avatar.Radius = Radius(squared);
            _avatar.ApplyNow();
        }
    }

    private static void OnHearingRangeChanged(float squared)
    {
        if (_hearing != null)
        {
            _hearing.Radius = Radius(squared);
            _hearing.ApplyNow();
        }
    }

    private static void OnMicrophoneRangeChanged(float squared)
    {
        if (_microphone != null)
        {
            _microphone.Radius = Radius(squared);
            _microphone.ApplyNow();
        }
    }

#if !BASIS_DISABLE_MICROPHONE
    private static void OnMicrophoneColorChanged(Color color)
    {
        if (_microphone != null)
        {
            color.a = MicrophoneAlpha;
            _microphone.Color = color;
            _microphone.ApplyNow();
        }
    }
#endif

    private static Color CurrentMicrophoneColor()
    {
#if !BASIS_DISABLE_MICROPHONE
        Color color = BasisLocalMicrophoneIconDriver.LastColor;
        color.a = MicrophoneAlpha;
        return color;
#else
        return new Color(0.5f, 0.5f, 0.5f, MicrophoneAlpha);
#endif
    }

    private static float Radius(float squared)
    {
        return Mathf.Sqrt(Mathf.Max(0f, squared));
    }

    private static RangeCircle Show(RangeCircle existing, float radius, Color color, float yOffset)
    {
        if (BasisLocalPlayer.Instance == null)
        {
            return existing;
        }

        RangeCircle rc = existing ?? new RangeCircle();
        if (rc.Instance == null)
        {
            rc.Handle = Addressables.LoadAssetAsync<GameObject>(AdaptiveCircleId);
            GameObject prefab = rc.Handle.WaitForCompletion();
            rc.Instance = Object.Instantiate(prefab, BasisLocalPlayer.Instance.transform);
            rc.Instance.transform.localPosition = new Vector3(0f, yOffset, 0f);
            rc.Instance.TryGetComponent(out rc.Circle);
        }
        rc.Radius = radius;
        rc.Color = color;
        rc.ApplyNow();
        return rc;
    }

    private static void Hide(ref RangeCircle rc)
    {
        if (rc == null)
        {
            return;
        }
        if (rc.Instance != null)
        {
            Object.Destroy(rc.Instance);
        }
        if (rc.Handle.IsValid())
        {
            Addressables.Release(rc.Handle);
        }
        rc = null;
    }
}
