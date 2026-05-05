using BasisNetworkCore.Security;
using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

[Serializable]
public class Configuration
{
    public const string ConfigFolderName = "config";
    public const string LogsFolderName = "logs";
    public const string InitialResourcesFolderName = "initialresources";
    public const string DefaultLibraryFolderName = "defaultlibrary";
    public int PeerLimit = ushort.MaxValue;
    public ushort SetPort = 4296;
    /// <summary>Display name returned by the unconnected server-info query — what shows up as the row title in a client server-list UI.</summary>
    public string ServerName = "Basis Server";
    /// <summary>Short MOTD returned alongside the server name in the info query response. Two short lines render cleanly in the list UI.</summary>
    public string ServerMotd = "";
    public bool UseNativeSockets = true;
    public bool NatPunchEnabled = false;
    public int PingInterval = 1500;
    public int DisconnectTimeout = 30000;
    public bool SimulatePacketLoss = false;
    public bool SimulateLatency = false;
    public int SimulationPacketLossChance = 10;
    public int SimulationMinLatency = 50;
    public int SimulationMaxLatency = 150;
    public int ReconnectDelay = 500;
    public int MaxConnectAttempts = 10;
    public bool ReuseAddresss = false;
    public bool DontRoute = false;
    public bool EnableStatistics = true;
    public bool IPv6Enabled = true;
    public int MtuOverride = 0;
    public bool MtuDiscovery = true;
    public bool DisconnectOnUnreachable = false;
    public bool AllowPeerAddressChange = true;
    public bool HasFileSupport = true;
    public string HealthCheckHost = "localhost";
    public ushort HealthCheckPort = 10666;
    public string HealthPath = "/health";
    public int BSRSMillisecondDefaultInterval = 50;
    public int BSRBaseMultiplier = 1;
    public float BSRSIncreaseRate = 0.005f;
    public float BSRSlowestSendRate = 2.55f;
    public float HighQualityDistance = 3f;
    public float MediumQualityDistance = 10f;
    public float LowQualityDistance = 20f;
    public bool OverrideAutoDiscoveryOfIpv = false;
    public string IPv4Address = "0.0.0.0";
    public string IPv6Address = "::1";
    public string Password = "default_password";
    public bool UseAuth = true;
    public bool UseAuthIdentity = true;
    public BasisUserRestrictionMode BasisUserRestrictionMode;
    public int HowManyDuplicateAuthCanExist = 2;
    public int AuthValidationTimeOutMiliseconds = 9000;
    public bool EnableConsole = true;
    public bool DisableWriteUnlessAdminPersistentFlag = true;
    public bool DisableReadUnlessAdminPersistentFlag = false;
    /// <summary>
    /// When true, the avatar reduction system bundles per-receiver avatar messages
    /// and emits them deflated on CompressedAvatarBundleChannel. Falls back to
    /// per-message uncompressed sends when a receiver has too few queued messages
    /// for compression to be worthwhile, or when the compressed result would
    /// exceed peer MTU. Clients must implement the matching decoder.
    /// </summary>
    public bool EnableAvatarBundleCompression = false;
    /// <summary>Minimum queued avatar messages to a single receiver before a bundle is even attempted.</summary>
    public int AvatarBundleMinMessages = 4;
    /// <summary>Minimum uncompressed bundle bytes before LZ4 compression is attempted. With LZ4 having near-zero per-call setup, 128 just guards the very smallest cases where LZ4 can't find any redundancy.</summary>
    public int AvatarBundleMinBytes = 128;
    public bool EnableBSRProfiling = false;
    public bool DisallowHeadless = false;

    // Global lockout defaults applied at server boot. Users need the matching
    // basis.resource.lockbypass.{avatar,prop,world} permission to load while locked.
    public bool AvatarsLocked = false;
    public bool PropsLocked = false;
    public bool WorldsLocked = true;
    /// <summary>
    /// When true, peers may not share saved-server entries through the content
    /// share system. Toggled live via the admin panel and persisted to config.xml
    /// alongside the other content lockouts. Default off so existing deployments
    /// behave as before.
    /// </summary>
    public bool ServersLocked = false;
    /// <summary>
    /// Read config from file. If no file is found create a default config file at filePath
    /// </summary>
    /// <param name="filePath">Path to config file</param>
    public static Configuration LoadFromXml(string filePath)
    {
        var serializer = new XmlSerializer(typeof(Configuration));
        if (File.Exists(filePath))
        {
            using var fileReader = new StreamReader(filePath);
            var config = (Configuration)serializer.Deserialize(fileReader);
            fileReader.Close();
            return config;
        }

        BNL.Log($"{filePath} not found, creating with default values");

        var defaultConfig = new Configuration();
        using var writer = new StreamWriter(filePath);
        serializer.Serialize(writer, defaultConfig);
        writer.Close();

        return defaultConfig;
    }

    /// <summary>
    /// Persist this configuration back to <paramref name="filePath"/>. Used by the
    /// admin panel to make in-game changes (server name, MOTD, whitelist mode)
    /// survive a restart. Writes via a sibling temp file + atomic move so a crash
    /// mid-write doesn't corrupt the live config.
    /// </summary>
    public void SaveToXml(string filePath)
    {
        var serializer = new XmlSerializer(typeof(Configuration));
        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = filePath + ".tmp";
        using (var writer = new StreamWriter(tempPath))
        {
            serializer.Serialize(writer, this);
        }
        if (File.Exists(filePath)) File.Replace(tempPath, filePath, null);
        else File.Move(tempPath, filePath);
    }

    /// <summary>
    /// Resolve the canonical config.xml path under <c>{BaseDirectory}/{ConfigFolderName}/config.xml</c>
    /// — same path the bootstrappers (BasisServerConsole.Program / Unity host runner) read on startup.
    /// </summary>
    public static string GetDefaultPath()
    {
        return Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, ConfigFolderName, "config.xml");
    }

    /// <summary>
    /// This code will override what is written in the config.xml if it finds
    /// an Environmental Variable with the same name as a public config field.
    ///
    /// On windows you can test this in the console:
    ///    $env:PeerLimit = "256"
    ///   .\BasisNetworkConsole.exe
    /// But it is intended to allow Linux admins to override defaults during launch.
    /// </summary>
    public void ProcessEnvironmentalOverrides()
    {
        Configuration config = this;

        // Override a configuration value only if we find a Environmental Variable that matches the name
        Type type = config.GetType();
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            string value = Environment.GetEnvironmentVariable(field.Name);
            if ( value != null )
            {
                BNL.Log($"Applying Environmental Override with Field:{field.Name} Value:{value}");

                if (field.FieldType == typeof(int))
                {
                    if (int.TryParse(value, out int number))
                    {
                        field.SetValue(config, number);
                    }
                    else
                    {
                        BNL.LogWarning("Could not cast to int. Failed Override");
                    }
                }
                else if (field.FieldType == typeof(ushort))
                {
                    if (ushort.TryParse(value, out ushort number))
                    {
                        field.SetValue(config, number);
                    }
                    else
                    {
                        BNL.LogWarning("Could not cast to ushort. Failed Override.");
                    }
                }
                else if (field.FieldType == typeof(float))
                {
                    if (float.TryParse(value, out float number))
                    {
                        field.SetValue(config, number);
                    }
                    else
                    {
                        BNL.LogWarning("Could not cast to ushort. Failed Override.");
                    }
                }
                else if (field.FieldType == typeof(string))
                {
                    field.SetValue(config, value);
                }
                else if (field.FieldType == typeof(bool))
                {
                    if (bool.TryParse(value, out bool boolResult))
                    {
                        field.SetValue(config, boolResult);
                    }
                    else
                    {
                        BNL.LogWarning($"Could not parse '{value}' as bool for field {field.Name}. Failed Override");
                    }
                }
                else
                {
                    BNL.LogWarning($"Environmental varible type could not be processed for Config Field:{field.Name} Value:{value}");
                }
            }
        }
    }
}
