using Basis.Network.Core;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct AdminRequest
        {
            private byte messageIndex;
            public AdminRequestMode GetAdminRequestMode()
            {
                return (AdminRequestMode)messageIndex;
            }
            public void Deserialize(NetDataReader reader)
            {
                int bytesAvailable = reader.AvailableBytes;
                if (bytesAvailable > 0)
                {
                    messageIndex = reader.GetByte();
                }
                else
                {
                    BNL.LogError($"Unable to read remaining bytes, available: {bytesAvailable}");
                }
            }

            public void Serialize(NetDataWriter writer, AdminRequestMode AdminRequestMode)
            {
                messageIndex = (byte)AdminRequestMode;
                writer.Put(messageIndex);
            }
        }
        public enum AdminRequestMode : byte
        {
            Ban,//bans a player
            Kick,//kicks a player
            IpAndBan,// bans and ip bans a player
            Message,// sends a message to a user
            MessageAll,// sends a message to all users
            UnBanIP,// unbans a user and unbans a associated ip
            UnBan,// unbans a user
          //  RequestBannedPlayers,// gets a list of banned players
           // TeleportTo,// teleport to a player
            TeleportAll,// teleports everyone
            TeleportPlayer,

            // Permission management (any user can request, only admins can modify)
            GetPermissions,     // request full permission snapshot (read-only for non-admins)
            SetUserGroup,       // admin: add/remove user from a group
            SetUserNode,        // admin: add/remove permission node from a user
            SetGroupNode,       // admin: add/remove permission node from a group
            CreateGroup,        // admin: create a new permission group
            DeleteGroup,        // admin: delete a permission group
            SetGroupParent,     // admin: add/remove a parent group from a group

            EnableShoutMode,    // admin: enable shout mode for a player (non-spatialized broadcast voice)
            DisableShoutMode,   // admin: disable shout mode for a player

            GlobalToggleAvatars, // admin: toggle global avatar loading lock
            GlobalToggleProps,   // admin: toggle global prop loading lock
            GlobalToggleWorlds,  // admin: toggle global world loading lock
            GlobalGetLockState,  // server→client: current global lock state
            GlobalGetHeadlessAudioState, // server→client: current global headless audio state
            SetGlobalHeadlessAudio, // admin: explicitly set headless audio clip playback state for headless clients
            GlobalGetHeadlessDisallowState, // server→client: current global headless disallow state
            SetGlobalHeadlessDisallow, // admin: explicitly allow/disallow headless client connections
            SetGlobalOpusPacketLoss, // admin: set Opus FEC packet-loss percent (0..100) applied to every client's encoder
            GlobalGetOpusPacketLossState, // server→client: current Opus FEC packet-loss percent

            SetUserOpusBitrate,           // admin: override a single user's Opus encoder bitrate (bps); 0 = clear override
            UserOpusBitrateOverride,      // server→target user: their current bitrate override (0 = none)
            SetGlobalOpusFrameDuration,   // admin: set the Opus frame duration in milliseconds (20 or 40)
            GlobalGetOpusFrameDurationState, // server→client: current Opus frame duration in milliseconds

            // ── Server config / allowlist (persisted to disk) ─────────────────
            SetServerName,    // admin: set Configuration.ServerName + persist to config.xml. Payload: [string name]
            SetServerMotd,    // admin: set Configuration.ServerMotd + persist to config.xml. Payload: [string motd]
            SetAllowlistMode, // admin: set Configuration.BasisUserRestrictionMode + persist. Payload: [byte BasisUserRestrictionMode]
            AddAllowlist,     // admin: add UUID to BasisAllowList.txt. Payload: [string uuid]
            RemoveAllowlist,  // admin: remove UUID from BasisAllowList.txt. Payload: [string uuid]

            GlobalToggleServers, // admin: toggle global server-share lock (BasisGlobalLockManager.ServersLocked).

            GlobalToggleThirdPerson, // admin: toggle the global third-person camera disable (BasisGlobalLockManager.ThirdPersonDisabled). State is appended as the 5th bool in GlobalGetLockState.

            // ── Default library (server-pushed library items, persisted to disk) ──
            // Payload: [byte mode (0=Avatar,1=World,2=Prop)][string url][string password]
            // Gated by PermNodes.ConfigurationEditor. Writes a new XML file under the
            // server's defaultlibrary/ folder and rebroadcasts the updated list.
            AddDefaultLibraryItem,

            // Payload: [string url]
            // Removes every defaultlibrary/ XML whose URL matches and rebroadcasts.
            RemoveDefaultLibraryItem,

            // admin: toggle the global strip of AdditionalAvatarDatas (blendshapes,
            // custom-behaviour params) on inbound avatar sync messages. Muscle/position/
            // rotation still propagate normally. State is appended as the 6th bool in
            // GlobalGetLockState.
            GlobalToggleAdditionalAvatarDataLock,

            // admin: set the per-category camera photo-metadata disallow mask (1 byte).
            // Each set bit disallows one embedding category for all clients; 0 = all allowed.
            // The current mask is appended as a trailing byte in GlobalGetLockState.
            SetGlobalCameraPolicy,

            GlobalGetCrashReportState, // server→client: whether client error/exception reporting is enabled
            SetGlobalCrashReporting,   // admin: enable/disable client error/exception reporting (persisted). Payload: [bool]

            GlobalGetAudioRangeLimits, // server→client: current max microphone + hearing range in metres. Payload: [float micMeters][float hearingMeters]
            SetGlobalAudioRangeLimits, // admin: set max microphone + hearing range in metres (persisted). Payload: [float micMeters][float hearingMeters]

            // ── Server log bundle (admin pulls logs/ + CrashReports/ as one compressed bundle) ──
            // The admin asks; the server packs its logs/ and CrashReports/ folders into one
            // container, LZ4-compresses it, and streams the result back in order over the
            // admin channel, split into chunks so a large bundle never relies on one datagram.
            RequestAllLogs,   // client→server (admin): build and stream the full log bundle. Gated by basis.admin.logs. No payload.
            LogBundleBegin,   // server→client: start of a transfer. Payload: [string serverNameSafe][string fileName][bool isCompressed][int payloadBytes][int rawBytes][int totalChunks]
            LogBundleChunk,   // server→client: one ordered chunk. Payload: [int chunkIndex][lenPrefixed bytes]
            LogBundleEnd,     // server→client: end of transfer. Payload: [bool ok][string message]

            // server→client: clear all locally loaded scenes regardless of netId.
            // No payload. Handles orphaned scenes the server doesn't know about.
            ClearAllScenes,

            DeleteAllLogs,    // client→server (admin): delete every file under logs/ + CrashReports/. Gated by basis.admin.logs. No payload. Server replies with a status Message.

            // ── Instance restriction policies (persisted; gated by basis.moderation.globallock) ──
            GlobalTogglePlayspaceMover, // admin: toggle the global playspace-mover lockout. State appended to GlobalGetLockState. Non-admins cannot grab/drag their play space while set.
            GlobalToggleDirectConnect,  // admin: toggle the global direct-connect (P2P) lockout. State appended to GlobalGetLockState. The server also refuses to broker P2P requests from non-admins while set.

            GlobalGetAvatarScaleLimits, // server→client: min/max avatar eye height in metres. Payload: [float minMeters][float maxMeters]
            SetGlobalAvatarScaleLimits, // admin: set min/max avatar eye height in metres (persisted). Non-admins are clamped to this range; admins bypass it. Payload: [float minMeters][float maxMeters]

            GlobalGetResourceLimits, // server→client: persisted DoS caps. Payload: [int maxContentSpheresPerPlayer]
            SetGlobalResourceLimits, // admin: set the persisted DoS caps. Payload: [int maxContentSpheresPerPlayer]

            // admin: toggle the global Cilbox lock. While set, every client blocks sandboxed Cilbox
            // code on avatars from running (props/worlds keep their own). State is appended in
            // GlobalGetLockState.
            GlobalToggleCilbox,

            // admin: toggle the global shared-image lock. While set, non-bypass clients can't share
            // new image pickups and won't accept inbound ones. Enforced client-side — image pickups
            // ride the generic scene relay, so the server can't single them out the way it blocks
            // content shares. State is appended as the trailing bool in GlobalGetLockState.
            GlobalToggleImages,

            // admin: enable/disable full-quality broadcast for a player. Payload: [ushort targetId][bool enable].
            // Session-only. While set the server bypasses the distance reduction system for that player.
            SetFullQualityBroadcast,

            // admin: set the server avatar-reduction (BSR) tuning. Persisted to config.xml and re-applied live.
            // Payload: [int defaultIntervalMs][int baseMultiplier][float increaseRate][float slowestSendRate]
            //          [float highDist][float medDist][float lowDist][bool bundleCompression]
            //          [int bundleMinMessages][int bundleMinBytes][bool profiling]
            SetGlobalReductionSettings,
            GlobalGetReductionSettings, // server→client: current BSR reduction settings (same field order as SetGlobalReductionSettings)

            SetGlobalOpusBitrate,      // admin: set the Opus encoder bitrate (bps) every client transmits with; 0 = clear back to client default. Payload: [int bps]
            GlobalGetOpusBitrateState, // server→client: current global Opus bitrate (bps, 0 = none). Payload: [int bps]

            // admin: toggle remote end-effector IK anchoring globally. State (disabled) is appended as the
            // trailing bool in GlobalGetLockState. Default false = feature on; admins flip on to disable.
            GlobalToggleEndEffectorIK,

            // admin: toggle the global text-chat lock. While set the server drops chat messages and
            // typing state from peers without basis.chat.lockbypass, so a modified client can't talk
            // past it. State is appended as the trailing bool in GlobalGetLockState.
            GlobalToggleTextChat,

            // admin: toggle the global voice lock. While set the server drops normal and shout voice
            // from peers without basis.voice.lockbypass, so a modified client can't talk past it.
            // State is appended as a trailing bool in GlobalGetLockState.
            GlobalToggleVoiceChat,

            // admin: toggle the global media-player lock. While set, non-bypass clients neither load
            // new media URLs nor accept inbound ones. Enforced client-side — media player state rides
            // the generic scene relay. State is appended as a trailing bool in GlobalGetLockState.
            GlobalToggleMediaPlayer,

            // admin: toggle the global camera-capture lock. While set, non-bypass clients can't take
            // photos. Enforced client-side — capture is entirely local. Separate from
            // SetGlobalCameraPolicy, which only strips metadata from photos still being taken.
            GlobalToggleCameraCapture,

            // admin: toggle the global prop-grabbing lock. While set, non-bypass clients can't pick up
            // props. Enforced client-side — grabbing is local interaction logic. Separate from
            // GlobalToggleProps, which blocks prop loading instead.
            GlobalTogglePropGrabbing,

            // admin: toggle forced safe display names. While set, clients strip rich-text markup
            // from other players' display names and disable TMP rich text on the nameplate.
            // Enforced client-side — nameplate rendering is entirely local.
            GlobalToggleSafeDisplayNames,
        }
    }
}
