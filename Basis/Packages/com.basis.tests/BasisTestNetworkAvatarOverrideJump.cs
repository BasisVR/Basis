using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine.InputSystem;
public class BasisTestNetworkAvatarOverrideJump : BasisAvatarMonoBehaviour
{
    public BasisPlayer BasisPlayer;
    public bool Isready;
    public DeliveryMethod Method = DeliveryMethod.Unreliable;
    public void Update()
    {
        if (IsInitalized && NetworkedPlayer.IsLocal)
        {
            if (Keyboard.current[Key.Space].wasPressedThisFrame)
            {
                NetworkMessageSend(Method);
            }
        }
    }
    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJump();
    }
}
