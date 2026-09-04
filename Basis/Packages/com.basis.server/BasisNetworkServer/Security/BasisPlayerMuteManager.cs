using Basis.Network.Core;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Per-player moderation mutes: voice and text chat as independent flags on one UUID-keyed
    /// record, persisted to muted_players.xml the way bans are so a rejoin or server restart
    /// keeps the mute. Enforcement lives in the shared gates the global locks already use
    /// (IsChatBlockedFor / IsVoiceBlockedFor), so chat, typing, normal voice and announce are all
    /// covered; MuteStateApply keeps the target's own client honest about what the server drops.
    /// </summary>
    public static class BasisPlayerMuteManager
    {
        private static readonly ConcurrentDictionary<string, MutedPlayer> MutedPlayers = new();
        private static readonly string MuteFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "muted_players.xml");

        public static bool UseFileOnDisc = true;

        public class MutedPlayer
        {
            public string UUID { get; set; }
            public bool VoiceMuted { get; set; }
            public bool TextMuted { get; set; }
            public string TimeOfMute { get; set; }
        }

        public static bool IsVoiceMuted(string uuid) =>
            uuid != null && MutedPlayers.TryGetValue(uuid, out MutedPlayer p) && p.VoiceMuted;

        public static bool IsTextMuted(string uuid) =>
            uuid != null && MutedPlayers.TryGetValue(uuid, out MutedPlayer p) && p.TextMuted;

        // Voice calls this per packet (~50x/sec per speaker) — the IsEmpty fast path keeps the
        // nobody-muted case at a single field read before any UUID resolution happens.
        public static bool IsVoiceMutedFor(NetPeer peer)
        {
            if (MutedPlayers.IsEmpty) return false;
            return NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && IsVoiceMuted(uuid);
        }

        public static bool IsTextMutedFor(NetPeer peer)
        {
            if (MutedPlayers.IsEmpty) return false;
            return NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && IsTextMuted(uuid);
        }

        /// <summary>
        /// Applies one mute flag for a UUID and returns the reply for the requesting moderator.
        /// Works on offline players too — the record is UUID-keyed and persisted — and pushes the
        /// new state to the target's client when they are connected.
        /// </summary>
        public static string Apply(string uuid, bool voice, bool muted)
        {
            if (string.IsNullOrWhiteSpace(uuid))
                return "UUID invalid";

            if (PermissionIntegration.Manager.Has(uuid, PermNodes.protection))
                return "Target is protected";

            MutedPlayer player = MutedPlayers.GetOrAdd(uuid, _ => new MutedPlayer
            {
                UUID = uuid,
                TimeOfMute = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            });

            if (voice) player.VoiceMuted = muted;
            else player.TextMuted = muted;

            if (!player.VoiceMuted && !player.TextMuted)
            {
                MutedPlayers.TryRemove(uuid, out _);
            }
            SaveMutedPlayers();

            NetPeer targetPeer = null;
            bool online = NetworkServer.AuthIdentity.UUIDToNetID(uuid, out int id) &&
                NetworkServer.AuthenticatedPeers.TryGetValue(id, out targetPeer);
            if (online)
            {
                SendStateToPeer(targetPeer);
            }

            string kind = voice ? "Voice" : "Text chat";
            string verb = muted ? "muted" : "unmuted";
            return online
                ? $"{kind} {verb} for {uuid}."
                : $"{kind} {verb} for {uuid} (offline; applies when they rejoin).";
        }

        /// <summary>Pushes the peer's full mute state (both flags) over the admin channel.</summary>
        public static void SendStateToPeer(NetPeer peer)
        {
            if (peer == null) return;
            bool voiceMuted = false;
            bool textMuted = false;
            if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) &&
                MutedPlayers.TryGetValue(uuid, out MutedPlayer p))
            {
                voiceMuted = p.VoiceMuted;
                textMuted = p.TextMuted;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MuteStateApply);
            writer.Put(voiceMuted);
            writer.Put(textMuted);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Join-time variant: the client's default is unmuted, so only muted players cost a message.
        /// </summary>
        public static void SendStateToPeerIfMuted(NetPeer peer)
        {
            if (MutedPlayers.IsEmpty || peer == null) return;
            if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && MutedPlayers.ContainsKey(uuid))
            {
                SendStateToPeer(peer);
            }
        }

        public static void SaveMutedPlayers()
        {
            if (!UseFileOnDisc) return;

            try
            {
                using FileStream fs = new(MuteFilePath, FileMode.Create);
                new XmlSerializer(typeof(List<MutedPlayer>)).Serialize(fs, MutedPlayers.Values.ToList());
            }
            catch (Exception ex)
            {
                BNL.LogError($"Save muted failed: {ex.Message}");
            }
        }

        public static void LoadMutedPlayers()
        {
            if (!File.Exists(MuteFilePath))
            {
                SaveMutedPlayers();
                return;
            }

            try
            {
                using FileStream fs = new(MuteFilePath, FileMode.Open);
                var list = (List<MutedPlayer>)new XmlSerializer(typeof(List<MutedPlayer>)).Deserialize(fs);

                MutedPlayers.Clear();
                foreach (var p in list)
                {
                    if (!string.IsNullOrEmpty(p.UUID) && (p.VoiceMuted || p.TextMuted))
                    {
                        MutedPlayers[p.UUID] = p;
                    }
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"Load muted failed: {ex.Message}");
            }
        }
    }
}
