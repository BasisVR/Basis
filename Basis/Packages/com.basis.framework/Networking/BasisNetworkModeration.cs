using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using BasisNetworkCore.Security;
using System;
using System.Collections.Generic;
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

    // ── Server config / whitelist (admin) ────────────────────────────────────
    // Each of these triggers a server-side write to config/config.xml or
    // BasisWhiteList.txt so the change is durable across restarts.

    public static void SetServerName(string name)
    {
        SendAdminRequest(AdminRequestMode.SetServerName, w => w.Put(name ?? string.Empty));
    }

    public static void SetServerMotd(string motd)
    {
        SendAdminRequest(AdminRequestMode.SetServerMotd, w => w.Put(motd ?? string.Empty));
    }

    public static void SetWhitelistMode(BasisUserRestrictionMode mode)
    {
        SendAdminRequest(AdminRequestMode.SetWhitelistMode, w => w.Put((byte)mode));
    }

    public static void AddWhitelist(string uuid)
    {
        if (!ValidateString(uuid, nameof(uuid))) return;
        SendAdminRequest(AdminRequestMode.AddWhitelist, w => w.Put(uuid));
    }

    public static void RemoveWhitelist(string uuid)
    {
        if (!ValidateString(uuid, nameof(uuid))) return;
        SendAdminRequest(AdminRequestMode.RemoveWhitelist, w => w.Put(uuid));
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

            BasisMainMenu.Instance.OpenDialogue("admin", message, "ok", value =>
            {
                // If we opened the menu solely to show this popup, close it again on dismiss.
                if (!menuWasAlreadyOpen)
                {
                    BasisMainMenu.Close();
                }
            });
            BasisDebug.LogError(message);
        }
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

            case AdminRequestMode.EnableShoutMode:
            case AdminRequestMode.DisableShoutMode:
                HandleShoutModeChanged(reader, mode == AdminRequestMode.EnableShoutMode);
                break;

            case AdminRequestMode.GlobalGetLockState:
                HandleGlobalLockState(reader);
                break;

            case AdminRequestMode.GlobalGetHeadlessAudioState:
                HandleGlobalHeadlessAudioState(reader);
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

            case AdminRequestMode.GlobalGetOpusFrameDurationState:
                HandleGlobalOpusFrameDurationState(reader);
                break;

            default:
                BasisDebug.LogError($"Unhandled admin command: {mode}", BasisDebug.LogTag.Networking);
                break;
        }
    }

    #region Shout Mode

    /// <summary>
    /// Fired when a player's shout mode state changes.
    /// </summary>
    public static event Action<ushort, bool> OnShoutModeChanged;

    /// <summary>
    /// True if the local player is currently in shout mode.
    /// </summary>
    public static bool LocalPlayerInShoutMode => Basis.Scripts.Networking.Transmitters.BasisAudioTransmission.IsInShoutMode;

    private static void HandleShoutModeChanged(NetDataReader reader, bool enabled)
    {
        ushort targetPlayerId = reader.GetUShort();
        string state = enabled ? "enabled" : "disabled";
        BasisDebug.Log($"Shout mode {state} for player {targetPlayerId}", BasisDebug.LogTag.Networking);

        // Check if this is the local player
        bool isLocalPlayer = BasisNetworkPlayer.LocalPlayer != null && targetPlayerId == BasisNetworkPlayer.LocalPlayer.playerId;
        if (isLocalPlayer)
        {
            // Set the local transmission channel
            Basis.Scripts.Networking.Transmitters.BasisAudioTransmission.IsInShoutMode = enabled;
            BasisDebug.Log($"Local player shout mode {state}", BasisDebug.LogTag.Networking);

            // Notify the local player with a visible dialogue
            if (enabled)
            {
                DisplayMessage("Shout mode ENABLED - your voice is now broadcast to everyone.");
            }
            else
            {
                DisplayMessage("Shout mode DISABLED - your voice is back to normal.");
            }
        }
        else
        {
            // For remote players, manage the global shout audio source
            if (enabled)
            {
                BasisShoutAudioDriver.EnableShoutMode(targetPlayerId);
            }
            else
            {
                BasisShoutAudioDriver.DisableShoutMode(targetPlayerId);
            }
        }

        OnShoutModeChanged?.Invoke(targetPlayerId, enabled);
    }

    /// <summary>
    /// Admin: Enable shout mode for a player (non-spatialized broadcast voice).
    /// </summary>
    public static void EnableShoutMode(ushort playerId)
    {
        SendAdminRequest(AdminRequestMode.EnableShoutMode,
            w => w.Put(playerId));
    }

    /// <summary>
    /// Admin: Disable shout mode for a player.
    /// </summary>
    public static void DisableShoutMode(ushort playerId)
    {
        SendAdminRequest(AdminRequestMode.DisableShoutMode,
            w => w.Put(playerId));
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
    /// Fired when the global lock state changes. Parameters: avatarsLocked, propsLocked, worldsLocked, serversLocked.
    /// </summary>
    public static event Action<bool, bool, bool, bool> OnGlobalLockStateChanged;

    /// <summary>
    /// Fired when the third-person camera lockout flag changes. Separate from
    /// <see cref="OnGlobalLockStateChanged"/> so existing 4-arg subscribers keep compiling.
    /// </summary>
    public static event Action<bool> OnGlobalThirdPersonDisabledChanged;

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
        BasisDebug.Log($"Global lock state updated - Avatars: {GlobalAvatarsLocked}, Props: {GlobalPropsLocked}, Worlds: {GlobalWorldsLocked}, Servers: {GlobalServersLocked}, ThirdPerson: {GlobalThirdPersonDisabled}", BasisDebug.LogTag.Networking);
        OnGlobalLockStateChanged?.Invoke(GlobalAvatarsLocked, GlobalPropsLocked, GlobalWorldsLocked, GlobalServersLocked);
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

    private static void HandleGlobalHeadlessAudioState(NetDataReader reader)
    {
        GlobalHeadlessAudioOff = reader.GetBool();
        BasisDebug.Log($"Global headless audio state updated - Headless audio off: {GlobalHeadlessAudioOff}", BasisDebug.LogTag.Networking);
        OnGlobalHeadlessAudioStateChanged?.Invoke(GlobalHeadlessAudioOff);
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
            BasisLocalPlayer.Instance.Teleport(hips.position, Quaternion.identity);
            return true;
        }

        BasisDebug.LogError($"Teleport failed: Invalid or missing player for ID {netId}");
        return false;
    }
}
