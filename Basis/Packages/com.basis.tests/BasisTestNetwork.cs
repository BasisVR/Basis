using Basis.Scripts.BasisSdk;
using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;
public class BasisTestNetwork : BasisAvatarMonoBehaviour
{
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
    public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod, bool IsADifferentAvatarLocally)
    {
        Debug.Log($"received {MessageIndex} {buffer.Length}");
    }
}
