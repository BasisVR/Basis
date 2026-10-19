using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Behaviour;
using Basis.Network.Core;
using UnityEngine.InputSystem;
[AutoStaticsCleanup]
public partial class BasisTestNetworkAvatarOverrideJump : BasisNetworkAvatarBehaviour
{
    new public static bool VisibleInAvatarMenu = false;
    public BasisPlayer BasisPlayer;
    public bool Isready;
    public DeliveryMethod Method = DeliveryMethod.Unreliable;
    public void Update()
    {
        if (IsInitialized && NetworkedPlayer.IsLocal)
        {
            if (Keyboard.current[Key.Space].wasPressedThisFrame)
            {
                NetworkMessageSend(Method);
            }
        }
    }
    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJumpRequest();
    }
}
