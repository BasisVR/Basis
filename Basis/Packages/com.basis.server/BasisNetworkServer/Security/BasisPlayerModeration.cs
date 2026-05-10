using Basis.Network.Core;
using BasisNetworkCore;
using BasisNetworkCore.Security;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using BasisNetworking.InitalData;
using BasisNetworking.InitialData;
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

            string ip = peer.Identity;
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
                    Require(peer, PermNodes.ModerationTeleport, () =>
                    {
                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, mode);
                        writer.Put(reader.GetUShort());
                        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(writer);
                    });
                    break;

                case AdminRequestMode.TeleportPlayer:
                    Require(peer, PermNodes.ModerationTeleport, () =>
                    {
                        ushort targetId = reader.GetUShort();
                        if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out var targetPeer))
                            return;

                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, mode);
                        writer.Put((ushort)peer.Id);
                        NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
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

                case AdminRequestMode.GlobalToggleServers:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Server share", BasisGlobalLockManager.ToggleServers()));
                    break;

                case AdminRequestMode.GlobalToggleThirdPerson:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Third-person camera", BasisGlobalLockManager.ToggleThirdPerson()));
                    break;

                case AdminRequestMode.SetGlobalHeadlessAudio:
                    Require(peer, PermNodes.ModerationHeadlessAudio, () =>
                        HandleHeadlessAudioSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalHeadlessDisallow:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleHeadlessDisallowSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalOpusPacketLoss:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleOpusPacketLossSet(peer, reader));
                    break;

                case AdminRequestMode.SetUserOpusBitrate:
                    Require(peer, PermNodes.ModerationOpusBitrate, () =>
                        HandleUserOpusBitrateSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalOpusFrameDuration:
                    Require(peer, PermNodes.ModerationOpusBitrate, () =>
                        HandleOpusFrameDurationSet(peer, reader));
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

                // ===== SERVER CONFIG =====
                case AdminRequestMode.SetServerName:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyServerName(reader.GetString())));
                    break;

                case AdminRequestMode.SetServerMotd:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyServerMotd(reader.GetString())));
                    break;

                case AdminRequestMode.SetWhitelistMode:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyWhitelistMode(reader.GetByte())));
                    break;

                case AdminRequestMode.AddWhitelist:
                    Require(peer, PermNodes.ModerationWhitelist, () =>
                        SendBackMessage(peer, ApplyWhitelistAdd(reader.GetString())));
                    break;

                case AdminRequestMode.RemoveWhitelist:
                    Require(peer, PermNodes.ModerationWhitelist, () =>
                        SendBackMessage(peer, ApplyWhitelistRemove(reader.GetString())));
                    break;

                case AdminRequestMode.AddDefaultLibraryItem:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                    {
                        byte itemMode = reader.GetByte();
                        string itemUrl = reader.GetString();
                        string itemPassword = reader.GetString();
                        SendBackMessage(peer, ApplyAddDefaultLibraryItem(itemMode, itemUrl, itemPassword));
                    });
                    break;

                case AdminRequestMode.RemoveDefaultLibraryItem:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                    {
                        string removeUrl = reader.GetString();
                        SendBackMessage(peer, ApplyRemoveDefaultLibraryItem(removeUrl));
                    });
                    break;
            }

            reader.Recycle();
        }

        // =========================
        // Server-config admin operations
        // =========================
        // Each mutation updates the live Configuration field (read on the next info-query
        // response, ServerMetaDataMessage, or connection check) and then persists the
        // current state of Configuration to config/config.xml so the change survives a
        // restart. SaveConfig is intentionally fire-and-forget on the calling thread —
        // the XML is small and admin operations are rare.

        private static string ApplyServerName(string newName)
        {
            if (newName == null) return "Name was null.";
            if (newName.Length > BasisNetworkCommons.ServerInfoNameMaxLength)
                newName = newName.Substring(0, BasisNetworkCommons.ServerInfoNameMaxLength);
            NetworkServer.Configuration.ServerName = newName;
            SaveConfig();
            return $"Server name set to '{newName}'.";
        }

        private static string ApplyServerMotd(string newMotd)
        {
            if (newMotd == null) newMotd = string.Empty;
            if (newMotd.Length > BasisNetworkCommons.ServerInfoMotdMaxLength)
                newMotd = newMotd.Substring(0, BasisNetworkCommons.ServerInfoMotdMaxLength);
            NetworkServer.Configuration.ServerMotd = newMotd;
            SaveConfig();
            return "Server MOTD updated.";
        }

        private static string ApplyWhitelistMode(byte mode)
        {
            if (!Enum.IsDefined(typeof(BasisUserRestrictionMode), mode))
                return $"Unknown restriction mode value {mode}.";
            BasisUserRestrictionMode parsed = (BasisUserRestrictionMode)mode;
            NetworkServer.Configuration.BasisUserRestrictionMode = parsed;
            SaveConfig();
            return $"Restriction mode set to {parsed}.";
        }

        private static string ApplyWhitelistAdd(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return "UUID was empty.";
            if (NetworkServer.Whitelist == null) return "Whitelist not initialized.";
            // Fire-and-forget: BasisWhiteList.AddToWhitelistAsync appends one line and
            // is safe to leave running while we report the operation back to the admin.
            _ = NetworkServer.Whitelist.AddToWhitelistAsync(uuid);
            return $"Added {uuid} to whitelist.";
        }

        private static string ApplyWhitelistRemove(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return "UUID was empty.";
            if (NetworkServer.Whitelist == null) return "Whitelist not initialized.";
            _ = NetworkServer.Whitelist.RemoveFromWhitelistAsync(uuid);
            return $"Removed {uuid} from whitelist.";
        }

        private static string ApplyAddDefaultLibraryItem(byte mode, string url, string password)
        {
            if (string.IsNullOrWhiteSpace(url)) return "URL was empty.";
            // Mode is the client's BundledContentHolder.Mode: 0=Avatar, 1=World, 2=Prop.
            if (mode > 2) return $"Unknown library mode {mode} (expected 0=Avatar, 1=World, 2=Prop).";

            // Defensive split of `url#fragment` — if the admin pasted a copy-able share
            // string with the password baked into the URL fragment, peel it off here so
            // the password lands in the Password field instead of the URL field. The
            // client UI normally splits this before sending, but this catches admins
            // who skipped that path or used an older client.
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                string fragment = url.Substring(hashIndex + 1);
                url = url.Substring(0, hashIndex);
                if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(fragment))
                {
                    try
                    {
                        password = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fragment));
                    }
                    catch
                    {
                        // Fragment wasn't valid base64; leave password empty rather than
                        // storing the raw fragment bytes.
                    }
                }
            }

            BasisDefaultLibraryConfiguration config = new BasisDefaultLibraryConfiguration
            {
                Mode = mode,
                Url = url,
                Password = password ?? string.Empty,
            };

            string written = BasisDefaultLibraryLoader.SaveItem(Configuration.DefaultLibraryFolderName, config);
            if (string.IsNullOrEmpty(written))
            {
                return "Failed to persist default library entry — see server log.";
            }

            // Push the updated list to every connected client so the new entry shows
            // up in their library immediately, not just on next connect.
            BasisNetworkServerLibrary.BroadcastLibraryToAll();
            return $"Default library entry added ({Path.GetFileName(written)}).";
        }

        private static string ApplyRemoveDefaultLibraryItem(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "URL was empty.";

            int removed = BasisDefaultLibraryLoader.RemoveItem(Configuration.DefaultLibraryFolderName, url);
            if (removed <= 0)
            {
                return $"No default library entry matched URL '{url}'.";
            }

            BasisNetworkServerLibrary.BroadcastLibraryToAll();
            return $"Removed {removed} default library entry(ies) for URL '{url}'.";
        }

        private static void SaveConfig()
        {
            try
            {
                NetworkServer.Configuration.SaveToXml(Configuration.GetDefaultPath());
            }
            catch (Exception e)
            {
                BNL.LogError($"Failed to persist server configuration: {e.Message}");
            }
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

        private static void HandleHeadlessAudioSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set headless audio clip playback: missing state value.");
                return;
            }

            bool headlessAudioOff = reader.GetBool();
            bool changed = BasisHeadlessAudioStateManager.SetHeadlessAudio(headlessAudioOff);
            string state = headlessAudioOff ? "OFF" : "ON";
            string notification = changed
                ? $"Headless audio clip playback is now {state}."
                : $"Headless audio clip playback was already {state}.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisHeadlessAudioStateManager.BroadcastState();
        }

        private static void HandleHeadlessDisallowSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set headless connection policy: missing state value.");
                return;
            }

            bool disallowHeadless = reader.GetBool();
            bool changed = BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(disallowHeadless);
            string state = disallowHeadless ? "DISALLOWED" : "ALLOWED";
            string notification = changed
                ? $"Headless clients are now {state}."
                : $"Headless clients were already {state}.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);

            if (disallowHeadless)
            {
                BasisHeadlessConnectionPolicyManager.DisconnectConnectedHeadlessPeers();
            }

            BasisHeadlessConnectionPolicyManager.BroadcastState();
        }

        private static void HandleOpusPacketLossSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set Opus packet loss: missing value byte.");
                return;
            }

            int percent = reader.GetByte();
            bool changed = BasisOpusPacketLossStateManager.SetPacketLossPercent(percent);
            int applied = BasisOpusPacketLossStateManager.PacketLossPercent;
            string notification = changed
                ? $"Opus FEC packet-loss % is now {applied}."
                : $"Opus FEC packet-loss % was already {applied}.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisOpusPacketLossStateManager.BroadcastState();
        }

        private static void HandleUserOpusBitrateSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 6) // ushort + int
            {
                SendBackMessage(peer, "Failed to set user Opus bitrate: missing payload.");
                return;
            }

            ushort targetId = reader.GetUShort();
            int requested = reader.GetInt();

            int applied = BasisUserOpusBitrateStateManager.SetBitrate(targetId, requested);

            if (NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out var targetPeer))
            {
                BasisUserOpusBitrateStateManager.SendOverrideToPeer(targetPeer, applied);
            }

            string notification = applied == 0
                ? $"Cleared Opus bitrate override for player {targetId}."
                : $"Opus bitrate override for player {targetId} is now {applied} bps.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
        }

        private static void HandleOpusFrameDurationSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set Opus frame duration: missing value byte.");
                return;
            }

            int requested = reader.GetByte();
            if (!BasisOpusFrameDurationStateManager.IsAcceptedDuration(requested))
            {
                SendBackMessage(peer, $"Failed to set Opus frame duration: only 20 or 40 ms are accepted (got {requested}).");
                return;
            }

            bool changed = BasisOpusFrameDurationStateManager.SetFrameDurationMs(requested);
            int applied = BasisOpusFrameDurationStateManager.FrameDurationMs;
            string notification = changed
                ? $"Opus frame duration is now {applied} ms."
                : $"Opus frame duration was already {applied} ms.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisOpusFrameDurationStateManager.BroadcastState();
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
