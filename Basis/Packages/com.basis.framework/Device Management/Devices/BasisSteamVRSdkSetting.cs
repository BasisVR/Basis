using Basis.BasisUI;
using Unity.Scripting.LifecycleManagement;

namespace Basis.Scripts.Device_Management.Devices
{
    [AutoStaticsCleanup]
    public static partial class BasisSteamVRSdkSetting
    {
        private static bool booted;
        private static bool bootedKnown;
        public static bool NeedsRestart => bootedKnown && booted != BasisSettingsDefaults.SteamVRNewestSdk.RawValue;
        public static bool ReadForBoot()
        {
            booted = BasisSettingsDefaults.SteamVRNewestSdk.RawValue;
            bootedKnown = true;
            return booted;
        }
    }
}
