using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
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

    public static void DisplayMessage(string message)
    {
        if (ValidateString(message, nameof(message)))
        {
            BasisMainMenu.Close();
            BasisMainMenu.Open();
            BasisMainMenu.Instance.OpenDialogue("admin", message, "ok", value => { });
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
    /// Fired when the global lock state changes. Parameters: avatarsLocked, propsLocked, worldsLocked.
    /// </summary>
    public static event Action<bool, bool, bool> OnGlobalLockStateChanged;

    private static void HandleGlobalLockState(NetDataReader reader)
    {
        GlobalAvatarsLocked = reader.GetBool();
        GlobalPropsLocked = reader.GetBool();
        GlobalWorldsLocked = reader.GetBool();
        BasisDebug.Log($"Global lock state updated - Avatars: {GlobalAvatarsLocked}, Props: {GlobalPropsLocked}, Worlds: {GlobalWorldsLocked}", BasisDebug.LogTag.Networking);
        OnGlobalLockStateChanged?.Invoke(GlobalAvatarsLocked, GlobalPropsLocked, GlobalWorldsLocked);
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
