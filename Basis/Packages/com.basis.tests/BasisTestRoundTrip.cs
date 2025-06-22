using Basis.Scripts.BasisSdk;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking;
using LiteNetLib;
using UnityEngine;
using UnityEngine.XR;

public class BasisTestRoundTrip : BasisAvatarMonoBehaviour
{
    public LiteNetLib.DeliveryMethod DeliveryMethod;
    void Awake()
    {
        Avatar.OnAvatarReady += OnAvatarReady;
        Avatar.OnAvatarNetworkReady += OnAvatarNetworkReady;
    }
    public void OnDestroy()
    {
        Avatar.OnAvatarReady -= OnAvatarReady;
        Avatar.OnAvatarNetworkReady -= OnAvatarNetworkReady;
    }

    private void OnAvatarNetworkReady(bool IsOwner)
    {
        if (IsOwner == false)
        {
            Debug.Log("I was not the owner");
            NetworkMessageSend(DeliveryMethod);
        }
        else
        {
            NetworkMessageSend(DeliveryMethod);
            Debug.Log("I was the owner of this avatars");
        }
    }
    private void OnAvatarReady(bool IsOwner)
    {
        Debug.Log("OnAvatarReady " + IsOwner);
    }

    public override void OnNetworkChange(byte messageIndex, bool IsLocallyOwned)
    {

    }

    public override void OnNetworkMessageReceived(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        // Null checks for buffer and Recipients arrays
        if (buffer == null)
        {
            if (BasisNetworkManagement.Players.TryGetValue(PlayerID, out Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer netPlayer))
            {
                if (netPlayer.Player.IsLocal)
                {
                    Debug.LogError($"Buffer is null. MessageIndex: {MessageIndex}, Was a loop back: {PlayerID}");
                }
                else
                {
                    Debug.LogError($"Buffer is null. MessageIndex: {MessageIndex}, Was from remote player: {PlayerID}");
                }
            }
            else
            {
                Debug.LogError($"Buffer is null. MessageIndex: {MessageIndex}, Was from unknown remote player: {PlayerID}");
            }
            return;
        }

        // Try to get the player from the network management system
        if (BasisNetworkManagement.Players.TryGetValue(PlayerID, out Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer Player))
        {
            Debug.Log($"Rec Avatar Message from player {Player.Player.DisplayName}, MessageIndex: {MessageIndex}, Buffer Length: {buffer.Length}, Recipients Count: {DeliveryMethod}");
        }
        else
        {
            Debug.Log($"Player ID {PlayerID} not found. MessageIndex: {MessageIndex}, Buffer Length: {buffer.Length}, Recipients Count: {DeliveryMethod}");
        }
    }

    public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
    {
        throw new System.NotImplementedException();
    }
}
