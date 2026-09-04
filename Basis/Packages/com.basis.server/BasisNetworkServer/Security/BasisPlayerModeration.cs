using Basis.Network.Core;
using Basis.Network.Core.Compression;
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
using BasisNetworking.InitialData;
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

        /// <summary>
        /// QueryPermission is the one admin-channel request any player may send, so it is the one
        /// that needs its own budget. A UI badge asks about each player once as they join and again
        /// when permissions change; the burst covers arriving into a full instance, the refill
        /// covers churn, and anything past that is someone walking the permission store one node
        /// at a time. Silent drop on exhaustion, same as the chat limiter — replying "slow down"
        /// hands a flooder an amplification vector.
        /// </summary>
        private static readonly BasisPeerRateLimiter PermissionQueryLimiter =
            new BasisPeerRateLimiter(tokensPerSecond: 10f, tokenBurst: 60f);

        /// <summary>Longest node or group name a query may name. Real ones are well under this.</summary>
        private const int MaxPermissionQueryLength = 128;

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
                reader.Recycle();
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
                    reader.Recycle();
                    return;
                }

                HandleGetPermissions(peer);
                reader.Recycle();
                return;
            }

            // ===== QUERY ONE PERMISSION / GROUP =====
            // Not gated, unlike the snapshot above. This answers a single yes/no about a single
            // player already in the room, which is what a client needs to badge staff on a
            // nameplate or gate a moderator-only door — a check every player has to be able to
            // make, not just the ones who could already read the whole table.
            if (mode == AdminRequestMode.QueryPermission)
            {
                HandleQueryPermission(peer, reader);
                reader.Recycle();
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

                case AdminRequestMode.EnableAnnounceMode:
                case AdminRequestMode.DisableAnnounceMode:
                    Require(peer, PermNodes.ModerationAnnounce, () =>
                        HandleAnnounceMode(peer, reader, mode == AdminRequestMode.EnableAnnounceMode));
                    break;

                case AdminRequestMode.EnableShoutMode:
                case AdminRequestMode.DisableShoutMode:
                    Require(peer, PermNodes.ModerationAnnounce, () =>
                        HandleShoutMode(peer, reader, mode == AdminRequestMode.EnableShoutMode));
                    break;

                case AdminRequestMode.SetFullQualityBroadcast:
                    Require(peer, PermNodes.ModerationFullQualityBroadcast, () =>
                        HandleFullQualityBroadcast(peer, reader));
                    break;

                case AdminRequestMode.SetVoiceMute:
                    Require(peer, PermNodes.ModerationMute, () =>
                        SendBackMessage(peer, BasisPlayerMuteManager.Apply(reader.GetString(), voice: true, reader.GetBool())));
                    break;

                case AdminRequestMode.SetTextMute:
                    Require(peer, PermNodes.ModerationMute, () =>
                        SendBackMessage(peer, BasisPlayerMuteManager.Apply(reader.GetString(), voice: false, reader.GetBool())));
                    break;

                case AdminRequestMode.ForceAvatar:
                    Require(peer, PermNodes.ModerationForceAvatar, () =>
                        HandleForceAvatar(peer, reader));
                    break;

                case AdminRequestMode.ForceAvatarAll:
                    Require(peer, PermNodes.ModerationForceAvatar, () =>
                        HandleForceAvatarAll(peer, reader));
                    break;

                case AdminRequestMode.SetLocomotionOverride:
                    Require(peer, PermNodes.ModerationLocomotion, () =>
                        HandleSetLocomotionOverride(peer, reader));
                    break;

                case AdminRequestMode.SetLocomotionOverrideAll:
                    Require(peer, PermNodes.ModerationLocomotion, () =>
                        HandleSetLocomotionOverrideAll(peer, reader));
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
                        HandleGlobalFeatureToggle(peer, "The third-person camera", BasisGlobalLockManager.ToggleThirdPerson()));
                    break;

                case AdminRequestMode.GlobalToggleAdditionalAvatarDataLock:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                    {
                        // Not HandleGlobalToggle: its "loading ENABLED/DISABLED" template reads
                        // inverted for this flag (it said DISABLED at the moment stripping turned
                        // ON), which misled admins into leaving face tracking stripped server-wide.
                        bool nowStripping = BasisGlobalLockManager.ToggleAdditionalAvatarDataLock();
                        HandleGlobalStateNotification(peer, nowStripping
                            ? "Additional avatar data (face tracking, avatar behaviour params) is now STRIPPED for everyone."
                            : "Additional avatar data (face tracking, avatar behaviour params) now flows normally.");
                    });
                    break;

                case AdminRequestMode.SetGlobalCameraPolicy:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleCameraPolicySet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalHeadlessAudio:
                    Require(peer, PermNodes.ModerationHeadlessAudio, () =>
                        HandleHeadlessAudioSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalCrashReporting:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleCrashReportingSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalAudioRangeLimits:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleAudioRangeLimitsSet(peer, reader));
                    break;

                case AdminRequestMode.GlobalTogglePlayspaceMover:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandlePlayspaceMoverToggle(peer));
                    break;

                case AdminRequestMode.GlobalToggleDirectConnect:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleDirectConnectToggle(peer));
                    break;

                case AdminRequestMode.GlobalToggleCilbox:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Avatar Cilbox code", BasisGlobalLockManager.ToggleCilbox()));
                    break;

                case AdminRequestMode.GlobalToggleImages:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Shared image", BasisGlobalLockManager.ToggleImages()));
                    break;

                case AdminRequestMode.GlobalToggleEndEffectorIK:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Remote end-effector IK", BasisGlobalLockManager.ToggleEndEffectorIK()));
                    break;

                case AdminRequestMode.GlobalToggleTextChat:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Text chat", BasisGlobalLockManager.ToggleTextChat()));
                    break;

                case AdminRequestMode.GlobalToggleVoiceChat:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Voice chat", BasisGlobalLockManager.ToggleVoiceChat()));
                    break;

                case AdminRequestMode.GlobalToggleMediaPlayer:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Media players", BasisGlobalLockManager.ToggleMediaPlayer()));
                    break;

                case AdminRequestMode.GlobalToggleCameraCapture:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Camera capture", BasisGlobalLockManager.ToggleCameraCapture()));
                    break;

                case AdminRequestMode.GlobalTogglePropGrabbing:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalFeatureToggle(peer, "Prop grabbing", BasisGlobalLockManager.TogglePropGrabbing()));
                    break;

                case AdminRequestMode.GlobalToggleSafeDisplayNames:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalProtectionToggle(peer, "Safe display names", BasisGlobalLockManager.ToggleSafeDisplayNames()));
                    break;

                case AdminRequestMode.SetGlobalAvatarScaleLimits:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleAvatarScaleLimitsSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalResourceLimits:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleResourceLimitsSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalReductionSettings:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleReductionSettingsSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalImageBandwidth:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleImageBandwidthSet(peer, reader));
                    break;

                case AdminRequestMode.RequestAllLogs:
                    Require(peer, PermNodes.AdminLogs, () =>
                        BasisServerLogBundleService.SendAllLogsToPeer(peer));
                    break;

                case AdminRequestMode.DeleteAllLogs:
                    Require(peer, PermNodes.AdminLogs, () =>
                        BasisServerLogBundleService.DeleteAllLogsForPeer(peer));
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

                case AdminRequestMode.SetGlobalOpusBitrate:
                    Require(peer, PermNodes.ModerationOpusBitrate, () =>
                        HandleGlobalOpusBitrateSet(peer, reader));
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

                case AdminRequestMode.SetAllowlistMode:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyAllowlistMode(reader.GetByte())));
                    break;

                case AdminRequestMode.SetGlobalPeerLimit:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        HandlePeerLimitSet(peer, reader));
                    break;

                case AdminRequestMode.AddAllowlist:
                    Require(peer, PermNodes.ModerationAllowlist, () =>
                        SendBackMessage(peer, ApplyAllowlistAdd(reader.GetString())));
                    break;

                case AdminRequestMode.RemoveAllowlist:
                    Require(peer, PermNodes.ModerationAllowlist, () =>
                        SendBackMessage(peer, ApplyAllowlistRemove(reader.GetString())));
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

        private static string ApplyAllowlistMode(byte mode)
        {
            BasisUserRestrictionMode parsed = (BasisUserRestrictionMode)mode;
            if (!Enum.IsDefined(typeof(BasisUserRestrictionMode), parsed))
                return $"Unknown restriction mode value {mode}.";
            NetworkServer.Configuration.BasisUserRestrictionMode = parsed;

            if (parsed == BasisUserRestrictionMode.RejoinOnly)
                BasisRejoinLockManager.CaptureCurrentPopulation();
            else
                BasisRejoinLockManager.Clear();

            SaveConfig();
            // Restriction mode rides on the lock-state payload; push it so connected clients refresh.
            BasisGlobalLockManager.BroadcastLockState();
            return $"Restriction mode set to {parsed}.";
        }

        private static string ApplyAllowlistAdd(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return "UUID was empty.";
            if (NetworkServer.AllowList == null) return "AllowList not initialized.";
            // Fire-and-forget: BasisAllowList.AddToAllowlistAsync appends one line and
            // is safe to leave running while we report the operation back to the admin.
            _ = NetworkServer.AllowList.AddToAllowlistAsync(uuid);
            return $"Added {uuid} to allowlist.";
        }

        private static string ApplyAllowlistRemove(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return "UUID was empty.";
            if (NetworkServer.AllowList == null) return "AllowList not initialized.";
            _ = NetworkServer.AllowList.RemoveFromAllowlistAsync(uuid);
            return $"Removed {uuid} from allowlist.";
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
            // SetUserGroup/SetUserNode/SetGroupNode/SetGroupParent all carry a trailing `add` bool.
            // It used to be left unread, so every "remove" button on the client added instead.
            string result;
            switch (mode)
            {
                case AdminRequestMode.SetUserGroup:
                {
                    string uuid = reader.GetString();
                    string group = reader.GetString();
                    bool add = reader.GetBool();
                    if (add) PermissionIntegration.Manager.AddUserToGroup(uuid, group);
                    else PermissionIntegration.Manager.RemoveUserFromGroup(uuid, group);
                    result = $"{(add ? "Added" : "Removed")} {uuid} {(add ? "to" : "from")} group '{group}'.";
                    break;
                }

                case AdminRequestMode.SetUserNode:
                {
                    string uuid = reader.GetString();
                    string node = reader.GetString();
                    bool add = reader.GetBool();
                    if (add) PermissionIntegration.Manager.AddUserNode(uuid, node);
                    else PermissionIntegration.Manager.RemoveUserNode(uuid, node);
                    result = $"{(add ? "Added" : "Removed")} node '{node}' {(add ? "to" : "from")} user {uuid}.";
                    break;
                }

                case AdminRequestMode.SetGroupNode:
                {
                    string group = reader.GetString();
                    string node = reader.GetString();
                    bool add = reader.GetBool();
                    if (add) PermissionIntegration.Manager.AddGroupNode(group, node);
                    else PermissionIntegration.Manager.RemoveGroupNode(group, node);
                    result = $"{(add ? "Added" : "Removed")} node '{node}' {(add ? "to" : "from")} group '{group}'.";
                    break;
                }

                case AdminRequestMode.CreateGroup:
                {
                    string group = reader.GetString();
                    PermissionIntegration.Manager.GetOrCreateGroup(group);
                    result = $"Group '{group}' created.";
                    break;
                }

                case AdminRequestMode.DeleteGroup:
                {
                    string group = reader.GetString();
                    result = PermissionIntegration.Manager.DeleteGroup(group)
                        ? $"Group '{group}' deleted."
                        : $"No group named '{group}'.";
                    break;
                }

                case AdminRequestMode.SetGroupParent:
                {
                    string group = reader.GetString();
                    string parent = reader.GetString();
                    bool add = reader.GetBool();
                    if (add) PermissionIntegration.Manager.AddGroupParent(group, parent);
                    else PermissionIntegration.Manager.RemoveGroupParent(group, parent);
                    result = $"Group '{group}' {(add ? "now inherits" : "no longer inherits")} '{parent}'.";
                    break;
                }

                default:
                    result = "Permission updated";
                    break;
            }

            SendBackMessage(peer, result);
        }

        private static void HandleQueryPermission(NetPeer peer, NetPacketReader reader)
        {
            ushort targetId = reader.GetUShort();
            byte kind = reader.GetByte();
            string value = reader.GetString();

            if (!PermissionQueryLimiter.TryConsume(peer))
            {
                return;
            }

            // Answered with an empty echo rather than the value: only a modified client gets here
            // (the sending side caps the same length), and there is no reason to spend the reply
            // carrying an oversized string back out again.
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxPermissionQueryLength)
            {
                SendQueryPermissionResult(peer, targetId, kind, string.Empty, held: false, targetFound: false);
                return;
            }

            // Only players connected right now can be asked about. Answering by UUID instead would
            // turn this into a lookup over the whole store, which is what GetPermissions gates.
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out NetPeer targetPeer) ||
                !NetworkServer.AuthIdentity.NetIDToUUID(targetPeer, out string targetUUID))
            {
                SendQueryPermissionResult(peer, targetId, kind, value, held: false, targetFound: false);
                return;
            }

            bool held = (AdminPermissionQueryKind)kind == AdminPermissionQueryKind.Group
                ? PermissionIntegration.Manager.IsInGroup(targetUUID, value)
                : PermissionIntegration.Manager.Has(targetUUID, value);

            SendQueryPermissionResult(peer, targetId, kind, value, held, targetFound: true);
        }

        private static void SendQueryPermissionResult(NetPeer peer, ushort targetId, byte kind, string value, bool held, bool targetFound)
        {
            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.QueryPermissionResult);
            writer.Put(targetId);
            writer.Put(kind);
            writer.Put(value ?? string.Empty);
            writer.Put(held);
            writer.Put(targetFound);

            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
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

        private static void HandleAnnounceMode(NetPeer peer, NetPacketReader reader, bool enable)
        {
            ushort id = reader.GetUShort();
            Basis.Network.Server.Generic.BasisSavedState.SetAnnounceMode(id, enable);
            BasisServerHandle.BasisServerHandleEvents.BroadcastAnnounceModeState(id, enable, (ushort)peer.Id);
        }

        private static void HandleShoutMode(NetPeer peer, NetPacketReader reader, bool enable)
        {
            ushort id = reader.GetUShort();
            Basis.Network.Server.Generic.BasisSavedState.SetShoutMode(id, enable);
            BasisServerHandle.BasisServerHandleEvents.BroadcastShoutModeState(id, enable, (ushort)peer.Id);
        }

        private static void HandleFullQualityBroadcast(NetPeer peer, NetPacketReader reader)
        {
            ushort id = reader.GetUShort();
            bool enable = reader.GetBool();
            BasisNetworkServer.BasisNetworkingReductionSystem.BasisServerReductionSystemEvents.SetBypassReduction(id, enable);
            SendBackMessage(peer, $"Full-quality broadcast {(enable ? "ENABLED" : "DISABLED")} for player {id}.");
        }

        /// <summary>
        /// Relays a moderator's avatar choice to the one peer it targets. The server never loads or
        /// validates the bundle itself — it only decides who is allowed to be told to wear it. The
        /// target's own avatar-change broadcast is what reaches everyone else, so
        /// <see cref="BasisSavedState"/> stays correct for late joiners with nothing extra here.
        /// </summary>
        private static void HandleSetLocomotionOverrideAll(NetPeer peer, NetPacketReader reader)
        {
            byte fields = reader.GetByte();
            float jumpHeight = reader.GetFloat();
            float walkSpeed = reader.GetFloat();
            float runSpeed = reader.GetFloat();
            float gravity = reader.GetFloat();
            byte movementMode = reader.GetByte();

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.LocomotionOverrideApply);
            writer.Put((ushort)peer.Id);
            writer.Put(fields);
            writer.Put(jumpHeight);
            writer.Put(walkSpeed);
            writer.Put(runSpeed);
            writer.Put(gravity);
            writer.Put(movementMode);

            int sent = 0;
            int protectedSkipped = 0;
            foreach (NetPeer target in NetworkServer.PeerSnapshot)
            {
                if (target == null || target.Id == peer.Id)
                {
                    continue;
                }

                if (NetworkServer.AuthIdentity.NetIDToUUID(target, out string targetUUID) && IsProtected(targetUUID))
                {
                    protectedSkipped++;
                    continue;
                }

                NetworkServer.TrySend(target, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
                sent++;
            }
            NetworkServer.ReturnWriter(writer);

            string verb = fields == 0 ? "cleared on" : "applied to";
            SendBackMessage(peer, protectedSkipped > 0
                ? $"Locomotion override {verb} {sent} player(s); {protectedSkipped} protected player(s) skipped."
                : $"Locomotion override {verb} {sent} player(s).");
        }

        private static void HandleSetLocomotionOverride(NetPeer peer, NetPacketReader reader)
        {
            ushort targetId = reader.GetUShort();
            byte fields = reader.GetByte();
            float jumpHeight = reader.GetFloat();
            float walkSpeed = reader.GetFloat();
            float runSpeed = reader.GetFloat();
            float gravity = reader.GetFloat();
            byte movementMode = reader.GetByte();

            if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out NetPeer targetPeer))
            {
                SendBackMessage(peer, "Player not found");
                return;
            }

            if (NetworkServer.AuthIdentity.NetIDToUUID(targetPeer, out string targetUUID) && IsProtected(targetUUID))
            {
                SendBackMessage(peer, "Target is protected");
                return;
            }

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.LocomotionOverrideApply);
            writer.Put((ushort)peer.Id);
            writer.Put(fields);
            writer.Put(jumpHeight);
            writer.Put(walkSpeed);
            writer.Put(runSpeed);
            writer.Put(gravity);
            writer.Put(movementMode);
            NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            SendBackMessage(peer, fields == 0
                ? $"Locomotion override cleared on player {targetId}."
                : $"Locomotion override applied to player {targetId}.");
        }

        private static void HandleForceAvatar(NetPeer peer, NetPacketReader reader)
        {
            ushort targetId = reader.GetUShort();
            string url = reader.GetString();
            string password = reader.GetString();
            byte embeddedSource = reader.GetByte();

            if (string.IsNullOrEmpty(url))
            {
                SendBackMessage(peer, "Avatar url invalid");
                return;
            }

            if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out NetPeer targetPeer))
            {
                SendBackMessage(peer, "Player not found");
                return;
            }

            // Same courtesy Kick/Ban extend: a protected user can't be dressed by another moderator.
            if (NetworkServer.AuthIdentity.NetIDToUUID(targetPeer, out string targetUUID) && IsProtected(targetUUID))
            {
                SendBackMessage(peer, "Target is protected");
                return;
            }

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.ForceAvatarApply);
            writer.Put((ushort)peer.Id);
            writer.Put(url);
            writer.Put(password ?? string.Empty);
            writer.Put(embeddedSource);
            NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            SendBackMessage(peer, $"Avatar forced on player {targetId}.");
        }

        /// <summary>
        /// The crowd version of <see cref="HandleForceAvatar"/> — one avatar, every peer. Sent peer by
        /// peer rather than through BroadcastMessageToClients because a blanket broadcast has no way to
        /// exempt <see cref="PermNodes.protection"/> holders, and force-dressing another moderator is
        /// exactly what that node exists to prevent. The payload is a plain ForceAvatarApply, so the
        /// client needs no separate receive path for this.
        /// </summary>
        private static void HandleForceAvatarAll(NetPeer peer, NetPacketReader reader)
        {
            string url = reader.GetString();
            string password = reader.GetString();
            byte embeddedSource = reader.GetByte();

            if (string.IsNullOrEmpty(url))
            {
                SendBackMessage(peer, "Avatar url invalid");
                return;
            }

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.ForceAvatarApply);
            writer.Put((ushort)peer.Id);
            writer.Put(url);
            writer.Put(password ?? string.Empty);
            writer.Put(embeddedSource);

            int sent = 0;
            int protectedSkipped = 0;
            foreach (NetPeer target in NetworkServer.PeerSnapshot)
            {
                if (target == null || target.Id == peer.Id)
                {
                    continue;
                }

                if (NetworkServer.AuthIdentity.NetIDToUUID(target, out string targetUUID) && IsProtected(targetUUID))
                {
                    protectedSkipped++;
                    continue;
                }

                NetworkServer.TrySend(target, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
                sent++;
            }
            NetworkServer.ReturnWriter(writer);

            SendBackMessage(peer, protectedSkipped > 0
                ? $"Avatar forced on {sent} player(s); {protectedSkipped} protected player(s) skipped."
                : $"Avatar forced on {sent} player(s).");
        }

        private static void HandleCrashReportingSet(NetPeer peer, NetPacketReader reader)
        {
            bool enabled = reader.GetBool();
            NetworkServer.Configuration.CrashReportingEnabled = enabled;
            SaveConfig();
            BasisCrashReportStateManager.SetEnabled(enabled);
            BasisCrashReportStateManager.BroadcastState();
            SendBackMessage(peer, $"Crash reporting {(enabled ? "ENABLED" : "DISABLED")}.");
        }

        private static void HandleAudioRangeLimitsSet(NetPeer peer, NetPacketReader reader)
        {
            float microphoneMeters = reader.GetFloat();
            float hearingMeters = reader.GetFloat();
            BasisAudioRangeLimitManager.SetLimits(microphoneMeters, hearingMeters);
            NetworkServer.Configuration.MaxMicrophoneRangeMeters = BasisAudioRangeLimitManager.MaxMicrophoneRangeMeters;
            NetworkServer.Configuration.MaxHearingRangeMeters = BasisAudioRangeLimitManager.MaxHearingRangeMeters;
            SaveConfig();
            BasisAudioRangeLimitManager.BroadcastState();
            SendBackMessage(peer, $"Audio range limits set: microphone {NetworkServer.Configuration.MaxMicrophoneRangeMeters} m, hearing {NetworkServer.Configuration.MaxHearingRangeMeters} m.");
        }

        private static void HandlePlayspaceMoverToggle(NetPeer peer)
        {
            bool locked = BasisGlobalLockManager.TogglePlayspaceMover();
            string state = locked ? "DISABLED" : "ENABLED";
            BroadcastGlobalLockNotice(peer,
                $"Playspace mover is now {state}.",
                $"The playspace mover has been globally {state} for non-admins by an admin.");
        }

        private static void HandleDirectConnectToggle(NetPeer peer)
        {
            bool locked = BasisGlobalLockManager.ToggleDirectConnect();
            string state = locked ? "DISABLED" : "ENABLED";
            BroadcastGlobalLockNotice(peer,
                $"Direct connections are now {state}.",
                $"Direct (peer-to-peer) connections have been globally {state} for non-admins by an admin.");
        }

        private static void HandleAvatarScaleLimitsSet(NetPeer peer, NetPacketReader reader)
        {
            float minMeters = reader.GetFloat();
            float maxMeters = reader.GetFloat();
            BasisAvatarScaleLimitManager.SetLimits(minMeters, maxMeters);
            NetworkServer.Configuration.MinAvatarEyeHeightMeters = BasisAvatarScaleLimitManager.MinMeters;
            NetworkServer.Configuration.MaxAvatarEyeHeightMeters = BasisAvatarScaleLimitManager.MaxMeters;
            SaveConfig();
            BasisAvatarScaleLimitManager.BroadcastState();
            SendBackMessage(peer, $"Avatar scale limits set: {NetworkServer.Configuration.MinAvatarEyeHeightMeters} m .. {NetworkServer.Configuration.MaxAvatarEyeHeightMeters} m.");
        }

        private static void HandleResourceLimitsSet(NetPeer peer, NetPacketReader reader)
        {
            int maxContentSpheresPerPlayer = reader.GetInt();
            BasisResourceLimitManager.SetLimits(maxContentSpheresPerPlayer);
            NetworkServer.Configuration.MaxContentSpheresPerPlayer = BasisResourceLimitManager.MaxContentSpheresPerPlayer;
            SaveConfig();
            BasisResourceLimitManager.BroadcastState();
            SendBackMessage(peer, $"Resource limits set: spheres/player {BasisResourceLimitManager.MaxContentSpheresPerPlayer}.");
        }

        private static void HandleReductionSettingsSet(NetPeer peer, NetPacketReader reader)
        {
            var config = NetworkServer.Configuration;
            config.BSRSMillisecondDefaultInterval = reader.GetInt();
            config.BSRBaseMultiplier = reader.GetInt();
            config.BSRSIncreaseRate = reader.GetFloat();
            config.BSRSlowestSendRate = reader.GetFloat();
            config.HighQualityDistance = reader.GetFloat();
            config.MediumQualityDistance = reader.GetFloat();
            config.LowQualityDistance = reader.GetFloat();
            config.EnableAvatarBundleCompression = reader.GetBool();
            config.AvatarBundleMinMessages = reader.GetInt();
            config.AvatarBundleMinBytes = reader.GetInt();
            config.EnableBSRProfiling = reader.GetBool();
            config.EnableAvatarBundleZstd = reader.GetBool();
            config.AvatarBundleZstdDeltaBundles = reader.GetBool();
            config.AvatarBundleZstdLevel = reader.GetInt();
            config.AvatarBundleZstdMaxShedTier = reader.GetInt();

            if (config.BSRSMillisecondDefaultInterval < 1) config.BSRSMillisecondDefaultInterval = 1;
            if (config.BSRBaseMultiplier < 1) config.BSRBaseMultiplier = 1;
            if (config.BSRSIncreaseRate < 0f) config.BSRSIncreaseRate = 0f;
            if (config.BSRSlowestSendRate < 0f) config.BSRSlowestSendRate = 0f;
            const float MaxQualityDistanceMeters = 1000f;

            // Upper bounds matter as much as lower ones here: the value is persisted to config.xml
            // and an unbounded distance pins every peer to the High avatar tier permanently.
            if (config.HighQualityDistance < 0f) config.HighQualityDistance = 0f;
            if (config.HighQualityDistance > MaxQualityDistanceMeters) config.HighQualityDistance = MaxQualityDistanceMeters;
            if (config.MediumQualityDistance < 0f) config.MediumQualityDistance = 0f;
            if (config.MediumQualityDistance > MaxQualityDistanceMeters) config.MediumQualityDistance = MaxQualityDistanceMeters;
            if (config.LowQualityDistance < 0f) config.LowQualityDistance = 0f;
            if (config.LowQualityDistance > MaxQualityDistanceMeters) config.LowQualityDistance = MaxQualityDistanceMeters;
            if (config.AvatarBundleMinMessages < 1) config.AvatarBundleMinMessages = 1;
            if (config.AvatarBundleMinBytes < 0) config.AvatarBundleMinBytes = 0;
            // Clamp to the range zstd actually accepts — a level outside it throws inside the
            // codec, and this value arrives from an admin client rather than from config.xml.
            if (config.AvatarBundleZstdLevel < BasisAvatarBundleZstd.MinLevel) config.AvatarBundleZstdLevel = BasisAvatarBundleZstd.MinLevel;
            if (config.AvatarBundleZstdLevel > BasisAvatarBundleZstd.MaxLevel) config.AvatarBundleZstdLevel = BasisAvatarBundleZstd.MaxLevel;

            NetworkServer.InitializePulseSettings();
            SaveConfig();
            BroadcastReductionSettings();
            SendBackMessage(peer, $"Reduction settings set: interval {config.BSRSMillisecondDefaultInterval}ms, base x{config.BSRBaseMultiplier}, rate {config.BSRSIncreaseRate}, slowest {config.BSRSlowestSendRate}, distances {config.HighQualityDistance}/{config.MediumQualityDistance}/{config.LowQualityDistance}m, bundle {config.EnableAvatarBundleCompression} (min {config.AvatarBundleMinMessages}msg/{config.AvatarBundleMinBytes}B), profiling {config.EnableBSRProfiling}. SlowestSendRate applies to new joins only.");
        }

        /// <summary>
        /// Applies the image/gif bandwidth budgets from an admin.
        ///
        /// The upload figure lands live in two places at once: it is what the server enforces from
        /// the next packet onward, and it is what new joiners are told to pace themselves to. It is
        /// NOT re-advertised to players already connected — the number rides
        /// <c>ServerMetaDataMessage</c>, which is only built at join and on a permission refresh —
        /// so lowering it takes hold immediately as a limit and gradually as a request. That
        /// asymmetry is safe in the direction that matters: the enforced value is the strict one.
        /// </summary>
        private static void HandleImageBandwidthSet(NetPeer peer, NetPacketReader reader)
        {
            var config = NetworkServer.Configuration;
            config.ImageShareEgressMegabitsPerSecond = reader.GetInt();
            config.ImageShareDownloadMegabitsPerSecond = reader.GetInt();
            config.ImageShareEgressEnforcementPercent = reader.GetInt();

            // 0 is meaningful on both rates — "unmetered" for download, "client keeps its own
            // conservative default" for upload — so only negatives are corrected.
            if (config.ImageShareEgressMegabitsPerSecond < 0) config.ImageShareEgressMegabitsPerSecond = 0;
            if (config.ImageShareDownloadMegabitsPerSecond < 0) config.ImageShareDownloadMegabitsPerSecond = 0;

            // Enforcing below what was advertised would drop honest clients doing exactly what they
            // were told, which is the one outcome this feature must never produce.
            if (config.ImageShareEgressEnforcementPercent < 100) config.ImageShareEgressEnforcementPercent = 100;
            if (config.ImageShareEgressEnforcementPercent > 1000) config.ImageShareEgressEnforcementPercent = 1000;

            SaveConfig();
            BroadcastImageBandwidth();
            SendBackMessage(peer,
                $"Image bandwidth set: upload {config.ImageShareEgressMegabitsPerSecond} Mb/s per sharer " +
                $"(enforced at {config.ImageShareEgressEnforcementPercent}%), " +
                $"download {config.ImageShareDownloadMegabitsPerSecond} Mb/s per joining player. " +
                "Upload applies live as a limit; the advertised figure reaches existing players on their next join or permission refresh.");
        }

        private static void WriteImageBandwidth(NetDataWriter writer)
        {
            var config = NetworkServer.Configuration;
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetImageBandwidth);
            writer.Put(config.ImageShareEgressMegabitsPerSecond);
            writer.Put(config.ImageShareDownloadMegabitsPerSecond);
            writer.Put(config.ImageShareEgressEnforcementPercent);
        }

        private static void BroadcastImageBandwidth()
        {
            var writer = NetworkServer.RentWriter();
            WriteImageBandwidth(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        public static void SendImageBandwidthToPeer(NetPeer peer)
        {
            var writer = NetworkServer.RentWriter();
            WriteImageBandwidth(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Applies the maximum player count from an admin.
        ///
        /// The gate that reads it runs per connection request, so the new cap binds from the next
        /// join onward. Setting it below the current population is allowed and drops nobody — the
        /// instance stops admitting players until it drains under the cap.
        /// </summary>
        private static void HandlePeerLimitSet(NetPeer peer, NetPacketReader reader)
        {
            var config = NetworkServer.Configuration;
            config.PeerLimit = reader.GetInt();

            // 0 or negative would seal the instance shut, and player ids are ushort on the wire, so
            // a cap past ushort.MaxValue could never be reached anyway.
            if (config.PeerLimit < 1) config.PeerLimit = 1;
            if (config.PeerLimit > ushort.MaxValue) config.PeerLimit = ushort.MaxValue;

            SaveConfig();
            BroadcastPeerLimit();
            SendBackMessage(peer,
                $"Max players set to {config.PeerLimit}. Applies from the next join; nobody connected now is disconnected.");
        }

        private static void WritePeerLimit(NetDataWriter writer)
        {
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetPeerLimit);
            writer.Put(NetworkServer.Configuration.PeerLimit);
        }

        private static void BroadcastPeerLimit()
        {
            var writer = NetworkServer.RentWriter();
            WritePeerLimit(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        public static void SendPeerLimitToPeer(NetPeer peer)
        {
            var writer = NetworkServer.RentWriter();
            WritePeerLimit(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        private static void WriteReductionSettings(NetDataWriter writer)
        {
            var config = NetworkServer.Configuration;
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetReductionSettings);
            writer.Put(config.BSRSMillisecondDefaultInterval);
            writer.Put(config.BSRBaseMultiplier);
            writer.Put(config.BSRSIncreaseRate);
            writer.Put(config.BSRSlowestSendRate);
            writer.Put(config.HighQualityDistance);
            writer.Put(config.MediumQualityDistance);
            writer.Put(config.LowQualityDistance);
            writer.Put(config.EnableAvatarBundleCompression);
            writer.Put(config.AvatarBundleMinMessages);
            writer.Put(config.AvatarBundleMinBytes);
            writer.Put(config.EnableBSRProfiling);
            writer.Put(config.EnableAvatarBundleZstd);
            writer.Put(config.AvatarBundleZstdDeltaBundles);
            writer.Put(config.AvatarBundleZstdLevel);
            writer.Put(config.AvatarBundleZstdMaxShedTier);
        }

        private static void BroadcastReductionSettings()
        {
            var writer = NetworkServer.RentWriter();
            WriteReductionSettings(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        public static void SendReductionSettingsToPeer(NetPeer peer)
        {
            var writer = NetworkServer.RentWriter();
            WriteReductionSettings(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Reply to the toggling admin, broadcast a one-line notice to everyone, then push the
        /// refreshed lock-state payload. Used by the restriction toggles whose state rides on
        /// GlobalGetLockState (playspace mover, direct connect).
        /// </summary>
        private static void BroadcastGlobalLockNotice(NetPeer peer, string adminReply, string broadcastNotice)
        {
            BNL.Log(broadcastNotice);
            SendBackMessage(peer, adminReply);

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
            writer.Put(broadcastNotice);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            PersistGlobalLockState();
            BasisGlobalLockManager.BroadcastLockState();
        }

        private static void HandleGlobalToggle(NetPeer peer, string contentType, bool nowLocked)
        {
            string state = nowLocked ? "DISABLED" : "ENABLED";
            HandleGlobalStateNotification(peer, $"{contentType} loading has been globally {state} by an admin.");
        }

        /// <summary>
        /// Notification for locks over a live feature rather than content loading (chat, voice,
        /// grabbing, ...). HandleGlobalToggle's "<c>X loading</c>" template reads as nonsense for
        /// these — nothing is being loaded — so they get a plain "<c>X has been ... DISABLED</c>".
        /// </summary>
        private static void HandleGlobalFeatureToggle(NetPeer peer, string featureName, bool nowLocked)
        {
            string state = nowLocked ? "DISABLED" : "ENABLED";
            HandleGlobalStateNotification(peer, $"{featureName} has been globally {state} by an admin.");
        }

        /// <summary>
        /// Notification for a protection that is ENABLED when its flag is set — the opposite sense
        /// to every lock above, so the shared templates would announce the exact inverse of what
        /// the admin just did.
        /// </summary>
        private static void HandleGlobalProtectionToggle(NetPeer peer, string protectionName, bool nowEnforced)
        {
            string state = nowEnforced ? "ENABLED" : "DISABLED";
            HandleGlobalStateNotification(peer, $"{protectionName} has been globally {state} by an admin.");
        }

        /// <summary>
        /// Notifies the toggling admin + all clients with a pre-composed, unambiguous message and
        /// rebroadcasts the lock state. Toggles whose semantics don't fit the
        /// "loading ENABLED/DISABLED" template (e.g. the additional-avatar-data strip) use this
        /// directly so the message can state what actually changed.
        /// </summary>
        private static void HandleGlobalStateNotification(NetPeer peer, string notification)
        {
            BNL.Log(notification);

            // Notify the admin who toggled it
            SendBackMessage(peer, notification);

            // Notify all clients about the change
            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
            writer.Put(notification);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            // Broadcast updated lock state so clients track it
            PersistGlobalLockState();
            BasisGlobalLockManager.BroadcastLockState();
        }

        /// <summary>
        /// Mirrors the live global lock state onto Configuration and writes config.xml. Every lock
        /// seeds itself from config at boot, so a toggle that isn't persisted here silently reverts
        /// on the next restart.
        /// </summary>
        private static void PersistGlobalLockState()
        {
            BasisGlobalLockManager.WriteToConfig(NetworkServer.Configuration);
            SaveConfig();
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

            // Seeded from Configuration.DisallowHeadless at boot — persist or it reverts on restart.
            NetworkServer.Configuration.DisallowHeadless = BasisHeadlessConnectionPolicyManager.HeadlessDisallowed;
            SaveConfig();
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

        private static void HandleCameraPolicySet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set camera metadata policy: missing mask byte.");
                return;
            }

            byte mask = reader.GetByte();
            BasisGlobalLockManager.SetCameraMetadataDisallowMask(mask);
            BNL.Log($"Camera photo-metadata disallow mask set to {mask}.");
            SendBackMessage(peer, $"Camera metadata policy updated (mask {mask}).");
            PersistGlobalLockState();
            BasisGlobalLockManager.BroadcastLockState();
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
                BasisUserOpusBitrateStateManager.SendOverrideToPeer(targetPeer, BasisUserOpusBitrateStateManager.EffectiveBitrateFor(targetId));
            }

            string notification = applied == 0
                ? $"Cleared Opus bitrate override for player {targetId}."
                : $"Opus bitrate override for player {targetId} is now {applied} bps.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
        }

        private static void HandleGlobalOpusBitrateSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 4)
            {
                SendBackMessage(peer, "Failed to set global Opus bitrate: missing payload.");
                return;
            }

            int requested = reader.GetInt();
            int applied = BasisUserOpusBitrateStateManager.SetGlobalBitrate(requested);

            string notification = applied == 0
                ? "Cleared the global Opus bitrate; clients use their default (or their per-user override)."
                : $"Global Opus bitrate is now {applied} bps (per-user overrides still win).";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisUserOpusBitrateStateManager.PushEffectiveToAllPeers();
            BasisUserOpusBitrateStateManager.BroadcastGlobalState();
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
