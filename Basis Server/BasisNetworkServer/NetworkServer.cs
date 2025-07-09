
using Basis.Network.Core;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using BasisDidLink;
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
        BasisPlayerModeration.UseFileOnDisc = configuration.HasFileSupport;
        IAuthIdentity.HasFileSupport = configuration.HasFileSupport;
        auth = new PasswordAuth(configuration.Password ?? string.Empty);
        authIdentity = new BasisDIDAuthIdentity();
        SetupServer(configuration);
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
            PacketPoolSize = 700,
            UnsyncedEvents = true,
            ReceivePollingTime = 250,

            SimulateLatency = configuration.SimulateLatency,
            SimulatePacketLoss = configuration.SimulatePacketLoss,
            SimulationMaxLatency = configuration.SimulationMaxLatency,
            SimulationMinLatency = configuration.SimulationMinLatency,
            SimulationPacketLossChance = configuration.SimulationPacketLossChance,
            MtuDiscovery = configuration.MtuDiscovery,
            MtuOverride = configuration.MtuOverride,
            /*
            ReuseAddress = configuration.ReuseAddress,
            MaxConnectAttempts = configuration.MaxConnectAttempts,
            ReconnectDelay = configuration.ReconnectDelay,
            DisconnectOnUnreachable = configuration.DisconnectOnUnreachable,
            DontRoute = configuration.DontRoute,
            */
        };
        NetDebug.Logger = new BasisServerLogger();
        StartListening(configuration);
    }
    public class BasisServerLogger : INetLogger
    {
        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            switch (level)
            {
                case NetLogLevel.Warning:
                    BNL.LogWarning(str);
                    break;
                case NetLogLevel.Error:
                    BNL.LogError(str);
                    break;
               // case NetLogLevel.Trace:
                  //  BNL.Log(str);
                    break;
              //  case NetLogLevel.Info:
                 //   BNL.Log(str);
                    break;
            }
        }
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
    public static void BroadcastMessageToClients(NetDataWriter Writer, byte channel, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        if (NetworkServer.CheckValidated(Writer))
        {
            server.SendToAll(Writer, channel, deliveryMethod);
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Writer, byte channel,NetPeer DontSendToMe, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        if (NetworkServer.CheckValidated(Writer))
        {
            server.SendToAll(Writer, channel, deliveryMethod, DontSendToMe);
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Writer, byte channel, ref List<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int MaxMessages = 70)
    {
        if (NetworkServer.CheckValidated(Writer))
        {
            int count = authenticatedClients.Count;
            if (deliveryMethod == DeliveryMethod.Sequenced)
            {
                for (int index = 0; index < count; index++)
                {
                    int Size = authenticatedClients[index].GetPacketsCountInQueue(channel, deliveryMethod);
                    if (Size <= MaxMessages)
                    {
                        authenticatedClients[index].Send(Writer, channel, deliveryMethod);
                    }
                }
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    authenticatedClients[index].Send(Writer, channel, deliveryMethod);
                }
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
                Peer.Send(Writer.Data,0,Writer.Length, MessageIndex, DeliveryMethod);
              //  BNL.Log($"sent {MessageIndex}");
            }
            else
            {
                BNL.LogError($"Message was larger then the preprogrammed channels {BasisNetworkCommons.TotalChannels}");
            }
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
