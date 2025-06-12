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
        {
            throw new ArgumentException($"{paramName} cannot be null or empty", paramName);
        }
    }

    public static void SendBan(string UUID, string Reason)
    {
        ValidateString(UUID, nameof(UUID));
        ValidateString(Reason, nameof(Reason));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.Ban);
        netDataWriter.Put(UUID);
        netDataWriter.Put(Reason);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }
    public static void TeleportAll(ushort DestinationPlayer)
    {
        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.TeleportAll);
        netDataWriter.Put(DestinationPlayer);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);

    }
    public static void SendIPBan(string UUID, string Reason)
    {
        ValidateString(UUID, nameof(UUID));
        ValidateString(Reason, nameof(Reason));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.IpAndBan);
        netDataWriter.Put(UUID);
        netDataWriter.Put(Reason);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }

    public static void SendKick(string UUID, string Reason)
    {
        ValidateString(UUID, nameof(UUID));
        ValidateString(Reason, nameof(Reason));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.Kick);
        netDataWriter.Put(UUID);
        netDataWriter.Put(Reason);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }

    public static void UnBan(string UUID)
    {
        ValidateString(UUID, nameof(UUID));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.UnBan);
        netDataWriter.Put(UUID);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }

    public static void UnIpBan(string UUID)
    {
        ValidateString(UUID, nameof(UUID));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.UnBanIP);
        netDataWriter.Put(UUID);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }

    public static void AddAdmin(string UUID)
    {
        ValidateString(UUID, nameof(UUID));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.AddAdmin);
        netDataWriter.Put(UUID);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }

    public static void RemoveAdmin(string UUID)
    {
        ValidateString(UUID, nameof(UUID));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.RemoveAdmin);
        netDataWriter.Put(UUID);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }
    public static void SendMessage(ushort UUID, string Message)
    {
        ValidateString(Message, nameof(Message));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.Message);
        netDataWriter.Put(UUID);
        netDataWriter.Put(Message);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }
    public static void SendMessageAll(string Message)
    {
        ValidateString(Message, nameof(Message));

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.MessageAll);
        netDataWriter.Put(Message);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }
    public static void DisplayMessage(string Message)
    {
        ValidateString(Message, nameof(Message));

        BasisUINotification.OpenNotification(Message, false, Vector3.zero);
    }
    public static void TeleportTo(ushort netId)
    {
        if (BasisNetworkManagement.Players.TryGetValue(netId, out var player))
        {
            if (player.Player != null && player.Player.BasisAvatar != null && player.Player.BasisAvatar.Animator != null)
            {
                Transform Trans = player.Player.BasisAvatar.Animator.GetBoneTransform(UnityEngine.HumanBodyBones.Hips);
                BasisLocalPlayer.Instance.Teleport(Trans.position, Trans.rotation);
            }
            else
            {
                BasisDebug.LogError("Missing Teleport To Player ");
            }
        }
        else
        {
            BasisDebug.LogError("Missing " + netId);
        }
    }
    public static void TeleportHere(ushort UUID)
    {

        AdminRequest AdminRequest = new AdminRequest();
        NetDataWriter netDataWriter = new NetDataWriter();
        AdminRequest.Serialize(netDataWriter, AdminRequestMode.TeleportPlayer);
        netDataWriter.Put(UUID);
        BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AdminMessage, DeliveryMethod.ReliableSequenced);
    }
    public static void AdminMessage(NetDataReader reader)
    {
        AdminRequest AdminRequest = new AdminRequest();
        AdminRequest.Deserialize(reader);
        AdminRequestMode Mode = AdminRequest.GetAdminRequestMode();
        switch (Mode)
        {
            // case AdminRequestMode.Ban:
            //    break;
            // case AdminRequestMode.Kick:
            //    break;
            //   case AdminRequestMode.IpAndBan:
            //     break;
            case AdminRequestMode.Message:
                DisplayMessage(reader.GetString());
                break;
            case AdminRequestMode.MessageAll:
                DisplayMessage(reader.GetString());
                break;
            // case AdminRequestMode.UnBanIP:
            //    break;
            //  case AdminRequestMode.UnBan:
            //   break;
            //  case AdminRequestMode.RequestBannedPlayers:
            //      break;
             case AdminRequestMode.TeleportPlayer:
                ushort PlayerID = reader.GetUShort();
                if (BasisNetworkManagement.Players.TryGetValue(PlayerID, out Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer player))
                {
                    if (player.Player != null && player.Player.BasisAvatar != null && player.Player.BasisAvatar.Animator != null)
                    {
                        Transform Trans = player.Player.BasisAvatar.Animator.GetBoneTransform(UnityEngine.HumanBodyBones.Hips);
                        BasisLocalPlayer.Instance.Teleport(Trans.position, Quaternion.identity);
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Teleport To Player ");
                    }
                }
                else
                {
                    BasisDebug.LogError("Trying to teleport to null player for id " + PlayerID);
                }
                break;
            case AdminRequestMode.TeleportAll:
                PlayerID = reader.GetUShort();
                if (BasisNetworkManagement.Players.TryGetValue(PlayerID, out player))
                {
                    if (player.Player != null && player.Player.BasisAvatar != null && player.Player.BasisAvatar.Animator != null)
                    {
                        Transform Trans = player.Player.BasisAvatar.Animator.GetBoneTransform(UnityEngine.HumanBodyBones.Hips);
                        BasisLocalPlayer.Instance.Teleport(Trans.position, Quaternion.identity);
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Teleport To Player ");
                    }
                }
                else
                {
                    BasisDebug.LogError("Trying to teleport to null player for id " + PlayerID);
                }
                break;
            //  case AdminRequestMode.AddAdmin:
            //   break;
            //   case AdminRequestMode.RemoveAdmin:
            //  break;
            default:
                BasisDebug.LogError("Missing Command " + Mode.ToString(), BasisDebug.LogTag.Networking);
                break;
        }
    }
}
