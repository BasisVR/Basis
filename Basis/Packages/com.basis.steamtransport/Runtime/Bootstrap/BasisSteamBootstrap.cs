using Steamworks;
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
            BasisSteamTransportTrace.Configure(ActiveSettings != null && ActiveSettings.EnableTransportTrace);
            BasisSteamTransportTrace.Clear();

            if (ActiveSettings != null && ActiveSettings.AutoInitialize)
            {
                EnsureInitialized(ActiveSettings);
            }
        }

        private void Update()
        {
            if (SteamClient.IsValid && ActiveSettings != null && ActiveSettings.RunCallbacksManually)
            {
                SteamClient.RunCallbacks();
            }

            SteamNetManager.PollActiveManagers();
        }

        private void OnDisable()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        public static bool EnsureInitialized(BasisSteamSettings settings)
        {
            HasTriedInitialization = true;
            settings = ResolveSettings(settings);

            if (settings == null)
            {
                Debug.LogWarning("[BasisSteamBootstrap] Missing BasisSteamSettings asset.");
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
                return SteamClient.IsValid;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BasisSteamBootstrap] Steam init failed: {ex.Message}");
                return false;
            }
        }

        public static void Shutdown()
        {
            if (SteamClient.IsValid)
            {
                SteamClient.Shutdown();
            }
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

            ActiveSettings = Resources.Load<BasisSteamSettings>(BasisSteamSettings.DefaultResourcesPath);
            return ActiveSettings;
        }
    }
}
