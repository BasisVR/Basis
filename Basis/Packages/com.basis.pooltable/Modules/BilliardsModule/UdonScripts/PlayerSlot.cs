using Basis;
using Basis.Network.Core;
using System;
public class PlayerSlot : BasisNetworkBehaviour
{

    [System.Serializable]
    public struct SyncPlayerSessionData
    {
        public byte slot;     // Player slot (default: 255)
        public bool leave;    // Whether the player wants to leave

        public SyncPlayerSessionData(byte slot, bool leave)
        {
            this.slot = slot;
            this.leave = leave;
        }

        // Convert to a byte array (2 bytes)
        public byte[] ToBytes()
        {
            byte[] data = new byte[2];
            data[0] = slot;
            data[1] = leave ? (byte)1 : (byte)0;
            return data;
        }

        // Convert from a byte array (expects 2 bytes)
        public static SyncPlayerSessionData FromBytes(byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                return new SyncPlayerSessionData(byte.MaxValue, false); // fallback default
            }

            return new SyncPlayerSessionData(
                data[0],
                data[1] != 0
            );
        }
    }
    public SyncPlayerSessionData SyncPlayerSession;
    private NetworkingManager networkingManager;

    public void _Init(NetworkingManager networkingManager_)
    {
        networkingManager = networkingManager_;
    }

    public void JoinSlot(int slot_)
    {
        if (slot_ > 3) return;
        SyncPlayerSession.slot = (byte)slot_;
        SyncPlayerSession.leave = false;
        TakeOwnership();// Networking.SetOwner(BasisNetworkPlayer.LocalPlayer, gameObject);
        RequestSerialization();
        OnDeserialization();
    }

    public void LeaveSlot(int slot_)
    {
        if (slot_ > 3) return;
        SyncPlayerSession.slot = (byte)slot_;
        SyncPlayerSession.leave = true;
        TakeOwnership();// Networking.SetOwner(BasisNetworkPlayer.LocalPlayer, gameObject);
        RequestSerialization();
        OnDeserialization();
    }

    private void RequestSerialization()
    {
        SendCustomNetworkEvent(SyncPlayerSession.ToBytes(), DeliveryMethod.ReliableOrdered);
    }
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        SyncPlayerSession = SyncPlayerSessionData.FromBytes(buffer);
        OnDeserialization();
    }
    public void OnDeserialization()
    {

        if (networkingManager == null) return;
        if (SyncPlayerSession.slot > 3) return;

        networkingManager._OnPlayerSlotChanged(this);
    }
}
