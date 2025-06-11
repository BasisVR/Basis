using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using LiteNetLib;
using UnityEngine;
public class BasisTestNetworkAvatar : BasisAvatarMonoBehaviour
{
    public byte[] SubmittingData;
    public ushort[] Recipients = null;
    public BasisPlayer BasisPlayer;
    public void Awake()
    {
        Avatar.OnAvatarReady += OnAvatarReady;
    }
    private void OnAvatarReady(bool IsOwner)
    {
        Debug.Log("was called!");
        if (BasisNetworkManagement.LocalPlayerIsConnected == false)
        {
            BasisNetworkPlayer.OnLocalPlayerJoined += OnLocalPlayerJoined;
        }
        else
        {
            SetupIfLocal();
        }
    }
    private void OnLocalPlayerJoined(BasisNetworkPlayer player1, BasisLocalPlayer player2)
    {
        SetupIfLocal();
    }
    public void OnDestroy()
    {
        BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalPlayerJoined;
    }
    private void SetupIfLocal()
    {
        if (BasisNetworkManagement.AvatarToPlayer(Avatar, out BasisPlayer))
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
        NetworkMessageSend(SubmittingData, DeliveryMethod.Unreliable, Recipients);
    }

    public override void OnNetworkChange(byte messageIndex, bool IsLocallyOwned)
    {
    }

    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
    }

    public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
    {
    }
}
