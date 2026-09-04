using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.UI.UI_Panels;
using BasisNetworkCore.Security;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using static BasisNetworkCore.Serializable.SerializableBasis;

public static class BasisNetworkModeration
{
    private static bool ValidateString(string param, string paramName)
    {
        if (string.IsNullOrEmpty(param))
        {
            BasisDebug.LogError($"{paramName} cannot be null or empty");
            return false;
        }
        return true;
    }
    public static bool ValidateForAnimator(BasisNetworkPlayer Player)
    {
        if (Player == null)
        {
            return false;
        }
        if (Player.Player == null)
        {
            return false;
        }
        if (Player.Player.BasisAvatar == null)
        {
            return false;
        }
        if (Player.Player.BasisAvatar.Animator == null)
        {
            return false;
        }
        return true;
    }

    private static void SendAdminRequest(AdminRequestMode mode, params Action<NetDataWriter>[] dataWriters)
    {
        if (BasisNetworkConnection.LocalPlayerPeer == null)
        {
            BasisDebug.LogWarning("Cannot send admin request: not connected to a server.");
            return;
        }

        var writer = new NetDataWriter();
        new AdminRequest().Serialize(writer, mode);

        foreach (var write in dataWriters)
            write(writer);

        BasisNetworkConnection.LocalPlayerPeer.Send(
            writer,
            BasisNetworkCommons.AdminChannel,
            Basis.Network.Core.DeliveryMethod.ReliableSequenced
        );
    }

    public static void SendBan(string uuid, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) reason = "An admin banned you";
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.Ban,
                w => w.Put(uuid),
                w => w.Put(reason));
        }
    }

    public static void SendIPBan(string uuid, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) reason = "An admin banned you";
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.IpAndBan,
                w => w.Put(uuid),
                w => w.Put(reason));
        }
    }

    public static void SendKick(string uuid, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) reason = "An admin kicked you";
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.Kick,
                w => w.Put(uuid),
                w => w.Put(reason));
        }
    }

    public static void UnBan(string uuid)
    {
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.UnBan, w => w.Put(uuid));
        }
    }

    public static void UnIpBan(string uuid)
    {
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.UnBanIP, w => w.Put(uuid));
        }
    }

    public static void SendMessage(ushort uuid, string message)
    {
        if (ValidateString(message, nameof(message)))
        {
            SendAdminRequest(AdminRequestMode.Message,
                w => w.Put(uuid),
                w => w.Put(message));
        }
    }

    public static void SendMessageAll(string message)
    {
        if (ValidateString(message, nameof(message)))
        {
            SendAdminRequest(AdminRequestMode.MessageAll,
                w => w.Put(message));
        }
    }

    public static void TeleportAll(ushort? destinationPlayerId)
    {
        if (destinationPlayerId.HasValue)
        {
            SendAdminRequest(AdminRequestMode.TeleportAll,
                w => w.Put(destinationPlayerId.Value));
        }
    }

    public static void TeleportHere(ushort uuid)
    {
        SendAdminRequest(AdminRequestMode.TeleportPlayer,
            w => w.Put(uuid));
    }

    /// <summary>
    /// Ask the server to put <paramref name="targetId"/> onto a specific avatar. Only the url,
    /// password and embedded kind travel — the target loads the bundle itself through the same
    /// path its library uses, so its own avatar-change broadcast is what shows the new avatar to
    /// everyone else. See <see cref="EncodeEmbeddedSource"/> for the trailing byte.
    /// </summary>
    public static void ForceAvatar(ushort targetId, string url, string password, byte embeddedSource)
    {
        if (ValidateString(url, nameof(url)))
        {
            SendAdminRequest(AdminRequestMode.ForceAvatar,
                w => w.Put(targetId),
                w => w.Put(url),
                w => w.Put(password ?? string.Empty),
                w => w.Put(embeddedSource));
        }
    }

    /// <summary>
    /// Ask the server to override <paramref name="targetId"/>'s jump height, movement speeds, gravity
    /// and character controller mode. Only the fields flagged in <see cref="BasisLocomotionValues.Fields"/>
    /// travel; the target applies them under a reserved key that world content can neither clear nor
    /// outrank. Session-only — nothing is persisted, and a reconnect starts clean.
    /// </summary>
    public static void SetLocomotionOverride(ushort targetId, BasisLocomotionValues values)
    {
        SendAdminRequest(AdminRequestMode.SetLocomotionOverride,
            w => w.Put(targetId),
            w => w.Put((byte)values.Fields),
            w => w.Put(values.JumpHeight),
            w => w.Put(values.WalkSpeed),
            w => w.Put(values.RunSpeed),
            w => w.Put(values.Gravity),
            w => w.Put((byte)values.Mode));
    }

    /// <summary>
    /// Drop the moderator override on <paramref name="targetId"/>, returning them to whatever the world
    /// and their own settings ask for.
    /// </summary>
    public static void ClearLocomotionOverride(ushort targetId)
    {
        SetLocomotionOverride(targetId, default);
    }

    /// <summary>
    /// <see cref="SetLocomotionOverride(ushort, BasisLocomotionValues)"/> aimed at the whole instance. The
    /// server sends it to everyone but the caller and skips players holding basis.protection.
    /// </summary>
    public static void SetLocomotionOverrideAll(BasisLocomotionValues values)
    {
        SendAdminRequest(AdminRequestMode.SetLocomotionOverrideAll,
            w => w.Put((byte)values.Fields),
            w => w.Put(values.JumpHeight),
            w => w.Put(values.WalkSpeed),
            w => w.Put(values.RunSpeed),
            w => w.Put(values.Gravity),
            w => w.Put((byte)values.Mode));
    }

    /// <summary>Drop the moderator locomotion override on every player in the instance.</summary>
    public static void ClearLocomotionOverrideAll()
    {
        SetLocomotionOverrideAll(default);
    }

    /// <summary>
    /// Convenience overload taking the library entry a moderator picked.
    /// </summary>
    public static void ForceAvatar(ushort targetId, BasisDataStoreItemKeys.ItemKey item)
    {
        if (item == null)
        {
            BasisDebug.LogError("ForceAvatar was given no item.");
            return;
        }
        ForceAvatar(targetId, item.Url, item.Pass, EncodeEmbeddedSource(item));
    }

    /// <summary>
    /// <see cref="ForceAvatar(ushort, string, string, byte)"/> aimed at the whole instance. The server
    /// sends it to everyone but the caller and skips players holding basis.protection.
    /// </summary>
    public static void ForceAvatarAll(string url, string password, byte embeddedSource)
    {
        if (ValidateString(url, nameof(url)))
        {
            SendAdminRequest(AdminRequestMode.ForceAvatarAll,
                w => w.Put(url),
                w => w.Put(password ?? string.Empty),
                w => w.Put(embeddedSource));
        }
    }

    /// <summary>
    /// Convenience overload taking the library entry a moderator picked.
    /// </summary>
    public static void ForceAvatarAll(BasisDataStoreItemKeys.ItemKey item)
    {
        if (item == null)
        {
            BasisDebug.LogError("ForceAvatarAll was given no item.");
            return;
        }
        ForceAvatarAll(item.Url, item.Pass, EncodeEmbeddedSource(item));
    }

    // ── Server config / allowlist (admin) ────────────────────────────────────
    // Each of these triggers a server-side write to config/config.xml or
    // BasisAllowList.txt so the change is durable across restarts.

    public static void SetServerName(string name)
    {
        SendAdminRequest(AdminRequestMode.SetServerName, w => w.Put(name ?? string.Empty));
    }

    public static void SetServerMotd(string motd)
    {
        SendAdminRequest(AdminRequestMode.SetServerMotd, w => w.Put(motd ?? string.Empty));
    }

    public static void SetAllowlistMode(BasisUserRestrictionMode mode)
    {
        SendAdminRequest(AdminRequestMode.SetAllowlistMode, w => w.Put((byte)mode));
    }

    public static void AddAllowlist(string uuid)
    {
        if (!ValidateString(uuid, nameof(uuid))) return;
        SendAdminRequest(AdminRequestMode.AddAllowlist, w => w.Put(uuid));
    }

    public static void RemoveAllowlist(string uuid)
    {
        if (!ValidateString(uuid, nameof(uuid))) return;
        SendAdminRequest(AdminRequestMode.RemoveAllowlist, w => w.Put(uuid));
    }

    /// <summary>
    /// Ask the server to persist a new default-library entry to disk and broadcast
    /// it to every connected client. Mode follows BundledContentHolder.Mode:
    /// 0=Avatar, 1=World, 2=Prop. Server-gated by PermNodes.ConfigurationEditor.
    /// </summary>
    public static void AddDefaultLibraryItem(byte mode, string url, string password)
    {
        if (!ValidateString(url, nameof(url))) return;
        SendAdminRequest(AdminRequestMode.AddDefaultLibraryItem,
            w => w.Put(mode),
            w => w.Put(url),
            w => w.Put(password ?? string.Empty));
    }

    /// <summary>
    /// Ask the server to drop every default-library entry whose URL matches and
    /// rebroadcast the updated list. Server-gated by PermNodes.ConfigurationEditor.
    /// </summary>
    public static void RemoveDefaultLibraryItem(string url)
    {
        if (!ValidateString(url, nameof(url))) return;
        SendAdminRequest(AdminRequestMode.RemoveDefaultLibraryItem,
            w => w.Put(url));
    }

    /// <summary>
    /// Admin: ask the server to bundle its logs/ and CrashReports/ folders and stream them
    /// back. <see cref="BasisLogBundleReceiver"/> reassembles, decompresses, and extracts them
    /// into a dated folder next to the local settings. Server-gated by basis.admin.logs.
    /// </summary>
    public static void RequestAllLogs()
    {
        BasisLogBundleReceiver.ArmForLocalRequest();
        SendAdminRequest(AdminRequestMode.RequestAllLogs);
    }

    /// <summary>
    /// Admin: ask the server to permanently delete every file under its logs/ and
    /// CrashReports/ folders — the same set <see cref="RequestAllLogs"/> pulls. The server
    /// replies with a status message. Server-gated by basis.admin.logs.
    /// </summary>
    public static void DeleteAllLogs()
    {
        SendAdminRequest(AdminRequestMode.DeleteAllLogs);
    }

    public static void DisplayMessage(string message)
    {
        if (ValidateString(message, nameof(message)))
        {
            // Remember whether the main menu was already open so we can return to the exact
            // prior state when the user dismisses the popup, instead of dropping them back
            // on a bare main menu (or a hotbar they weren't looking at).
            bool menuWasAlreadyOpen = BasisMainMenu.Instance != null;

            if (!menuWasAlreadyOpen)
            {
                BasisMainMenu.Open();
            }
            else if (BasisMainMenu.Instance.Dialogue)
            {
                // OpenDialogue refuses to stack; release the existing one first.
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }

            BasisMainMenu.Instance.OpenDialogue(BasisLocalization.Get("settings.admin.title"), message, BasisLocalization.Get("ui.ok"), value =>
            {
                // If we opened the menu solely to show this popup, close it again on dismiss.
                if (!menuWasAlreadyOpen)
                {
                    BasisMainMenu.Close();
                }
            }, category: BasisNotificationCategory.Player);
            BasisDebug.Log(message);
        }
    }

    /// <summary>
    /// Like <see cref="DisplayMessage"/> but adds an "open folder" button that reveals
    /// <paramref name="folderPath"/> in the OS file browser. As with <see cref="DisplayMessage"/>,
    /// the popup itself is not an error — the caller logs at whatever level fits.
    /// </summary>
    public static void DisplayMessageWithFolder(string message, string folderPath)
    {
        if (!ValidateString(message, nameof(message))) return;

        bool menuWasAlreadyOpen = BasisMainMenu.Instance != null;
        if (!menuWasAlreadyOpen)
        {
            BasisMainMenu.Open();
        }
        else if (BasisMainMenu.Instance.Dialogue)
        {
            BasisMainMenu.Instance.Dialogue.ReleaseInstance();
        }

        BasisMainMenu.Instance.OpenDialogue(BasisLocalization.Get("settings.admin.title"), message, BasisLocalization.Get("ui.openFolder"), BasisLocalization.Get("ui.ok"), accepted =>
        {
            if (accepted) BasisFileBrowserUtility.Reveal(folderPath);
            // If we opened the menu solely to show this popup, close it again on dismiss.
            if (!menuWasAlreadyOpen)
            {
                BasisMainMenu.Close();
            }
        }, category: BasisNotificationCategory.Player);
    }

    public static void AdminMessage(NetDataReader reader)
    {
        var request = new AdminRequest();
        request.Deserialize(reader);
        AdminRequestMode mode = request.GetAdminRequestMode();

        switch (mode)
        {
            case AdminRequestMode.Message:
            case AdminRequestMode.MessageAll:
                DisplayMessage(reader.GetString());
                break;

            case AdminRequestMode.TeleportPlayer:
            case AdminRequestMode.TeleportAll:
                ushort playerId = reader.GetUShort();
                TryTeleportToPlayer(playerId);
                break;

            case AdminRequestMode.GetPermissions:
                HandlePermissionsResponse(reader);
                break;

            case AdminRequestMode.QueryPermissionResult:
                HandlePermissionQueryResult(reader);
                break;

            case AdminRequestMode.EnableShoutMode:
            case AdminRequestMode.DisableShoutMode:
                HandleShoutModeChanged(reader, mode == AdminRequestMode.EnableShoutMode);
                break;

            case AdminRequestMode.EnableAnnounceMode:
            case AdminRequestMode.DisableAnnounceMode:
                HandleAnnounceModeChanged(reader, mode == AdminRequestMode.EnableAnnounceMode);
                break;

            case AdminRequestMode.GlobalGetLockState:
                HandleGlobalLockState(reader);
                break;

            case AdminRequestMode.GlobalGetHeadlessAudioState:
                HandleGlobalHeadlessAudioState(reader);
                break;

            case AdminRequestMode.GlobalGetCrashReportState:
                HandleCrashReportState(reader);
                break;

            case AdminRequestMode.GlobalGetHeadlessDisallowState:
                HandleGlobalHeadlessDisallowState(reader);
                break;

            case AdminRequestMode.GlobalGetOpusPacketLossState:
                HandleGlobalOpusPacketLossState(reader);
                break;

            case AdminRequestMode.UserOpusBitrateOverride:
                HandleUserOpusBitrateOverride(reader);
                break;

            case AdminRequestMode.ForceAvatarApply:
                HandleForcedAvatar(reader);
                break;

            case AdminRequestMode.LocomotionOverrideApply:
                HandleLocomotionOverride(reader);
                break;

            case AdminRequestMode.GlobalGetOpusFrameDurationState:
                HandleGlobalOpusFrameDurationState(reader);
                break;

            case AdminRequestMode.GlobalGetOpusBitrateState:
                HandleGlobalOpusBitrateState(reader);
                break;

            case AdminRequestMode.GlobalGetAudioRangeLimits:
                HandleAudioRangeLimits(reader);
                break;

            case AdminRequestMode.GlobalGetAvatarScaleLimits:
                HandleAvatarScaleLimits(reader);
                break;

            case AdminRequestMode.GlobalGetResourceLimits:
                HandleResourceLimits(reader);
                break;

            case AdminRequestMode.GlobalGetImageBandwidth:
                HandleImageBandwidth(reader);
                break;

            case AdminRequestMode.GlobalGetPeerLimit:
                HandlePeerLimit(reader);
                break;

            case AdminRequestMode.GlobalGetReductionSettings:
                HandleReductionSettings(reader);
                break;

            case AdminRequestMode.MuteStateApply:
                HandleMuteStateApply(reader);
                break;

            case AdminRequestMode.LogBundleBegin:
                BasisLogBundleReceiver.Begin(reader);
                break;

            case AdminRequestMode.LogBundleChunk:
                BasisLogBundleReceiver.Chunk(reader);
                break;

            case AdminRequestMode.LogBundleEnd:
                BasisLogBundleReceiver.End(reader);
                break;

            default:
                BasisDebug.LogError($"Unhandled admin command: {mode}", BasisDebug.LogTag.Networking);
                break;
        }
    }

    #region Announce Mode

    /// <summary>
    /// Fired when a player's announce mode state changes.
    /// </summary>
    public static event Action<ushort, bool> OnAnnounceModeChanged;

    /// <summary>
    /// True if the local player is currently in announce mode.
    /// </summary>
    public static bool LocalPlayerInAnnounceMode => Basis.Scripts.Networking.Transmitters.BasisAudioTransmission.IsInAnnounceMode;

    private static void HandleAnnounceModeChanged(NetDataReader reader, bool enabled)
    {
        ushort targetPlayerId = reader.GetUShort();
        ushort initiatorPlayerId = reader.AvailableBytes >= 2 ? reader.GetUShort() : targetPlayerId;
        string state = enabled ? "enabled" : "disabled";
        BasisDebug.Log($"Announce mode {state} for player {targetPlayerId}", BasisDebug.LogTag.Networking);

        // Check if this is the local player
        bool isLocalPlayer = BasisNetworkPlayer.LocalPlayer != null && targetPlayerId == BasisNetworkPlayer.LocalPlayer.playerId;
        if (isLocalPlayer)
        {
            // Set the local transmission channel
            Basis.Scripts.Networking.Transmitters.BasisAudioTransmission.IsInAnnounceMode = enabled;
            BasisDebug.Log($"Local player announce mode {state}", BasisDebug.LogTag.Networking);

            bool forcedByOther = initiatorPlayerId != targetPlayerId;
            if (forcedByOther && !BasisTalkModeManager.LocalCanAnnounce())
            {
                string initiatorName = ResolveDisplayName(initiatorPlayerId);
                DisplayMessage(enabled
                    ? $"{initiatorName} enabled announce mode for you - your voice is now broadcast to everyone."
                    : $"{initiatorName} disabled announce mode for you - your voice is back to normal.");
            }
        }
        else
        {
            // For remote players, manage the global announce audio source
            if (enabled)
            {
                BasisAnnounceAudioDriver.EnableAnnounceMode(targetPlayerId);
            }
            else
            {
                BasisAnnounceAudioDriver.DisableAnnounceMode(targetPlayerId);
            }
        }

        OnAnnounceModeChanged?.Invoke(targetPlayerId, enabled);
    }

    #endregion

    #region Shout Mode

    /// <summary>
    /// Fired when an admin grants or revokes shout mode for a player.
    /// </summary>
    public static event Action<ushort, bool> OnShoutModeChanged;

    private static readonly HashSet<ushort> adminShoutPlayers = new HashSet<ushort>();

    /// <summary>
    /// True if an admin currently has this player in shout mode. This is the GRANT, not the
    /// mode: a player who picked shout from their own menu bar is not in here. The audio
    /// widening keys off <see cref="BasisRemotePlayer.TalkMode"/> either way; this only drives
    /// the admin UI's enable/disable label.
    /// </summary>
    public static bool IsInShoutMode(ushort playerId) => adminShoutPlayers.Contains(playerId);

    /// <summary>True if an admin currently has the local player in shout mode.</summary>
    public static bool LocalPlayerInShoutMode =>
        BasisNetworkPlayer.LocalPlayer != null && adminShoutPlayers.Contains(BasisNetworkPlayer.LocalPlayer.playerId);

    private static void HandleShoutModeChanged(NetDataReader reader, bool enabled)
    {
        ushort targetPlayerId = reader.GetUShort();
        ushort initiatorPlayerId = reader.AvailableBytes >= 2 ? reader.GetUShort() : targetPlayerId;
        string state = enabled ? "enabled" : "disabled";
        BasisDebug.Log($"Shout mode {state} for player {targetPlayerId}", BasisDebug.LogTag.Networking);

        if (enabled) adminShoutPlayers.Add(targetPlayerId);
        else adminShoutPlayers.Remove(targetPlayerId);

        // Only the target acts on this. Unlike announce there is no second audio path to build
        // for a remote shouter: the target enters the mode, its ordinary talk-mode broadcast
        // reaches every client, and each listener's own transmit tick widens from there.
        bool isLocalPlayer = BasisNetworkPlayer.LocalPlayer != null && targetPlayerId == BasisNetworkPlayer.LocalPlayer.playerId;
        if (isLocalPlayer)
        {
            BasisTalkModeManager.OnAdminShoutChanged(enabled);

            bool forcedByOther = initiatorPlayerId != targetPlayerId;
            if (forcedByOther && !BasisTalkModeManager.LocalCanShout())
            {
                string initiatorName = ResolveDisplayName(initiatorPlayerId);
                DisplayMessage(enabled
                    ? $"{initiatorName} put you in shout mode - your voice now carries twice as far."
                    : $"{initiatorName} took you out of shout mode - your voice is back to normal.");
            }
        }

        OnShoutModeChanged?.Invoke(targetPlayerId, enabled);
    }

    /// <summary>
    /// Admin: put a player into shout mode (double range, louder, still spatialized).
    /// </summary>
    public static void EnableShoutMode(ushort playerId)
    {
        SendAdminRequest(AdminRequestMode.EnableShoutMode,
            w => w.Put(playerId));
    }

    /// <summary>
    /// Admin: take a player back out of shout mode.
    /// </summary>
    public static void DisableShoutMode(ushort playerId)
    {
        SendAdminRequest(AdminRequestMode.DisableShoutMode,
            w => w.Put(playerId));
    }

    #endregion

    #region Announce Mode (continued)

    private static string ResolveDisplayName(ushort playerId)
    {
        if (BasisNetworkPlayers.Players.TryGetValue(playerId, out var player) && player != null)
        {
            string name = player.SafeDisplayName;
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "An admin";
    }

    /// <summary>
    /// Admin: Enable announce mode for a player (non-spatialized broadcast voice).
    /// </summary>
    public static void EnableAnnounceMode(ushort playerId)
    {
        SendAdminRequest(AdminRequestMode.EnableAnnounceMode,
            w => w.Put(playerId));
    }

    /// <summary>
    /// Admin: Disable announce mode for a player.
    /// </summary>
    public static void DisableAnnounceMode(ushort playerId)
    {
        SendAdminRequest(AdminRequestMode.DisableAnnounceMode,
            w => w.Put(playerId));
    }

    /// <summary>
    /// Admin: enable/disable full-quality broadcast for a player. While enabled the server
    /// bypasses the distance reduction system and sends their High avatar data to everyone.
    /// </summary>
    public static void SetFullQualityBroadcast(ushort playerId, bool enable)
    {
        SendAdminRequest(AdminRequestMode.SetFullQualityBroadcast,
            w => w.Put(playerId),
            w => w.Put(enable));
    }

    #endregion

    #region Moderation Mute

    /// <summary>
    /// Server-pushed moderation mute on the LOCAL player's voice. The server drops the audio
    /// regardless — this flag folds into <see cref="VoiceBlockedLocally"/> so the mic stops
    /// uploading a stream the server discards.
    /// </summary>
    public static bool LocalPlayerVoiceMutedByModerator { get; private set; }

    /// <summary>
    /// Server-pushed moderation mute on the LOCAL player's text chat. Folds into
    /// <see cref="BasisNetworkHandleChat.LockedByServer"/> so the composer greys out instead of
    /// silently swallowing messages the server drops.
    /// </summary>
    public static bool LocalPlayerTextMutedByModerator { get; private set; }

    /// <summary>Fired when the local player's moderation voice mute changes.</summary>
    public static event Action<bool> OnLocalVoiceMutedByModeratorChanged;

    /// <summary>Fired when the local player's moderation text mute changes.</summary>
    public static event Action<bool> OnLocalTextMutedByModeratorChanged;

    /// <summary>Moderator: mute or unmute a player's voice for the whole server. UUID-keyed and persisted server-side, so it survives a rejoin.</summary>
    public static void SetVoiceMute(string uuid, bool muted)
    {
        if (!ValidateString(uuid, nameof(uuid))) return;
        SendAdminRequest(AdminRequestMode.SetVoiceMute,
            w => w.Put(uuid),
            w => w.Put(muted));
    }

    /// <summary>Moderator: mute or unmute a player's text chat (messages and typing) for the whole server. UUID-keyed and persisted server-side, so it survives a rejoin.</summary>
    public static void SetTextMute(string uuid, bool muted)
    {
        if (!ValidateString(uuid, nameof(uuid))) return;
        SendAdminRequest(AdminRequestMode.SetTextMute,
            w => w.Put(uuid),
            w => w.Put(muted));
    }

    private static void HandleMuteStateApply(NetDataReader reader)
    {
        bool voiceMuted = reader.GetBool();
        bool textMuted = reader.GetBool();

        bool voiceChanged = voiceMuted != LocalPlayerVoiceMutedByModerator;
        bool textChanged = textMuted != LocalPlayerTextMutedByModerator;
        LocalPlayerVoiceMutedByModerator = voiceMuted;
        LocalPlayerTextMutedByModerator = textMuted;

        if (voiceChanged) OnLocalVoiceMutedByModeratorChanged?.Invoke(voiceMuted);
        if (textChanged) OnLocalTextMutedByModeratorChanged?.Invoke(textMuted);

        if (voiceChanged)
        {
            DisplayMessage(voiceMuted
                ? "A moderator muted your voice - other players cannot hear you until you are unmuted."
                : "A moderator unmuted your voice - other players can hear you again.");
        }
        if (textChanged)
        {
            DisplayMessage(textMuted
                ? "A moderator muted your text chat - your messages will not be delivered until you are unmuted."
                : "A moderator unmuted your text chat - your messages are delivered again.");
        }
    }

    #endregion

    #region Permission Management

    /// <summary>
    /// Client-side representation of a permission group.
    /// </summary>
    public class PermGroupData
    {
        public string Name;
        public List<string> Nodes = new List<string>();
        public List<string> Parents = new List<string>();
    }

    /// <summary>
    /// Client-side representation of a permission user.
    /// </summary>
    public class PermUserData
    {
        public string Uuid;
        public List<string> Groups = new List<string>();
        public List<string> Nodes = new List<string>();
    }

    /// <summary>
    /// Full permission snapshot received from the server.
    /// </summary>
    public class PermissionSnapshot
    {
        public List<PermGroupData> Groups = new List<PermGroupData>();
        public List<PermUserData> Users = new List<PermUserData>();
    }

    /// <summary>
    /// Last received permission snapshot from the server.
    /// </summary>
    public static PermissionSnapshot LastPermissionSnapshot;

    /// <summary>
    /// Fired when a permission snapshot is received from the server.
    /// </summary>
    public static event Action<PermissionSnapshot> OnPermissionsReceived;

    private static void HandlePermissionsResponse(NetDataReader reader)
    {
        var snapshot = new PermissionSnapshot();

        int groupCount = reader.GetInt();
        for (int i = 0; i < groupCount; i++)
        {
            var group = new PermGroupData();
            group.Name = reader.GetString();
            int nodeCount = reader.GetInt();
            for (int n = 0; n < nodeCount; n++)
                group.Nodes.Add(reader.GetString());
            int parentCount = reader.GetInt();
            for (int p = 0; p < parentCount; p++)
                group.Parents.Add(reader.GetString());
            snapshot.Groups.Add(group);
        }

        int userCount = reader.GetInt();
        for (int i = 0; i < userCount; i++)
        {
            var user = new PermUserData();
            user.Uuid = reader.GetString();
            int userGroupCount = reader.GetInt();
            for (int g = 0; g < userGroupCount; g++)
                user.Groups.Add(reader.GetString());
            int userNodeCount = reader.GetInt();
            for (int n = 0; n < userNodeCount; n++)
                user.Nodes.Add(reader.GetString());
            snapshot.Users.Add(user);
        }

        LastPermissionSnapshot = snapshot;
        OnPermissionsReceived?.Invoke(snapshot);
    }

    /// <summary>
    /// Request the full permission snapshot from the server. Any user can call this.
    /// </summary>
    public static void RequestPermissions()
    {
        SendAdminRequest(AdminRequestMode.GetPermissions);
    }

    /// <summary>
    /// One server answer to <see cref="QueryPermissionNode"/> or <see cref="QueryPermissionGroup"/>.
    /// The request is echoed back in full, so a caller matches a reply by comparing what it asked
    /// rather than by tracking a request id — which also means one reply satisfies every caller
    /// that happened to ask the same question.
    /// </summary>
    public struct PermissionQueryResult
    {
        /// <summary>Player the question was about.</summary>
        public ushort PlayerId;

        /// <summary>Whether <see cref="Value"/> named a permission node or a group.</summary>
        public AdminPermissionQueryKind Kind;

        /// <summary>The node or group name that was asked about.</summary>
        public string Value;

        /// <summary>The answer. Always false when <see cref="PlayerFound"/> is false.</summary>
        public bool Held;

        /// <summary>False when that player was not connected by the time the server looked.</summary>
        public bool PlayerFound;
    }

    /// <summary>
    /// Fired for every permission query answered by the server.
    /// </summary>
    public static event Action<PermissionQueryResult> OnPermissionQueryResult;

    /// <summary>
    /// Ask the server whether one player currently in this instance holds a permission node.
    /// Any user may ask — unlike <see cref="RequestPermissions"/>, this returns one yes/no about
    /// one player rather than the whole table. The answer arrives on
    /// <see cref="OnPermissionQueryResult"/>; the server rate limits per peer and silently drops
    /// what is over budget, so a query is not guaranteed an answer. For the local player read
    /// <see cref="BasisNetworkManagement.LocalPermissions"/> directly instead — it is already here.
    /// </summary>
    public static void QueryPermissionNode(ushort playerId, string node)
    {
        if (ValidateString(node, nameof(node)))
        {
            SendPermissionQuery(playerId, AdminPermissionQueryKind.Node, node);
        }
    }

    /// <summary>
    /// Ask the server whether one player currently in this instance belongs to a permission group
    /// ("role"), counting groups inherited through a parent chain. Same delivery and limits as
    /// <see cref="QueryPermissionNode"/>.
    /// </summary>
    public static void QueryPermissionGroup(ushort playerId, string group)
    {
        if (ValidateString(group, nameof(group)))
        {
            SendPermissionQuery(playerId, AdminPermissionQueryKind.Group, group);
        }
    }

    private static void SendPermissionQuery(ushort playerId, AdminPermissionQueryKind kind, string value)
    {
        SendAdminRequest(AdminRequestMode.QueryPermission,
            w => w.Put(playerId),
            w => w.Put((byte)kind),
            w => w.Put(value));
    }

    private static void HandlePermissionQueryResult(NetDataReader reader)
    {
        PermissionQueryResult result = new PermissionQueryResult
        {
            PlayerId = reader.GetUShort(),
            Kind = (AdminPermissionQueryKind)reader.GetByte(),
            Value = reader.GetString(),
            Held = reader.GetBool(),
            PlayerFound = reader.GetBool(),
        };

        OnPermissionQueryResult?.Invoke(result);
    }

    /// <summary>
    /// Admin: Add or remove a user from a group.
    /// </summary>
    public static void SetUserGroup(string uuid, string group, bool add)
    {
        if (ValidateString(uuid, nameof(uuid)) && ValidateString(group, nameof(group)))
        {
            SendAdminRequest(AdminRequestMode.SetUserGroup,
                w => w.Put(uuid),
                w => w.Put(group),
                w => w.Put(add));
        }
    }

    /// <summary>
    /// Admin: Add or remove a permission node from a user.
    /// </summary>
    public static void SetUserNode(string uuid, string node, bool add)
    {
        if (ValidateString(uuid, nameof(uuid)) && ValidateString(node, nameof(node)))
        {
            SendAdminRequest(AdminRequestMode.SetUserNode,
                w => w.Put(uuid),
                w => w.Put(node),
                w => w.Put(add));
        }
    }

    /// <summary>
    /// Admin: Add or remove a permission node from a group.
    /// </summary>
    public static void SetGroupNode(string groupName, string node, bool add)
    {
        if (ValidateString(groupName, nameof(groupName)) && ValidateString(node, nameof(node)))
        {
            SendAdminRequest(AdminRequestMode.SetGroupNode,
                w => w.Put(groupName),
                w => w.Put(node),
                w => w.Put(add));
        }
    }

    /// <summary>
    /// Admin: Create a new permission group.
    /// </summary>
    public static void CreateGroup(string groupName)
    {
        if (ValidateString(groupName, nameof(groupName)))
        {
            SendAdminRequest(AdminRequestMode.CreateGroup,
                w => w.Put(groupName));
        }
    }

    /// <summary>
    /// Admin: Delete a permission group.
    /// </summary>
    public static void DeleteGroup(string groupName)
    {
        if (ValidateString(groupName, nameof(groupName)))
        {
            SendAdminRequest(AdminRequestMode.DeleteGroup,
                w => w.Put(groupName));
        }
    }

    /// <summary>
    /// Admin: Add or remove a parent group from a group.
    /// </summary>
    public static void SetGroupParent(string groupName, string parentName, bool add)
    {
        if (ValidateString(groupName, nameof(groupName)) && ValidateString(parentName, nameof(parentName)))
        {
            SendAdminRequest(AdminRequestMode.SetGroupParent,
                w => w.Put(groupName),
                w => w.Put(parentName),
                w => w.Put(add));
        }
    }

    #endregion

    #region Global Lock State

    /// <summary>
    /// Current global lock state received from the server.
    /// </summary>
    public static bool GlobalAvatarsLocked { get; private set; }
    public static bool GlobalPropsLocked { get; private set; }
    public static bool GlobalWorldsLocked { get; private set; }
    /// <summary>
    /// True when the server has globally disabled saved-server sharing through
    /// the content-share system. UIs that initiate server shares should disable
    /// their share buttons while this is set.
    /// </summary>
    public static bool GlobalServersLocked { get; private set; }

    /// <summary>
    /// Server-pushed third-person camera lockout. While true, the local desktop client must
    /// hard-disable third-person (no toggle, no zoom). Mirrored to
    /// <see cref="BasisLocalCameraDriver.AdminThirdPersonLocked"/> via
    /// <see cref="OnGlobalThirdPersonDisabledChanged"/>.
    /// </summary>
    public static bool GlobalThirdPersonDisabled { get; private set; }

    /// <summary>
    /// Server-pushed lock on AdditionalAvatarDatas. While true, the server strips
    /// AdditionalAvatarDatas (blendshapes, custom-behaviour params) from every inbound
    /// avatar sync message before propagating to other peers. Muscle/position/rotation
    /// still sync normally.
    /// </summary>
    public static bool GlobalAdditionalAvatarDataLock { get; private set; }

    /// <summary>
    /// Fired when the global lock state changes. Parameters: avatarsLocked, propsLocked, worldsLocked, serversLocked.
    /// </summary>
    public static event Action<bool, bool, bool, bool> OnGlobalLockStateChanged;

    /// <summary>
    /// Fired when the third-person camera lockout flag changes. Separate from
    /// <see cref="OnGlobalLockStateChanged"/> so existing 4-arg subscribers keep compiling.
    /// </summary>
    public static event Action<bool> OnGlobalThirdPersonDisabledChanged;

    /// <summary>
    /// Fired when the additional-avatar-data lock flag changes. Separate event so
    /// existing 4-arg <see cref="OnGlobalLockStateChanged"/> subscribers keep compiling.
    /// </summary>
    public static event Action<bool> OnGlobalAdditionalAvatarDataLockChanged;

    /// <summary>
    /// Server-pushed per-category camera photo-metadata disallow mask. A set bit forbids
    /// that embedding category for every client regardless of the user's own toggles.
    /// 0 = everything allowed. Bits are the CameraPolicy_* constants.
    /// </summary>
    public static byte GlobalCameraDisallowMask { get; private set; }

    public const byte CameraPolicy_TagPeople = 1 << 0;
    public const byte CameraPolicy_PersonDetails = 1 << 1;
    public const byte CameraPolicy_CameraExif = 1 << 2;
    public const byte CameraPolicy_CaptureInfo = 1 << 3;
    public const byte CameraPolicy_Photographer = 1 << 4;
    public const byte CameraPolicy_World = 1 << 5;

    /// <summary>True when the server forbids the given camera metadata category (a CameraPolicy_* bit).</summary>
    public static bool IsCameraCategoryDisallowed(byte categoryBit) => (GlobalCameraDisallowMask & categoryBit) != 0;

    /// <summary>Fired when the server-pushed camera metadata policy mask changes.</summary>
    public static event Action<byte> OnGlobalCameraPolicyChanged;

    /// <summary>
    /// Server-pushed player join restriction mode (Normal / AllowList / RejoinOnly). Cached from the
    /// lock-state payload — sent on connect and whenever an admin changes it — so the admin panel
    /// toggles can reflect the live server state instead of always reading off.
    /// </summary>
    public static BasisUserRestrictionMode GlobalUserRestrictionMode { get; private set; } = BasisUserRestrictionMode.Normal;

    /// <summary>Fired when the server-pushed restriction mode changes.</summary>
    public static event Action<BasisUserRestrictionMode> OnGlobalRestrictionModeChanged;

    /// <summary>
    /// Current headless audio state received from the server.
    /// True means headless clients should keep BasisAudioClipPlayer off.
    /// </summary>
    public static bool GlobalHeadlessAudioOff { get; private set; }

    /// <summary>
    /// Fired when the global headless audio state changes.
    /// Parameter: headlessAudioOff.
    /// </summary>
    public static event Action<bool> OnGlobalHeadlessAudioStateChanged;

    /// <summary>
    /// Current headless connection policy received from the server.
    /// True means headless clients are not allowed to remain connected.
    /// </summary>
    public static bool GlobalHeadlessDisallowed { get; private set; }

    /// <summary>
    /// Fired when the global headless disallow state changes.
    /// Parameter: headlessDisallowed.
    /// </summary>
    public static event Action<bool> OnGlobalHeadlessDisallowStateChanged;

    /// <summary>
    /// Server-pushed lock: while true, non-admin players cannot use the playspace mover.
    /// Admins (basis.moderation.globallock) are exempt — see <see cref="LocalPlayerHasGlobalLockBypass"/>.
    /// </summary>
    public static bool GlobalPlayspaceMoverLocked { get; private set; }

    /// <summary>Fired when the playspace-mover lockout flag changes.</summary>
    public static event Action<bool> OnGlobalPlayspaceMoverLockedChanged;

    /// <summary>
    /// Server-pushed lock: while true, the server refuses to broker direct (P2P) connections for
    /// non-admin players and clients hide the direct-connect control. Admins are exempt.
    /// </summary>
    public static bool GlobalDirectConnectLocked { get; private set; }

    /// <summary>Fired when the direct-connect lockout flag changes.</summary>
    public static event Action<bool> OnGlobalDirectConnectLockedChanged;

    /// <summary>
    /// Server-pushed lock: while true, every avatar that loads has its Cilbox sandbox host + proxies
    /// stripped by ContentPolice, so no avatar script runs. Load-time only — avatars already loaded
    /// keep their Cilbox until they reload. Props and worlds keep their own Cilbox.
    /// </summary>
    public static bool GlobalCilboxLocked { get; private set; }

    /// <summary>Fired when the Cilbox lock flag changes.</summary>
    public static event Action<bool> OnGlobalCilboxLockChanged;

    /// <summary>
    /// Server-pushed lock: while true, non-bypass clients can't share new image pickups and won't
    /// accept inbound ones. Enforced client-side by the image-pickup package (image pickups ride the
    /// generic scene relay, so the server can't single them out like content shares). Admins with the
    /// global-lock bypass are exempt.
    /// </summary>
    public static bool GlobalImagesLocked { get; private set; }

    /// <summary>
    /// Server-pushed disable of remote end-effector IK anchoring. While true, clients stop two-bone-IK
    /// anchoring remote avatars' tracked hands/feet and fall back to pure-FK playback. Default false
    /// (feature on); mirrored to <see cref="BasisNetworkReceiver.EndEffectorIKEnabled"/> on parse.
    /// </summary>
    public static bool GlobalEndEffectorIKDisabled { get; private set; }

    /// <summary>
    /// Server-pushed lock: while true, peers without basis.chat.lockbypass can't send text chat or
    /// typing state. Enforced server-side (chat has its own channel, so the server drops it outright);
    /// this flag exists so the local composer can grey out instead of silently swallowing messages.
    /// </summary>
    public static bool GlobalTextChatLocked { get; private set; }

    /// <summary>Fired when the shared-image lock flag changes.</summary>
    public static event Action<bool> OnGlobalImagesLockedChanged;

    /// <summary>
    /// Server-pushed lock: while true, peers without basis.voice.lockbypass can't transmit voice.
    /// Enforced server-side (voice has its own channels); clients also stop transmitting so a locked
    /// user isn't burning upstream bandwidth into a stream the server discards.
    /// </summary>
    public static bool GlobalVoiceChatLocked { get; private set; }

    /// <summary>
    /// Server-pushed lock: while true, non-bypass clients neither load new media player URLs nor
    /// accept inbound ones. Enforced client-side by the media player package (media state rides the
    /// generic scene relay, so the server can't single it out). Already-playing media keeps playing.
    /// </summary>
    public static bool GlobalMediaPlayerLocked { get; private set; }

    /// <summary>
    /// Server-pushed lock: while true, non-bypass clients can't capture photos. Enforced client-side
    /// — capture is entirely local, so nothing reaches the server to block. Distinct from
    /// <see cref="GlobalCameraDisallowMask"/>, which only strips metadata from photos still taken.
    /// </summary>
    public static bool GlobalCameraCaptureLocked { get; private set; }

    /// <summary>
    /// Server-pushed lock: while true, non-bypass clients can't pick up or grab props. Enforced
    /// client-side — grabbing is local interaction logic. Distinct from <see cref="GlobalPropsLocked"/>,
    /// which blocks prop loading rather than handling already-spawned ones.
    /// </summary>
    public static bool GlobalPropGrabbingLocked { get; private set; }

    /// <summary>
    /// Server-pushed policy: while true, other players' display names render with rich-text markup
    /// stripped and TMP rich text disabled. Applies to everyone; there is no bypass.
    /// </summary>
    public static bool GlobalSafeDisplayNamesForced { get; private set; }

    /// <summary>Fired when the text-chat lock flag changes.</summary>
    public static event Action<bool> OnGlobalTextChatLockedChanged;

    /// <summary>Fired when the voice lock flag changes.</summary>
    public static event Action<bool> OnGlobalVoiceChatLockedChanged;

    /// <summary>Fired when the media-player lock flag changes.</summary>
    public static event Action<bool> OnGlobalMediaPlayerLockedChanged;

    /// <summary>Fired when the camera-capture lock flag changes.</summary>
    public static event Action<bool> OnGlobalCameraCaptureLockedChanged;

    /// <summary>Fired when the prop-grabbing lock flag changes.</summary>
    public static event Action<bool> OnGlobalPropGrabbingLockedChanged;

    /// <summary>Fired when the forced-safe-display-names flag changes.</summary>
    public static event Action<bool> OnGlobalSafeDisplayNamesForcedChanged;

    /// <summary>Fired when the remote end-effector IK disable flag changes (true = disabled).</summary>
    public static event Action<bool> OnGlobalEndEffectorIKDisabledChanged;

    /// <summary>
    /// True when the local player holds the global-lock moderation permission (or the '*' wildcard),
    /// which exempts them from the admin-controlled avatar-scale clamp, playspace-mover lockout, and
    /// direct-connect lockout. Mirrors the server's permission check; direct connect is still gated
    /// server-side, so this is just UI/UX for that one.
    /// </summary>
    public static bool LocalPlayerHasGlobalLockBypass()
    {
        var perms = BasisNetworkManagement.LocalPermissions;
        return perms != null &&
               (perms.Contains(BasisPermissions.PermNodes.All) ||
                perms.Contains(BasisPermissions.PermNodes.ModerationGlobalLock));
    }

    /// <summary>
    /// True when the local player may still send text chat while <see cref="GlobalTextChatLocked"/>
    /// is on. Mirrors the server's own check exactly (basis.chat.lockbypass, or the '*' wildcard) —
    /// this gate is cosmetic, the server drops the message either way, so the two must agree or a
    /// bypass holder would see a greyed-out composer that would in fact have worked.
    /// </summary>
    public static bool LocalPlayerHasChatLockBypass()
    {
        var perms = BasisNetworkManagement.LocalPermissions;
        return perms != null &&
               (perms.Contains(BasisPermissions.PermNodes.All) ||
                perms.Contains(BasisPermissions.PermNodes.ChatLockBypass));
    }

    /// <summary>
    /// True when the local player may still transmit voice while <see cref="GlobalVoiceChatLocked"/>
    /// is on. Mirrors the server's own check exactly (basis.voice.lockbypass, or the '*' wildcard) —
    /// the server drops the audio either way, so the two must agree or a bypass holder would mute
    /// themselves locally when the server would in fact have relayed them.
    /// </summary>
    public static bool LocalPlayerHasVoiceLockBypass()
    {
        var perms = BasisNetworkManagement.LocalPermissions;
        return perms != null &&
               (perms.Contains(BasisPermissions.PermNodes.All) ||
                perms.Contains(BasisPermissions.PermNodes.VoiceLockBypass));
    }

    /// <summary>
    /// True when the local player must stop transmitting voice. The server drops it regardless —
    /// this exists so a locked or moderation-muted client doesn't keep encoding and uploading a
    /// discarded stream.
    /// </summary>
    public static bool VoiceBlockedLocally =>
        (GlobalVoiceChatLocked && !LocalPlayerHasVoiceLockBypass()) || LocalPlayerVoiceMutedByModerator;

    /// <summary>
    /// True when the local player may not load media player URLs (outbound or inbound).
    /// Client-enforced, so this IS the whole gate — admins are exempt via the global-lock bypass.
    /// </summary>
    public static bool MediaPlayerBlockedLocally =>
        GlobalMediaPlayerLocked && !LocalPlayerHasGlobalLockBypass();

    /// <summary>
    /// True when the local player may not capture photos. Client-enforced, so this IS the whole
    /// gate — admins are exempt via the global-lock bypass.
    /// </summary>
    public static bool CameraCaptureBlockedLocally =>
        GlobalCameraCaptureLocked && !LocalPlayerHasGlobalLockBypass();

    /// <summary>
    /// True when the local player may not pick up props. Client-enforced, so this IS the whole
    /// gate — admins are exempt via the global-lock bypass.
    /// </summary>
    public static bool PropGrabbingBlockedLocally =>
        GlobalPropGrabbingLocked && !LocalPlayerHasGlobalLockBypass();

    private static void HandleGlobalLockState(NetDataReader reader)
    {
        GlobalAvatarsLocked = reader.GetBool();
        GlobalPropsLocked = reader.GetBool();
        GlobalWorldsLocked = reader.GetBool();
        // ServersLocked was added after the original three; older servers won't
        // include it. Tolerate the short payload by leaving the existing value
        // (defaults to false) when the bool isn't there.
        if (reader.AvailableBytes >= 1) GlobalServersLocked = reader.GetBool();
        // ThirdPersonDisabled appended after ServersLocked — same backward-compat trick.
        // Only fire OnGlobalThirdPersonDisabledChanged if the flag actually flipped to keep
        // listeners from doing redundant snap-to-first-person work every reconnect.
        if (reader.AvailableBytes >= 1)
        {
            bool nextThirdPerson = reader.GetBool();
            if (nextThirdPerson != GlobalThirdPersonDisabled)
            {
                GlobalThirdPersonDisabled = nextThirdPerson;
                OnGlobalThirdPersonDisabledChanged?.Invoke(GlobalThirdPersonDisabled);
            }
        }
        // AdditionalAvatarDataLock appended after ThirdPersonDisabled. Same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            bool nextAdditionalAvatarDataLock = reader.GetBool();
            if (nextAdditionalAvatarDataLock != GlobalAdditionalAvatarDataLock)
            {
                GlobalAdditionalAvatarDataLock = nextAdditionalAvatarDataLock;
                OnGlobalAdditionalAvatarDataLockChanged?.Invoke(GlobalAdditionalAvatarDataLock);
            }
        }
        // CameraMetadataDisallowMask appended after AdditionalAvatarDataLock (1 byte). Same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            byte nextCameraMask = reader.GetByte();
            if (nextCameraMask != GlobalCameraDisallowMask)
            {
                GlobalCameraDisallowMask = nextCameraMask;
                OnGlobalCameraPolicyChanged?.Invoke(GlobalCameraDisallowMask);
            }
        }
        // BasisUserRestrictionMode appended after CameraMetadataDisallowMask (1 byte). Same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            byte nextRestriction = reader.GetByte();
            BasisUserRestrictionMode parsedRestriction = (BasisUserRestrictionMode)nextRestriction;
            if (Enum.IsDefined(typeof(BasisUserRestrictionMode), parsedRestriction))
            {
                if (parsedRestriction != GlobalUserRestrictionMode)
                {
                    GlobalUserRestrictionMode = parsedRestriction;
                    OnGlobalRestrictionModeChanged?.Invoke(GlobalUserRestrictionMode);
                }
            }
        }
        // PlayspaceMoverLocked appended after BasisUserRestrictionMode — same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            bool nextPlayspaceLocked = reader.GetBool();
            if (nextPlayspaceLocked != GlobalPlayspaceMoverLocked)
            {
                GlobalPlayspaceMoverLocked = nextPlayspaceLocked;
                OnGlobalPlayspaceMoverLockedChanged?.Invoke(GlobalPlayspaceMoverLocked);
            }
        }
        // DirectConnectLocked appended after PlayspaceMoverLocked — same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            bool nextDirectConnectLocked = reader.GetBool();
            if (nextDirectConnectLocked != GlobalDirectConnectLocked)
            {
                GlobalDirectConnectLocked = nextDirectConnectLocked;
                OnGlobalDirectConnectLockedChanged?.Invoke(GlobalDirectConnectLocked);
            }
        }
        // CilboxLocked appended after DirectConnectLocked — same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            bool nextCilboxLocked = reader.GetBool();
            if (nextCilboxLocked != GlobalCilboxLocked)
            {
                GlobalCilboxLocked = nextCilboxLocked;
                OnGlobalCilboxLockChanged?.Invoke(GlobalCilboxLocked);
            }
        }
        // ImagesLocked appended after CilboxLocked — same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            bool nextImagesLocked = reader.GetBool();
            if (nextImagesLocked != GlobalImagesLocked)
            {
                GlobalImagesLocked = nextImagesLocked;
                OnGlobalImagesLockedChanged?.Invoke(GlobalImagesLocked);
            }
        }
        // EndEffectorIKDisabled appended after ImagesLocked — same back-compat trick. Default off (feature
        // on). Mirror onto the receiver static so all remote playback picks up the server-wide state.
        if (reader.AvailableBytes >= 1)
        {
            bool nextEndEffectorIKDisabled = reader.GetBool();
            if (nextEndEffectorIKDisabled != GlobalEndEffectorIKDisabled)
            {
                GlobalEndEffectorIKDisabled = nextEndEffectorIKDisabled;
                BasisNetworkReceiver.EndEffectorIKEnabled = !nextEndEffectorIKDisabled;
                OnGlobalEndEffectorIKDisabledChanged?.Invoke(GlobalEndEffectorIKDisabled);
            }
        }
        // TextChatLocked appended after EndEffectorIKDisabled — same back-compat trick.
        if (reader.AvailableBytes >= 1)
        {
            bool nextTextChatLocked = reader.GetBool();
            if (nextTextChatLocked != GlobalTextChatLocked)
            {
                GlobalTextChatLocked = nextTextChatLocked;
                OnGlobalTextChatLockedChanged?.Invoke(GlobalTextChatLocked);
            }
        }
        // VoiceChat/MediaPlayer/CameraCapture/PropGrabbing appended after TextChatLocked — same
        // back-compat trick, each guarded independently so a short payload stops cleanly.
        if (reader.AvailableBytes >= 1)
        {
            bool nextVoiceChatLocked = reader.GetBool();
            if (nextVoiceChatLocked != GlobalVoiceChatLocked)
            {
                GlobalVoiceChatLocked = nextVoiceChatLocked;
                OnGlobalVoiceChatLockedChanged?.Invoke(GlobalVoiceChatLocked);
            }
        }
        if (reader.AvailableBytes >= 1)
        {
            bool nextMediaPlayerLocked = reader.GetBool();
            if (nextMediaPlayerLocked != GlobalMediaPlayerLocked)
            {
                GlobalMediaPlayerLocked = nextMediaPlayerLocked;
                OnGlobalMediaPlayerLockedChanged?.Invoke(GlobalMediaPlayerLocked);
            }
        }
        if (reader.AvailableBytes >= 1)
        {
            bool nextCameraCaptureLocked = reader.GetBool();
            if (nextCameraCaptureLocked != GlobalCameraCaptureLocked)
            {
                GlobalCameraCaptureLocked = nextCameraCaptureLocked;
                OnGlobalCameraCaptureLockedChanged?.Invoke(GlobalCameraCaptureLocked);
            }
        }
        if (reader.AvailableBytes >= 1)
        {
            bool nextPropGrabbingLocked = reader.GetBool();
            if (nextPropGrabbingLocked != GlobalPropGrabbingLocked)
            {
                GlobalPropGrabbingLocked = nextPropGrabbingLocked;
                OnGlobalPropGrabbingLockedChanged?.Invoke(GlobalPropGrabbingLocked);
            }
        }
        if (reader.AvailableBytes >= 1)
        {
            bool nextSafeDisplayNames = reader.GetBool();
            if (nextSafeDisplayNames != GlobalSafeDisplayNamesForced)
            {
                GlobalSafeDisplayNamesForced = nextSafeDisplayNames;
                OnGlobalSafeDisplayNamesForcedChanged?.Invoke(GlobalSafeDisplayNamesForced);
            }
        }
        BasisDebug.Log($"Global lock state updated - Avatars: {GlobalAvatarsLocked}, Props: {GlobalPropsLocked}, Worlds: {GlobalWorldsLocked}, Servers: {GlobalServersLocked}, ThirdPerson: {GlobalThirdPersonDisabled}, AdditionalAvatarData: {GlobalAdditionalAvatarDataLock}, CameraMask: {GlobalCameraDisallowMask}, Restriction: {GlobalUserRestrictionMode}, PlayspaceMover: {GlobalPlayspaceMoverLocked}, DirectConnect: {GlobalDirectConnectLocked}, Cilbox: {GlobalCilboxLocked}, Images: {GlobalImagesLocked}, EndEffectorIKDisabled: {GlobalEndEffectorIKDisabled}, TextChat: {GlobalTextChatLocked}, VoiceChat: {GlobalVoiceChatLocked}, MediaPlayer: {GlobalMediaPlayerLocked}, CameraCapture: {GlobalCameraCaptureLocked}, PropGrabbing: {GlobalPropGrabbingLocked}, SafeDisplayNames: {GlobalSafeDisplayNamesForced}", BasisDebug.LogTag.Networking);
        OnGlobalLockStateChanged?.Invoke(GlobalAvatarsLocked, GlobalPropsLocked, GlobalWorldsLocked, GlobalServersLocked);
    }

    /// <summary>
    /// Drops every server-pushed global lock back to its default and notifies listeners. Called on
    /// disconnect: these are process-wide statics, so without this a server's locks stay in force
    /// offline and in whatever the client loads next — a locked camera or playspace mover would
    /// stay locked in a local world until the player happened to join another server.
    /// Each flag only fires its event when it actually changes, matching HandleGlobalLockState.
    /// </summary>
    public static void ResetGlobalLockState()
    {
        bool contentLocksChanged = GlobalAvatarsLocked || GlobalPropsLocked || GlobalWorldsLocked || GlobalServersLocked;
        GlobalAvatarsLocked = false;
        GlobalPropsLocked = false;
        GlobalWorldsLocked = false;
        GlobalServersLocked = false;

        // Assign before firing so a listener reading the property back sees the cleared value.
        if (GlobalThirdPersonDisabled) { GlobalThirdPersonDisabled = false; OnGlobalThirdPersonDisabledChanged?.Invoke(false); }
        if (GlobalAdditionalAvatarDataLock) { GlobalAdditionalAvatarDataLock = false; OnGlobalAdditionalAvatarDataLockChanged?.Invoke(false); }
        if (GlobalPlayspaceMoverLocked) { GlobalPlayspaceMoverLocked = false; OnGlobalPlayspaceMoverLockedChanged?.Invoke(false); }
        if (GlobalDirectConnectLocked) { GlobalDirectConnectLocked = false; OnGlobalDirectConnectLockedChanged?.Invoke(false); }
        if (GlobalCilboxLocked) { GlobalCilboxLocked = false; OnGlobalCilboxLockChanged?.Invoke(false); }
        if (GlobalImagesLocked) { GlobalImagesLocked = false; OnGlobalImagesLockedChanged?.Invoke(false); }
        if (GlobalTextChatLocked) { GlobalTextChatLocked = false; OnGlobalTextChatLockedChanged?.Invoke(false); }
        if (GlobalVoiceChatLocked) { GlobalVoiceChatLocked = false; OnGlobalVoiceChatLockedChanged?.Invoke(false); }
        if (GlobalMediaPlayerLocked) { GlobalMediaPlayerLocked = false; OnGlobalMediaPlayerLockedChanged?.Invoke(false); }
        if (GlobalCameraCaptureLocked) { GlobalCameraCaptureLocked = false; OnGlobalCameraCaptureLockedChanged?.Invoke(false); }
        if (GlobalPropGrabbingLocked) { GlobalPropGrabbingLocked = false; OnGlobalPropGrabbingLockedChanged?.Invoke(false); }
        if (GlobalSafeDisplayNamesForced) { GlobalSafeDisplayNamesForced = false; OnGlobalSafeDisplayNamesForcedChanged?.Invoke(false); }

        if (GlobalEndEffectorIKDisabled)
        {
            GlobalEndEffectorIKDisabled = false;
            BasisNetworkReceiver.EndEffectorIKEnabled = true;
            OnGlobalEndEffectorIKDisabledChanged?.Invoke(false);
        }

        if (GlobalCameraDisallowMask != 0)
        {
            GlobalCameraDisallowMask = 0;
            OnGlobalCameraPolicyChanged?.Invoke(GlobalCameraDisallowMask);
        }

        if (GlobalUserRestrictionMode != BasisUserRestrictionMode.Normal)
        {
            GlobalUserRestrictionMode = BasisUserRestrictionMode.Normal;
            OnGlobalRestrictionModeChanged?.Invoke(GlobalUserRestrictionMode);
        }

        // Moderation mutes are per-server state exactly like the locks above.
        if (LocalPlayerVoiceMutedByModerator) { LocalPlayerVoiceMutedByModerator = false; OnLocalVoiceMutedByModeratorChanged?.Invoke(false); }
        if (LocalPlayerTextMutedByModerator) { LocalPlayerTextMutedByModerator = false; OnLocalTextMutedByModeratorChanged?.Invoke(false); }

        if (contentLocksChanged)
        {
            OnGlobalLockStateChanged?.Invoke(false, false, false, false);
        }
    }

    /// <summary>
    /// Admin: Toggle global avatar loading.
    /// </summary>
    public static void GlobalToggleAvatars()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleAvatars);
    }

    /// <summary>
    /// Admin: Toggle global prop loading.
    /// </summary>
    public static void GlobalToggleProps()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleProps);
    }

    /// <summary>
    /// Admin: Toggle global world loading.
    /// </summary>
    public static void GlobalToggleWorlds()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleWorlds);
    }

    /// <summary>
    /// Admin: Toggle the global lock on saved-server sharing through the content-share system.
    /// </summary>
    public static void GlobalToggleServers()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleServers);
    }

    /// <summary>
    /// Admin: toggle the global third-person camera lockout. Server flips the flag,
    /// persists it to config.xml, and broadcasts the new GlobalGetLockState payload.
    /// </summary>
    public static void GlobalToggleThirdPerson()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleThirdPerson);
    }

    /// <summary>
    /// Admin: toggle the global strip of AdditionalAvatarDatas on inbound avatar sync
    /// messages. Server flips the flag and starts dropping the additional-data payload
    /// from every inbound message; muscle/position/rotation still propagate normally.
    /// </summary>
    public static void GlobalToggleAdditionalAvatarDataLock()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleAdditionalAvatarDataLock);
    }

    /// <summary>
    /// Admin: toggle the global playspace-mover lockout. Server flips the flag, persists it to
    /// config.xml, and broadcasts the new GlobalGetLockState payload. Non-admins can't grab/drag
    /// their play space while set.
    /// </summary>
    public static void GlobalTogglePlayspaceMover()
    {
        SendAdminRequest(AdminRequestMode.GlobalTogglePlayspaceMover);
    }

    /// <summary>
    /// Admin: toggle the global direct-connect (P2P) lockout. Server flips the flag, persists it,
    /// broadcasts the new lock state, and refuses to broker P2P for non-admins while set.
    /// </summary>
    public static void GlobalToggleDirectConnect()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleDirectConnect);
    }

    /// <summary>
    /// Admin: toggle the global Cilbox lock. While set, every client blocks sandboxed Cilbox code
    /// on avatars from running. Server flips the flag and broadcasts the new lock state.
    /// </summary>
    public static void GlobalToggleCilbox()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleCilbox);
    }

    /// <summary>
    /// Admin: toggle the global shared-image lock. While set, non-bypass clients can't share new
    /// image pickups and won't accept inbound ones. Server flips the flag and broadcasts the new
    /// lock state; the image-pickup package honors it client-side.
    /// </summary>
    public static void GlobalToggleImages()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleImages);
    }

    /// <summary>
    /// Admin: toggle the global text-chat lock. While set the server drops chat messages and typing
    /// state from peers without basis.chat.lockbypass, and clients grey out their chat composer.
    /// </summary>
    public static void GlobalToggleTextChat()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleTextChat);
    }

    /// <summary>
    /// Admin: toggle the global voice lock. While set the server drops normal and announce voice from
    /// peers without basis.voice.lockbypass, and those clients stop transmitting.
    /// </summary>
    public static void GlobalToggleVoiceChat()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleVoiceChat);
    }

    /// <summary>
    /// Admin: toggle the global media-player lock. While set, non-bypass clients neither load new
    /// media URLs nor accept inbound ones. Honored client-side by the media player package.
    /// </summary>
    public static void GlobalToggleMediaPlayer()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleMediaPlayer);
    }

    /// <summary>
    /// Admin: toggle the global camera-capture lock. While set, non-bypass clients can't take
    /// photos. Honored client-side by the camera package.
    /// </summary>
    public static void GlobalToggleCameraCapture()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleCameraCapture);
    }

    /// <summary>
    /// Admin: toggle the global prop-grabbing lock. While set, non-bypass clients can't pick up
    /// props. Honored client-side by the pickup interactables.
    /// </summary>
    public static void GlobalTogglePropGrabbing()
    {
        SendAdminRequest(AdminRequestMode.GlobalTogglePropGrabbing);
    }

    /// <summary>Admin: toggle forced safe display names server-wide.</summary>
    public static void GlobalToggleSafeDisplayNames()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleSafeDisplayNames);
    }

    /// <summary>
    /// Admin: toggle remote end-effector IK anchoring server-wide. Server flips the flag and
    /// broadcasts the new lock state; every client mirrors it onto BasisNetworkReceiver.
    /// </summary>
    public static void GlobalToggleEndEffectorIK()
    {
        SendAdminRequest(AdminRequestMode.GlobalToggleEndEffectorIK);
    }

    /// <summary>
    /// Admin: set the per-category camera photo-metadata disallow mask. A set bit forbids
    /// that embedding category for every client. The server stores it and rebroadcasts it
    /// in GlobalGetLockState.
    /// </summary>
    public static void SetGlobalCameraPolicy(byte disallowMask)
    {
        SendAdminRequest(
            AdminRequestMode.SetGlobalCameraPolicy,
            w => w.Put(disallowMask));
    }

    private static void HandleGlobalHeadlessAudioState(NetDataReader reader)
    {
        GlobalHeadlessAudioOff = reader.GetBool();
        BasisDebug.Log($"Global headless audio state updated - Headless audio off: {GlobalHeadlessAudioOff}", BasisDebug.LogTag.Networking);
        OnGlobalHeadlessAudioStateChanged?.Invoke(GlobalHeadlessAudioOff);
    }

    /// <summary>
    /// Server-pushed flag: whether clients may report errors/exceptions. Defaults false so
    /// nothing is sent until a server explicitly enables it (older servers never do).
    /// </summary>
    public static bool CrashReportingEnabled { get; private set; }

    /// <summary>Fired when the server-pushed crash-reporting state changes.</summary>
    public static event Action<bool> OnCrashReportingStateChanged;

    private static void HandleCrashReportState(NetDataReader reader)
    {
        CrashReportingEnabled = reader.GetBool();
        BasisDebug.Log($"Crash reporting {(CrashReportingEnabled ? "enabled" : "disabled")} by server", BasisDebug.LogTag.Networking);
        OnCrashReportingStateChanged?.Invoke(CrashReportingEnabled);
    }

    /// <summary>
    /// Server-pushed ceiling (metres) for the local microphone (voice transmit) range. Clients clamp
    /// their Microphone Range slider and effective range to this. Defaults to 25 until a server pushes it.
    /// </summary>
    public static float ServerMaxMicrophoneRangeMeters { get; private set; } = 25f;

    /// <summary>Server-pushed ceiling (metres) for the local hearing (audio receive) range.</summary>
    public static float ServerMaxHearingRangeMeters { get; private set; } = 25f;

    /// <summary>Fired when the server pushes new audio range limits (microphone, hearing) in metres.</summary>
    public static event Action<float, float> OnAudioRangeLimitsChanged;

    private static void HandleAudioRangeLimits(NetDataReader reader)
    {
        ServerMaxMicrophoneRangeMeters = reader.GetFloat();
        ServerMaxHearingRangeMeters = reader.GetFloat();
        SMModuleDistanceBasedReductions.SetMicrophoneRangeCapMeters(ServerMaxMicrophoneRangeMeters);
        SMModuleDistanceBasedReductions.SetHearingRangeCapMeters(ServerMaxHearingRangeMeters);
        BasisDebug.Log($"Audio range limits from server → microphone {ServerMaxMicrophoneRangeMeters} m, hearing {ServerMaxHearingRangeMeters} m", BasisDebug.LogTag.Networking);
        OnAudioRangeLimitsChanged?.Invoke(ServerMaxMicrophoneRangeMeters, ServerMaxHearingRangeMeters);
    }

    /// <summary>
    /// Admin: set the server-wide maximum microphone and hearing range (metres). Persisted to
    /// config.xml and broadcast to every client.
    /// </summary>
    public static void SetGlobalAudioRangeLimits(float microphoneMeters, float hearingMeters)
    {
        if (microphoneMeters < 1f) microphoneMeters = 1f;
        if (hearingMeters < 1f) hearingMeters = 1f;
        SendAdminRequest(
            AdminRequestMode.SetGlobalAudioRangeLimits,
            w => w.Put(microphoneMeters),
            w => w.Put(hearingMeters));
    }

    /// <summary>
    /// Server-pushed minimum avatar eye height (metres) for non-admin players. Defaults to 0.1 m
    /// until a server pushes a value; admins bypass the clamp.
    /// </summary>
    public static float ServerMinAvatarEyeHeightMeters { get; private set; } = 0.1f;

    /// <summary>Server-pushed maximum avatar eye height (metres) for non-admin players.</summary>
    public static float ServerMaxAvatarEyeHeightMeters { get; private set; } = 100f;

    /// <summary>Fired when the server pushes new avatar scale limits (min, max) in metres.</summary>
    public static event Action<float, float> OnAvatarScaleLimitsChanged;

    private static void HandleAvatarScaleLimits(NetDataReader reader)
    {
        ServerMinAvatarEyeHeightMeters = reader.GetFloat();
        ServerMaxAvatarEyeHeightMeters = reader.GetFloat();
        BasisDebug.Log($"Avatar scale limits from server -> {ServerMinAvatarEyeHeightMeters} m .. {ServerMaxAvatarEyeHeightMeters} m", BasisDebug.LogTag.Networking);
        OnAvatarScaleLimitsChanged?.Invoke(ServerMinAvatarEyeHeightMeters, ServerMaxAvatarEyeHeightMeters);
        // Re-apply so a non-admin already out of range is corrected immediately, not only on their
        // next scale change. The clamp is a no-op for admins, so re-applying is harmless for them.
        if (BasisLocalPlayer.Instance != null && BasisLocalPlayer.PlayerReady)
        {
            BasisHeightDriver.ApplyScaleAndHeight();
        }
    }

    /// <summary>
    /// Admin: set the server-wide min/max avatar eye height (metres). Persisted to config.xml and
    /// broadcast to every client; non-admins are clamped to it.
    /// </summary>
    public static void SetGlobalAvatarScaleLimits(float minMeters, float maxMeters)
    {
        if (minMeters < 0.01f) minMeters = 0.01f;
        if (maxMeters < minMeters) maxMeters = minMeters;
        SendAdminRequest(
            AdminRequestMode.SetGlobalAvatarScaleLimits,
            w => w.Put(minMeters),
            w => w.Put(maxMeters));
    }

    /// <summary>Server-pushed cap on active content-share spheres per player.</summary>
    public static int ServerMaxContentSpheresPerPlayer { get; private set; } = 32;

    /// <summary>Fired when the server pushes new resource limits (spheres/player).</summary>
    public static event Action<int> OnResourceLimitsChanged;

    private static void HandleResourceLimits(NetDataReader reader)
    {
        ServerMaxContentSpheresPerPlayer = reader.GetInt();
        OnResourceLimitsChanged?.Invoke(ServerMaxContentSpheresPerPlayer);
    }

    /// <summary>
    /// Admin: set the server-wide resource caps (content spheres per player).
    /// Persisted to config.xml and broadcast to every admin panel.
    /// </summary>
    public static void SetGlobalResourceLimits(int maxContentSpheresPerPlayer)
    {
        if (maxContentSpheresPerPlayer < 1) maxContentSpheresPerPlayer = 1;
        SendAdminRequest(
            AdminRequestMode.SetGlobalResourceLimits,
            w => w.Put(maxContentSpheresPerPlayer));
    }

    /// <summary>Server-pushed BSR reduction settings. Mirror of the config.xml BSR block; populated on connect and on every admin change.</summary>
    public static int ServerBSRSMillisecondDefaultInterval { get; private set; } = 50;
    public static int ServerBSRBaseMultiplier { get; private set; } = 1;
    public static float ServerBSRSIncreaseRate { get; private set; } = 0.005f;
    public static float ServerBSRSlowestSendRate { get; private set; } = 2.55f;
    public static float ServerHighQualityDistance { get; private set; } = 10f;
    public static float ServerMediumQualityDistance { get; private set; } = 20f;
    public static float ServerLowQualityDistance { get; private set; } = 40f;
    public static bool ServerEnableAvatarBundleCompression { get; private set; } = true;
    public static int ServerAvatarBundleMinMessages { get; private set; } = 4;
    public static int ServerAvatarBundleMinBytes { get; private set; } = 128;
    public static bool ServerEnableBSRProfiling { get; private set; } = false;
    /// <summary>Hybrid avatar-bundle codec: keyframe/full bundles on dictionary Zstd, delta-only on LZ4.</summary>
    public static bool ServerEnableAvatarBundleZstd { get; private set; } = true;
    public static bool ServerAvatarBundleZstdDeltaBundles { get; private set; } = false;
    public static int ServerAvatarBundleZstdLevel { get; private set; } = -2;
    public static int ServerAvatarBundleZstdMaxShedTier { get; private set; } = 1;

    /// <summary>Fired when the server pushes new BSR reduction settings. The Server* values above hold the current set.</summary>
    public static event Action OnReductionSettingsChanged;

    /// <summary>
    /// Server-pushed image/gif bandwidth budgets, in megabits per second.
    ///
    /// Upload is per sharing player and is the same number the server advertises in
    /// <c>ServerMetaDataMessage</c> for the image pickup system to pace itself against; it is
    /// mirrored here so the admin panel can show and edit it. Download is the rate the server
    /// replays cached images to one arriving player and has no client-side counterpart at all.
    /// </summary>
    public static int ServerImageUploadMegabitsPerSecond { get; private set; } = 200;
    public static int ServerImageDownloadMegabitsPerSecond { get; private set; } = 200;

    /// <summary>Headroom the server allows over the advertised upload budget before it drops, as a percentage.</summary>
    public static int ServerImageEgressEnforcementPercent { get; private set; } = 150;

    /// <summary>Fired when the server pushes new image bandwidth budgets.</summary>
    public static event Action OnImageBandwidthChanged;

    private static void HandleImageBandwidth(NetDataReader reader)
    {
        ServerImageUploadMegabitsPerSecond = reader.GetInt();
        ServerImageDownloadMegabitsPerSecond = reader.GetInt();
        ServerImageEgressEnforcementPercent = reader.GetInt();
        OnImageBandwidthChanged?.Invoke();
    }

    /// <summary>
    /// Admin: set the image/gif bandwidth budgets. Persisted to config.xml and applied live.
    ///
    /// Upload is what one sharer may spend of the server's egress — advertised to clients so they
    /// pace themselves, and enforced server-side so a modified one cannot ignore it. Download is
    /// the rate cached images are replayed to an arriving player. 0 means "unmetered" for download
    /// and "leave the client on its own conservative default" for upload.
    /// </summary>
    public static void SetGlobalImageBandwidth(int uploadMegabits, int downloadMegabits, int enforcementPercent)
    {
        if (uploadMegabits < 0) uploadMegabits = 0;
        if (downloadMegabits < 0) downloadMegabits = 0;
        if (enforcementPercent < 100) enforcementPercent = 100;
        if (enforcementPercent > 1000) enforcementPercent = 1000;
        SendAdminRequest(
            AdminRequestMode.SetGlobalImageBandwidth,
            w => w.Put(uploadMegabits),
            w => w.Put(downloadMegabits),
            w => w.Put(enforcementPercent));
    }

    /// <summary>
    /// Server-pushed maximum player count — the mirror of Configuration.PeerLimit. Pushed on connect
    /// and on every admin change, so the admin panel edits the live cap rather than a stale one.
    /// </summary>
    public static int ServerPeerLimit { get; private set; } = ushort.MaxValue;

    /// <summary>Fired when the server pushes a new maximum player count.</summary>
    public static event Action<int> OnPeerLimitChanged;

    private static void HandlePeerLimit(NetDataReader reader)
    {
        ServerPeerLimit = reader.GetInt();
        OnPeerLimitChanged?.Invoke(ServerPeerLimit);
    }

    /// <summary>
    /// Admin: set the maximum number of simultaneously connected players. Persisted to config.xml
    /// and enforced from the next join onward — lowering it past the current population never
    /// disconnects anyone, the server just stops admitting players until it drains under the cap.
    /// </summary>
    public static void SetGlobalPeerLimit(int peerLimit)
    {
        if (peerLimit < 1) peerLimit = 1;
        if (peerLimit > ushort.MaxValue) peerLimit = ushort.MaxValue;
        SendAdminRequest(
            AdminRequestMode.SetGlobalPeerLimit,
            w => w.Put(peerLimit));
    }

    private static void HandleReductionSettings(NetDataReader reader)
    {
        ServerBSRSMillisecondDefaultInterval = reader.GetInt();
        ServerBSRBaseMultiplier = reader.GetInt();
        ServerBSRSIncreaseRate = reader.GetFloat();
        ServerBSRSlowestSendRate = reader.GetFloat();
        ServerHighQualityDistance = reader.GetFloat();
        ServerMediumQualityDistance = reader.GetFloat();
        ServerLowQualityDistance = reader.GetFloat();
        ServerEnableAvatarBundleCompression = reader.GetBool();
        ServerAvatarBundleMinMessages = reader.GetInt();
        ServerAvatarBundleMinBytes = reader.GetInt();
        ServerEnableBSRProfiling = reader.GetBool();
        ServerEnableAvatarBundleZstd = reader.GetBool();
        ServerAvatarBundleZstdDeltaBundles = reader.GetBool();
        ServerAvatarBundleZstdLevel = reader.GetInt();
        ServerAvatarBundleZstdMaxShedTier = reader.GetInt();
        OnReductionSettingsChanged?.Invoke();
    }

    /// <summary>
    /// Admin: set the server avatar-reduction (BSR) tuning. Persisted to config.xml, re-applied live,
    /// and broadcast to every admin panel. SlowestSendRate only affects clients that join afterwards.
    /// </summary>
    /// <remarks>
    /// The four hybrid-codec arguments are optional and default to "leave as-is": null sends back
    /// whatever the server last pushed. The admin panel has no controls for them yet, and a caller
    /// that does not know about a setting must not be able to silently reset it — the wire message
    /// carries the whole block, so omitting a field is not an option at this layer.
    /// </remarks>
    public static void SetGlobalReductionSettings(int defaultIntervalMs, int baseMultiplier, float increaseRate, float slowestSendRate, float highDistance, float mediumDistance, float lowDistance, bool bundleCompression, int bundleMinMessages, int bundleMinBytes, bool profiling,
        bool? bundleZstd = null, bool? bundleZstdDeltas = null, int? bundleZstdLevel = null, int? bundleZstdMaxShedTier = null)
    {
        if (defaultIntervalMs < 1) defaultIntervalMs = 1;
        if (baseMultiplier < 1) baseMultiplier = 1;
        if (increaseRate < 0f) increaseRate = 0f;
        if (slowestSendRate < 0f) slowestSendRate = 0f;
        if (highDistance < 0f) highDistance = 0f;
        if (mediumDistance < 0f) mediumDistance = 0f;
        if (lowDistance < 0f) lowDistance = 0f;
        if (bundleMinMessages < 1) bundleMinMessages = 1;
        if (bundleMinBytes < 0) bundleMinBytes = 0;
        SendAdminRequest(
            AdminRequestMode.SetGlobalReductionSettings,
            w => w.Put(defaultIntervalMs),
            w => w.Put(baseMultiplier),
            w => w.Put(increaseRate),
            w => w.Put(slowestSendRate),
            w => w.Put(highDistance),
            w => w.Put(mediumDistance),
            w => w.Put(lowDistance),
            w => w.Put(bundleCompression),
            w => w.Put(bundleMinMessages),
            w => w.Put(bundleMinBytes),
            w => w.Put(profiling),
            w => w.Put(bundleZstd ?? ServerEnableAvatarBundleZstd),
            w => w.Put(bundleZstdDeltas ?? ServerAvatarBundleZstdDeltaBundles),
            w => w.Put(bundleZstdLevel ?? ServerAvatarBundleZstdLevel),
            w => w.Put(bundleZstdMaxShedTier ?? ServerAvatarBundleZstdMaxShedTier));
    }

    private static void HandleGlobalHeadlessDisallowState(NetDataReader reader)
    {
        GlobalHeadlessDisallowed = reader.GetBool();
        BasisDebug.Log($"Global headless connection policy updated - Headless disallowed: {GlobalHeadlessDisallowed}", BasisDebug.LogTag.Networking);
        OnGlobalHeadlessDisallowStateChanged?.Invoke(GlobalHeadlessDisallowed);
    }

    /// <summary>
    /// Admin: Set headless audio clip playback state for headless clients.
    /// </summary>
    public static void SetGlobalHeadlessAudio(bool headlessAudioOff)
    {
        SendAdminRequest(
            AdminRequestMode.SetGlobalHeadlessAudio,
            w => w.Put(headlessAudioOff));
    }

    /// <summary>
    /// Admin: enable or disable client error/exception reporting server-wide. Persisted to
    /// config.xml and broadcast to every client.
    /// </summary>
    public static void SetGlobalCrashReporting(bool enabled)
    {
        SendAdminRequest(
            AdminRequestMode.SetGlobalCrashReporting,
            w => w.Put(enabled));
    }

    /// <summary>
    /// Admin: Allow or disallow headless clients from remaining connected.
    /// </summary>
    public static void SetGlobalHeadlessDisallow(bool headlessDisallowed)
    {
        SendAdminRequest(
            AdminRequestMode.SetGlobalHeadlessDisallow,
            w => w.Put(headlessDisallowed));
    }

    /// <summary>
    /// Last Opus FEC packet-loss percentage received from the server (0..100).
    /// Admins can change it; every connected client applies the new value to its
    /// local encoder on the fly via <see cref="LocalOpusSettings.SetPacketLossPercent"/>.
    /// </summary>
    public static int GlobalOpusPacketLossPercent { get; private set; } = 10;

    /// <summary>Fired when the server-pushed Opus FEC packet-loss percentage changes.</summary>
    public static event Action<int> OnGlobalOpusPacketLossChanged;

    private static void HandleGlobalOpusPacketLossState(NetDataReader reader)
    {
        int percent = reader.GetByte();
        GlobalOpusPacketLossPercent = percent;
        LocalOpusSettings.SetPacketLossPercent(percent);
        BasisDebug.Log($"Global Opus FEC packet-loss percent updated → {percent}%", BasisDebug.LogTag.Networking);
        OnGlobalOpusPacketLossChanged?.Invoke(percent);
    }

    /// <summary>
    /// Admin: Set the Opus FEC packet-loss percentage (0..100) applied to every
    /// client's encoder. Higher values trade bitrate for better resilience on
    /// lossy networks.
    /// </summary>
    public static void SetGlobalOpusPacketLoss(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        SendAdminRequest(
            AdminRequestMode.SetGlobalOpusPacketLoss,
            w => w.Put((byte)percent));
    }

    /// <summary>
    /// Local Opus bitrate (bps) currently overridden by the server for this client.
    /// 0 means no override — the encoder uses <see cref="LocalOpusSettings.DefaultBitrate"/>.
    /// </summary>
    public static int LocalOpusBitrateOverride => LocalOpusSettings.BitrateOverride;

    /// <summary>Fired when the server pushes a per-user bitrate override to this client.</summary>
    public static event Action<int> OnLocalOpusBitrateOverrideChanged;

    /// <summary>
    /// Fired when a moderator's locomotion override lands on this client. A value with no fields set
    /// means the override was cleared.
    /// </summary>
    public static event Action<BasisLocomotionValues> OnLocomotionOverrideChanged;

    private static void HandleLocomotionOverride(NetDataReader reader)
    {
        ushort initiatorId = reader.GetUShort();
        byte fields = reader.GetByte();
        float jumpHeight = reader.GetFloat();
        float walkSpeed = reader.GetFloat();
        float runSpeed = reader.GetFloat();
        float gravity = reader.GetFloat();
        byte movementMode = reader.GetByte();

        BasisLocomotionOverrides.Remove(BasisLocomotionOverrides.AdminKey);

        BasisLocomotionField applied = (BasisLocomotionField)fields & BasisLocomotionField.All;
        if (applied == BasisLocomotionField.None)
        {
            BasisDebug.Log($"Locomotion override cleared by player {initiatorId}", BasisDebug.LogTag.Networking);
            OnLocomotionOverrideChanged?.Invoke(default);
            return;
        }

        if (movementMode > (byte)BasisLocalCharacterDriver.Mode.NoClip)
        {
            movementMode = (byte)BasisLocalCharacterDriver.Mode.Walk;
        }

        BasisLocomotionValues values = new BasisLocomotionValues
        {
            Fields = applied,
            JumpHeight = jumpHeight,
            WalkSpeed = walkSpeed,
            RunSpeed = runSpeed,
            Gravity = gravity,
            Mode = (BasisLocalCharacterDriver.Mode)movementMode,
        };

        BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, values);
        BasisDebug.Log($"Locomotion override applied by player {initiatorId} ({applied})", BasisDebug.LogTag.Networking);
        OnLocomotionOverrideChanged?.Invoke(values);
    }

    private static void HandleUserOpusBitrateOverride(NetDataReader reader)
    {
        int bps = reader.GetInt();
        LocalOpusSettings.SetBitrateOverride(bps);
        BasisDebug.Log(
            bps > 0
                ? $"Local Opus bitrate override updated → {bps} bps"
                : "Local Opus bitrate override cleared (using default)",
            BasisDebug.LogTag.Networking);
        OnLocalOpusBitrateOverrideChanged?.Invoke(bps);
    }

    /// <summary>
    /// Admin: Override (or clear) a single user's Opus encoder bitrate. Pass 0 to clear.
    /// Targeted by netId (the runtime ushort player id).
    /// </summary>
    public static void SetUserOpusBitrate(ushort targetPlayerId, int bitrateBps)
    {
        if (bitrateBps < 0) bitrateBps = 0;
        SendAdminRequest(
            AdminRequestMode.SetUserOpusBitrate,
            w => w.Put(targetPlayerId),
            w => w.Put(bitrateBps));
    }

    /// <summary>
    /// Last global Opus bitrate (bps) received from the server. 0 means no global
    /// override — clients use their default. Display-only: the encoder value each
    /// client applies arrives per-peer via <see cref="AdminRequestMode.UserOpusBitrateOverride"/>,
    /// where a per-user override wins over this global.
    /// </summary>
    public static int GlobalOpusBitrate { get; private set; }

    /// <summary>Fired when the server-pushed global Opus bitrate changes.</summary>
    public static event Action<int> OnGlobalOpusBitrateChanged;

    private static void HandleGlobalOpusBitrateState(NetDataReader reader)
    {
        int bps = reader.GetInt();
        GlobalOpusBitrate = bps;
        BasisDebug.Log(
            bps > 0
                ? $"Global Opus bitrate updated → {bps} bps"
                : "Global Opus bitrate cleared (clients use their default)",
            BasisDebug.LogTag.Networking);
        OnGlobalOpusBitrateChanged?.Invoke(bps);
    }

    /// <summary>
    /// Admin: Set (or clear with 0) the Opus encoder bitrate every client transmits with.
    /// Per-user overrides set via <see cref="SetUserOpusBitrate"/> still win over this.
    /// </summary>
    public static void SetGlobalOpusBitrate(int bitrateBps)
    {
        if (bitrateBps < 0) bitrateBps = 0;
        SendAdminRequest(
            AdminRequestMode.SetGlobalOpusBitrate,
            w => w.Put(bitrateBps));
    }

    /// <summary>Last Opus frame duration (ms) received from the server. 20 or 40.</summary>
    public static int GlobalOpusFrameDurationMs { get; private set; } = 20;

    /// <summary>Fired when the server-pushed Opus frame duration changes.</summary>
    public static event Action<int> OnGlobalOpusFrameDurationChanged;

    private static void HandleGlobalOpusFrameDurationState(NetDataReader reader)
    {
        int ms = reader.GetByte();
        GlobalOpusFrameDurationMs = ms;
        SharedOpusSettings.SetDesiredDurationInSeconds(ms / 1000f);
        BasisDebug.Log($"Global Opus frame duration updated → {ms} ms", BasisDebug.LogTag.Networking);
        OnGlobalOpusFrameDurationChanged?.Invoke(ms);
    }

    /// <summary>
    /// Admin: Set the global Opus frame duration in milliseconds. Only 20 and 40 are accepted.
    /// </summary>
    public static void SetGlobalOpusFrameDuration(int ms)
    {
        if (ms != 20 && ms != 40)
        {
            BasisDebug.LogError($"SetGlobalOpusFrameDuration rejects {ms} — only 20 or 40 ms are supported.");
            return;
        }
        SendAdminRequest(
            AdminRequestMode.SetGlobalOpusFrameDuration,
            w => w.Put((byte)ms));
    }

    #endregion

    #region Force Avatar

    /// <summary>
    /// Packs a library entry's embedded flags into the single byte the force-avatar payload
    /// carries, so the entry rebuilds on the target the same kind it is here. 0 = plain bee url,
    /// 1 = embedded bee url, 2 = embedded addressable.
    /// </summary>
    public static byte EncodeEmbeddedSource(BasisDataStoreItemKeys.ItemKey item)
    {
        if (item == null || !item.EmbeddedSettings.IsEmbedded)
        {
            return 0;
        }
        return item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable ? (byte)2 : (byte)1;
    }

    private static void HandleForcedAvatar(NetDataReader reader)
    {
        ushort initiatorId = reader.GetUShort();
        string url = reader.GetString();
        string password = reader.GetString();
        byte embeddedSource = reader.GetByte();

        if (string.IsNullOrEmpty(url))
        {
            BasisDebug.LogError("Forced avatar arrived with no url.", BasisDebug.LogTag.Networking);
            return;
        }

        BasisDataStoreItemKeys.EmbeddedSettings embedded = embeddedSource switch
        {
            1 => BasisDataStoreItemKeys.EmbeddedSettings.BEEUrl,
            2 => BasisDataStoreItemKeys.EmbeddedSettings.Addressable,
            _ => BasisDataStoreItemKeys.EmbeddedSettings.Default,
        };

        // An addressable names content already inside this build, so there is no url to vet.
        // Everything else is fetched from the url as written, and that must be http(s).
        bool isAddressable = embedded.IsEmbedded && embedded.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable;
        if (!isAddressable && !Basis.Scripts.Common.BasisUrlSecurity.IsHttpUrlAllowed(url, out string urlReason))
        {
            BasisDebug.LogError($"Refusing forced avatar url: {urlReason}", BasisDebug.LogTag.Networking);
            return;
        }

        BasisDataStoreItemKeys.ItemKey item = new BasisDataStoreItemKeys.ItemKey
        {
            Mode = BundledContentHolder.Mode.Avatar,
            PlacementType = BundledContentHolder.PlacementType.SpawnAtRaycast,
            Url = url,
            Pass = password ?? string.Empty,
            EmbeddedSettings = embedded,
            PinnedSettings = BasisDataStoreItemKeys.PinnedSettings.Default,
        };

        // Named in the popup the target gets, so the fallback has to read as prose rather than as
        // a raw id — the moderator is a remote player here and should always resolve.
        string initiator = BasisNetworkPlayers.Players.TryGetValue(initiatorId, out BasisNetworkPlayer moderator)
            && moderator.Player != null
            && !string.IsNullOrWhiteSpace(moderator.Player.SafeDisplayName)
                ? moderator.Player.SafeDisplayName
                : BasisLocalization.Get("settings.admin.forceAvatar.notice.unknownModerator");

        _ = ApplyForcedAvatar(initiator, item);
    }

    private static async Task ApplyForcedAvatar(string initiator, BasisDataStoreItemKeys.ItemKey item)
    {
        try
        {
            // The moderator picked this out of their own library, so this client may never have
            // seen it. CacheNewItem rather than PreloadMetaDataForItem: the preload path deletes
            // the stored file and drops the key when a bundle fails to parse, and this entry is
            // not in our library for it to be dropping.
            if (!CachedMetaData.ContainsMetaData(item.Url))
            {
                CachedMetaData.CacheNewItemResult result = await CachedMetaData.CacheNewItem(item);
                if (result.Cached == null)
                {
                    BasisDebug.LogError(
                        $"Forced avatar '{item.Url}' could not be fetched{(result.IsTransient ? " (remote unreachable)" : string.Empty)}.",
                        BasisDebug.LogTag.Networking);
                    return;
                }
                CachedMetaData.SetMetaData(item.Url, result.Cached);
            }

            await ContentLoader.LoadAvatar(item);

            // Popped after the load, not before: the message names an avatar the wearer is already
            // wearing, so it can't announce a swap that then fails to happen. DisplayMessage is the
            // same popup every other moderator action uses, and logs itself to the notification
            // history on dismiss.
            DisplayMessage(BasisLocalization.Get("settings.admin.forceAvatar.notice.body", initiator));
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Forced avatar '{item.Url}' failed to load: {ex.Message}", BasisDebug.LogTag.Networking);
        }
    }

    #endregion

    public static bool TryTeleportToPlayer(ushort netId)
    {
        if (BasisNetworkPlayers.Players.TryGetValue(netId, out var player) && ValidateForAnimator(player))
        {
            Transform hips = player.Player.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null)
            {
                BasisDebug.LogError($"Teleport failed: Avatar has no Hips bone for player {netId}");
                return false;
            }
            BasisLocalPlayer.Instance.Teleport(hips.position, Quaternion.identity, mode: BasisTeleportMode.WorldFeet);
            return true;
        }

        BasisDebug.LogError($"Teleport failed: Invalid or missing player for ID {netId}");
        return false;
    }
}
