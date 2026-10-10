using Basis.Network.Core;
using System.Reflection;
using System.Xml.Serialization;
using Xunit;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class LiteNetLibRegistrationTests
{
    [Fact]
    public void LiteNetLibRegistersAsTheDefaultStack()
    {
        BasisLiteNetLibTransport.Register();
        Assert.Equal(BasisLiteNetLibTransport.StackId, BasisNetworkStackRegistry.DefaultId);
        Assert.True(BasisNetworkStackRegistry.IsRegistered(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal("LiteNetLib", BasisNetworkStackRegistry.GetDisplayName(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal(1, BasisNetworkStackRegistry.Stacks.Count(
            s => string.Equals(s.Id, BasisNetworkStackRegistry.LiteNetLibId, StringComparison.OrdinalIgnoreCase)));
        Assert.IsType<HostPortConnectionTargetParser>(BasisNetworkStackRegistry.GetParser(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal(typeof(LNLTransportConfig), BasisTransportConfigStore.RegisteredTypes[BasisNetworkStackRegistry.LiteNetLibId]);
    }

    [Fact]
    public void Register_DuplicateId_IsIgnored()
    {
        BasisLiteNetLibTransport.Register();
        BasisNetworkStackRegistry.Register(BasisNetworkStackRegistry.LiteNetLibId, "Imposter", (listener, configuration) => null!);
        Assert.Equal("LiteNetLib", BasisNetworkStackRegistry.GetDisplayName(BasisNetworkStackRegistry.LiteNetLibId));
        Assert.Equal(1, BasisNetworkStackRegistry.Stacks.Count(
            s => string.Equals(s.Id, BasisNetworkStackRegistry.LiteNetLibId, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void LiteNetLibJoinsACompositeAndIsFoundByType()
    {
        BasisLiteNetLibTransport.Register();
        string original = BasisNetworkStackRegistry.ActiveStackId;
        string other = ConfigTestSupport.NewStackId();
        BasisNetworkStackRegistry.Register(other, "Other", (listener, configuration) => new ScriptedTransport(other, listener));
        try
        {
            NetManager both = BasisNetworkStackRegistry.Create($"litenetlib, {other}", new EventBasedNetListener(), new Configuration());
            BasisMultiTransportNetManager composite = Assert.IsType<BasisMultiTransportNetManager>(both);
            Assert.IsType<LNLNetManager>(composite.Transports[0]);
            Assert.NotNull(both.LiteNetLibManager());
            Assert.NotNull(both.FindCapability<IBasisTransportScaling>());

            NetManager one = BasisNetworkStackRegistry.Create(other, new EventBasedNetListener(), new Configuration());
            Assert.Null(one.LiteNetLibManager());
            Assert.Null(one.FindCapability<IBasisTransportScaling>());
        }
        finally
        {
            BasisNetworkStackRegistry.SetActiveStackId(original);
        }
    }

    [Fact]
    public void LiteNetLibOffersPeerIntroductionAndDirectSockets()
    {
        BasisLiteNetLibTransport.Register();
        Assert.Null(BasisNetworkStackRegistry.CreateIntroducer(ConfigTestSupport.NewStackId(), null!));
        IBasisP2PSocket socket = BasisNetworkStackRegistry.CreateP2PSocket(BasisNetworkStackRegistry.LiteNetLibId, new EventBasedNetListener(), (target, sameNetwork, token) => { });
        Assert.IsType<BasisLiteNetLibP2PSocket>(socket);
        Assert.True(socket.Start());
        try
        {
            Assert.True(socket.LocalPort > 0);
        }
        finally
        {
            socket.Stop();
        }
    }
}

[Collection("Basis config file statics")]
public class LiteNetLibConfigFileTests
{
    [Fact]
    public void LoadingTheServerConfigWritesTheLiteNetLibSidecar()
    {
        BasisLiteNetLibTransport.Register();
        using var dir = new ConfigTestSupport.TempDir();
        Configuration.LoadFromXml(dir.File("config.xml"));
        string sidecar = Path.Combine(dir.Path, BasisTransportConfigStore.TransportsFolderName,
            BasisNetworkStackRegistry.LiteNetLibId + ".xml");
        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public void Serialize_InjectsLnlTransportDocComments()
    {
        BasisLiteNetLibTransport.Register();
        using var writer = new StringWriter();
        BasisConfigXmlDocs.Serialize(new XmlSerializer(typeof(LNLTransportConfig)), typeof(LNLTransportConfig), new LNLTransportConfig(), writer);
        string xml = writer.ToString();
        Assert.Contains("LiteNetLib transport tuning", xml);
        Assert.Contains("<MultiSocketCount>1</MultiSocketCount>", xml);
    }

    [Fact]
    public void StampVersionStampsTheLiteNetLibConfig()
    {
        var lnlCfg = new LNLTransportConfig();
        BasisConfigXmlDocs.StampVersion(lnlCfg);
        Assert.Equal(LNLTransportConfig.CurrentConfigVersion, lnlCfg.ConfigVersion);
    }

    [Fact]
    public void TransportConfigsReportTheirDisconnectTimeout()
    {
        IBasisTransportTimeouts timeouts = new LNLTransportConfig { DisconnectTimeout = 4321 };
        Assert.Equal(4321, timeouts.DisconnectTimeoutMs);
    }
}

public class LiteNetLibConfigMigrationTests
{
    [Fact]
    public void Migration_RetiresTheOldDefaultsButKeepsDeliberateValues()
    {
        var legacy = new LNLTransportConfig { MaxUnreliableQueuePerPeer = 256, PacketPoolSizeMax = 262144 };
        legacy.MigrateFrom(7);
        Assert.Equal(0, legacy.MaxUnreliableQueuePerPeer);
        Assert.Equal(0, legacy.PacketPoolSizeMax);

        // Someone who pinned a value meant it. Only the exact shipped defaults are retired,
        // because those are the ones nobody chose.
        var deliberate = new LNLTransportConfig { MaxUnreliableQueuePerPeer = 1024, PacketPoolSizeMax = 100000 };
        deliberate.MigrateFrom(7);
        Assert.Equal(1024, deliberate.MaxUnreliableQueuePerPeer);
        Assert.Equal(100000, deliberate.PacketPoolSizeMax);
    }

    [Fact]
    public void Migration_DoesNotReRunOnCurrentFiles()
    {
        // A file already at version 8 that says 256 means 256 — by then it can only have got there
        // by someone typing it.
        var current = new LNLTransportConfig { MaxUnreliableQueuePerPeer = 256 };
        current.MigrateFrom(LNLTransportConfig.CurrentConfigVersion);
        Assert.Equal(256, current.MaxUnreliableQueuePerPeer);
    }
}

[Collection("BasisCpuBudget")]
public class LiteNetLibCoreBudgetTests
{
    /// <summary>
    /// The same squeeze against the transport's per-peer pool, which sizes itself the same way and
    /// did not survive it: its floor went to Math.Clamp as a min under the grant as a max, and a
    /// grant of 3 threw '4' cannot be greater than 3 out of the whole logic pass. That pass is
    /// where reliable delivery, peer timeouts and NTP live, and the throw lands before the loop's
    /// sleep — so the failure was not one dropped pass but a spin producing nothing but errors.
    /// </summary>
    [Fact]
    public void PeerUpdateSizingSurvivesAGrantBelowItsFloor()
    {
        var squeeze = new BasisCoreLease[Math.Max(8, BasisCpuBudget.TotalCores * 2)];
        for (int i = 0; i < squeeze.Length; i++)
        {
            squeeze[i] = BasisCpuBudget.Register("test-peer-squeeze-" + i, 1, () => 4096, 8.0);
        }

        var manager = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener());
        var optionsFor = typeof(LiteNetLib.NetManager)
            .GetMethod("GetPeerUpdateOptions", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(optionsFor);

        try
        {
            foreach (var l in squeeze) l.ReportDemand(1.0);

            bool sawGrantUnderFloor = false;
            for (int step = 0; step < 200; step++)
            {
                BasisCpuBudget.Rebalance();

                // What the host does every tick: the transport runs on whatever the split gave it.
                manager.PeerUpdateWorkerCap = BasisCpuBudget.PeerUpdateCap;
                if (BasisCpuBudget.PeerUpdateCap < BasisCpuBudget.MinWorkersPerPool)
                {
                    sawGrantUnderFloor = true;
                }

                foreach (int peers in new[] { 0, 1, 200, 4000 })
                {
                    var options = (ParallelOptions)optionsFor.Invoke(manager, new object[] { peers })!;

                    Assert.True(options.MaxDegreeOfParallelism >= 1,
                        $"{options.MaxDegreeOfParallelism} is not a legal MaxDegreeOfParallelism");
                    Assert.True(options.MaxDegreeOfParallelism <= BasisCpuBudget.TotalCores,
                        $"{options.MaxDegreeOfParallelism} workers exceeds {BasisCpuBudget.TotalCores} cores");
                    Assert.True(options.MaxDegreeOfParallelism <= BasisCpuBudget.PeerUpdateCap,
                        $"{options.MaxDegreeOfParallelism} workers exceeds the grant of {BasisCpuBudget.PeerUpdateCap}");
                }
            }

            Assert.True(sawGrantUnderFloor,
                $"the squeeze never drove the grant under the floor of {BasisCpuBudget.MinWorkersPerPool}, " +
                "so this never exercised the case it exists for");
        }
        finally
        {
            foreach (var l in squeeze) BasisCpuBudget.Unregister(l);
            for (int i = 0; i < 200; i++) BasisCpuBudget.Rebalance();
        }
    }
}

/// <summary>Per-transport config sidecars ({configDir}/transports/{stackId}.xml) via the static store.</summary>
[Collection("Basis config file statics")]
public class LiteNetLibConfigStoreTests
{
    private static void EnsureLiteNetLibRegistered()
        => BasisLiteNetLibTransport.Register();

    private static string SidecarPath(string configDir)
        => Path.Combine(configDir, BasisTransportConfigStore.TransportsFolderName,
            BasisNetworkStackRegistry.LiteNetLibId + ".xml");

    [Fact]
    public void LnlTransportConfig_Defaults()
    {
        var cfg = new LNLTransportConfig();
        // 7: added MergeHoldMs, PeerUpdateParallelism, MaxUnreliableQueuePerPeer,
        // PeerUpdatePeersPerWorker and MaxSendSockets, so existing files get rewritten with them.
        // 8: MaxUnreliableQueuePerPeer and PacketPoolSizeMax became 0 = auto-scaled, and the old
        // fixed values are actively migrated away because they were harmful at scale.
        // 9: added CompactMerged, so existing files get rewritten with it.
        // 10: added MaxPriorityUnreliableQueuePerPeer, which splits voice out of the bulk queue.
        Assert.Equal(10, LNLTransportConfig.CurrentConfigVersion);
        Assert.Equal(0, cfg.MaxPriorityUnreliableQueuePerPeer);   // 0 = auto from population + memory
        Assert.True(cfg.CompactMerged);
        Assert.Equal(0, cfg.MaxSendSockets);   // 0 = auto: half the cores, 4 to 64
        Assert.Equal(0, cfg.PeerUpdatePeersPerWorker);
        Assert.Equal(0, cfg.MaxUnreliableQueuePerPeer);   // 0 = auto from population + memory
        Assert.Equal(0, cfg.ConfigVersion);
        Assert.Equal(3f, cfg.MergeHoldMs);
        Assert.Equal(0, cfg.PeerUpdateParallelism);
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
        Assert.Equal(0, cfg.PacketPoolSizeMax);   // 0 = auto from population + memory
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
