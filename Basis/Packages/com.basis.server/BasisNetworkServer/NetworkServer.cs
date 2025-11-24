using Basis.Network.Core;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using BasisDidLink;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

public static class NetworkServer
{
    public static EventBasedNetListener Listener;
    public static NetManager Server;
    public static ConcurrentDictionary<int, NetPeer> AuthenticatedPeers = new();
    public static Configuration Configuration;
    public static IAuth Auth;
    public static IAuthIdentity AuthIdentity;
    #region Server Entry Point

    public static void StartServer(Configuration configuration)
    {
        Configuration = configuration;

        InitializePulseSettings();
        InitializeAuth();
        SetupServer(configuration);
        SubscribeEvents();

        if (configuration.EnableStatistics)
            BasisStatistics.StartWorkerThread(Server);

        BNL.Log("Server Worker Threads Booted");
    }

    private static void InitializePulseSettings()
    {
        BasisServerReductionSystemEvents.BSRBaseMultiplier = Configuration.BSRBaseMultiplier;
        BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval = Configuration.BSRSMillisecondDefaultInterval;
        BasisServerReductionSystemEvents.BSRSIncreaseRate = Configuration.BSRSIncreaseRate;
    }

    private static void InitializeAuth()
    {
        BasisPlayerModeration.UseFileOnDisc = Configuration.HasFileSupport;
        IAuthIdentity.HasFileSupport = Configuration.HasFileSupport;

        Auth = new PasswordAuth(Configuration.Password ?? string.Empty);
        AuthIdentity = new BasisDIDAuthIdentity();
    }

    private static void SubscribeEvents()
    {
        BasisServerHandleEvents.SubscribeServerEvents();
        BasisPlayerModeration.LoadBannedPlayers();
    }

    #endregion

    #region Server Setup

    public static void SetupServer(Configuration configuration)
    {
        Listener = new EventBasedNetListener();

        Server = new NetManager(Listener)
        {
            AutoRecycle = false,
            UnconnectedMessagesEnabled = false,
            NatPunchEnabled = configuration.NatPunchEnabled,
            AllowPeerAddressChange = configuration.AllowPeerAddressChange,
            BroadcastReceiveEnabled = false,
            UseNativeSockets = configuration.UseNativeSockets,
            ChannelsCount = BasisNetworkCommons.TotalChannels,
            EnableStatistics = configuration.EnableStatistics,
            IPv6Enabled = configuration.IPv6Enabled,
            UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
            PingInterval = configuration.PingInterval,
            DisconnectTimeout = configuration.DisconnectTimeout,
            UnsyncedEvents = true,
            ReceivePollingTime = BasisNetworkCommons.ReceivePollingTime,
            PacketPoolSize = BasisNetworkCommons.PacketPoolSize,
            SimulateLatency = configuration.SimulateLatency,
            SimulatePacketLoss = configuration.SimulatePacketLoss,
            SimulationMaxLatency = configuration.SimulationMaxLatency,
            SimulationMinLatency = configuration.SimulationMinLatency,
            SimulationPacketLossChance = configuration.SimulationPacketLossChance,
            MtuDiscovery = configuration.MtuDiscovery,
            MtuOverride = configuration.MtuOverride
        };

        NetDebug.Logger = new BasisServerLogger();
        StartListening(configuration);
    }

    public static void StartListening(Configuration configuration)
    {
        if (configuration.OverrideAutoDiscoveryOfIpv)
        {
            BNL.Log($"Server Wiring up SetPort {Configuration.SetPort} IPv6Address {Configuration.IPv6Address}");
            Server.Start(Configuration.IPv4Address, Configuration.IPv6Address, Configuration.SetPort);
        }
        else
        {
            BNL.Log($"Server Wiring up SetPort {Configuration.SetPort}");
            Server.Start(Configuration.SetPort);
        }
    }

    #endregion
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, NetPeer sender, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer)) return;

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
        if (!CheckValidated(writer)) return;

        foreach (var client in clients)
        {
            TrySend(client, writer, channel, deliveryMethod, maxMessages);
        }
    }

    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ref List<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer)) return;

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
