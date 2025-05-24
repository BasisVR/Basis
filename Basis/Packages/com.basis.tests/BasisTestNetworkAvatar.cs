using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using LiteNetLib;
using UnityEngine;
public class BasisTestNetworkAvatar : MonoBehaviour
{
    [Header("Assign Ahead Of Time")]
    public BasisAvatar avatar;
    public byte MessageIndexTest;
    public byte[] SubmittingData;
    public ushort[] Recipients = null;
    public BasisPlayer BasisPlayer;
    public void Awake()
    {
        avatar.OnAvatarReady += OnAvatarReady;
    }
    private void OnAvatarReady(bool IsOwner)
    {
        Debug.Log("was called!");
        if (BasisNetworkManagement.LocalPlayerIsConnected == false)
        {
            BasisNetworkManagement.OnLocalPlayerJoined += OnLocalPlayerJoined;
        }
        else
        {
            SetupIfLocal();
        }
        //recieve messages
        avatar.OnNetworkMessageReceived += OnNetworkMessageReceived;
    }
    private void OnLocalPlayerJoined(BasisNetworkPlayer player1, BasisLocalPlayer player2)
    {
        SetupIfLocal();
    }
    public void OnDestroy()
    {
        BasisNetworkManagement.OnLocalPlayerJoined -= OnLocalPlayerJoined;
    }
    private void SetupIfLocal()
    {
        if (BasisNetworkManagement.AvatarToPlayer(avatar, out BasisPlayer))
        {
            if (BasisPlayer.IsLocal)
            {
                InvokeRepeating(nameof(LoopSend), 0, 1);//local avatar lets start sending data!
            }
        }
    }
    public void LoopSend()
    {
        Debug.Log("Sening Loop Data");
        avatar.NetworkMessageSend(MessageIndexTest, SubmittingData, DeliveryMethod.Unreliable, Recipients);
    }

    private void OnNetworkMessageReceived(ushort PlayerID, byte MessageIndex, byte[] buffer, DeliveryMethod Method = DeliveryMethod.ReliableSequenced)
    {
        //  Debug.Log("OnNetworkMessageReceived from player " + PlayerID + " with message Index " + MessageIndex + " | " + buffer.Length);
    }
}
