using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using LiteNetLib;
using LiteNetLib.Utils;
using static BasisNetworkCore.Serializable.SerializableBasis;
using Basis.Network.Core;
using BasisNetworkCore;

namespace BasisNetworkServer.Security
{
    public static class BasisPlayerModeration
    {
        private static readonly ConcurrentDictionary<string, BannedPlayer> BannedPlayers = new ConcurrentDictionary<string, BannedPlayer>();
        private static readonly HashSet<string> BannedUUIDs = new();
        private static readonly string BanFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "banned_players.xml");

        public class BannedPlayer
        {
            public string UUID { get; set; }
            public string BannedIp { get; set; }
            public string Reason { get; set; }
            public bool HasBannedIp { get; set; }
            public string TimeOfBan { get; set; }
        }
        public static bool GetBannedReason(string UUID, out string Reason)
        {
            if (BannedPlayers.TryGetValue(UUID, out BannedPlayer Player))
            {
                Reason = Player.Reason;
                return true;
            }
            else
            {
                Reason = string.Empty;
                return false;
            }
        }
        public static bool IsIpBanned(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                throw new ArgumentException("[Error] IP address cannot be null or empty.");

            return BannedPlayers.Values.Any(bp => bp.HasBannedIp && bp.BannedIp == ip);
        }
        public static void SaveBannedPlayers()
        {
            try
            {
                using FileStream fs = new(BanFilePath, FileMode.Create);
                new XmlSerializer(typeof(List<BannedPlayer>)).Serialize(fs, BannedPlayers.Values.ToList());
            }
            catch (Exception ex)
            {
                BNL.LogError($"[Error] Failed to save banned players: {ex.Message}");
            }
        }

        public static void LoadBannedPlayers()
        {
            if (File.Exists(BanFilePath) == false)
            {
                SaveBannedPlayers();
            }
            try
            {
                using FileStream fs = new(BanFilePath, FileMode.Open);
                var serializer = new XmlSerializer(typeof(List<BannedPlayer>));
                var loadedList = (List<BannedPlayer>)serializer.Deserialize(fs);

                BannedPlayers.Clear();
                BannedUUIDs.Clear();

                foreach (var player in loadedList)
                {
                    BannedPlayers[player.UUID] = player;
                    BannedUUIDs.Add(player.UUID);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load banned players: {ex.Message}");
            }
        }

        public static string Ban(string UUID, string reason)
        {
            if (string.IsNullOrEmpty(UUID))
                return "[Error] UUID cannot be null or empty.";
            if (string.IsNullOrEmpty(reason))
                return "[Error] Reason cannot be null or empty.";

            if (!NetworkServer.authIdentity.UUIDToNetID(UUID, out NetPeer peer))
            {
                return $"[Error] Unable to find player: {UUID}";
            }

            NetworkServer.server.DisconnectPeer(peer, Encoding.UTF8.GetBytes(reason));

            if (BannedUUIDs.Contains(UUID))
            {
                BannedUUIDs.Remove(UUID);
            }

            BannedPlayer bannedPlayer = new BannedPlayer
            {
                UUID = UUID,
                Reason = reason,
                HasBannedIp = false,
                TimeOfBan = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                BannedIp = string.Empty
            };

            BannedPlayers[UUID] = bannedPlayer;
            BannedUUIDs.Add(UUID);
            SaveBannedPlayers();

            return $"Player {UUID} banned successfully for reason: {reason}";
        }

        public static string IpBan(string UUID, string reason)
        {
            if (string.IsNullOrEmpty(UUID))
                return "[Error] UUID cannot be null or empty.";
            if (string.IsNullOrEmpty(reason))
                return "[Error] Reason cannot be null or empty.";

            if (!NetworkServer.authIdentity.UUIDToNetID(UUID, out NetPeer peer))
                return $"[Error] Unable to find player: {UUID}";

            NetworkServer.server.DisconnectPeer(peer, Encoding.UTF8.GetBytes(reason));
            string ip = peer.Address.ToString();

            if (BannedUUIDs.Contains(UUID))
                return $"[Info] Player {UUID} is already banned.";

            BannedPlayer bannedPlayer = new BannedPlayer
            {
                UUID = UUID,
                BannedIp = ip,
                Reason = reason,
                HasBannedIp = true,
                TimeOfBan = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };

            BannedPlayers[UUID] = bannedPlayer;
            BannedUUIDs.Add(UUID);
            SaveBannedPlayers();

            return $"Player {UUID} and IP {ip} banned successfully for reason: {reason}";
        }

        public static string Kick(string UUID, string reason)
        {
            if (string.IsNullOrEmpty(UUID))
                return "[Error] UUID cannot be null or empty.";
            if (string.IsNullOrEmpty(reason))
                return "[Error] Reason cannot be null or empty.";

            if (!NetworkServer.authIdentity.UUIDToNetID(UUID, out NetPeer peer))
                return $"[Error] Unable to find player: {UUID}";

            NetworkServer.server.DisconnectPeer(peer, Encoding.UTF8.GetBytes(reason));
            return $"Player {UUID} kicked successfully.";
        }

        public static bool IsBanned(string UUID)
        {
            if (string.IsNullOrEmpty(UUID))
                throw new ArgumentException("[Error] UUID cannot be null or empty.");

            return BannedUUIDs.Contains(UUID);
        }

        public static bool Unban(string UUID)
        {
            if (string.IsNullOrEmpty(UUID))
                throw new ArgumentException("[Error] UUID cannot be null or empty.");

            if (!BannedUUIDs.Contains(UUID))
                return false;

            BannedPlayers.TryRemove(UUID, out _);
            BannedUUIDs.Remove(UUID);
            SaveBannedPlayers();
            return true;
        }

        public static bool UnbanIp(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                throw new ArgumentException("[Error] IP address cannot be null or empty.");

            var toRemoveList = BannedPlayers.Values.Where(bp => bp.HasBannedIp && bp.BannedIp == ip).ToList();
            if (!toRemoveList.Any())
                return false;

            foreach (var player in toRemoveList)
            {
                BannedPlayers.TryRemove(player.UUID, out _);
                BannedUUIDs.Remove(player.UUID);
            }

            SaveBannedPlayers();
            return true;
        }
        public static void OnAdminMessage(NetPeer peer, NetDataReader reader)
        {
            if (!NetworkServer.authIdentity.NetIDToUUID(peer, out string UUID))
            {
                string msg = $"Netpeer was not in database {peer.Address}";
                BNL.LogError(msg);
                SendBackMessage(peer, msg);
                return;
            }
            if (!NetworkServer.authIdentity.IsNetPeerAdmin(UUID))
            {
                string msg = $"Was not admin! {UUID}";
                BNL.LogError(msg);
                SendBackMessage(peer, msg);
                return;
            }
            AdminRequest AdminRequest = new AdminRequest();
            AdminRequest.Deserialize(reader);
            AdminRequestMode Mode = AdminRequest.GetAdminRequestMode();
            switch (Mode)
            {
                case AdminRequestMode.Ban:
                    string ReturnMessage = Ban(reader.GetString(), reader.GetString());
                    SendBackMessage(peer, ReturnMessage);
                    break;
                case AdminRequestMode.Kick:
                    ReturnMessage = Kick(reader.GetString(), reader.GetString());
                    SendBackMessage(peer, ReturnMessage);
                    break;
                case AdminRequestMode.IpAndBan:
                    ReturnMessage = IpBan(reader.GetString(), reader.GetString());
                    SendBackMessage(peer, ReturnMessage);
                    break;
                case AdminRequestMode.Message:
                    ushort RPI = reader.GetUShort();
                    NetPeer RemotePeer = NetworkServer.chunkedNetPeerArray.GetPeer(RPI);
                    string Message = reader.GetString();
                    SendBackMessage(RemotePeer, Message);
                    BNL.Log($"sending Message | {Message}");

                    break;
                case AdminRequestMode.MessageAll:
                    NetDataWriter Writer = new NetDataWriter(true, 4);
                    AdminRequest OutAdminRequest = new AdminRequest();
                    OutAdminRequest.Serialize(Writer, AdminRequestMode.MessageAll);
                    Message = reader.GetString();
                    Writer.Put(Message);
                    NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AdminMessage, peer, BasisPlayerArray.GetSnapshot(), DeliveryMethod.ReliableOrdered);
                    BNL.Log($"sending MessageAll | {Message}");
                    break;
                case AdminRequestMode.UnBanIP:
                    if (UnbanIp(reader.GetString()))
                    {
                        ReturnMessage = "Successfully Unbanned";
                    }
                    else
                    {
                        ReturnMessage = "failed to unban no ban existed!";
                    }
                    SendBackMessage(peer, ReturnMessage);
                    break;
                case AdminRequestMode.UnBan:
                    if (Unban(reader.GetString()))
                    {
                        ReturnMessage = "Successfully Unbanned";
                    }
                    else
                    {
                        ReturnMessage = "failed to unban";
                    }
                    SendBackMessage(peer, ReturnMessage);
                    break;
                //  case AdminRequestMode.RequestBannedPlayers:
                //      break;
                // case AdminRequestMode.TeleportTo:
                //    break;
                case AdminRequestMode.TeleportAll:

                    Writer = new NetDataWriter(true, 4);
                    OutAdminRequest = new AdminRequest();
                    OutAdminRequest.Serialize(Writer, AdminRequestMode.TeleportAll);
                    ushort PlayerDestination = reader.GetUShort();
                    Writer.Put(PlayerDestination);
                    NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AdminMessage, peer, BasisPlayerArray.GetSnapshot(), DeliveryMethod.ReliableOrdered);
                    BNL.Log($"sending TeleportAll destination is NetID {PlayerDestination}");
                    break;
                case AdminRequestMode.AddAdmin:
                    string AddingAdmin = reader.GetString();
                    if (NetworkServer.authIdentity.AddNetPeerAsAdmin(AddingAdmin))
                    {
                        SendBackMessage(peer, $"Added Admin {AddingAdmin}");
                    }
                    else
                    {
                        SendBackMessage(peer, $"Failed to Added Admin {AddingAdmin}");
                    }
                    break;
                case AdminRequestMode.RemoveAdmin:
                    string RemoveAdmin = reader.GetString();
                    if (NetworkServer.authIdentity.RemoveNetPeerAsAdmin(RemoveAdmin))
                    {
                        SendBackMessage(peer, $"Removing Admin {RemoveAdmin}");
                    }
                    else
                    {
                        SendBackMessage(peer, $"Failed to Remove Admin {RemoveAdmin}");
                    }
                    break;
                case AdminRequestMode.TeleportPlayer:
                    Writer = new NetDataWriter(true, 4);
                    OutAdminRequest = new AdminRequest();
                    OutAdminRequest.Serialize(Writer, AdminRequestMode.TeleportPlayer);
                    PlayerDestination = reader.GetUShort();
                    Writer.Put(PlayerDestination);

                    NetworkServer.SendOutValidated(peer, Writer, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableOrdered);
                    break;
                default:
                    BNL.LogError("Missing Mode!");
                    ReturnMessage = "Missing mode";
                    SendBackMessage(peer, ReturnMessage);
                    break;
            }
        }
        public static void SendBackMessage(NetPeer Peer, string ReturnMessage)
        {
            if (string.IsNullOrEmpty(ReturnMessage))
            {
                BNL.LogError("trying to send a empty message to client " + Peer.Id);
                return;
            }
            NetDataWriter Writer = new NetDataWriter(true, 4);
            AdminRequest OutAdminRequest = new AdminRequest();
            OutAdminRequest.Serialize(Writer, AdminRequestMode.Message);
            Writer.Put(ReturnMessage);
            NetworkServer.SendOutValidated(Peer, Writer, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableOrdered);
        }
    }
}
