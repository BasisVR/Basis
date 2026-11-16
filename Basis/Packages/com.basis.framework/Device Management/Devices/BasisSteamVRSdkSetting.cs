using Basis.BasisUI;
using Basis.Scripts.Drivers;
using Basis.Scripts.Settings;
using Unity.Scripting.LifecycleManagement;

namespace Basis.Scripts.Device_Management.Devices
{
    [AutoStaticsCleanup]
    public static partial class BasisSteamVRSdkSetting
    {
        private static bool booted;
        private static bool bootedKnown;
        private static bool eyeGazePromptOpen;
        private static bool eyeGazeAskedThisRun;
        private static bool waitingForStartup;
        private static bool restartQueued;
        private static string pendingSerial;
        private static string pendingName;
        public static bool NeedsRestart => bootedKnown && booted != BasisSettingsDefaults.SteamVRNewestSdk.RawValue;
        public static bool ReadForBoot()
        {
            booted = BasisSettingsDefaults.SteamVRNewestSdk.RawValue;
            bootedKnown = true;
            return booted;
        }
        public static void OfferRestart()
        {
            if (!NeedsRestart || !Basis.Scripts.Networking.BasisAppRelaunch.IsSupported || BasisMainMenu.Instance == null)
            {
                return;
            }
            if (BasisMainMenu.Instance.Dialogue)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }
            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("settings.tracker.steamvr.newestSdk.restart.title"),
                BasisLocalization.Get("settings.tracker.steamvr.newestSdk.restart.prompt"),
                BasisLocalization.Get("settings.tracker.steamvr.newestSdk.restart.now"),
                BasisLocalization.Get("settings.tracker.steamvr.newestSdk.restart.later"),
                accepted =>
                {
                    if (accepted) Basis.Scripts.Networking.BasisAppRelaunch.RebootAndReconnect();
                });
        }
        public static void OfferForEyeGazeHeadset(string serial, string headsetName)
        {
            if (eyeGazePromptOpen || eyeGazeAskedThisRun || BasisSafeBoot.SafeMode || BasisSettingsDefaults.SteamVRNewestSdk.RawValue || (bootedKnown && booted))
            {
                return;
            }
            if (BasisSettingsSystem.LoadBool(DecidedKey(serial), false))
            {
                return;
            }
            pendingSerial = serial ?? string.Empty;
            pendingName = headsetName;
            if (BasisStartupPrompts.Finished)
            {
                ShowEyeGazePrompt();
                return;
            }
            if (!waitingForStartup)
            {
                waitingForStartup = true;
                BasisFrameClock.AddRequest();
                BasisFrameClock.OnTick += WaitForStartup;
            }
        }
        private static void WaitForStartup()
        {
            if (!BasisStartupPrompts.Finished)
            {
                return;
            }
            StopWaiting();
            ShowEyeGazePrompt();
        }
        private static void StopWaiting()
        {
            if (!waitingForStartup)
            {
                return;
            }
            waitingForStartup = false;
            BasisFrameClock.OnTick -= WaitForStartup;
            BasisFrameClock.RemoveRequest();
        }
        private static void ShowEyeGazePrompt()
        {
            if (eyeGazePromptOpen || eyeGazeAskedThisRun || BasisSettingsDefaults.SteamVRNewestSdk.RawValue)
            {
                return;
            }
            eyeGazePromptOpen = true;
            eyeGazeAskedThisRun = true;
            string serial = pendingSerial;
            string body = BasisLocalization.Get("settings.tracker.steamvr.eyeGazePrompt.body");
            if (!string.IsNullOrEmpty(pendingName))
            {
                body = BasisLocalization.Get("settings.tracker.steamvr.eyeGazePrompt.detected", pendingName) + "\n\n" + body;
            }
            BasisFeatureDetectedPrompt.Show(BasisLocalization.Get("settings.tracker.steamvr.eyeGazePrompt.title"), body, nameof(BasisSteamVRSdkSetting),
                onDecided: choice =>
                {
                    eyeGazePromptOpen = false;
                    if (choice == BasisFeatureDetectedPrompt.Choice.No)
                    {
                        return;
                    }
                    BasisSettingsSystem.SaveBool(DecidedKey(serial), true);
                    if (choice == BasisFeatureDetectedPrompt.Choice.Enable)
                    {
                        BasisSettingsDefaults.SteamVRNewestSdk.SetValue(true);
                        QueueRestartOffer();
                    }
                },
                onDismissed: () => eyeGazePromptOpen = false);
        }
        private static void QueueRestartOffer()
        {
            if (restartQueued)
            {
                return;
            }
            restartQueued = true;
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += RestartOfferTick;
        }
        private static void RestartOfferTick()
        {
            restartQueued = false;
            BasisFrameClock.OnTick -= RestartOfferTick;
            BasisFrameClock.RemoveRequest();
            OfferRestart();
        }
        private static string DecidedKey(string serial) => $"steamvr.newestsdk.eyegaze.decided.{serial}";
    }
}
