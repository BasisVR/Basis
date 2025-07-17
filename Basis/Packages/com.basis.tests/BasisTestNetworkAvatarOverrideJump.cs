using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
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

    public override void OnNetworkReady(bool IsLocallyOwned)
    {
        Debug.Log("OnAvatarReady");
        if (IsLocallyOwned)
        {
            Isready = true;
        }
    }

    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod, bool IsADifferentAvatarLocally)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJump();
    }
}
