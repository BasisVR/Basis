using Basis.Network.Core;
using BasisNetworkCore;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using BasisServerHandle;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

namespace BasisNetworkServer.Security
{
    public static class BasisPlayerModeration
    {
        private static readonly ConcurrentDictionary<string, BannedPlayer> BannedPlayers = new();
        private static readonly ConcurrentDictionary<string, byte> BannedUUIDs = new();
        private static readonly string BanFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "banned_players.xml");

        public static bool UseFileOnDisc = true;

        public class BannedPlayer
        {
            public string UUID { get; set; }
            public string BannedIp { get; set; }
            public string Reason { get; set; }
            public bool HasBannedIp { get; set; }
            public string TimeOfBan { get; set; }
        }

        // =========================
        // Core Ban Logic
        // =========================

        public static string Ban(string UUID, string reason)
        {
            if (!ValidateTarget(UUID, reason, out var peer, out var error))
                return error;

            if (IsProtected(UUID))
                return "Target is protected";

            peer.Disconnect(Encoding.UTF8.GetBytes(reason));

            BannedPlayer bannedPlayer = new()
            {
                UUID = UUID,
                Reason = reason,
                HasBannedIp = false,
                TimeOfBan = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                BannedIp = string.Empty
            };

            BannedPlayers[UUID] = bannedPlayer;
            BannedUUIDs[UUID] = 0;
            SaveBannedPlayers();

            return $"Player {UUID} banned.";
        }

        public static string IpBan(string UUID, string reason)
        {
            if (!ValidateTarget(UUID, reason, out var peer, out var error))
                return error;

            if (IsProtected(UUID))
                return "Target is protected";

            string ip = peer.Address.ToString();
            peer.Disconnect(Encoding.UTF8.GetBytes(reason));

            BannedPlayer bannedPlayer = new()
            {
                UUID = UUID,
                BannedIp = ip,
                Reason = reason,
                HasBannedIp = true,
                TimeOfBan = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };

            BannedPlayers[UUID] = bannedPlayer;
            BannedUUIDs[UUID] = 0;
            SaveBannedPlayers();

            return $"Player {UUID} and IP {ip} banned.";
        }

        public static string Kick(string UUID, string reason)
        {
            if (!ValidateTarget(UUID, reason, out var peer, out var error))
                return error;

            if (IsProtected(UUID))
                return "Target is protected";

            peer.Disconnect(Encoding.UTF8.GetBytes(reason));
            return $"Player {UUID} kicked.";
        }

        private static bool ValidateTarget(string UUID, string reason, out NetPeer peer, out string error)
        {
            peer = null;
            error = "";

            if (string.IsNullOrEmpty(UUID))
            {
                error = "UUID invalid";
                return false;
            }

            if (string.IsNullOrEmpty(reason))
            {
                error = "Reason invalid";
                return false;
            }

            if (!NetworkServer.AuthIdentity.UUIDToNetID(UUID, out int id) ||
                !NetworkServer.AuthenticatedPeers.TryGetValue(id, out peer))
            {
                error = "Player not found";
                return false;
            }

            return true;
        }

        private static bool IsProtected(string uuid)
        {
            return PermissionIntegration.Manager.Has(uuid, PermNodes.protection);
        }

        // =========================
        // Ban Storage
        // =========================

        public static void SaveBannedPlayers()
        {
            if (!UseFileOnDisc) return;

            try
            {
                using FileStream fs = new(BanFilePath, FileMode.Create);
                new XmlSerializer(typeof(List<BannedPlayer>)).Serialize(fs, BannedPlayers.Values.ToList());
            }
            catch (Exception ex)
            {
                BNL.LogError($"Save banned failed: {ex.Message}");
            }
        }

        public static void LoadBannedPlayers()
        {
            if (!File.Exists(BanFilePath))
            {
                SaveBannedPlayers();
                return;
            }

            try
            {
                using FileStream fs = new(BanFilePath, FileMode.Open);
                var list = (List<BannedPlayer>)new XmlSerializer(typeof(List<BannedPlayer>)).Deserialize(fs);

                BannedPlayers.Clear();
                BannedUUIDs.Clear();

                foreach (var p in list)
                {
                    BannedPlayers[p.UUID] = p;
                    BannedUUIDs[p.UUID] = 0;
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"Load banned failed: {ex.Message}");
            }
        }

        public static bool IsBanned(string UUID) => BannedUUIDs.ContainsKey(UUID);

        public static bool Unban(string UUID)
        {
            if (!BannedUUIDs.ContainsKey(UUID))
                return false;

            BannedPlayers.TryRemove(UUID, out _);
            BannedUUIDs.TryRemove(UUID, out _);
            SaveBannedPlayers();
            return true;
        }

        public static bool UnbanIp(string ip)
        {
            var list = BannedPlayers.Values.Where(p => p.HasBannedIp && p.BannedIp == ip).ToList();
            if (!list.Any()) return false;

            foreach (var p in list)
            {
                BannedPlayers.TryRemove(p.UUID, out _);
                BannedUUIDs.TryRemove(p.UUID, out _);
            }

            SaveBannedPlayers();
            return true;
        }

        // =========================
        // Admin Entry Point
        // =========================

        public static void OnAdminMessage(NetPeer peer, NetPacketReader reader)
        {
            if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out string UUID))
            {
                SendBackMessage(peer, "UUID not found");
                return;
            }

            AdminRequest req = new();
            req.Deserialize(reader);
            var mode = req.GetAdminRequestMode();

            // ===== VIEW PERMISSIONS =====
            if (mode == AdminRequestMode.GetPermissions)
            {
                if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.PermissionsView))
                {
                    SendBackMessage(peer, "No permission: view");
                    return;
                }

                HandleGetPermissions(peer);
                return;
            }

            switch (mode)
            {
                case AdminRequestMode.Ban:
                    Require(peer, PermNodes.ModerationBan, () =>
                        SendBackMessage(peer, Ban(reader.GetString(), reader.GetString())));
                    break;

                case AdminRequestMode.Kick:
                    Require(peer, PermNodes.ModerationKick, () =>
                        SendBackMessage(peer, Kick(reader.GetString(), reader.GetString())));
                    break;

                case AdminRequestMode.IpAndBan:
                    Require(peer, PermNodes.ModerationIpBan, () =>
                        SendBackMessage(peer, IpBan(reader.GetString(), reader.GetString())));
                    break;

                case AdminRequestMode.UnBan:
                    Require(peer, PermNodes.ModerationUnban, () =>
                        SendBackMessage(peer, Unban(reader.GetString()) ? "Unbanned" : "Failed"));
                    break;

                case AdminRequestMode.UnBanIP:
                    Require(peer, PermNodes.ModerationUnbanIp, () =>
                        SendBackMessage(peer, UnbanIp(reader.GetString()) ? "Unbanned" : "Failed"));
                    break;

                case AdminRequestMode.Message:
                    Require(peer, PermNodes.ModerationMessage, () =>
                    {
                        ushort id = reader.GetUShort();
                        if (NetworkServer.AuthenticatedPeers.TryGetValue(id, out var target))
                            SendBackMessage(target, reader.GetString());
                    });
                    break;

                case AdminRequestMode.MessageAll:
                    Require(peer, PermNodes.ModerationMessageAll, () =>
                    {
                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
                        writer.Put(reader.GetString());
                        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(writer);
                    });
                    break;

                case AdminRequestMode.TeleportAll:
                case AdminRequestMode.TeleportPlayer:
                    Require(peer, PermNodes.ModerationTeleport, () =>
                    {
                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, mode);
                        writer.Put(reader.GetUShort());
                        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(writer);
                    });
                    break;

                case AdminRequestMode.EnableShoutMode:
                case AdminRequestMode.DisableShoutMode:
                    Require(peer, PermNodes.ModerationShout, () =>
                        HandleShoutMode(peer, reader, mode == AdminRequestMode.EnableShoutMode));
                    break;

                // ===== GLOBAL LOCK =====
                case AdminRequestMode.GlobalToggleAvatars:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Avatar", BasisGlobalLockManager.ToggleAvatars()));
                    break;

                case AdminRequestMode.GlobalToggleProps:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Prop", BasisGlobalLockManager.ToggleProps()));
                    break;

                case AdminRequestMode.GlobalToggleWorlds:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "World", BasisGlobalLockManager.ToggleWorlds()));
                    break;

                // ===== PERMISSION EDIT =====
                case AdminRequestMode.SetUserGroup:
                case AdminRequestMode.SetUserNode:
                case AdminRequestMode.SetGroupNode:
                case AdminRequestMode.CreateGroup:
                case AdminRequestMode.DeleteGroup:
                case AdminRequestMode.SetGroupParent:
                    Require(peer, PermNodes.PermissionsEdit, () =>
                        HandlePermissionEdit(mode, peer, reader));
                    break;
            }

            reader.Recycle();
        }

        // =========================
        // Helpers
        // =========================

        private static void Require(NetPeer peer, string perm, Action action)
        {
            if (!PermissionIntegration.HasValidRequirement(peer, perm))
            {
                SendBackMessage(peer, $"No permission: {perm}");
                return;
            }

            action();
        }

        private static void HandlePermissionEdit(AdminRequestMode mode, NetPeer peer, NetPacketReader reader)
        {
            switch (mode)
            {
                case AdminRequestMode.SetUserGroup:
                    PermissionIntegration.Manager.AddUserToGroup(reader.GetString(), reader.GetString());
                    break;

                case AdminRequestMode.SetUserNode:
                    PermissionIntegration.Manager.AddUserNode(reader.GetString(), reader.GetString());
                    break;

                case AdminRequestMode.SetGroupNode:
                    PermissionIntegration.Manager.AddGroupNode(reader.GetString(), reader.GetString());
                    break;

                case AdminRequestMode.CreateGroup:
                    PermissionIntegration.Manager.GetOrCreateGroup(reader.GetString());
                    break;

                case AdminRequestMode.DeleteGroup:
                    PermissionIntegration.Manager.DeleteGroup(reader.GetString());
                    break;

                case AdminRequestMode.SetGroupParent:
                    PermissionIntegration.Manager.AddGroupParent(reader.GetString(), reader.GetString());
                    break;
            }

            SendBackMessage(peer, "Permission updated");
        }

        private static void HandleGetPermissions(NetPeer peer)
        {
            var snap = PermissionIntegration.Manager.Snapshot();

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GetPermissions);

            writer.Put(snap.Groups.Count);
            foreach (var g in snap.Groups.Values)
            {
                writer.Put(g.Name);
                writer.Put(g.Nodes.Count);
                foreach (var n in g.Nodes)
                {
                    writer.Put(n);
                }

                writer.Put(g.Parents.Count);
                foreach (var p in g.Parents)
                {
                    writer.Put(p);
                }
            }

            writer.Put(snap.Users.Count);
            foreach (var u in snap.Users.Values)
            {
                writer.Put(u.Uuid);
                writer.Put(u.Groups.Count);
                foreach (var g in u.Groups)
                {
                    writer.Put(g);
                }

                writer.Put(u.Nodes.Count);
                foreach (var n in u.Nodes)
                {
                    writer.Put(n);
                }
            }

            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        private static void HandleShoutMode(NetPeer peer, NetPacketReader reader, bool enable)
        {
            ushort id = reader.GetUShort();
            Basis.Network.Server.Generic.BasisSavedState.SetShoutMode(id, enable);
            BasisServerHandle.BasisServerHandleEvents.BroadcastShoutModeState(id, enable);
        }

        private static void HandleGlobalToggle(NetPeer peer, string contentType, bool nowLocked)
        {
            string state = nowLocked ? "DISABLED" : "ENABLED";
            string notification = $"{contentType} loading has been globally {state} by an admin.";
            BNL.Log(notification);

            // Notify the admin who toggled it
            SendBackMessage(peer, $"{contentType} loading is now {state}.");

            // Notify all clients about the change
            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
            writer.Put(notification);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            // Broadcast updated lock state so clients track it
            BasisGlobalLockManager.BroadcastLockState();
        }

        public static void SendBackMessage(NetPeer peer, string msg)
        {
            if (string.IsNullOrEmpty(msg))
            {
                return;
            }

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.Message);
            writer.Put(msg);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
        public static bool GetBannedReason(string UUID, out string reason)
        {
            if (BannedPlayers.TryGetValue(UUID, out BannedPlayer player))
            {
                reason = player.Reason;
                return true;
            }

            reason = string.Empty;
            return false;
        }
        public static bool IsIpBanned(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                return false;
            }

            return BannedPlayers.Values.Any(p => p.HasBannedIp && p.BannedIp == ip);
        }
    }
}
