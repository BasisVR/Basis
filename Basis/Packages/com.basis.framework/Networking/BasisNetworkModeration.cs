using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using UnityEditor;
using UnityEngine;
using static BasisNetworkCore.Serializable.SerializableBasis;

public static class BasisNetworkModeration
{
    private static void ValidateString(string param, string paramName)
    {
        if (string.IsNullOrEmpty(param))
            throw new ArgumentException($"{paramName} cannot be null or empty", paramName);
    }

    private static void SendAdminRequest(AdminRequestMode mode, params Action<NetDataWriter>[] dataWriters)
    {
        var writer = new NetDataWriter();
        new AdminRequest().Serialize(writer, mode);
        foreach (var write in dataWriters)
            write(writer);

        BasisNetworkManagement.LocalPlayerPeer.Send(
            writer,
            BasisNetworkCommons.AdminChannel,
            DeliveryMethod.ReliableSequenced
        );
    }

    public static void SendBan(string uuid, string reason)
    {
        ValidateString(uuid, nameof(uuid));
        ValidateString(reason, nameof(reason));

        SendAdminRequest(AdminRequestMode.Ban,
            w => w.Put(uuid),
            w => w.Put(reason));
    }

    public static void SendIPBan(string uuid, string reason)
    {
        ValidateString(uuid, nameof(uuid));
        ValidateString(reason, nameof(reason));

        SendAdminRequest(AdminRequestMode.IpAndBan,
            w => w.Put(uuid),
            w => w.Put(reason));
    }

    public static void SendKick(string uuid, string reason)
    {
        ValidateString(uuid, nameof(uuid));
        ValidateString(reason, nameof(reason));

        SendAdminRequest(AdminRequestMode.Kick,
            w => w.Put(uuid),
            w => w.Put(reason));
    }

    public static void UnBan(string uuid)
    {
        ValidateString(uuid, nameof(uuid));
        SendAdminRequest(AdminRequestMode.UnBan, w => w.Put(uuid));
    }

    public static void UnIpBan(string uuid)
    {
        ValidateString(uuid, nameof(uuid));
        SendAdminRequest(AdminRequestMode.UnBanIP, w => w.Put(uuid));
    }

    public static void AddAdmin(string uuid)
    {
        ValidateString(uuid, nameof(uuid));
        SendAdminRequest(AdminRequestMode.AddAdmin, w => w.Put(uuid));
    }

    public static void RemoveAdmin(string uuid)
    {
        ValidateString(uuid, nameof(uuid));
        SendAdminRequest(AdminRequestMode.RemoveAdmin, w => w.Put(uuid));
    }

    public static void SendMessage(ushort uuid, string message)
    {
        ValidateString(message, nameof(message));
        SendAdminRequest(AdminRequestMode.Message,
            w => w.Put(uuid),
            w => w.Put(message));
    }

    public static void SendMessageAll(string message)
    {
        ValidateString(message, nameof(message));
        SendAdminRequest(AdminRequestMode.MessageAll, w => w.Put(message));
    }

    public static void TeleportAll(ushort destinationPlayerId)
    {
        SendAdminRequest(AdminRequestMode.TeleportAll, w => w.Put(destinationPlayerId));
    }

    public static void TeleportHere(ushort uuid)
    {
        SendAdminRequest(AdminRequestMode.TeleportPlayer, w => w.Put(uuid));
    }

    public static void DisplayMessage(string message)
    {
        ValidateString(message, nameof(message));
        BasisUINotification.OpenNotification(message, false, Vector3.zero);
    }

    public static void TeleportTo(ushort netId)
    {
        if (BasisNetworkManagement.Players.TryGetValue(netId, out var player) &&
            player?.Player?.BasisAvatar?.Animator != null)
        {
            Transform hips = player.Player.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
            BasisLocalPlayer.Instance.Teleport(hips.position, hips.rotation);
        }
        else
        {
            BasisDebug.LogError($"Missing Teleport To Player or invalid player ID: {netId}");
        }
    }

    public static void AdminMessage(NetDataReader reader)
    {
        var request = new AdminRequest();
        request.Deserialize(reader);
        var mode = request.GetAdminRequestMode();

        switch (mode)
        {
            case AdminRequestMode.Message:
            case AdminRequestMode.MessageAll:
                DisplayMessage(reader.GetString());
                break;

            case AdminRequestMode.TeleportPlayer:
            case AdminRequestMode.TeleportAll:
                ushort playerId = reader.GetUShort();
                if (BasisNetworkManagement.Players.TryGetValue(playerId, out var player) &&
                    player?.Player?.BasisAvatar?.Animator != null)
                {
                    Transform trans = player.Player.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
                    BasisLocalPlayer.Instance.Teleport(trans.position, Quaternion.identity);
                }
                else
                {
                    BasisDebug.LogError($"Teleport failed: Invalid or missing player for ID {playerId}");
                }
                break;

            default:
                BasisDebug.LogError($"Unhandled admin command: {mode}", BasisDebug.LogTag.Networking);
                break;
        }
    }
}
