using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using BasisDidLink;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static BasisPermissions.PermissionManager;

public static class NetworkServer
{
    public static EventBasedNetListener Listener;
    public static NetManager Server;
    public static ConcurrentDictionary<int, NetPeer> AuthenticatedPeers = new();
    public static readonly object AuthenticatedPeerTag = new object();
    public static Configuration Configuration;
    /// <summary>
    /// Allow-list consulted at <see cref="BasisServerHandle.BasisServerHandleEvents.OnNetworkAccepted"/>
    /// when <see cref="Configuration.BasisUserRestrictionMode"/> is set to <c>AllowList</c>.
    /// File-backed (BasisAllowList.txt under the config folder) so admin-panel mutations
    /// persist across restarts.
    /// </summary>
    public static BasisNetworkServer.Security.BasisAllowList AllowList;
    public static BasisNetworkServer.Security.BasisBanList BanList;
    // Cached snapshot rebuilt on connect/disconnect — avoids ToArray() alloc on every broadcast.
    private static volatile NetPeer[] _peerSnapshot = Array.Empty<NetPeer>();
    // Guards the read-then-publish: OnNetworkAccepted runs on parallel DID-auth continuations, so
    // concurrent joins could otherwise lost-update _peerSnapshot to a stale array that drops a peer.
    private static readonly object _peerSnapshotLock = new object();
    public static NetPeer[] PeerSnapshot => _peerSnapshot;

    public static void RebuildPeerSnapshot()
    {
        lock (_peerSnapshotLock)
        {
            _peerSnapshot = AuthenticatedPeers.Values.ToArray();
        }
    }

    // Centralized NetDataWriter pool — single source of truth for all server code.
    // Capped so writers don't accumulate unboundedly after player count spikes.
    private static readonly ConcurrentQueue<NetDataWriter> _writerPool = new();
    // Depth follows the machine: this pool absorbs writers borrowed concurrently, and how many that
    // is scales with how many threads can be in flight. A literal cap was too small on a large host
    // (writers allocated instead of reused) and wasteful on a small one.
    private static readonly int MaxPooledWriters = BasisCpuBudget.ConcurrencyWidth(perCore: 4, min: 32, max: 2048);
    public static NetDataWriter RentWriter(int initialCapacity = 208)
    {
        if (_writerPool.TryDequeue(out var writer)) return writer;
        return new NetDataWriter(true, initialCapacity);
    }
    // Reset() only rewinds the cursor; the backing array keeps its high-water size forever. One
    // oversized serialization (a join batch, a resource blob) would otherwise park a permanently
    // inflated writer in the pool, and with enough of them the pool converges to
    // MaxPooledWriters x largest-payload-ever. Oversized writers are dropped instead.
    private const int MaxPooledWriterCapacity = 64 * 1024;
    public static void ReturnWriter(NetDataWriter writer)
    {
        writer.Reset();
        if (writer.Capacity <= MaxPooledWriterCapacity && _writerPool.Count < MaxPooledWriters)
        {
            _writerPool.Enqueue(writer);
        }
        // else: drop it — GC reclaims, keeps pool bounded
    }

    public static IAuth Auth;
    public static IAuthIdentity AuthIdentity;
    public static int HighQualityLength;
    #region Server Entry Point

    public static void StartServer(Configuration configuration)
    {
        StopServer();
        Configuration = configuration;

        // Rejoin-only lockdown means "the players here right now" — meaningless after a restart, and a
        // persisted RejoinOnly would boot with an empty snapshot and lock everyone out. Reset to Normal.
        if (configuration.BasisUserRestrictionMode == BasisNetworkCore.Security.BasisUserRestrictionMode.RejoinOnly)
            configuration.BasisUserRestrictionMode = BasisNetworkCore.Security.BasisUserRestrictionMode.Normal;

        HighQualityLength = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
        InitializePulseSettings();
        InitializeAuth();
        BasisHeadlessConnectionPolicyManager.InitializeFromConfig(configuration.DisallowHeadless);
        BasisNetworkServer.Security.BasisGlobalLockManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisCrashReportStateManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisAudioRangeLimitManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisAvatarScaleLimitManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisResourceLimitManager.InitializeFromConfig(configuration);
        SetupServer(configuration);
        SubscribeEvents(Configuration);

        if (configuration.EnableStatistics)
        {
            BasisStatistics.StartWorkerThread(Server);
        }

        BasisNetworkUdpDropMonitor.Start();
        BasisServerMemoryReclaim.Start();

        BNL.Log("Server Worker Threads Booted");
    }

    public static void StopServer()
    {
        if (Server == null) return;
        try
        {
            Server.Stop();
        }
        catch (Exception ex)
        {
            BNL.LogWarning($"NetworkServer.StopServer failed: {ex.Message}");
        }
        BasisNetworkUdpDropMonitor.Stop();
        BasisServerMemoryReclaim.Stop();
        // StartServer builds a fresh AuthIdentity; without this the old one stays subscribed to
        // the static OnAuthReceived event — pinned forever, and handling every auth packet twice.
        // Left non-null so a straggling disconnect event can still resolve UUIDs while stopping.
        try { AuthIdentity?.DeInitialize(); }
        catch (Exception ex) { BNL.LogWarning($"AuthIdentity.DeInitialize failed: {ex.Message}"); }
        Server = null;
        Listener = null;
        AuthenticatedPeers.Clear();
        _peerSnapshot = Array.Empty<NetPeer>();
    }

    public static void InitializePulseSettings()
    {
        BasisServerReductionSystemEvents.SetMaxDegreeOfParallelism(Configuration.BSRMaxDegreeOfParallelism);
        BasisServerReductionSystemEvents.SetSendPhaseBudgetPercent(Configuration.BSRSendPhaseBudgetPercent);
        int configuredMaxSockets = Basis.Network.Core.BasisTransportConfigStore
            .Get<Basis.Network.Core.LNLTransportConfig>(
                Basis.Network.Core.BasisNetworkStackRegistry.LiteNetLibId).MaxSendSockets;
        // 0 = auto, derived from the core count. See BasisCpuBudget.AutoMaxSendSockets.
        BasisServerReductionSystemEvents.MaxSendSockets = configuredMaxSockets > 0
            ? configuredMaxSockets
            : Basis.Network.Core.BasisCpuBudget.AutoMaxSendSockets;
        BasisServerReductionSystemEvents.BSRBaseMultiplier = Configuration.BSRBaseMultiplier;
        BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval = Configuration.BSRSMillisecondDefaultInterval;
        BasisServerReductionSystemEvents.BSRSIncreaseRate = Configuration.BSRSIncreaseRate;
        BasisServerReductionSystemEvents.DistanceUpdateIntervalTicks = Math.Max(1, Configuration.DistanceUpdateIntervalTicks);
        BasisServerReductionSystemEvents.EnableComputeOffload = Configuration.EnableComputeOffload;
        BasisServerReductionSystemEvents.ComputeDevice = Configuration.ComputeDevice;
        BasisServerReductionSystemEvents.ComputeDistanceUpdateIntervalTicks = Math.Max(1, Configuration.ComputeDistanceUpdateIntervalTicks);
        BasisServerReductionSystemEvents.HighDistanceSq = Configuration.HighQualityDistance * Configuration.HighQualityDistance;
        BasisServerReductionSystemEvents.MediumDistanceSq = Configuration.MediumQualityDistance * Configuration.MediumQualityDistance;
        BasisServerReductionSystemEvents.LowDistanceSq = Configuration.LowQualityDistance * Configuration.LowQualityDistance;
        BasisServerReductionSystemEvents.EnableAvatarBundleCompression = Configuration.EnableAvatarBundleCompression;
        BasisServerReductionSystemEvents.AvatarBundleMinMessages = Configuration.AvatarBundleMinMessages;
        BasisServerReductionSystemEvents.AvatarBundleMinBytes = Configuration.AvatarBundleMinBytes;
        BasisServerReductionSystemEvents.EnableAvatarBundleZstd = Configuration.EnableAvatarBundleZstd;
        BasisServerReductionSystemEvents.AvatarBundleZstdDeltaBundles = Configuration.AvatarBundleZstdDeltaBundles;
        BasisServerReductionSystemEvents.AvatarBundleZstdLevel = Configuration.AvatarBundleZstdLevel;
        BasisServerReductionSystemEvents.AvatarBundleZstdMaxShedTier = Configuration.AvatarBundleZstdMaxShedTier;
        // Level lives on the codec rather than being passed per call: it decides how the pooled
        // compression contexts are built, so a change has to invalidate them.
        Basis.Network.Core.Compression.BasisAvatarBundleZstd.SetLevel(Configuration.AvatarBundleZstdLevel);
        BasisServerReductionSystemEvents.EnableAvatarDeltaCompression = Configuration.EnableAvatarDeltaCompression;
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeIntervalMs = Configuration.AvatarDeltaKeyframeIntervalMs;
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeMaxIntervalMs = Configuration.AvatarDeltaKeyframeMaxIntervalMs;
        BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = Configuration.StripAdditionalDataAtLowQuality;
        BSRProfiler.Enabled = Configuration.EnableBSRProfiling || Configuration.HealthIncludeBSRProfiling;
        BSRProfiler.WriteToLog = Configuration.EnableBSRProfiling && !Configuration.HealthIncludeBSRProfiling;
        BasisServerReductionSystemEvents.WriteLoadLog = !Configuration.HealthIncludeBSRProfiling;
        // Re-broadcast when a (re)applied config changes the live value so already-connected
        // clients stay consistent with what new joiners are told (this also runs from the
        // admin reduction-settings reload, not just boot).
        if (BasisNetworkServer.Security.BasisOpusFrameDurationStateManager.SetFrameDurationMs(Configuration.VoiceFrameDurationMs))
        {
            BasisNetworkServer.Security.BasisOpusFrameDurationStateManager.BroadcastState();
        }
        // Report whether the Zstd path is actually live, not just whether it is configured on:
        // with no dictionary embedded the setting is inert, and that is exactly the state
        // someone reading this line after a bandwidth measurement needs to be able to tell apart.
        string zstdState = !Configuration.EnableAvatarBundleZstd ? "off"
            : !Basis.Network.Core.Compression.BasisAvatarBundleZstd.Available ? "INERT (no dictionary embedded)"
            : $"on (level {Configuration.AvatarBundleZstdLevel}, dictGen {Basis.Network.Core.Compression.BasisAvatarBundleZstd.DictionaryGeneration}, maxShedTier {Configuration.AvatarBundleZstdMaxShedTier}{(Configuration.AvatarBundleZstdDeltaBundles ? ", deltas too" : "")})";
        BNL.Log($"[BSR] AvatarBundleCompression={Configuration.EnableAvatarBundleCompression} (minMsgs={Configuration.AvatarBundleMinMessages}, minBytes={Configuration.AvatarBundleMinBytes}) BundleZstd={zstdState} DeltaCompression={Configuration.EnableAvatarDeltaCompression} (keyframeMs={Configuration.AvatarDeltaKeyframeIntervalMs}) VoiceFrameDurationMs={BasisNetworkServer.Security.BasisOpusFrameDurationStateManager.FrameDurationMs}");
    }

    /// <summary>
    /// Re-applies every setting that can take effect without a restart and pushes the new state to
    /// connected clients, so a runtime edit (console /config, admin panel) is live immediately.
    ///
    /// Deliberately not a re-run of StartServer's boot sequence: InitializeAuth builds a fresh
    /// BasisDIDAuthIdentity that subscribes to a static event and reloads permissions, allow and ban
    /// lists from disc, so calling it here would double-handle every auth packet and discard runtime
    /// moderation state. Only the password comparer is rebuilt. Bind-time settings are excluded and
    /// reported by <see cref="Configuration.RequiresRestart"/>.
    /// </summary>
    public static void ApplyLiveConfiguration()
    {
        if (Configuration == null) return;

        InitializePulseSettings();

        Auth = new PasswordAuth(Configuration.Password ?? string.Empty);

        BasisHeadlessConnectionPolicyManager.InitializeFromConfig(Configuration.DisallowHeadless);
        BasisNetworkServer.Security.BasisGlobalLockManager.InitializeFromConfig(Configuration);
        BasisNetworkServer.Security.BasisCrashReportStateManager.InitializeFromConfig(Configuration);
        BasisNetworkServer.Security.BasisAudioRangeLimitManager.InitializeFromConfig(Configuration);
        BasisNetworkServer.Security.BasisAvatarScaleLimitManager.InitializeFromConfig(Configuration);
        BasisNetworkServer.Security.BasisResourceLimitManager.InitializeFromConfig(Configuration);

        if (Server == null) return;

        BasisHeadlessConnectionPolicyManager.BroadcastState();
        BasisNetworkServer.Security.BasisGlobalLockManager.BroadcastLockState();
        BasisNetworkServer.Security.BasisCrashReportStateManager.BroadcastState();
        BasisNetworkServer.Security.BasisAudioRangeLimitManager.BroadcastState();
        BasisNetworkServer.Security.BasisAvatarScaleLimitManager.BroadcastState();
        BasisNetworkServer.Security.BasisResourceLimitManager.BroadcastState();
    }

    private static void InitializeAuth()
    {
        var HasFileSupport = Configuration.HasFileSupport;
        BasisPlayerModeration.UseFileOnDisc = HasFileSupport;
        BasisPlayerMuteManager.UseFileOnDisc = HasFileSupport;
        IAuthIdentity.HasFileSupport = HasFileSupport;

        Auth = new PasswordAuth(Configuration.Password ?? string.Empty);
        AuthIdentity = new BasisDIDAuthIdentity();

        if (HasFileSupport)
        {
            // Keep permissions with other config files
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string configDir = Path.Combine(baseDir, Configuration.ConfigFolderName);

            Directory.CreateDirectory(configDir);
            PermissionIntegration.Init(Path.Combine(configDir, "permissions.xml"));
            AllowList = new BasisNetworkServer.Security.BasisAllowList(Path.Combine(configDir, "BasisAllowList.txt"));
            BanList = new BasisNetworkServer.Security.BasisBanList(Path.Combine(configDir, "BasisBanList.txt"));
        }
        else
        {
            PermissionIntegration.InitWithoutDisc();
            // Best-effort in-memory allowlist when the host disabled disk support.
            AllowList = new BasisNetworkServer.Security.BasisAllowList();
            BanList = new BasisNetworkServer.Security.BasisBanList();
        }
    }

    private static void SubscribeEvents(Configuration Configuration)
    {
        BasisServerHandleEvents.SubscribeServerEvents();
        // Three independent disk loads with disjoint state; overlap them at boot.
        System.Threading.Tasks.Parallel.Invoke(
            BasisPlayerModeration.LoadBannedPlayers,
            BasisPlayerMuteManager.LoadMutedPlayers,
            () => BasisNetworkChat.LoadWordFilter(Configuration));
        BasisNetworkStackRegistry.RegisterIntroducerFactory(
            BasisNetworkStackRegistry.LiteNetLibId,
            _ => new BasisNetworkServer.LNLPeerIntroducer());
        BasisNetworkServer.BasisServerP2PBroker.Initialize();
    }

    #endregion

    #region Server Setup

    public static void SetupServer(Configuration configuration)
    {
        Listener = new EventBasedNetListener();
        Server = BasisNetworkStackRegistry.Create(configuration.NetworkStackId, Listener, configuration);

        NetDebug.Logger = new BasisServerLogger();
        StartListening(configuration);
    }

    public static void StartListening(Configuration configuration)
    {
        IPAddress ipv4, ipv6;
        if (configuration.OverrideAutoDiscoveryOfIpv)
        {
            if (!IPAddress.TryParse(Configuration.IPv4Address, out ipv4))
            {
                BNL.LogWarning($"Failed to parse IPv4 bind address '{Configuration.IPv4Address}', falling back to 0.0.0.0");
                ipv4 = IPAddress.Any;
            }
            if (!IPAddress.TryParse(Configuration.IPv6Address, out ipv6))
            {
                BNL.LogWarning($"Failed to parse IPv6 bind address '{Configuration.IPv6Address}', falling back to [::]");
                ipv6 = IPAddress.IPv6Any;
            }
        }
        else
        {
            ipv4 = IPAddress.Any;
            ipv6 = IPAddress.IPv6Any;
        }

        // Read straight from the config rather than from the mirror in the reduction system, so
        // this does not depend on InitializePulseSettings having run first. 0 is auto, which always
        // derives more than one, so only an explicit 1 means "never add a socket".
        if (Server is LNLNetManager lnlServer && lnlServer.manager != null)
        {
            lnlServer.manager.AllowSendSocketGrowth = Basis.Network.Core.BasisTransportConfigStore
                .Get<Basis.Network.Core.LNLTransportConfig>(
                    Basis.Network.Core.BasisNetworkStackRegistry.LiteNetLibId).MaxSendSockets != 1;
        }

        Server.Start(ipv4, ipv6, configuration.SetPort);
        BNL.Log($"Listening on UDP port {configuration.SetPort}");
        BNL.Log($"  IPv4 bind: {ipv4}");
        BNL.Log($"  IPv6 bind: [{ipv6}]");
    }
    #endregion
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, NetPeer sender, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int senderId = sender.Id;
        int sent = 0;
        foreach (var client in clients)
        {
            if (client.Id != senderId && TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
            {
                sent++;
            }
        }
        BasisNetworkStatistics.RecordOutboundBatch(channel, sent, (long)sent * writer.Length);
    }
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int sent = 0;
        foreach (var client in clients)
        {
            if (TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
            {
                sent++;
            }
        }
        BasisNetworkStatistics.RecordOutboundBatch(channel, sent, (long)sent * writer.Length);
    }

    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ref List<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int count = clients.Count;
        int sent = 0;
        for (int Index = 0; Index < count; Index++)
        {
            NetPeer client = clients[Index];
            if (TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
            {
                sent++;
            }
        }
        BasisNetworkStatistics.RecordOutboundBatch(channel, sent, (long)sent * writer.Length);
    }

    public static void TrySend(NetPeer client, NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod, int maxMessages = 70)
    {
        if (TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
        {
            BasisNetworkStatistics.RecordOutbound(channel, writer.Length);
        }
    }

    // Returns true if the send actually went out (vs dropped by the per-channel queue cap).
    // Splits the queue/send decision from the stats record so broadcast loops can fold N×
    // Interlocked into one RecordOutboundBatch call per (channel, broadcast).
    private static bool TrySendNoRecord(NetPeer client, NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod, int maxMessages)
    {
        if (deliveryMethod == DeliveryMethod.Sequenced || deliveryMethod == DeliveryMethod.Unreliable)
        {
            int queuedMessages = client.GetPacketsCountInQueue(channel, deliveryMethod);
            if (queuedMessages > maxMessages)
            {
                return false;
            }
        }
        client.Send(writer, channel, deliveryMethod);
        return true;
    }
    public static bool CheckValidated(NetDataWriter writer)
    {
        if (writer.Length == 0)
        {
            BNL.LogError("Trying to send a message with zero length!");
            return false;
        }
        return true;
    }
}
