using System;
using Basis.Network.Core;

namespace BasisNetworkServer.Security
{
    public static class BasisServerProtectionSettings
    {
        public const string Logins = "Logins", Relays = "Relays", Rates = "Rate limits", Caps = "Per-player caps", Reports = "Error reports", Logging = "Logging";
        private static bool registered;
        public static void RegisterAll()
        {
            if (registered) return;
            registered = true;
            LNLNetManager.ConnectCookiePressure = () =>
            {
                int max = NetworkServer.Configuration?.MaxPendingLogins ?? 0;
                return max > 0 && BasisPendingLoginGate.Pending >= Math.Max(1, max / 2);
            };
            Add(nameof(Configuration.MaxPendingLoginsPerAddress), Logins, 0, 4096);
            Add(nameof(Configuration.MaxPendingLogins), Logins, 0, 65535);
            Add(nameof(Configuration.MaxSceneRelayMegabitsPerSecondPerPlayer), Relays, 0, 100000);
            Add(nameof(Configuration.MaxAvatarRelayMegabitsPerSecondPerPlayer), Relays, 0, 100000);
            Add(nameof(Configuration.MaxRelayPayloadBytes), Relays, 0, 64 * 1024 * 1024);
            Add(nameof(Configuration.AvatarChangesPerSecondPerPlayer), Rates, 0, 1000);
            Add(nameof(Configuration.AvatarChangeBurstPerPlayer), Rates, 0, 100000);
            Add(nameof(Configuration.OwnershipRequestsPerSecondPerPlayer), Rates, 0, 100000);
            Add(nameof(Configuration.OwnershipRequestBurstPerPlayer), Rates, 0, 1000000);
            Add(nameof(Configuration.LoadResourcesPerSecondPerPlayer), Rates, 0, 10000);
            Add(nameof(Configuration.LoadResourceBurstPerPlayer), Rates, 0, 1000000);
            Add(nameof(Configuration.StateChangesPerSecondPerPlayer), Rates, 0, 1000);
            Add(nameof(Configuration.StateChangeBurstPerPlayer), Rates, 0, 100000);
            Add(nameof(Configuration.CameraEventsPerSecondPerPlayer), Rates, 0, 1000);
            Add(nameof(Configuration.CameraEventBurstPerPlayer), Rates, 0, 100000);
            Add(nameof(Configuration.MaxOwnershipEntries), Caps, 0, 1000000);
            Add(nameof(Configuration.MaxLoadedResourcesPerPlayer), Caps, 1, 1000000);
            Add(nameof(Configuration.MaxNetworkIdsPerPlayer), Caps, 1, 65536);
            Add(nameof(Configuration.MaxResourceIdLength), Caps, 0, 65535);
            Add(nameof(Configuration.MaxResourcePasswordLength), Caps, 0, 65535);
            Add(nameof(Configuration.MaxResourceUrlLength), Caps, 0, 65535);
            Add(nameof(Configuration.CrashReportsPerSecondPerPlayer), Reports, 0, 1000);
            Add(nameof(Configuration.CrashReportBurstPerPlayer), Reports, 0, 100000);
            Add(nameof(Configuration.MaxCrashReportKilobytesPerPlayer), Reports, 1, 1024 * 1024);
            Add(nameof(Configuration.MaxCrashReportFolderMegabytes), Reports, 1, 1024 * 1024);
            Add(nameof(Configuration.MaxCrashReportDecompressedKilobytes), Reports, 1, 1024);
            Add(nameof(Configuration.RepeatedLogIntervalSeconds), Logging, 0, 3600);
        }
        public static void ApplyLive(Configuration configuration)
        {
            if (configuration == null) return;
            System.Threading.Interlocked.Exchange(ref BNLThrottle.IntervalMs, System.Math.Max(0, configuration.RepeatedLogIntervalSeconds) * 1000L);
        }
        private static void Add(string field, string group, double min, double max) => BasisProtectionSettings.RegisterField(() => NetworkServer.Configuration, typeof(Configuration), field, group, min, max);
    }
}
