using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Behaviour;
using Basis.Network.Core;
using UnityEngine;
[AutoStaticsCleanup]
public partial class BasisTestNetwork : BasisNetworkAvatarBehaviour
{
    new public static bool VisibleInAvatarMenu = false;
    public bool Send = false;
    public ushort[] Players;
    public byte[] SendingOutBytes = new byte[3];
    public void LateUpdate()
    {
        if (Send)
        {
            ServerReductionSystemMessageSend(SendingOutBytes);
            Send = false;
        }
    }
    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        Debug.Log($"received {MessageIndex} {buffer.Length}");
    }
}
