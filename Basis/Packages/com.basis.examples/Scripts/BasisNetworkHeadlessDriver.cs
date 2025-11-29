using Basis;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.Headless;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class BasisNetworkHeadlessDriver : BasisNetworkBehaviour
{
    private enum MessageType : byte
    {
        AssignIndex = 0,   // payload: ushort index
        StartJumping = 1,   // no payload
        StopJumping = 2,
        PauseMove = 3,   // no payload
        UnpauseMove = 4    // no payload
    }

    [Header("Spawn / Teleport Targets")]
    public Transform[] transforms;
    [HideInInspector]
    public BasisLoadableBundle[] GeneratedRandomizedAvatars;

    [Header("Server-assigned index counter (wraps by transforms.Length)")]
    public ushort CurrentIndex;

    public BasisLoadableBundle[] BaseData;

    public void Awake()
    {
        if (transforms == null || transforms.Length == 0)
        {
            BasisDebug.LogWarning("[HeadlessDriver] No transforms configured; cannot generate avatars.", BasisDebug.LogTag.Remote);
            return;
        }

        if (BaseData == null || BaseData.Length == 0)
        {
            BasisDebug.LogWarning("[HeadlessDriver] No base avatar data provided; cannot generate avatars.", BasisDebug.LogTag.Remote);
            return;
        }

        GeneratedRandomizedAvatars = new BasisLoadableBundle[transforms.Length];

        System.Random rng = new System.Random();
        for (int Index = 0; Index < GeneratedRandomizedAvatars.Length; Index++)
        {
            int randomIndex = rng.Next(BaseData.Length);
            BasisLoadableBundle baseInfo = BaseData[randomIndex];
            GeneratedRandomizedAvatars[Index] = baseInfo;
        }

        BasisDebug.Log($"[HeadlessDriver] Generated {GeneratedRandomizedAvatars.Length} randomized avatars.", BasisDebug.LogTag.Remote);
    }
#if !UNITY_SERVER
    public void Update()
    {
        if (IsLocalOwner())
        {
            if (Keyboard.current.f1Key.wasPressedThisFrame)
            {
                SendJump();
                BasisDebug.Log($"[HeadlessDriver] SendJump", BasisDebug.LogTag.Remote);
            }
            if (Keyboard.current.f2Key.wasPressedThisFrame)
            {
                StopJump();
                BasisDebug.Log($"[HeadlessDriver] StopJump", BasisDebug.LogTag.Remote);
            }
            if (Keyboard.current.f3Key.wasPressedThisFrame)
            {
                SendUnpauseMovement();
                BasisDebug.Log($"[HeadlessDriver] SendUnpauseMovement", BasisDebug.LogTag.Remote);
            }
            if (Keyboard.current.f4Key.wasPressedThisFrame)
            {
                SendPauseMovement();
                BasisDebug.Log($"[HeadlessDriver] SendPauseMovement", BasisDebug.LogTag.Remote);
            }
        }
    }
#endif
    public override void OnPlayerJoined(BasisNetworkPlayer player)
    {
#if !UNITY_SERVER
        if (IsLocalOwner())
        {
            if (transforms == null || transforms.Length == 0)
            {
                BasisDebug.LogWarning("[HeadlessDriver] No transforms configured; cannot assign indices.", BasisDebug.LogTag.Remote);
                return;
            }

            // Assign an index for this player (wrap to stay within bounds)
            ushort assigned = (ushort)(CurrentIndex % transforms.Length);
            CurrentIndex++;

            // Send: [msgId=0][ushort index]
            byte[] msg = BuildAssignIndexMsg(assigned);
            SendCustomNetworkEvent(msg, DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });

            BasisDebug.Log($"[HeadlessDriver] Player {player.playerId} joined; assigned index {assigned}.", BasisDebug.LogTag.Remote);
        }
#endif
    }

    // ====== Public send helpers you can call from anywhere (UI, input, server logic) ======
    public void SendJump(ushort[] targets = null) => SendCustomNetworkEvent(new byte[] { (byte)MessageType.StartJumping }, DeliveryMethod.ReliableSequenced, targets);
    public void StopJump(ushort[] targets = null) => SendCustomNetworkEvent(new byte[] { (byte)MessageType.StopJumping }, DeliveryMethod.ReliableSequenced, targets);
    public void SendPauseMovement(ushort[] targets = null) => SendCustomNetworkEvent(new byte[] { (byte)MessageType.PauseMove }, DeliveryMethod.ReliableSequenced, targets);

    public void SendUnpauseMovement(ushort[] targets = null) => SendCustomNetworkEvent(new byte[] { (byte)MessageType.UnpauseMove }, DeliveryMethod.ReliableSequenced, targets);

    // Build: [msgId=0][ushort index]
    private static byte[] BuildAssignIndexMsg(ushort index)
    {
        byte[] bytes = new byte[1 + sizeof(ushort)];
        bytes[0] = (byte)MessageType.AssignIndex;
        byte[] idx = BitConverter.GetBytes(index);
        // BitConverter is little-endian on most platforms; both ends use same method
        bytes[1] = idx[0];
        bytes[2] = idx[1];
        return bytes;
    }

    public override async void OnNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
    {
        if (buffer == null || buffer.Length == 0)
        {
            BasisDebug.LogWarning($"[HeadlessDriver] Bad message from {playerID}: empty payload.", BasisDebug.LogTag.Remote);
            return;
        }

        MessageType type = (MessageType)buffer[0];

        switch (type)
        {
            case MessageType.AssignIndex:
                {
                    // Expecting [msgId][ushort index]
                    if (buffer.Length < 1 + sizeof(ushort))
                    {
                        BasisDebug.LogWarning($"[HeadlessDriver] Bad AssignIndex from {playerID}: payload too small.", BasisDebug.LogTag.Remote);
                        return;
                    }

                    ushort index = BitConverter.ToUInt16(buffer, 1);

                    if (transforms == null || transforms.Length == 0)
                    {
                        BasisDebug.LogWarning("[HeadlessDriver] No transforms configured; cannot teleport.", BasisDebug.LogTag.Remote);
                        return;
                    }

                    if (index >= transforms.Length)
                    {
                        BasisDebug.LogWarning($"[HeadlessDriver] Player {playerID} requested out-of-range index {index}.", BasisDebug.LogTag.Remote);
                        return;
                    }

                    Transform target = transforms[index];
                    if (target == null)
                    {
                        BasisDebug.LogWarning($"[HeadlessDriver] Transform at index {index} is null.", BasisDebug.LogTag.Remote);
                        return;
                    }

                    var data = GeneratedRandomizedAvatars[index];
                    target.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                    HandlePauseMovement(playerID);
                    BasisLocalPlayer.Instance.Teleport(Position, Rotation);
                    await BasisLocalPlayer.Instance.CreateAvatar(0, data);

                    BasisDebug.Log($"[HeadlessDriver] Teleported player {playerID} to transform[{index}] at {Position}.", BasisDebug.LogTag.Remote);
                    break;
                }

            case MessageType.StartJumping:
                {
                    StartJumping(playerID);
                    break;
                }
            case MessageType.StopJumping:
                {
                    StopJump(playerID);
                    break;
                }
            case MessageType.PauseMove:
                {
                    HandlePauseMovement(playerID);
                    break;
                }

            case MessageType.UnpauseMove:
                {
                    HandleUnpauseMovement(playerID);
                    break;
                }

            default:
                {
                    BasisDebug.LogWarning($"[HeadlessDriver] Unknown msg type {(byte)type} from {playerID}.", BasisDebug.LogTag.Remote);
                    break;
                }
        }
    }
    private void StartJumping(ushort playerID)
    {
        if (BasisHeadlessInput.Instance != null)
        {
            BasisHeadlessInput.Instance.ForceJump = true;
        }
        BasisDebug.Log($"[HeadlessDriver] Jump RPC from {playerID}.", BasisDebug.LogTag.Remote);
    }
    private void StopJump(ushort playerID)
    {
        if (BasisHeadlessInput.Instance != null)
        {
            BasisHeadlessInput.Instance.ForceJump = false;
        }
        BasisDebug.Log($"[HeadlessDriver] Jump RPC from {playerID}.", BasisDebug.LogTag.Remote);
    }
    private void HandlePauseMovement(ushort playerID)
    {
        if (BasisHeadlessInput.Instance != null)
        {
            BasisHeadlessInput.Instance.StopMovement();
        }
        BasisDebug.Log($"[HeadlessDriver] PauseMovement RPC from {playerID}.", BasisDebug.LogTag.Remote);
    }

    private void HandleUnpauseMovement(ushort playerID)
    {
        if (BasisHeadlessInput.Instance != null)
        {
            BasisHeadlessInput.Instance.ResumeMovement();
        }
        BasisDebug.Log($"[HeadlessDriver] UnpauseMovement RPC from {playerID}.", BasisDebug.LogTag.Remote);
    }
}
