using Unity.Scripting.LifecycleManagement;
using Basis;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;

/// <summary>
/// What gets written into a photo file: account-wide preferences relocated here from the
/// framework's BasisSettingsDefaults, since only the camera reads them. The binding keys are
/// unchanged so existing saved values continue to load, and the Chat &amp; UI page's reset still
/// resets them (through <see cref="SettingsProvider.AddPageResetAction"/>) exactly as it did when
/// they lived in the framework. The camera panel's own Reset leaves them alone on purpose — see
/// BasisHandHeldCameraPanelProvider.BuildPhotoMetadataGroup.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisPhotoMetadataSettings
{
    static BasisPhotoMetadataSettings()
    {
        BasisSettingsBindingPostLoad.Register(typeof(BasisPhotoMetadataSettings));
    }

    public const string PhotoTagging_NoOne = "No One";
    public const string PhotoTagging_EveryoneInPhoto = "Everyone In Photo";
    public const string PhotoTagging_JustMe = "Just Me";

    public static BasisSettingsBinding<string> PhotoMetadataTagging = new("photometadatatagging", new BasisPlatformDefault<string>(PhotoTagging_NoOne));

    // Additional photo metadata, all opt-in (off by default).
    public static BasisSettingsBinding<bool> PhotoEmbedCameraSettings = new("photoembedcamerasettings", new BasisPlatformDefault<bool>(false));
    public static BasisSettingsBinding<bool> PhotoEmbedCaptureInfo = new("photoembedcaptureinfo", new BasisPlatformDefault<bool>(false));
    public static BasisSettingsBinding<bool> PhotoEmbedPhotographer = new("photoembedphotographer", new BasisPlatformDefault<bool>(false));
    public static BasisSettingsBinding<bool> PhotoEmbedWorld = new("photoembedworld", new BasisPlatformDefault<bool>(false));
    public static BasisSettingsBinding<bool> PhotoEmbedPersonDetails = new("photoembedpersondetails", new BasisPlatformDefault<bool>(false));

    [RuntimeInitializeOnLoadMethod]
    private static void Register()
    {
        SettingsProvider.AddPageResetAction(SettingsProvider.ChatTabKey, ResetToDefaults);
    }

    public static void ResetToDefaults()
    {
        PhotoMetadataTagging.ResetToDefault();
        PhotoEmbedPersonDetails.ResetToDefault();
        PhotoEmbedCameraSettings.ResetToDefault();
        PhotoEmbedCaptureInfo.ResetToDefault();
        PhotoEmbedPhotographer.ResetToDefault();
        PhotoEmbedWorld.ResetToDefault();
    }
}
