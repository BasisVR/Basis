using BasisNetworkServer.Security;
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
    public int PeerLimit = 1024;
    public ushort SetPort = 4296;
    public bool UseNativeSockets = true;
    public bool NatPunchEnabled = true;
    public int PingInterval = 1500;
    public int DisconnectTimeout = 5000;
    public bool SimulatePacketLoss = false;
    public bool SimulateLatency = false;
    public int SimulationPacketLossChance = 10;
    public int SimulationMinLatency = 50;
    public int SimulationMaxLatency = 150;
    public int ReconnectDelay = 500;
    public int MaxConnectAttempts = 10;
    public bool ReuseAddress = false;
    public bool DontRoute = false;
    public bool EnableStatistics = true;
    public bool IPv6Enabled = true;
    public int MtuOverride = 1500;
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
    public float BSRSlowestSendRate = 2.5f;
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
                    if (value == "true")
                    {
                        field.SetValue(config, true);
                    }
                    else if (value == "false")
                    {
                        field.SetValue(config, false);
                    }
                    else
                    {
                        BNL.LogWarning("Boolean field was not a true or false string. Failed Override");
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
