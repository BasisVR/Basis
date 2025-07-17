using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;
public class BasisTestNetworkAvatar : BasisAvatarMonoBehaviour
{
    public byte[] SubmittingData;
    public ushort[] Recipients = null;
    public BasisPlayer BasisPlayer;
    public void LoopSend()
    {
        Debug.Log("Sening Loop Data");
        NetworkMessageSend(SubmittingData, DeliveryMethod.Unreliable, Recipients);
    }

    public override void OnNetworkReady(byte messageIndex, bool IsLocallyOwned)
    {
        if (IsLocallyOwned)
        {
            InvokeRepeating(nameof(LoopSend), 0, 1);
        }
    }
}
