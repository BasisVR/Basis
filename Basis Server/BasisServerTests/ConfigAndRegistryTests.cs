using Basis.Network.Core;
using BasisNetworkCore.Security;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.Xml.Serialization;
using Xunit;

namespace BasisServerTests;

/// <summary>Shared helpers: unique temp dirs under %TEMP%\BasisCfgTests and reflection-based field round-trip checks.</summary>
internal static class ConfigTestSupport
{
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BasisCfgTests", Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Set every public instance field to a value different from its current one.</summary>
    internal static void MutateAllFields(object target)
    {
        foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object current = field.GetValue(target)!;
            Type type = field.FieldType;
            if (type == typeof(string)) field.SetValue(target, (string)current + "_x");
            else if (type == typeof(bool)) field.SetValue(target, !(bool)current);
            else if (type == typeof(int)) field.SetValue(target, (int)current + 7);
            else if (type == typeof(ushort)) field.SetValue(target, (ushort)((ushort)current + 5));
            else if (type == typeof(float)) field.SetValue(target, (float)current + 1.5f);
            else if (type == typeof(byte)) field.SetValue(target, (byte)((byte)current + 3));
            else if (type.IsEnum) field.SetValue(target, Enum.GetValues(type).Cast<object>().First(v => !Equals(v, current)));
            else throw new NotSupportedException($"MutateAllFields: unhandled field type {type} on {field.Name} — update the test helper.");
        }
    }

    internal static void AssertFieldsEqual(object expected, object actual, params string[] skipFields)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (FieldInfo field in expected.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (skipFields.Contains(field.Name)) continue;
            object? expectedValue = field.GetValue(expected);
            object? actualValue = field.GetValue(actual);
            Assert.True(Equals(expectedValue, actualValue),
                $"Field {field.Name}: expected '{expectedValue}' but got '{actualValue}'");
        }
    }

    internal static string NewStackId() => "teststack-" + Guid.NewGuid().ToString("N");
}

/// <summary>Public parameterless type with no registered docs, for BasisConfigXmlDocs pass-through checks.</summary>
public sealed class DocLessTestConfig
{
    public int Value = 3;
}

/// <summary>Pins the shipped defaults of the server Configuration, including the v42 bandwidth-batch settings.</summary>
public class ServerConfigurationDefaultsTests
{
    [Fact]
    public void Defaults_NetworkingAndHealth()
    {
        var cfg = new Configuration();
        Assert.Equal(ushort.MaxValue, cfg.PeerLimit);
        Assert.Equal(4296, cfg.SetPort);
        Assert.Equal("Basis Server", cfg.ServerName);
        Assert.Equal("", cfg.ServerMotd);
        Assert.True(cfg.EnableStatistics);
        Assert.True(cfg.HasFileSupport);
        Assert.Equal("localhost", cfg.HealthCheckHost);
        Assert.Equal(10666, cfg.HealthCheckPort);
        Assert.Equal("/health", cfg.HealthPath);
        Assert.False(cfg.HealthIncludeBSRProfiling);
        Assert.False(cfg.OverrideAutoDiscoveryOfIpv);
        Assert.Equal("0.0.0.0", cfg.IPv4Address);
        Assert.Equal("::", cfg.IPv6Address);
        Assert.Equal("", cfg.NetworkStackId);
    }

    [Fact]
    public void Defaults_V42BandwidthFeatures()
    {
        var cfg = new Configuration();
        Assert.Equal(20, cfg.VoiceFrameDurationMs);
        Assert.True(cfg.EnableAvatarBundleCompression);
        Assert.Equal(2, cfg.AvatarBundleMinMessages);
        Assert.Equal(128, cfg.AvatarBundleMinBytes);
        Assert.True(cfg.EnableAvatarDeltaCompression);
        Assert.Equal(500, cfg.AvatarDeltaKeyframeIntervalMs);
        Assert.Equal(2000, cfg.AvatarDeltaKeyframeMaxIntervalMs);
        Assert.True(cfg.StripAdditionalDataAtLowQuality);
        Assert.True(cfg.EnableUplinkAvatarDelta);
    }

    [Fact]
    public void Defaults_AuthContentLocksAndLimits()
    {
        var cfg = new Configuration();
        Assert.Equal("default_password", cfg.Password);
        Assert.True(cfg.UseAuth);
        Assert.True(cfg.UseAuthIdentity);
        Assert.Equal(BasisUserRestrictionMode.Normal, cfg.BasisUserRestrictionMode);
        Assert.Equal(2, cfg.HowManyDuplicateAuthCanExist);
        Assert.Equal(9000, cfg.AuthValidationTimeOutMiliseconds);
        Assert.False(cfg.AvatarsLocked);
        Assert.False(cfg.PropsLocked);
        Assert.True(cfg.WorldsLocked);
        Assert.False(cfg.ServersLocked);
        Assert.False(cfg.ThirdPersonDisabled);
        Assert.False(cfg.AdditionalAvatarDataLock);
        Assert.Equal(0, cfg.CameraMetadataDisallowMask);
        Assert.Equal(25f, cfg.MaxMicrophoneRangeMeters);
        Assert.Equal(25f, cfg.MaxHearingRangeMeters);
        Assert.Equal(0.1f, cfg.MinAvatarEyeHeightMeters);
        Assert.Equal(100f, cfg.MaxAvatarEyeHeightMeters);
        Assert.False(cfg.DisallowHeadless);
    }

    [Fact]
    public void Defaults_ReductionSystemCurve()
    {
        var cfg = new Configuration();
        Assert.Equal(50, cfg.BSRSMillisecondDefaultInterval);
        Assert.Equal(1, cfg.BSRBaseMultiplier);
        Assert.Equal(0.005f, cfg.BSRSIncreaseRate);
        Assert.Equal(2.55f, cfg.BSRSlowestSendRate);
        Assert.Equal(10f, cfg.HighQualityDistance);
        Assert.Equal(20f, cfg.MediumQualityDistance);
        Assert.Equal(40f, cfg.LowQualityDistance);
        Assert.False(cfg.EnableBSRProfiling);
    }

    [Fact]
    public void Defaults_RestApiAndDiagnostics()
    {
        var cfg = new Configuration();
        Assert.False(cfg.ApiEnabled);
        Assert.Equal("localhost", cfg.ApiHost);
        Assert.Equal(10667, cfg.ApiPort);
        Assert.Equal("", cfg.ApiKey);
        Assert.True(cfg.CrashReportingEnabled);
        Assert.Equal(10000, cfg.MaxDatabaseEntries);
        Assert.Equal(256, cfg.MaxDatabaseNameLength);
        Assert.Equal(1000, cfg.MaxDatabasePayloadEntries);
        Assert.Equal(32, cfg.MaxContentSpheresPerPlayer);
    }

    [Fact]
    public void Defaults_VersioningAndFolderConstants()
    {
        Assert.Equal(4, Configuration.CurrentConfigVersion);
        Assert.Equal(0, new Configuration().ConfigVersion);
        Assert.Equal("config", Configuration.ConfigFolderName);
        Assert.Equal("logs", Configuration.LogsFolderName);
        string defaultPath = Configuration.GetDefaultPath();
        Assert.True(Path.IsPathRooted(defaultPath));
        Assert.EndsWith(Path.Combine(Configuration.ConfigFolderName, "config.xml"), defaultPath);
    }

    [Fact]
    public void DefaultServerPort_MatchesParserDefaultPort()
    {
        Assert.Equal(LNLConnectionTargetParser.DefaultPort, new Configuration().SetPort);
        Assert.Equal(4296, LNLConnectionTargetParser.DefaultPort);
    }
}

/// <summary>
/// config.xml load/save behavior. Shares a collection with the transport-store tests because
/// Configuration.LoadFromXml/SaveToXml also drive the static BasisTransportConfigStore.
/// </summary>
[Collection("Basis config file statics")]
public class ConfigurationPersistenceTests
{
    [Fact]
    public void LoadFromXml_MissingFile_WritesDefaultsWithDocComments()
    {
        using var dir = new ConfigTestSupport.TempDir();
        string path = dir.File("config.xml");

        Configuration loaded = Configuration.LoadFromXml(path);

        Assert.True(File.Exists(path));
        ConfigTestSupport.AssertFieldsEqual(new Configuration(), loaded, nameof(Configuration.ConfigVersion));
        Assert.Equal(Configuration.CurrentConfigVersion, loaded.ConfigVersion);

        string xml = File.ReadAllText(path);
        Assert.Contains("<!--", xml);
        Assert.Contains("Basis dedicated-server configuration", xml);
        Assert.Contains("<PeerLimit>", xml);

        string sidecar = Path.Combine(dir.Path, BasisTransportConfigStore.TransportsFolderName,
            BasisNetworkStackRegistry.LiteNetLibId + ".xml");
        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryPublicField()
    {
        using var dir = new ConfigTestSupport.TempDir();
        string path = dir.File("config.xml");

        var expected = new Configuration();
        ConfigTestSupport.MutateAllFields(expected);
        expected.SaveToXml(path);

        Configuration loaded = Configuration.LoadFromXml(path);
        ConfigTestSupport.AssertFieldsEqual(expected, loaded, nameof(Configuration.ConfigVersion));
        Assert.Equal(Configuration.CurrentConfigVersion, loaded.ConfigVersion);
    }

    [Fact]
    public void LoadFromXml_PartialFile_KeepsValues_HealsMissing_IgnoresUnknownElements()
    {
        using var dir = new ConfigTestSupport.TempDir();
        string path = dir.File("config.xml");
        File.WriteAllText(path,
            "<Configuration>" +
            "<SetPort>5555</SetPort>" +
            "<ServerName>Partial Server</ServerName>" +
            "<NotARealSetting>ignored</NotARealSetting>" +
            "</Configuration>");

        Configuration loaded = Configuration.LoadFromXml(path);

        Assert.Equal(5555, loaded.SetPort);
        Assert.Equal("Partial Server", loaded.ServerName);
        Assert.Equal(new Configuration().PeerLimit, loaded.PeerLimit);
        Assert.Equal(Configuration.CurrentConfigVersion, loaded.ConfigVersion);

        string healed = File.ReadAllText(path);
        Assert.Contains("<SetPort>5555</SetPort>", healed);
        Assert.Contains("EnableUplinkAvatarDelta", healed);
        Assert.Contains("VoiceFrameDurationMs", healed);
        Assert.DoesNotContain("NotARealSetting", healed);
        Assert.False(BasisConfigXmlDocs.IsMissingAnyField(path, typeof(Configuration)));
    }

    [Fact]
    public void LoadFromXml_MalformedOrEmptyFile_ThrowsInvalidOperation()
    {
        using var dir = new ConfigTestSupport.TempDir();
        string garbagePath = dir.File("garbage.xml");
        File.WriteAllText(garbagePath, "this is not xml at all <<<>");
        Assert.ThrowsAny<InvalidOperationException>(() => Configuration.LoadFromXml(garbagePath));

        string emptyPath = dir.File("empty.xml");
        File.WriteAllText(emptyPath, "");
        Assert.ThrowsAny<InvalidOperationException>(() => Configuration.LoadFromXml(emptyPath));
    }

    [Fact]
    public void EnvironmentalOverrides_ApplyByFieldName_AndRejectUnparseableValues()
    {
        Environment.SetEnvironmentVariable("PeerLimit", "512");
        Environment.SetEnvironmentVariable("ServerMotd", "env motd");
        Environment.SetEnvironmentVariable("UseAuthIdentity", "false");
        Environment.SetEnvironmentVariable("BSRSIncreaseRate", "0.25");
        Environment.SetEnvironmentVariable("SetPort", "not-a-port");
        try
        {
            var cfg = new Configuration();
            cfg.ProcessEnvironmentalOverrides();

            Assert.Equal(512, cfg.PeerLimit);
            Assert.Equal("env motd", cfg.ServerMotd);
            Assert.False(cfg.UseAuthIdentity);
            Assert.Equal(0.25f, cfg.BSRSIncreaseRate);
            Assert.Equal(4296, cfg.SetPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PeerLimit", null);
            Environment.SetEnvironmentVariable("ServerMotd", null);
            Environment.SetEnvironmentVariable("UseAuthIdentity", null);
            Environment.SetEnvironmentVariable("BSRSIncreaseRate", null);
            Environment.SetEnvironmentVariable("SetPort", null);
        }
    }
}

/// <summary>Per-transport config sidecars ({configDir}/transports/{stackId}.xml) via the static store.</summary>
[Collection("Basis config file statics")]
public class TransportConfigStoreTests
{
    private static void EnsureLiteNetLibRegistered()
        => RuntimeHelpers.RunClassConstructor(typeof(BasisNetworkStackRegistry).TypeHandle);

    private static string SidecarPath(string configDir)
        => Path.Combine(configDir, BasisTransportConfigStore.TransportsFolderName,
            BasisNetworkStackRegistry.LiteNetLibId + ".xml");

    [Fact]
    public void LnlTransportConfig_Defaults()
    {
        var cfg = new LNLTransportConfig();
        Assert.Equal(2, LNLTransportConfig.CurrentConfigVersion);
        Assert.Equal(0, cfg.ConfigVersion);
        Assert.True(cfg.UseNativeSockets);
        Assert.True(cfg.NatPunchEnabled);
        Assert.Equal(32, cfg.NatPortPredictionRange);
        Assert.Equal(1500, cfg.PingInterval);
        Assert.Equal(30000, cfg.DisconnectTimeout);
        Assert.False(cfg.SimulatePacketLoss);
        Assert.False(cfg.SimulateLatency);
        Assert.Equal(10, cfg.SimulationPacketLossChance);
        Assert.Equal(50, cfg.SimulationMinLatency);
        Assert.Equal(150, cfg.SimulationMaxLatency);
        Assert.Equal(500, cfg.ReconnectDelay);
        Assert.Equal(10, cfg.MaxConnectAttempts);
        Assert.False(cfg.ReuseAddresss);
        Assert.False(cfg.DontRoute);
        Assert.True(cfg.IPv6Enabled);
        Assert.Equal(0, cfg.MtuOverride);
        Assert.True(cfg.MtuDiscovery);
        Assert.False(cfg.DisconnectOnUnreachable);
        Assert.True(cfg.AllowPeerAddressChange);
        Assert.Equal(1, cfg.MultiSocketCount);
        // Packet pool scales with peer count rather than sitting at a fixed ceiling; the floor
        // stays PacketPoolSize, so small servers behave exactly as before.
        Assert.Equal(48, cfg.PacketPoolSizePerPeer);
        Assert.Equal(262144, cfg.PacketPoolSizeMax);
    }

    [Fact]
    public void LoadAll_CreatesDefaultSidecarWithDocComments()
    {
        EnsureLiteNetLibRegistered();
        using var dir = new ConfigTestSupport.TempDir();

        BasisTransportConfigStore.LoadAll(dir.Path);

        string sidecar = SidecarPath(dir.Path);
        Assert.True(File.Exists(sidecar));
        string xml = File.ReadAllText(sidecar);
        Assert.Contains("<!--", xml);
        Assert.Contains("LiteNetLib transport tuning", xml);
        Assert.Contains("MultiSocketCount", xml);

        var cfg = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
        Assert.Equal(1500, cfg.PingInterval);
        Assert.Equal(LNLTransportConfig.CurrentConfigVersion, cfg.ConfigVersion);
    }

    [Fact]
    public void SaveAllThenLoadAll_RoundTripsEveryPublicField()
    {
        EnsureLiteNetLibRegistered();
        using var dir = new ConfigTestSupport.TempDir();
        BasisTransportConfigStore.LoadAll(dir.Path);

        var expected = new LNLTransportConfig();
        ConfigTestSupport.MutateAllFields(expected);
        BasisTransportConfigStore.Set(BasisNetworkStackRegistry.LiteNetLibId, expected);
        BasisTransportConfigStore.SaveAll(dir.Path);
        BasisTransportConfigStore.LoadAll(dir.Path);

        var loaded = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
        Assert.NotSame(expected, loaded);
        ConfigTestSupport.AssertFieldsEqual(expected, loaded, nameof(LNLTransportConfig.ConfigVersion));
        Assert.Equal(LNLTransportConfig.CurrentConfigVersion, loaded.ConfigVersion);
    }

    [Fact]
    public void LoadAll_PartialSidecar_KeepsValueAndHealsMissingFields()
    {
        EnsureLiteNetLibRegistered();
        using var dir = new ConfigTestSupport.TempDir();
        string sidecar = SidecarPath(dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        File.WriteAllText(sidecar, "<LNLTransportConfig><PingInterval>777</PingInterval></LNLTransportConfig>");

        BasisTransportConfigStore.LoadAll(dir.Path);

        var cfg = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
        Assert.Equal(777, cfg.PingInterval);
        Assert.True(cfg.UseNativeSockets);
        Assert.Equal(32, cfg.NatPortPredictionRange);
        Assert.Equal(LNLTransportConfig.CurrentConfigVersion, cfg.ConfigVersion);

        string healed = File.ReadAllText(sidecar);
        Assert.Contains("<PingInterval>777</PingInterval>", healed);
        Assert.Contains("MultiSocketCount", healed);
    }

    [Fact]
    public void LoadAll_CorruptSidecar_RecreatesDefaultsWithoutThrowing()
    {
        EnsureLiteNetLibRegistered();
        using var dir = new ConfigTestSupport.TempDir();
        string sidecar = SidecarPath(dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        File.WriteAllText(sidecar, "{ definitely not xml )");

        BasisTransportConfigStore.LoadAll(dir.Path);

        var cfg = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
        Assert.Equal(1500, cfg.PingInterval);
        Assert.Contains("UseNativeSockets", File.ReadAllText(sidecar));
    }

    [Fact]
    public void Get_UnknownId_CreatesAndCachesOneInstance()
    {
        string uid = ConfigTestSupport.NewStackId();
        var first = BasisTransportConfigStore.Get<LNLTransportConfig>(uid);
        var second = BasisTransportConfigStore.Get<LNLTransportConfig>(uid);
        Assert.Same(first, second);
        Assert.Same(first, BasisTransportConfigStore.Get(uid));
    }

    [Fact]
    public void Get_EmptyOrNullId_RoutesToDefaultStack()
    {
        EnsureLiteNetLibRegistered();
        var direct = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
        Assert.Same(direct, BasisTransportConfigStore.Get<LNLTransportConfig>(""));
        Assert.Same(direct, BasisTransportConfigStore.Get<LNLTransportConfig>(null!));
        Assert.Null(BasisTransportConfigStore.Get(""));
        Assert.Null(BasisTransportConfigStore.Get(null!));
    }

    [Fact]
    public void Set_StoresInstance_AndArgumentGuardsThrow()
    {
        string uid = ConfigTestSupport.NewStackId();
        var mine = new LNLTransportConfig { PingInterval = 4242 };
        BasisTransportConfigStore.Set(uid, mine);
        Assert.Same(mine, BasisTransportConfigStore.Get<LNLTransportConfig>(uid));

        Assert.Throws<ArgumentException>(() => BasisTransportConfigStore.Set("", new LNLTransportConfig()));
        Assert.Throws<ArgumentNullException>(() => BasisTransportConfigStore.Set<LNLTransportConfig>(uid, null!));
        Assert.Throws<ArgumentException>(() => BasisTransportConfigStore.RegisterType("", typeof(LNLTransportConfig)));
        Assert.Throws<ArgumentNullException>(() => BasisTransportConfigStore.RegisterType(uid, null!));
    }

    [Fact]
    public void RegisterType_ListsType_AndReRegistrationKeepsExistingConfig()
    {
        EnsureLiteNetLibRegistered();
        Assert.Equal(typeof(LNLTransportConfig),
            BasisTransportConfigStore.RegisteredTypes[BasisNetworkStackRegistry.LiteNetLibId]);

        string uid = ConfigTestSupport.NewStackId();
        BasisTransportConfigStore.RegisterType(uid, typeof(LNLTransportConfig));
        Assert.True(BasisTransportConfigStore.RegisteredTypes.ContainsKey(uid));

        var first = BasisTransportConfigStore.Get<LNLTransportConfig>(uid);
        BasisTransportConfigStore.RegisterType(uid, typeof(LNLTransportConfig));
        Assert.Same(first, BasisTransportConfigStore.Get<LNLTransportConfig>(uid));
    }
}

/// <summary>ConnectionTarget property-bag semantics.</summary>
public class ConnectionTargetTests
{
    [Fact]
    public void Constructor_NormalizesNullsToEmpty()
    {
        var target = new ConnectionTarget(null!, null!);
        Assert.Equal("", target.StackId);
        Assert.Equal("", target.Raw);
    }

    [Fact]
    public void SetAndGet_KeysAreCaseInsensitive()
    {
        var target = new ConnectionTarget("litenetlib", "raw");
        target.Set("Address", "example.com");
        Assert.Equal("example.com", target.Get("ADDRESS"));
        Assert.Equal("example.com", target.Get(ConnectionTarget.Keys.Address));
        Assert.True(target.TryGet("address", out string? value));
        Assert.Equal("example.com", value);
    }

    [Fact]
    public void Set_NullValueStoresEmpty_AndEmptyKeyIsIgnored()
    {
        var target = new ConnectionTarget();
        target.Set(ConnectionTarget.Keys.Password, null!);
        Assert.Equal("", target.Get(ConnectionTarget.Keys.Password));

        target.Set("", "value");
        Assert.Single(target.Properties);
    }

    [Fact]
    public void Get_UnknownOrNullKey_ReturnsFallback()
    {
        var target = new ConnectionTarget();
        Assert.Null(target.Get("missing"));
        Assert.Equal("fallback", target.Get("missing", "fallback"));
        Assert.Equal("fallback", target.Get(null!, "fallback"));
        Assert.False(target.TryGet("missing", out string? value));
        Assert.Null(value);
        Assert.False(target.TryGet(null!, out _));
    }

    [Fact]
    public void KeyConstants_AreStable()
    {
        Assert.Equal("address", ConnectionTarget.Keys.Address);
        Assert.Equal("port", ConnectionTarget.Keys.Port);
        Assert.Equal("password", ConnectionTarget.Keys.Password);
        Assert.Equal("lobbyId", ConnectionTarget.Keys.LobbyId);
    }
}

/// <summary>host:port / [IPv6]:port / #password connection-string parsing and formatting.</summary>
public class LNLConnectionTargetParserTests
{
    [Theory]
    [InlineData("example.com:5000", "example.com", 5000, true, "")]
    [InlineData("example.com", "example.com", 4296, false, "")]
    [InlineData("192.168.1.5:4297", "192.168.1.5", 4297, true, "")]
    [InlineData("example.com:5000#hunter2", "example.com", 5000, true, "hunter2")]
    [InlineData("example.com#hunter2", "example.com", 4296, false, "hunter2")]
    [InlineData("[::1]:5001", "::1", 5001, true, "")]
    [InlineData("[2001:db8::1]", "2001:db8::1", 4296, false, "")]
    [InlineData("[2001:db8::1]:5002#pw", "2001:db8::1", 5002, true, "pw")]
    [InlineData("::1", "::1", 4296, false, "")]
    [InlineData("2001:db8::1", "2001:db8::1", 4296, false, "")]
    [InlineData("[::1", "[::1", 4296, false, "")]
    [InlineData("host:0", "host:0", 4296, false, "")]
    [InlineData("host:65536", "host:65536", 4296, false, "")]
    [InlineData("host:", "host:", 4296, false, "")]
    [InlineData("host:abc", "host:abc", 4296, false, "")]
    [InlineData("  example.com  ", "example.com", 4296, false, "")]
    public void TryParse_HandlesHostsPortsIpv6AndPasswords(
        string raw, string expectedAddress, int expectedPort, bool expectedPortProvided, string expectedPassword)
    {
        bool ok = LNLConnectionTargetParser.TryParseConnectionString(
            raw, out string address, out ushort port, out bool portProvided, out string password);

        Assert.True(ok);
        Assert.Equal(expectedAddress, address);
        Assert.Equal(expectedPort, port);
        Assert.Equal(expectedPortProvided, portProvided);
        Assert.Equal(expectedPassword, password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#pw")]
    public void TryParse_RejectsInputWithoutAnAddress(string? raw)
    {
        bool ok = LNLConnectionTargetParser.TryParseConnectionString(
            raw!, out string address, out ushort port, out bool portProvided, out _);

        Assert.False(ok);
        Assert.Equal("", address);
        Assert.Equal(LNLConnectionTargetParser.DefaultPort, port);
        Assert.False(portProvided);
    }

    [Fact]
    public void Parse_PopulatesAddressPortAndPassword()
    {
        var parser = new LNLConnectionTargetParser();
        var target = new ConnectionTarget(BasisNetworkStackRegistry.LiteNetLibId, "example.com:5000#pw");
        parser.Parse(target);

        Assert.Equal("example.com", target.Get(ConnectionTarget.Keys.Address));
        Assert.Equal("5000", target.Get(ConnectionTarget.Keys.Port));
        Assert.Equal("pw", target.Get(ConnectionTarget.Keys.Password));
    }

    [Fact]
    public void Parse_NullTarget_DoesNotThrow()
    {
        new LNLConnectionTargetParser().Parse(null!);
    }

    [Fact]
    public void Parse_UnparseableRaw_LeavesPropertiesUnset()
    {
        var parser = new LNLConnectionTargetParser();
        var target = new ConnectionTarget(BasisNetworkStackRegistry.LiteNetLibId, "");
        parser.Parse(target);
        Assert.Null(target.Get(ConnectionTarget.Keys.Address));
        Assert.Null(target.Get(ConnectionTarget.Keys.Port));
    }

    [Fact]
    public void Format_HostAndPassword()
    {
        var parser = new LNLConnectionTargetParser();
        var target = new ConnectionTarget();
        target.Set(ConnectionTarget.Keys.Address, "example.com");
        target.Set(ConnectionTarget.Keys.Port, "5000");
        Assert.Equal("example.com:5000", parser.Format(target));

        target.Set(ConnectionTarget.Keys.Password, "pw");
        Assert.Equal("example.com:5000#pw", parser.Format(target));
    }

    [Fact]
    public void Format_BracketsIpv6Addresses()
    {
        var parser = new LNLConnectionTargetParser();
        var target = new ConnectionTarget();
        target.Set(ConnectionTarget.Keys.Address, "::1");
        target.Set(ConnectionTarget.Keys.Port, "5001");
        Assert.Equal("[::1]:5001", parser.Format(target));
    }

    [Fact]
    public void Format_DefaultsPort_AndReturnsEmptyForNullTarget()
    {
        var parser = new LNLConnectionTargetParser();
        var target = new ConnectionTarget();
        target.Set(ConnectionTarget.Keys.Address, "127.0.0.1");
        Assert.Equal("127.0.0.1:4296", parser.Format(target));
        Assert.Equal(string.Empty, parser.Format(null!));
    }

    [Theory]
    [InlineData("example.com:5000#pw")]
    [InlineData("[2001:db8::1]:4296")]
    [InlineData("10.0.0.2:4297")]
    [InlineData("example.com")]
    public void ParseFormatParse_RoundTripsAddressPortAndPassword(string raw)
    {
        var parser = new LNLConnectionTargetParser();
        var first = new ConnectionTarget(BasisNetworkStackRegistry.LiteNetLibId, raw);
        parser.Parse(first);

        var second = new ConnectionTarget(BasisNetworkStackRegistry.LiteNetLibId, parser.Format(first));
        parser.Parse(second);

        Assert.Equal(first.Get(ConnectionTarget.Keys.Address), second.Get(ConnectionTarget.Keys.Address));
        Assert.Equal(first.Get(ConnectionTarget.Keys.Port), second.Get(ConnectionTarget.Keys.Port));
        Assert.Equal(first.Get(ConnectionTarget.Keys.Password), second.Get(ConnectionTarget.Keys.Password));
    }
}

/// <summary>
/// BasisNetworkStackRegistry register/lookup semantics. All mutations use unique test stack ids;
/// the active stack id is restored after each test that changes it.
/// </summary>
public class NetworkStackRegistryTests
{
    private sealed class RecordingParser : IConnectionTargetParser
    {
        public void Parse(ConnectionTarget target) { }
        public string Format(ConnectionTarget target) => "custom";
    }

    private sealed class StubIntroducer : IPeerIntroducer
    {
        public bool Initialize(NetManager activeManager) => true;
        public void Introduce(IPEndPoint aInternal, IPEndPoint aExternal, IPEndPoint bInternal, IPEndPoint bExternal, string token) { }
        public bool IsPairOffloaded(int peerIdA, int peerIdB) => false;
        public void Shutdown() { }
    }

    [Fact]
    public void DefaultStack_IsLiteNetLib()
    {
        Assert.Equal("litenetlib", BasisNetworkStackRegistry.LiteNetLibId);
        Assert.Equal(BasisNetworkStackRegistry.LiteNetLibId, BasisNetworkStackRegistry.DefaultId);
        Assert.True(BasisNetworkStackRegistry.IsRegistered(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal("LiteNetLib", BasisNetworkStackRegistry.GetDisplayName(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal(1, BasisNetworkStackRegistry.Stacks.Count(
            s => string.Equals(s.Id, BasisNetworkStackRegistry.LiteNetLibId, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void IsRegistered_HandlesCaseNullEmptyAndUnknown()
    {
        Assert.True(BasisNetworkStackRegistry.IsRegistered("LITENETLIB"));
        Assert.False(BasisNetworkStackRegistry.IsRegistered(null!));
        Assert.False(BasisNetworkStackRegistry.IsRegistered(""));
        Assert.False(BasisNetworkStackRegistry.IsRegistered("unknown-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void GetParser_FallsBackToDefaultStackParser()
    {
        var byId = BasisNetworkStackRegistry.GetParser(BasisNetworkStackRegistry.LiteNetLibId);
        Assert.IsType<LNLConnectionTargetParser>(byId);
        Assert.Same(byId, BasisNetworkStackRegistry.GetParser(""));
        Assert.Same(byId, BasisNetworkStackRegistry.GetParser(null!));
        Assert.Same(byId, BasisNetworkStackRegistry.GetParser("unknown-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Register_DuplicateId_IsIgnored()
    {
        BasisNetworkStackRegistry.Register(BasisNetworkStackRegistry.LiteNetLibId, "Imposter", (listener, configuration) => null!);
        Assert.Equal("LiteNetLib", BasisNetworkStackRegistry.GetDisplayName(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal(1, BasisNetworkStackRegistry.Stacks.Count(
            s => string.Equals(s.Id, BasisNetworkStackRegistry.LiteNetLibId, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Register_NewStack_SupportsLookupsAndParserRegistration()
    {
        string uid = ConfigTestSupport.NewStackId();
        BasisNetworkStackRegistry.Register(uid, "Test Stack", (listener, configuration) => null!);

        Assert.True(BasisNetworkStackRegistry.IsRegistered(uid));
        Assert.Equal("Test Stack", BasisNetworkStackRegistry.GetDisplayName(uid));
        Assert.Contains(BasisNetworkStackRegistry.Stacks, s => s.Id == uid && s.DisplayName == "Test Stack");

        // no parser yet: falls back to the default stack's parser
        Assert.Same(BasisNetworkStackRegistry.GetParser(BasisNetworkStackRegistry.DefaultId),
            BasisNetworkStackRegistry.GetParser(uid));

        var parser = new RecordingParser();
        BasisNetworkStackRegistry.RegisterParser(uid, parser);
        Assert.Same(parser, BasisNetworkStackRegistry.GetParser(uid));
    }

    [Fact]
    public void GetDisplayName_FallsBackToIdWhenUnknownOrUnnamed()
    {
        string uid = ConfigTestSupport.NewStackId();
        BasisNetworkStackRegistry.Register(uid, "", (listener, configuration) => null!);
        Assert.Equal(uid, BasisNetworkStackRegistry.GetDisplayName(uid));

        string unknown = "unknown-" + Guid.NewGuid().ToString("N");
        Assert.Equal(unknown, BasisNetworkStackRegistry.GetDisplayName(unknown));
        Assert.Equal("LiteNetLib", BasisNetworkStackRegistry.GetDisplayName(null!));
    }

    [Fact]
    public void Registration_ArgumentGuardsThrow()
    {
        Assert.Throws<ArgumentException>(() => BasisNetworkStackRegistry.Register("", "x", (listener, configuration) => null!));
        Assert.Throws<ArgumentNullException>(() => BasisNetworkStackRegistry.Register(ConfigTestSupport.NewStackId(), "x", null!));
        Assert.Throws<ArgumentException>(() => BasisNetworkStackRegistry.RegisterParser("", new RecordingParser()));
        Assert.Throws<ArgumentNullException>(() => BasisNetworkStackRegistry.RegisterParser(ConfigTestSupport.NewStackId(), null!));
    }

    [Fact]
    public void RegisteringHooksForUnknownStack_WarnsWithoutThrowing()
    {
        string unknown = "unknown-" + Guid.NewGuid().ToString("N");
        BasisNetworkStackRegistry.RegisterParser(unknown, new RecordingParser());
        BasisNetworkStackRegistry.RegisterTick(unknown, () => { });
        BasisNetworkStackRegistry.RegisterProbe(unknown, (target, timeoutMs, ct) => Task.FromResult(new ServerProbeResult()));
        BasisNetworkStackRegistry.RegisterIntroducerFactory(unknown, manager => new StubIntroducer());
        Assert.False(BasisNetworkStackRegistry.IsRegistered(unknown));
    }

    [Fact]
    public void SetActiveStackId_FiresEventOnlyOnRealChange()
    {
        string original = BasisNetworkStackRegistry.ActiveStackId;
        var events = new List<string>();
        Action<string> handler = events.Add;
        BasisNetworkStackRegistry.ActiveStackChanged += handler;
        try
        {
            string uid = ConfigTestSupport.NewStackId();
            BasisNetworkStackRegistry.SetActiveStackId(uid);
            Assert.Equal(uid, BasisNetworkStackRegistry.ActiveStackId);
            Assert.Equal(new[] { uid }, events);

            BasisNetworkStackRegistry.SetActiveStackId(uid);
            Assert.Single(events);

            // case-only change is not a change
            BasisNetworkStackRegistry.SetActiveStackId(uid.ToUpperInvariant());
            Assert.Single(events);
            Assert.Equal(uid, BasisNetworkStackRegistry.ActiveStackId);
        }
        finally
        {
            BasisNetworkStackRegistry.ActiveStackChanged -= handler;
            BasisNetworkStackRegistry.SetActiveStackId(original);
        }
    }

    [Fact]
    public void TickActive_InvokesRegisteredTick_AndSwallowsExceptions()
    {
        string original = BasisNetworkStackRegistry.ActiveStackId;
        string uid = ConfigTestSupport.NewStackId();
        int ticks = 0;
        BasisNetworkStackRegistry.Register(uid, "Tick Stack", (listener, configuration) => null!);
        BasisNetworkStackRegistry.RegisterTick(uid, () => { ticks++; throw new InvalidOperationException("boom"); });
        try
        {
            BasisNetworkStackRegistry.SetActiveStackId(uid);
            BasisNetworkStackRegistry.TickActive();
            BasisNetworkStackRegistry.TickActive();
            Assert.Equal(2, ticks);

            BasisNetworkStackRegistry.SetActiveStackId("");
            BasisNetworkStackRegistry.TickActive();
            Assert.Equal(2, ticks);
        }
        finally
        {
            BasisNetworkStackRegistry.SetActiveStackId(original);
        }
    }

    [Fact]
    public void CreateIntroducer_UsesFactoryRegisteredForStack()
    {
        string uid = ConfigTestSupport.NewStackId();
        var introducer = new StubIntroducer();
        BasisNetworkStackRegistry.Register(uid, "Introducer Stack", (listener, configuration) => null!);
        BasisNetworkStackRegistry.RegisterIntroducerFactory(uid, manager => introducer);

        Assert.Same(introducer, BasisNetworkStackRegistry.CreateIntroducer(uid, null!));
    }

    [Fact]
    public async Task ProbeAsync_NullTarget_ReportsError()
    {
        ServerProbeResult result = await BasisNetworkStackRegistry.ProbeAsync(null!, 50, CancellationToken.None);
        Assert.False(result.Reachable);
        Assert.Equal("Target is null", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_UsesProbeRegisteredForStack()
    {
        string uid = ConfigTestSupport.NewStackId();
        BasisNetworkStackRegistry.Register(uid, "Probe Stack", (listener, configuration) => null!);
        BasisNetworkStackRegistry.RegisterProbe(uid, (target, timeoutMs, ct) =>
            Task.FromResult(new ServerProbeResult { Reachable = true, Name = "probe-stub", RoundTripMs = timeoutMs }));

        ServerProbeResult result = await BasisNetworkStackRegistry.ProbeAsync(
            new ConnectionTarget(uid, "example.com"), 123, CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal("probe-stub", result.Name);
        Assert.Equal(123, result.RoundTripMs);
    }
}

/// <summary>
/// Read-only pins of the BasisServerMessageRegistry inbound binding table. EnsureInitialized only
/// fills the handler array with lambdas (no live server callbacks), so inspecting it has no side
/// effects. All registry-mutating tests live in ServerMessageRegistryTests
/// (PermissionAndMessageCatalogTests.cs); this class stays presence-only so both can run in parallel.
/// </summary>
public class ServerMessageRegistryBindingTableTests
{
    private static readonly byte[] ExpectedInboundChannels =
    {
        BasisNetworkCommons.AuthIdentityChannel,
        BasisNetworkCommons.VoiceChannel,
        BasisNetworkCommons.ShoutVoiceChannel,
        BasisNetworkCommons.AudioRecipientsChannel,
        BasisNetworkCommons.PlayerAvatarHighChannel,
        BasisNetworkCommons.PlayerAvatarHighAdditionalChannel,
        BasisNetworkCommons.AvatarChangeMessageChannel,
        BasisNetworkCommons.AvatarChannel,
        BasisNetworkCommons.ChatChannel,
        BasisNetworkCommons.GetCurrentOwnerRequestChannel,
        BasisNetworkCommons.ChangeCurrentOwnerRequestChannel,
        BasisNetworkCommons.RemoveCurrentOwnerRequestChannel,
        BasisNetworkCommons.netIDAssignChannel,
        BasisNetworkCommons.SceneChannel,
        BasisNetworkCommons.LoadResourceChannel,
        BasisNetworkCommons.UnloadResourceChannel,
        BasisNetworkCommons.PreloadReadyChannel,
        BasisNetworkCommons.ContentShareChannel,
        BasisNetworkCommons.DeltaAvatarChannel,
        BasisNetworkCommons.ServerBoundChannel,
        BasisNetworkCommons.StoreDatabaseChannel,
        BasisNetworkCommons.RequestStoreDatabaseChannel,
        BasisNetworkCommons.AdminChannel,
        BasisNetworkCommons.ServerStatisticsChannel,
        BasisNetworkCommons.CameraPIPStateChannel,
        BasisNetworkCommons.CameraPIPPositionChannel,
        BasisNetworkCommons.EventsChannel,
        BasisNetworkCommons.AudioRecipientsLargeChannel,
        BasisNetworkCommons.AudioRecipientsInvertedChannel,
        BasisNetworkCommons.AudioRecipientsInvertedLargeChannel,
        BasisNetworkCommons.AudioRecipientsBitfieldChannel,
        BasisNetworkCommons.P2PChannel,
        BasisNetworkCommons.ModifyResourceChannel,
        BasisNetworkCommons.DirectSceneServerChannel,
        BasisNetworkCommons.DirectAvatarServerChannel,
        BasisNetworkCommons.RegistryControlChannel,
    };

    [Fact]
    public void ExpectedInboundChannelTable_IsDistinctInRange_AndIncludesUplinkDelta()
    {
        Assert.Equal(ExpectedInboundChannels.Length, ExpectedInboundChannels.Distinct().Count());
        Assert.All(ExpectedInboundChannels, channel => Assert.True(channel < BasisNetworkCommons.TotalChannels));
        Assert.Contains(BasisNetworkCommons.DeltaAvatarChannel, ExpectedInboundChannels);
    }

    [Fact]
    public void EveryExpectedInboundChannel_HasACoreHandlerBound()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        foreach (byte channel in ExpectedInboundChannels)
        {
            Assert.True(BasisServerMessageRegistry.ResolveCore(channel) != null,
                $"channel {channel} should have an inbound core handler bound");
        }
    }

    [Fact]
    public void AvatarMovementChannels_ShareOneHandler()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        Assert.Same(
            BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.PlayerAvatarHighChannel),
            BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel));
    }

    [Fact]
    public void PluginChannels_HaveNoCoreBinding_SoMultiplexDispatchIsReachable()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        for (byte channel = BasisNetworkCommons.PluginReliableChannel; channel <= BasisNetworkCommons.PluginUnreliableChannel; channel++)
        {
            Assert.True(BasisNetworkCommons.IsPluginChannel(channel));
            Assert.Null(BasisServerMessageRegistry.ResolveCore(channel));
        }
    }
}

/// <summary>BasisConfigXmlDocs: doc-comment injection, version stamping, and missing-field detection.</summary>
public class ConfigXmlDocsTests
{
    private static string SerializeWithDocs(Type type, object value)
    {
        using var writer = new StringWriter();
        BasisConfigXmlDocs.Serialize(new XmlSerializer(type), type, value, writer);
        return writer.ToString();
    }

    private static void WriteCompleteConfigFile(string path, Configuration cfg)
    {
        using var writer = new StreamWriter(path);
        BasisConfigXmlDocs.Serialize(new XmlSerializer(typeof(Configuration)), typeof(Configuration), cfg, writer);
    }

    [Fact]
    public void Serialize_InjectsServerConfigHeaderSectionAndFieldComments()
    {
        string xml = SerializeWithDocs(typeof(Configuration), new Configuration());
        Assert.Contains("<!--", xml);
        Assert.Contains("Basis dedicated-server configuration", xml);
        Assert.Contains("===== Networking / listener =====", xml);
        Assert.Contains("Maximum number of simultaneously connected peers", xml);
        Assert.Contains("<PeerLimit>65535</PeerLimit>", xml);
    }

    [Fact]
    public void Serialize_InjectsLnlTransportDocComments()
    {
        string xml = SerializeWithDocs(typeof(LNLTransportConfig), new LNLTransportConfig());
        Assert.Contains("LiteNetLib transport tuning", xml);
        Assert.Contains("<MultiSocketCount>1</MultiSocketCount>", xml);
    }

    [Fact]
    public void Serialize_TypeWithoutRegisteredDocs_EmitsNoComments()
    {
        string xml = SerializeWithDocs(typeof(DocLessTestConfig), new DocLessTestConfig());
        Assert.DoesNotContain("<!--", xml);
        Assert.Contains("<Value>3</Value>", xml);
    }

    [Fact]
    public void StampVersion_SetsInstanceToTypeCurrentVersion()
    {
        var serverCfg = new Configuration();
        BasisConfigXmlDocs.StampVersion(serverCfg);
        Assert.Equal(Configuration.CurrentConfigVersion, serverCfg.ConfigVersion);

        var lnlCfg = new LNLTransportConfig();
        BasisConfigXmlDocs.StampVersion(lnlCfg);
        Assert.Equal(LNLTransportConfig.CurrentConfigVersion, lnlCfg.ConfigVersion);

        BasisConfigXmlDocs.StampVersion(null!);
        BasisConfigXmlDocs.StampVersion(new DocLessTestConfig());
    }

    [Fact]
    public void NeedsUpgrade_TrueWhenStampedVersionIsBehind()
    {
        var cfg = new Configuration(); // ConfigVersion 0 < CurrentConfigVersion
        Assert.True(BasisConfigXmlDocs.NeedsUpgrade("Z:\\does\\not\\exist.xml", typeof(Configuration), cfg));
    }

    [Fact]
    public void NeedsUpgrade_FalseWhenCurrentVersionAndFileComplete()
    {
        using var dir = new ConfigTestSupport.TempDir();
        string path = dir.File("config.xml");
        var cfg = new Configuration();
        BasisConfigXmlDocs.StampVersion(cfg);
        WriteCompleteConfigFile(path, cfg);

        Assert.False(BasisConfigXmlDocs.NeedsUpgrade(path, typeof(Configuration), cfg));
        Assert.False(BasisConfigXmlDocs.IsMissingAnyField(path, typeof(Configuration)));
    }

    [Fact]
    public void IsMissingAnyField_DetectsARemovedElement()
    {
        using var dir = new ConfigTestSupport.TempDir();
        string path = dir.File("config.xml");
        WriteCompleteConfigFile(path, new Configuration());

        XDocument doc = XDocument.Load(path);
        doc.Root!.Element("PeerLimit")!.Remove();
        doc.Save(path);

        Assert.True(BasisConfigXmlDocs.IsMissingAnyField(path, typeof(Configuration)));
    }

    [Fact]
    public void IsMissingAnyField_MissingOrUnreadableFile_ReportsFalse()
    {
        using var dir = new ConfigTestSupport.TempDir();
        Assert.False(BasisConfigXmlDocs.IsMissingAnyField(dir.File("nope.xml"), typeof(Configuration)));

        string garbage = dir.File("garbage.xml");
        File.WriteAllText(garbage, "not xml <<<>");
        Assert.False(BasisConfigXmlDocs.IsMissingAnyField(garbage, typeof(Configuration)));
    }
}
