using Steamworks;
using Basis.EventDriver;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    [DefaultExecutionOrder(-14950)]
    public class BasisSteamBootstrap : MonoBehaviour
    {
        public BasisSteamSettings Settings;

        public static BasisSteamBootstrap Instance;
        public static BasisSteamSettings ActiveSettings { get; private set; }
        public static bool IsInitialized => SteamClient.IsValid;
        public static bool HasTriedInitialization { get; private set; }
        public static bool HasRequestedRelayWarmup { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance != null)
            {
                return;
            }

            GameObject bootstrapObject = new GameObject(nameof(BasisSteamBootstrap));
            DontDestroyOnLoad(bootstrapObject);
            bootstrapObject.hideFlags = HideFlags.DontSave;
            bootstrapObject.AddComponent<BasisSteamBootstrap>();
        }

        private void OnEnable()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }

            Instance = this;
            ActiveSettings = ResolveSettings(Settings);
            BasisSteamTransportMetrics.Reset();
            BasisSteamTransportTrace.Configure(ActiveSettings != null && ActiveSettings.EnableTransportTrace);
            BasisSteamTransportTrace.Clear();

            if (SteamClient.IsValid)
            {
                EnsureRelayWarmup();
            }

            if (ActiveSettings != null && ActiveSettings.AutoInitialize)
            {
                EnsureInitialized(ActiveSettings);
            }

            BasisEventDriver.OnUpdate -= Tick;
            BasisEventDriver.OnUpdate += Tick;
        }

        private static void Tick()
        {
            if (SteamClient.IsValid && ActiveSettings != null && ActiveSettings.RunCallbacksManually)
            {
                SteamClient.RunCallbacks();
            }

            BasisSteamTransportTrace.FlushPending();
            SteamNetManager.PollActiveManagers();
        }

        private void OnDisable()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            BasisEventDriver.OnUpdate -= Tick;
        }

        private void OnApplicationQuit()
        {
            BasisSteamTransportTrace.FlushPending(force: true);
            Shutdown();
        }

        public static bool EnsureInitialized(BasisSteamSettings settings)
        {
            HasTriedInitialization = true;
            settings = ResolveSettings(settings);

            if (settings == null)
            {
                BasisDebug.LogError("Missing BasisSteamSettings asset. Cannot initialize Steam.", BasisDebug.LogTag.Networking);
                return false;
            }

            ActiveSettings = settings;
            BasisSteamTransportTrace.Configure(ActiveSettings != null && ActiveSettings.EnableTransportTrace);

            if (SteamClient.IsValid)
            {
                return true;
            }

            try
            {
                if (settings.RestartAppIfNecessary && SteamClient.RestartAppIfNecessary(settings.AppId))
                {
                    return false;
                }

                SteamClient.Init(settings.AppId, asyncCallbacks: !settings.RunCallbacksManually);
                EnsureRelayWarmup();
                return SteamClient.IsValid;
            }
            catch (System.Exception ex)
            {
                BasisDebug.LogError($"Steam init failed: {ex.Message}", BasisDebug.LogTag.Networking);
                return false;
            }
        }

        public static void Shutdown()
        {
            BasisSteamLobbyService.HandleSteamShutdown();

            if (SteamClient.IsValid)
            {
                SteamClient.Shutdown();
            }

            HasRequestedRelayWarmup = false;
        }

        public static BasisSteamSettings ResolveSettings(BasisSteamSettings settings = null)
        {
            if (settings != null)
            {
                ActiveSettings = settings;
                return ActiveSettings;
            }

            if (ActiveSettings != null)
            {
                return ActiveSettings;
            }

            ActiveSettings = ScriptableObject.CreateInstance<BasisSteamSettings>();
            ActiveSettings.hideFlags = HideFlags.DontSave;
            return ActiveSettings;
        }

        private static void EnsureRelayWarmup()
        {
            if (HasRequestedRelayWarmup || !SteamClient.IsValid)
            {
                return;
            }

            SteamNetworkingUtils.InitRelayNetworkAccess();
            HasRequestedRelayWarmup = true;
        }
    }
}
