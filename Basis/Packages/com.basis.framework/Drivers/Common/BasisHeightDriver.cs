using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
public static class BasisHeightDriver
{
    public static void ApplyScaleAndHeight()
    {
        RevaluateUnscaledHeight(SMModuleCalibration.HeightMode);
        ApplyScale(SMModuleCalibration.ApplyCustomScale, SMModuleCalibration.SelectedScale);
        ChooseHeightToUse(SMModuleCalibration.HeightMode);
    }
    public static void OnAvatarFBCalibration()
    {
        CapturePlayerHeight();
        ApplyScaleAndHeight();
    }
    /// <summary>
    ///
    /// </summary>
    /// <param name="ScaleAvatar"></param>
    /// <param name="SelectedScale">is in meters</param>
    public static void ApplyScale(bool ScaleAvatar,float SelectedScale)
    {
        // compute and apply scale
        heightScaleFactor = SelectedScale / UnScaledSelectedAvatarHeight;

        BasisDebug.Log($"Applying Scale to Avatar {heightScaleFactor}", BasisDebug.LogTag.Avatar);
        if (!ScaleAvatar)
        {
            heightScaleFactor = 1;
        }
        ApplyAvatarScale(heightScaleFactor);
        BasisLocalPlayer.Instance.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        });
    }
    /// <summary>
    /// 1 = normal
    /// </summary>
    /// <param name="ScaleFactor"></param>
    public static void ApplyAvatarScale(float ScaleFactor)
    {
        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.", BasisDebug.LogTag.Avatar);
            return;
        }
        var avatarDriver = player.LocalAvatarDriver;
        var boneDriver = player.LocalBoneDriver;

        if (avatarDriver == null || boneDriver == null)
        {
            BasisDebug.LogError("Avatar or Bone driver missing; cannot apply custom height.", BasisDebug.LogTag.Avatar);
            return;
        }
        BasisDebug.Log($"Height Scaling Factor is {ScaleFactor}", BasisDebug.LogTag.Avatar);
        avatarDriver.ScaleAvatarModification.SetAvatarheightOverride(ScaleFactor);
        int count = boneDriver.ControlsLength;
        for (int Index = 0; Index < count; Index++)
        {
            //we scale up the local tpose data so it matches (avatar related still)
            BasisLocalBoneControl c = boneDriver.Controls[Index];
            c.TposeLocalScaled.position = c.TposeLocal.position * ScaleFactor;
            c.TposeLocalScaled.rotation = c.TposeLocal.rotation;
            c.ScaledOffset = c.Offset * ScaleFactor;
        }
    }
    /// <summary>
    /// we always capture the right player height as we only use unscaled data.
    /// </summary>
    public static void CapturePlayerHeight()
    {
        BasisLocalHeightCalculator.CalculatePlayerEyeHeight();
        BasisLocalHeightCalculator.CalculatePlayerArmSpan();
    }
    /// <summary>
    /// captures the avatar scale
    /// it does this by first scaling the avatar back to its original size and then up from that.
    /// </summary>
    public static void CaptureAvatarHeight()
    {
        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.", BasisDebug.LogTag.Avatar);
            return;
        }
        var avatarDriver = player.LocalAvatarDriver;
        if (avatarDriver == null)
        {
            BasisDebug.LogError("Avatar or Bone driver missing; cannot apply custom height.", BasisDebug.LogTag.Avatar);
            return;
        }
        float ApplyScale = avatarDriver.ScaleAvatarModification.ApplyScale;
        ApplyAvatarScale(1);//we set the avatar scale to 1 to grab good arm spans
        BasisLocalHeightCalculator.CalculateAvatarEyeHeight();
        BasisLocalHeightCalculator.CalculateAvatarArmSpan();
        BasisLocalHeightCalculator.ValidateEyeToArmSizes();
        ApplyAvatarScale(ApplyScale);
        BasisLocalPlayer.Instance.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        });
    }
    public static void RevaluateUnscaledHeight(BasisSelectedHeightMode Height)
    {
        switch (Height)
        {
            case BasisSelectedHeightMode.ArmSpan:
                UnScaledSelectedAvatarHeight = AvatarArmSpan;
                break;
            case BasisSelectedHeightMode.EyeHeight:
                UnScaledSelectedAvatarHeight = AvatarEyeHeight;
                break;
        }
    }
    /// <summary>
    /// Chooses the active height metrics and scale ratios based on the provided mode.
    /// </summary>
    /// <param name="Height">Selection mode: <see cref="BasisSelectedHeightMode.ArmSpan"/>,
    /// <see cref="BasisSelectedHeightMode.EyeHeight"/>, or <see cref="BasisSelectedHeightMode.Custom"/>.</param>
    public static void ChooseHeightToUse(BasisSelectedHeightMode Height)
    {
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            Height = BasisSelectedHeightMode.EyeHeight;
        }
        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.", BasisDebug.LogTag.Avatar);
            return;
        }
        var avatarDriver = player.LocalAvatarDriver;
        if (avatarDriver == null)
        {
            BasisDebug.LogError("Avatar or Bone driver missing; cannot apply custom height.", BasisDebug.LogTag.Avatar);
            return;
        }
        float ApplyScale = avatarDriver.ScaleAvatarModification.ApplyScale;
        switch (Height)
        {
            case BasisSelectedHeightMode.ArmSpan:
                SelectedPlayerHeight = PlayerArmSpan * ApplyScale;
                SelectedAvatarHeight = AvatarArmSpan * ApplyScale;
                UnScaledSelectedAvatarHeight = AvatarArmSpan;
                break;
            case BasisSelectedHeightMode.EyeHeight:
                SelectedPlayerHeight = PlayerEyeHeight * ApplyScale;
                SelectedAvatarHeight = AvatarEyeHeight * ApplyScale;
                UnScaledSelectedAvatarHeight = AvatarEyeHeight;
                break;
        }

        //1.6 / 1.7 = 
        PlayerToAvatarScale = SelectedPlayerHeight / SelectedAvatarHeight * ApplyScale;
        AvatarToPlayerScale = SelectedAvatarHeight / SelectedPlayerHeight * ApplyScale;
        if (SelectedPlayerHeight <= 0f)
        {
            SelectedPlayerHeight = 1.6f;
        }
        if (SelectedAvatarHeight <= 0f)
        {
            SelectedAvatarHeight = 1.6f;
        }
        if (PlayerToAvatarScale <= 0f)
        {
            PlayerToAvatarScale = 1;
        }
        if (AvatarToPlayerScale <= 0f)
        {
            AvatarToPlayerScale = 1;
        }
        BasisDebug.Log($"Height Mode is {Height} with height {SelectedPlayerHeight} with avatar height {SelectedAvatarHeight} with selected player to default scale {PlayerToAvatarScale} select avatar to avatar scale {AvatarToPlayerScale}", BasisDebug.LogTag.Avatar);
    }
    public static float heightScaleFactor = 1;
    /// <summary>
    /// Fallback height (meters) used when no measurement is available.
    /// not the total height but the eye height
    /// </summary>
    public const float FallbackHeightInMeters = 1.61f;
    /// <summary>
    /// Measured eye height for the player (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
    /// </summary>
    public static float PlayerEyeHeight = FallbackHeightInMeters;
    /// <summary>
    /// Measured eye height for the avatar (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
    /// </summary>
    public static float AvatarEyeHeight = FallbackHeightInMeters;
    /// <summary>
    /// Measured arm span for the player (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
    /// </summary>
    public static float PlayerArmSpan = FallbackHeightInMeters;
    /// <summary>
    /// Measured arm span for the avatar (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
    /// </summary>
    public static float AvatarArmSpan = FallbackHeightInMeters;
    /// <summary>
    /// The player height (meters)"/>.
    /// </summary>
    public static float SelectedPlayerHeight = FallbackHeightInMeters;
    /// <summary>
    /// The avatar height (meters)/>.
    /// </summary>
    public static float SelectedAvatarHeight = FallbackHeightInMeters;
    /// <summary>
    /// The avatar height (meters)/>.
    /// </summary>
    public static float UnScaledSelectedAvatarHeight = FallbackHeightInMeters;
    /// <summary>
    /// The player-to-default scale/>.
    /// </summary>
    public static float PlayerToAvatarScale = 1f;
    /// <summary>
    /// The avatar-to-avatar-default scale currently"/>.
    /// </summary>
    public static float AvatarToPlayerScale = 1f;
}
