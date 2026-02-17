using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using BasisDidLink;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

public static class NetworkServer
{
    public static EventBasedNetListener Listener;
    public static LNLNetManager Server;
    public static ConcurrentDictionary<int, NetPeer> AuthenticatedPeers = new();
    public static Configuration Configuration;
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
        SubscribeEvents();

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
        Server = new LNLNetManager(Listener, configuration);

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
