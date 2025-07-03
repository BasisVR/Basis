using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking;
using LiteNetLib;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisTestNetworkAvatarOverrideJump : BasisAvatarMonoBehaviour
{
    public ushort[] Recipients = null;
    public BasisPlayer BasisPlayer;
    public bool Isready;
    public byte[] Buffer;
    public DeliveryMethod Method = DeliveryMethod.Unreliable;
    public void Awake()
    {
        Avatar.OnAvatarReady += OnAvatarReady;
    }
    public void OnDestroy()
    {
        Avatar.OnAvatarReady -= OnAvatarReady;
    }
    private void OnAvatarReady(bool IsOwner)
    {
        Debug.Log("OnAvatarReady");
        if (IsOwner)
        {
            if (BasisNetworkManagement.AvatarToPlayer(Avatar, out BasisPlayer))
            {
                Isready = true;
                Debug.Log("Actually ran!");
            }
        }
    }

    public void Update()
    {
        if (Isready)
        {
            if (Keyboard.current[Key.Space].wasPressedThisFrame)
            {
                NetworkMessageSend(Buffer, Method, Recipients);
            }
        }
    }

    public override void OnNetworkChange(byte messageIndex, bool IsLocallyOwned)
    {

    }

    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJump();
    }

    public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
    {

    }
}
