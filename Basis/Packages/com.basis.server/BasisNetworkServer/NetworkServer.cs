using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using Basis.Scripts.Networking.Steam;
using BasisDidLink;
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
    public static Configuration Configuration;
    // Cached snapshot rebuilt on connect/disconnect — avoids ToArray() alloc on every broadcast.
    private static volatile NetPeer[] _peerSnapshot = Array.Empty<NetPeer>();
    public static NetPeer[] PeerSnapshot => _peerSnapshot;

    public static void RebuildPeerSnapshot()
    {
        _peerSnapshot = AuthenticatedPeers.Values.ToArray();
    }

    // Centralized NetDataWriter pool — single source of truth for all server code.
    private static readonly ConcurrentQueue<NetDataWriter> _writerPool = new();
    public static NetDataWriter RentWriter(int initialCapacity = 208)
    {
        if (_writerPool.TryDequeue(out var writer)) return writer;
        return new NetDataWriter(true, initialCapacity);
    }
    public static void ReturnWriter(NetDataWriter writer)
    {
        writer.Reset();
        _writerPool.Enqueue(writer);
    }

    public static IAuth Auth;
    public static IAuthIdentity AuthIdentity;
    public static int HighQualityLength;
    #region Server Entry Point

    public static void StartServer(Configuration configuration)
    {
        Configuration = configuration;

        HighQualityLength = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
        InitializePulseSettings();
        InitializeAuth();
        SetupServer(configuration);
        SubscribeEvents(Configuration);

        if (configuration.EnableStatistics)
        {
            BasisStatistics.StartWorkerThread(Server);
        }

        BNL.Log("Server Worker Threads Booted");
    }

    private static void InitializePulseSettings()
    {
        BasisServerReductionSystemEvents.BSRBaseMultiplier = Configuration.BSRBaseMultiplier;
        BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval = Configuration.BSRSMillisecondDefaultInterval;
        BasisServerReductionSystemEvents.BSRSIncreaseRate = Configuration.BSRSIncreaseRate;
        BasisServerReductionSystemEvents.HighDistanceSq = Configuration.HighQualityDistance * Configuration.HighQualityDistance;
        BasisServerReductionSystemEvents.MediumDistanceSq = Configuration.MediumQualityDistance * Configuration.MediumQualityDistance;
        BasisServerReductionSystemEvents.LowDistanceSq = Configuration.LowQualityDistance * Configuration.LowQualityDistance;
        BSRProfiler.Enabled = Configuration.EnableBSRProfiling;
    }

    private static void InitializeAuth()
    {
        var HasFileSupport = Configuration.HasFileSupport;
        BasisPlayerModeration.UseFileOnDisc = HasFileSupport;
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
        }
        else
        {
            PermissionIntegration.InitWithoutDisc();
        }
    }

    private static void SubscribeEvents(Configuration Configuration)
    {
        BasisServerHandleEvents.SubscribeServerEvents();
        BasisPlayerModeration.LoadBannedPlayers();
        BasisNetworkChat.LoadWordFilter(Configuration);
    }

    #endregion

    #region Server Setup

    public static void SetupServer(Configuration configuration)
    {
        Listener = new EventBasedNetListener();
        Server = BasisTransportFactory.Create(Listener, configuration);

        NetDebug.Logger = new BasisServerLogger();
        StartListening(configuration);
    }

    public static void StartListening(Configuration configuration)
    {
        if (configuration.OverrideAutoDiscoveryOfIpv)
        {
            IPAddress? IPv4Address, IPv6Address;
            if (!IPAddress.TryParse(Configuration.IPv4Address, out IPv4Address))
            {
                BNL.LogWarning("Failed to parse IPv4 bind address, falling back to 0.0.0.0");
                IPv4Address = IPAddress.Parse("0.0.0.0");
            }

            if (!IPAddress.TryParse(Configuration.IPv6Address, out IPv6Address))
            {
                BNL.LogWarning("Failed to parse IPv6 bind address, falling back to ::1");
                IPv6Address = IPAddress.Parse("::1");
            }

            BNL.Log($"Server Wiring up SetPort {Configuration.SetPort} IPv6Address {Configuration.IPv6Address}");
            Server.Start(IPv4Address, IPv6Address, Configuration.SetPort);
        }
        else
        {
            BNL.Log($"Server Wiring up SetPort {Configuration.SetPort}");
            Server.Start(IPAddress.Any, IPAddress.IPv6Any, Configuration.SetPort);
        }
    }
    #endregion
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, NetPeer sender, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        foreach (var client in clients)
        {
            if (client.Id != sender.Id)
            {
                TrySend(client, writer, channel, deliveryMethod, maxMessages);
            }
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        foreach (var client in clients)
        {
            TrySend(client, writer, channel, deliveryMethod, maxMessages);
        }
    }

    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ref List<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int count = clients.Count;
        for (int Index = 0; Index < count; Index++)
        {
            NetPeer client = clients[Index];
            TrySend(client, writer, channel, deliveryMethod, maxMessages);
        }
    }

    public static void TrySend(NetPeer client, NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod, int maxMessages = 70)
    {
        if (deliveryMethod == DeliveryMethod.Sequenced || deliveryMethod == DeliveryMethod.Unreliable)
        {
            int queuedMessages = client.GetPacketsCountInQueue(channel, deliveryMethod);
            if (queuedMessages <= maxMessages)
            {
                BasisNetworkStatistics.RecordOutbound(channel, writer.Length);
                client.Send(writer, channel, deliveryMethod);
            }
            else
            {
                // BNL.LogError("Skipping send out of Channel " + channel);
            }
        }
        else
        {
            BasisNetworkStatistics.RecordOutbound(channel, writer.Length);
            client.Send(writer, channel, deliveryMethod);
        }
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
