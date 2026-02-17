using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
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
        if (ValidateString(uuid, nameof(uuid)) && ValidateString(reason, nameof(reason)))
        {
            SendAdminRequest(AdminRequestMode.Ban,
                w => w.Put(uuid),
                w => w.Put(reason));
        }
    }

    public static void SendIPBan(string uuid, string reason)
    {
        if (ValidateString(uuid, nameof(uuid)) && ValidateString(reason, nameof(reason)))
        {
            SendAdminRequest(AdminRequestMode.IpAndBan,
                w => w.Put(uuid),
                w => w.Put(reason));
        }
    }

    public static void SendKick(string uuid, string reason)
    {
        if (ValidateString(uuid, nameof(uuid)) && ValidateString(reason, nameof(reason)))
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

    public static void AddAdmin(string uuid)
    {
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.AddAdmin, w => w.Put(uuid));
        }
    }

    public static void RemoveAdmin(string uuid)
    {
        if (ValidateString(uuid, nameof(uuid)))
        {
            SendAdminRequest(AdminRequestMode.RemoveAdmin, w => w.Put(uuid));
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

            default:
                BasisDebug.LogError($"Unhandled admin command: {mode}", BasisDebug.LogTag.Networking);
                break;
        }
    }
    public static bool TryTeleportToPlayer(ushort netId)
    {
        if (BasisNetworkPlayers.Players.TryGetValue(netId, out var player) && ValidateForAnimator(player))
        {
            Transform hips = player.Player.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
            BasisLocalPlayer.Instance.Teleport(hips.position, Quaternion.identity);
            return true;
        }

        BasisDebug.LogError($"Teleport failed: Invalid or missing player for ID {netId}");
        return false;
    }
}
