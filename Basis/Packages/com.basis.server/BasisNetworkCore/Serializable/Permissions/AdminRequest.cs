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

            EnableAnnounceMode,    // admin: enable announce mode for a player (non-spatialized broadcast voice)
            DisableAnnounceMode,   // admin: disable announce mode for a player

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

            // admin: toggle the global voice lock. While set the server drops normal and announce voice
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

            // moderator: put one player onto a specific avatar. The server authorizes and relays
            // the payload to that single peer, which loads it through the same path the library
            // uses — so the target's own avatar-change broadcast is what propagates it to everyone
            // else and updates the server's stored record for late joiners.
            // Payload: [ushort targetId][string url][string password][byte embeddedSource]
            // embeddedSource: 0 = plain bee url, 1 = embedded bee url, 2 = embedded addressable.
            ForceAvatar,

            // server→client: load this avatar now. Same payload as ForceAvatar with the target id
            // replaced by the initiating moderator's id.
            ForceAvatarApply,

            // moderator: put EVERY player onto a specific avatar. Same payload as ForceAvatar minus
            // the target id. The server fans ForceAvatarApply out to every peer except the sender,
            // skipping anyone holding basis.protection, so the client needs no new receive path.
            // Payload: [string url][string password][byte embeddedSource]
            ForceAvatarAll,

            // moderator: override one player's jump height, walk/run speed, gravity and character
            // controller mode. The server authorizes and relays to that single peer, which applies it
            // under a reserved key world content cannot clear or outrank. Session-only, not persisted.
            // Payload: [ushort targetId][byte fields][float jumpHeight][float walkSpeed][float runSpeed]
            //          [float gravity][byte mode]
            // fields is a bitmask: 1 jumpHeight, 2 walkSpeed, 4 runSpeed, 8 gravity, 16 mode.
            // fields == 0 clears the override. mode: 0 = Walk, 1 = Fly, 2 = NoClip.
            SetLocomotionOverride,

            // server→client: apply this locomotion override now. Same payload as SetLocomotionOverride
            // with the target id replaced by the initiating moderator's id.
            LocomotionOverrideApply,

            // moderator: override EVERY player's locomotion. Same payload as SetLocomotionOverride minus
            // the target id. The server fans LocomotionOverrideApply out to every peer except the sender,
            // skipping anyone holding basis.protection, so the client needs no new receive path.
            // Payload: [byte fields][float jumpHeight][float walkSpeed][float runSpeed][float gravity][byte mode]
            SetLocomotionOverrideAll,

            // admin: set the image/gif bandwidth budgets. Persisted to config.xml and applied live.
            // Upload is what one sharer may spend of the server's egress, and is BOTH advertised to
            // clients (so they pace themselves) and enforced server-side (so a modified one cannot
            // ignore it). Download is the rate the server replays cached images to one arriving
            // player, which has no client half at all — the joiner never asked for it.
            // Payload: [int uploadMbps][int downloadMbps][int enforcementPercent]
            SetGlobalImageBandwidth,
            GlobalGetImageBandwidth, // server→client: current image bandwidth budgets (same field order)

            // admin: set the maximum number of simultaneously connected players. Persisted to
            // config.xml and read live by the connection gate, so a new cap is in force from the
            // very next join. Lowering it below the current population disconnects nobody — the
            // instance simply stops admitting players until it drains under the cap.
            // Payload: [int peerLimit], clamped server-side to 1..ushort.MaxValue.
            SetGlobalPeerLimit,
            GlobalGetPeerLimit, // server→client: current maximum player count. Payload: [int peerLimit]

            // moderator: set one player's voice-mute state. While muted the server drops their
            // normal and announce voice at the source. Keyed by UUID and persisted
            // (muted_players.xml), so a rejoin stays muted until a moderator unmutes.
            // Payload: [string uuid][bool muted]
            SetVoiceMute,

            // moderator: set one player's text-chat mute state. While muted the server drops
            // their chat messages and typing state. Same persistence as SetVoiceMute; the two
            // mutes are independent flags on one record.
            // Payload: [string uuid][bool muted]
            SetTextMute,

            // server→target client: your current moderation mute state, sent on change and on
            // join while muted. Enforcement is server-side; this exists so the composer can grey
            // out and the mic can stop uploading a stream the server discards.
            // Payload: [bool voiceMuted][bool textMuted]
            MuteStateApply,

            // Ask whether ONE player currently in this instance holds one permission node, or
            // belongs to one permission group. Deliberately not gated: GetPermissions hands over
            // the whole table and needs basis.permissions.view, while this answers a single
            // yes/no about someone already visible in the room, which is what a staff nameplate
            // or a moderator-only door needs. Bounded to connected players so it cannot be used
            // to walk the store, and rate limited per peer so it cannot be used to enumerate one
            // at speed. Requests that name an absent player answer false with TargetFound clear.
            // Payload: [ushort targetPlayerId][byte AdminPermissionQueryKind][string value]
            QueryPermission,

            // server→client: the answer to a QueryPermission. Echoes the request so a caller can
            // match a reply to what it asked without the protocol carrying a request id.
            // Payload: [ushort targetPlayerId][byte AdminPermissionQueryKind][string value]
            //          [bool held][bool targetFound]
            QueryPermissionResult,

            // admin: put a player into shout mode - twice their microphone range and a level
            // boost, still fully spatialized. Unlike announce this carries no separate voice
            // channel: the target client enters the mode and its ordinary talk-mode broadcast
            // is what widens every listener. Appended at the end of the enum so no existing
            // value renumbers.
            // Payload: [ushort targetPlayerId]
            EnableShoutMode,
            DisableShoutMode,   // admin: take a player back out of shout mode
        }

        /// <summary>
        /// What a <see cref="AdminRequestMode.QueryPermission"/> is asking about.
        /// </summary>
        public enum AdminPermissionQueryKind : byte
        {
            /// <summary>A permission node, e.g. basis.moderation.kick. Wildcards resolve normally.</summary>
            Node,

            /// <summary>A permission group ("role"), matched through parent-group inheritance.</summary>
            Group,
        }
    }
}
