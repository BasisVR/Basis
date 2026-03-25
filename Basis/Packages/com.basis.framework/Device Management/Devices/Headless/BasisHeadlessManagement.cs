using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Headless;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Headless bootstrap/cleanup and auto-connect flow for server builds.
/// Handles scene stripping (textures, probes, UI), config load, and network connect.
/// </summary>
public class BasisHeadlessManagement : BasisBaseTypeManagement
{
    /// <summary>Injected/created headless eye input.</summary>
    public BasisHeadlessInput BasisHeadlessInput;

    /// <summary>Network password loaded from config or default.</summary>
    public static string Password = "default_password";

    /// <summary>Server IP loaded from config or default.</summary>
    public static string Ip = "localhost";

    /// <summary>Server port loaded from config or default.</summary>
    public static int Port = 4296;

    /// <summary>
    /// Scene change hook used in headless to aggressively strip visuals and free memory.
    /// </summary>
    private void OnSceneLoadeded(Scene arg0, Scene arg1)
    {
        RemoveAllMaterialTextures();
        RemoveAllReflectionProbes();
        RemoveAllText();
        Resources.UnloadUnusedAssets();
    }

    /// <summary>
    /// Iterates all renderers and clears common texture slots on their materials.
    /// </summary>
    private void RemoveAllMaterialTextures()
    {
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        HashSet<Material> processedMats = new HashSet<Material>();

        foreach (Renderer renderer in renderers)
        {
            foreach (Material mat in renderer.materials)
            {
                if (mat == null || processedMats.Contains(mat))
                    continue;

                ShaderUtilSafe.ClearAllKnownTextures(mat);
                processedMats.Add(mat);
            }
        }

        Debug.Log("All textures cleared from all materials.");
    }

    /// <summary>
    /// Utility to clear commonly-used texture properties without Editor-only APIs.
    /// </summary>
    public static class ShaderUtilSafe
    {
        // Commonly used texture property names across Standard/URP shaders
        private static readonly string[] commonTextureProps =
        {
            "_MainTex", "_BaseMap", "_BumpMap", "_EmissionMap", "_MetallicGlossMap",
            "_ParallaxMap", "_OcclusionMap", "_DetailMask", "_DetailAlbedoMap", "_DetailNormalMap"
        };

        /// <summary>
        /// Sets all known texture properties to null on the material if present.
        /// </summary>
        public static void ClearAllKnownTextures(Material material)
        {
            foreach (string prop in commonTextureProps)
            {
                if (material.HasProperty(prop))
                {
                    material.SetTexture(prop, null);
                }
            }
        }
    }

    /// <summary>
    /// Destroys all ReflectionProbe GameObjects in the scene.
    /// </summary>
    private void RemoveAllReflectionProbes()
    {
        ReflectionProbe[] probes = FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (ReflectionProbe probe in probes)
        {
            Destroy(probe.gameObject);
        }

        Debug.Log("All reflection probes removed from scene.");
    }

    /// <summary>
    /// Destroys all Canvas components (to remove headless UI).
    /// </summary>
    private void RemoveAllText()
    {
        Canvas[] Canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Canvas Canvas in Canvases)
        {
            Destroy(Canvas);
        }

        Debug.Log("All reflection probes removed from scene.");
    }

    /// <summary>
    /// Loads config.xml from <see cref="Application.dataPath"/> or creates it with defaults.
    /// Updates <see cref="Password"/>, <see cref="Ip"/>, and <see cref="Port"/>.
    /// </summary>
    public static void LoadOrCreateConfigXml()
    {
        string filePath = Path.Combine(Application.dataPath, "config.xml");
        string defaultPassword = Password;
        string defaultIp = Ip;
        int defaultPort = Port;
        string envPassword = ReadEnvironmentString("Password");
        string envIp = ReadEnvironmentString("Ip");
        int? envPort = ReadEnvironmentInt("Port");

        if (envPassword != null && envIp != null && envPort.HasValue)
        {
            Password = envPassword;
            Ip = envIp;
            Port = envPort.Value;
            return;
        }

        XElement root = null;
        if (File.Exists(filePath))
        {
            var doc = XDocument.Load(filePath);
            root = doc.Element("Configuration");
        }
        else
        {
            TryCreateDefaultConfigXml(filePath, defaultPassword, defaultIp, defaultPort);
        }

        Password = envPassword ?? root?.Element("Password")?.Value ?? defaultPassword;
        Ip = envIp ?? root?.Element("Ip")?.Value ?? defaultIp;
        Port = envPort ?? ReadXmlInt(root?.Element("Port")?.Value, defaultPort);
    }

    private static void TryCreateDefaultConfigXml(string filePath, string password, string ip, int port)
    {
        try
        {
            var defaultConfig = new XElement("Configuration",
                new XElement("Password", password),
                new XElement("Ip", ip),
                new XElement("Port", port)
            );
            new XDocument(defaultConfig).Save(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Unable to create default headless config at '{filePath}'. Continuing with environment/default values. {ex.Message}");
        }
    }

    private static string ReadEnvironmentString(string envName)
    {
        string envValue = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return null;
        }

        return envValue;
    }

    private static int? ReadEnvironmentInt(string envName)
    {
        string envValue = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return null;
        }

        if (int.TryParse(envValue, out int parsed))
        {
            return parsed;
        }

        Debug.LogWarning($"Invalid headless environment variable '{envName}' value '{envValue}'. Falling back to config.xml/defaults.");
        return null;
    }

    private static int ReadXmlInt(string value, int fallback)
    {
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Reads config, loads default scene (if configured), and connects to network as client.
    /// </summary>
    public async void ConnectToNetwork()
    {
        LoadOrCreateConfigXml();
        await CreateAssetBundle();
        BasisNetworkManagement.Instance.Ip = Ip;
        BasisNetworkManagement.Instance.Password = Password;
        BasisNetworkManagement.Instance.IsHostMode = false;
        BasisNetworkManagement.Instance.Port = (ushort)Port;
        BasisNetworkManagement.Instance.Connect();
        BasisDebug.Log("connecting to default");
    }

    /// <summary>
    /// Loads the configured default scene via Addressables or AssetBundle when in headless.
    /// No-op if not configured for scene provided here.
    /// </summary>
    public async Task CreateAssetBundle()
    {
        if (BundledContentHolder.Instance.UseSceneProvidedHere)
        {
            BasisDebug.Log("using Local Asset Bundle or Addressable", BasisDebug.LogTag.Networking);
            if (BundledContentHolder.Instance.UseAddressablesToLoadScene)
            {
                await BasisSceneLoad.LoadSceneAddressables(BundledContentHolder.Instance.DefaultScene.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
            }
            else
            {
                await BasisSceneLoad.LoadSceneAssetBundle(BundledContentHolder.Instance.DefaultScene);
            }
        }
    }

    /// <inheritdoc/>
    public override void StartSDK()
    {
#if UNITY_SERVER
        if (BasisLocalPlayer.PlayerReady && BasisLocalPlayer.Instance != null)
        {
            EnsureHeadlessInput();
        }
        else
        {
            BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerReadyForHeadless;
            BasisLocalPlayer.OnLocalPlayerInitalized += OnLocalPlayerReadyForHeadless;
        }
        BasisDebug.Log(nameof(StartSDK), BasisDebug.LogTag.Device);

        // Name the player
        BasisLocalPlayer.Instance.DisplayName = GenerateRandomPlayerName();
        BasisLocalPlayer.Instance.SetSafeDisplayname();

        // Connect (immediately or when network manager appears)
        if (BasisNetworkManagement.Instance != null)
        {
            ConnectToNetwork();
        }
        else
        {
            BasisNetworkManagement.OnEnableInstanceCreate += ConnectToNetwork;
        }

        // Strip visuals on scene switches
        SceneManager.activeSceneChanged += OnSceneLoadeded;
#endif
        BasisDebug.Log(nameof(StartSDK), BasisDebug.LogTag.Device);
    }

    private void OnDestroy()
    {
#if UNITY_SERVER
        BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerReadyForHeadless;
#endif
    }

#if UNITY_SERVER
    private void OnLocalPlayerReadyForHeadless()
    {
        BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerReadyForHeadless;
        EnsureHeadlessInput();
    }

    private void EnsureHeadlessInput()
    {
        if (BasisHeadlessInput != null)
        {
            return;
        }

        if (BasisLocalPlayer.Instance == null)
        {
            BasisDebug.LogWarning("Headless input creation delayed: LocalPlayer instance is null.", BasisDebug.LogTag.Device);
            return;
        }

        GameObject gameObject = new GameObject("Headless Eye");
        gameObject.transform.parent = BasisLocalPlayer.Instance.transform;

        BasisHeadlessInput = gameObject.AddComponent<BasisHeadlessInput>();
        BasisHeadlessInput.Initialize("Desktop Eye", nameof(Basis.Scripts.Device_Management.Devices.Headless.BasisHeadlessInput));
        BasisDeviceManagement.Instance.TryAdd(BasisHeadlessInput);
    }
#endif

    /// <inheritdoc/>
    public override void StopSDK()
    {
        BasisDebug.Log(nameof(StopSDK), BasisDebug.LogTag.Device);
    }

    /// <inheritdoc/>
    public override bool IsDeviceBootable(string BootRequest)
    {
        if (BootRequest == "Headless")
        {
            return true;
        }
        return false;
    }

    // Randomized name generation bits
    public static string[] adjectives = { "Swift", "Brave", "Clever", "Fierce", "Nimble", "Silent", "Bold", "Lucky", "Strong", "Mighty", "Sneaky", "Fearless", "Wise", "Vicious", "Daring" };
    public static string[] nouns = { "Warrior", "Hunter", "Mage", "Rogue", "Paladin", "Shaman", "Knight", "Archer", "Monk", "Druid", "Assassin", "Sorcerer", "Ranger", "Guardian", "Berserker" };
    public static string[] titles = { "the Swift", "the Bold", "the Silent", "the Brave", "the Fierce", "the Wise", "the Protector", "the Shadow", "the Flame", "the Phantom" };
    /// <summary>Animal list for name flair.</summary>
    public static string[] animals = { "Wolf", "Tiger", "Eagle", "Dragon", "Lion", "Bear", "Hawk", "Panther", "Raven", "Serpent", "Fox", "Falcon" };

    /// <summary>Unity rich-text color names with hex values.</summary>
    public static (string Name, string Hex)[] colors =
    {
        ("Red", "#FF0000"),
        ("Blue", "#0000FF"),
        ("Green", "#008000"),
        ("Yellow", "#FFFF00"),
        ("Black", "#000000"),
        ("White", "#FFFFFF"),
        ("Silver", "#C0C0C0"),
        ("Golden", "#FFD700"),
        ("Crimson", "#DC143C"),
        ("Azure", "#007FFF"),
        ("Emerald", "#50C878"),
        ("Amber", "#FFBF00")
    };

    /// <summary>
    /// Generates a flavored rich-text display name (not guaranteed globally unique).
    /// </summary>
    public static string GenerateRandomPlayerName()
    {
        System.Random random = new System.Random();

        string adjective = adjectives[random.Next(adjectives.Length)];
        string noun = nouns[random.Next(nouns.Length)];
        string title = titles[random.Next(titles.Length)];
        (string Name, string Hex) color = colors[random.Next(colors.Length)];
        string animal = animals[random.Next(animals.Length)];

        string colorText = $"<color={color.Hex}>{color.Name}</color>";
        string generatedName = $"{adjective}{noun} {title} of the {colorText} {animal}";

        return $"{generatedName}";
    }
}
