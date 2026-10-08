using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using UnityEngine;

[AutoStaticsCleanup]
public partial class SMModuleSitStand : BasisSettingsBase
{
    public static bool IsSteatedMode = false;
    public static float MissingHeightDelta = 0;

    /// <summary>
    /// The persisted seatedmode setting must be re-applied once the local player actually exists:
    /// the boot settings replay can reach this module BEFORE the player/HMD are ready, which either
    /// half-applied seated mode (flag set with a ZERO lift — the seated eye poll had no device and
    /// fell back to the same value the delta subtracts) or missed entirely if the module woke after
    /// the replay. Either way the mode selection wasn't restored between sessions.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void HookLocalPlayerInitialized()
    {
        Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.OnLocalPlayerInitialized -= ReapplyStoredMode;
        Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.OnLocalPlayerInitialized += ReapplyStoredMode;
    }

    private static void ReapplyStoredMode()
    {
        string stored = BasisSettingsDefaults.SitStand.RawValue;
        stored = string.IsNullOrEmpty(stored) ? string.Empty : stored.ToLower();

        // Force a clean re-application against the live player: a pre-player replay may have left
        // the flag set with a zero lift.
        IsSteatedMode = false;
        MissingHeightDelta = 0;

        if (stored == SettingsProviderIK.SeatedMode_Seated.ToLower())
        {
            BasisDebug.Log("Reapplying persisted Seated Mode for the initialized player", BasisDebug.LogTag.Avatar);
            var player = Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.Instance;
            if (player != null)
            {
                // One frame late so the HMD device has attached — the seated lift is sized from a live
                // eye poll, and polling before the device exists yields a zero lift.
                player.ExecuteNextFrame(() =>
                {
                    IsSteatedMode = false;
                    MissingHeightDelta = 0;
                    ApplySeated();
                });
            }
            else
            {
                ApplySeated();
            }
        }
        // Standing is the cleared state — the normal boot/avatar-load flow resolves heights itself.
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName != BasisSettingsDefaults.SitStand.BindingKey)
        {
            return;
        }
        if (optionValue == SettingsProviderIK.SeatedMode_Standing.ToLower())
        {
            ApplyStanding();
        }
        else if (optionValue == SettingsProviderIK.SeatedMode_Seated.ToLower())
        {
            if (!IsSteatedMode)
            {
                ApplySeated();
            }
        }
    }

    private static void ApplyStanding()
    {
        BasisDebug.Log($"Mode Set To Standing Mode");
        bool wasSeated = IsSteatedMode;
        MissingHeightDelta = 0;
        IsSteatedMode = false;
        if (wasSeated && Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.Instance != null)
        {
            // Restore the standing eye height (persisted/genuine when available; live poll for a
            // fresh user) and re-resolve heights so the height-changed callback fires — camera
            // scale and the menu reposition to the un-lifted tracking space.
            BasisHeightDriver.CapturePlayerHeight(recaptureEyeHeight: false);
            BasisHeightDriver.ApplyScaleAndHeight();
            BasisHeightDriver.ScheduleHeightChangeCallback(BasisHeightDriver.HeightModeChange.OnSitStandChanged);
        }
    }

    private static void ApplySeated()
    {
        BasisHeightDriver.CapturePlayerHeight();
        MissingHeightDelta = BasisHeightDriver.FallbackHeightInMeters - BasisHeightDriver.PlayerEyeHeight;
        IsSteatedMode = true;
        BasisDebug.Log($"Mode Set To Seated Mode {MissingHeightDelta}");
        if (Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.Instance != null)
        {
            // Now that seated mode is active, the denominator becomes the virtual standing
            // eye (the seated capture above measured the real seated eye only to size the
            // lift). Re-resolve so DeviceScale matches the lifted space, and fire the
            // height-changed callback — without it the menu stayed at the pre-lift height.
            BasisHeightDriver.CapturePlayerHeight();
            BasisHeightDriver.ApplyScaleAndHeight();
            BasisHeightDriver.ScheduleHeightChangeCallback(BasisHeightDriver.HeightModeChange.OnSitStandChanged);
        }
    }

    public override void ChangedSettings()
    {
    }
}
