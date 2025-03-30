using Basis.Network.Core;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using BasisDidLink;
using BasisNetworkServer;
using BasisNetworkServer.Security;
using BasisServerHandle;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
public static class NetworkServer
{
    public static EventBasedNetListener listener;
    public static NetManager server;
    public static ConcurrentDictionary<ushort, NetPeer> Peers = new ConcurrentDictionary<ushort, NetPeer>();
    public static StripedNetPeerArray chunkedNetPeerArray = new StripedNetPeerArray();
    public static Configuration Configuration;
    public static IAuth auth;
    public static IAuthIdentity authIdentity;
    public static void StartServer(Configuration configuration)
    {
        Configuration = configuration;
        BasisServerReductionSystem.Configuration = configuration;
        auth = new PasswordAuth(configuration.Password ?? string.Empty);
        authIdentity = new BasisDIDAuthIdentity();
        SetupServer(configuration);
        BasisPlayerPackerPlayers.Initalize();
        BasisServerHandleEvents.SubscribeServerEvents();
        BasisPlayerModeration.LoadBannedPlayers();
        if (configuration.EnableStatistics)
        {
            BasisStatistics.StartWorkerThread(NetworkServer.server);
        }
        BNL.Log("Server Worker Threads Booted");

    }
    #region Server Setup
    public static void SetupServer(Configuration configuration)
    {
        listener = new EventBasedNetListener();
        server = new NetManager(listener)
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
            PacketPoolSize = 2000,
            UnsyncedEvents = true,
        };
        StartListening(configuration);
    }

    public static void StartListening(Configuration configuration)
    {
        if (configuration.OverrideAutoDiscoveryOfIpv)
        {
            BNL.Log("Server Wiring up SetPort " + Configuration.SetPort + "IPv6Address " + Configuration.IPv6Address);
            server.Start(Configuration.IPv4Address, Configuration.IPv6Address, Configuration.SetPort);
        }
        else
        {
            BNL.Log("Server Wiring up SetPort " + Configuration.SetPort);
            server.Start(Configuration.SetPort);
        }
    }
    #endregion
    public static void BroadcastMessageToClients(NetDataWriter Writer, byte channel, NetPeer sender, ReadOnlySpan<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        if (NetworkServer.CheckValidated(Writer))
        {
            foreach (NetPeer client in authenticatedClients)
            {
                if (client.Id != sender.Id)
                {
                    client.Send(Writer, channel, deliveryMethod);
                }
            }
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Writer, byte channel, ReadOnlySpan<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        if (NetworkServer.CheckValidated(Writer))
        {
            int count = authenticatedClients.Length;
            for (int index = 0; index < count; index++)
            {
                authenticatedClients[index].Send(Writer, channel, deliveryMethod);
            }
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Writer, byte channel, ref List<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        if (NetworkServer.CheckValidated(Writer))
        {
            int count = authenticatedClients.Count;
            for (int index = 0; index < count; index++)
            {
                authenticatedClients[index].Send(Writer, channel, deliveryMethod);
            }
        }
    }
    public static void SendOutValidated(NetPeer Peer, NetDataWriter Writer, byte MessageIndex, DeliveryMethod DeliveryMethod = DeliveryMethod.ReliableSequenced)
    {
        if (Writer.Length <= 0)
        {
            BNL.LogError("trying to sending a message without a length SendOutValidated : " + MessageIndex);
        }
        else
        {
            if (MessageIndex <= BasisNetworkCommons.TotalChannels)
            {
                Peer.Send(Writer, MessageIndex, DeliveryMethod);
              //  BNL.Log($"sent {MessageIndex}");
            }
            else
            {
                BNL.LogError($"Message was larger then the preprogrammed channels {BasisNetworkCommons.TotalChannels}");
            }
        }
    }
    public static void SendOutValidated(NetPeer Peer, byte[] Data, byte MessageIndex, DeliveryMethod DeliveryMethod = DeliveryMethod.ReliableSequenced)
    {
        if (MessageIndex <= BasisNetworkCommons.TotalChannels)
        {
            Peer.Send(Data, MessageIndex, DeliveryMethod);
            //  BNL.Log($"sent {MessageIndex}");
        }
        else
        {
            BNL.LogError($"Message was larger then the preprogrammed channels {BasisNetworkCommons.TotalChannels}");
        }
    }
    public static bool CheckValidated(NetDataWriter Writer)
    {
        if (Writer.Length == 0)
        {
            BNL.LogError("trying to sending a message without a length!");
            return false;
        }
        return true;
    }
}
