using Basis;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using System;
public class PlayerSlot : BasisNetworkBehaviour
{
    private const byte MaxSlotIndex = 3;

    private NetworkingManager networkingManager;

    public NetworkSyncPlayerSlot SyncedSlot = new NetworkSyncPlayerSlot();

    [Serializable]
    public class NetworkSyncPlayerSlot
    {
        [NonSerialized]
        public byte slot = byte.MaxValue;

        [NonSerialized]
        public bool leave = false;
    }

    public void Initialization(NetworkingManager networkingManager_)
    {
        networkingManager = networkingManager_;
    }

    public void JoinSlot(byte slot_)
    {
        if (!IsValidSlot(slot_)) return;

        SyncedSlot.slot = slot_;
        SyncedSlot.leave = false;

        NetworkPunchAsync();
    }

    public void LeaveSlot(int slot_)
    {
        if (!IsValidSlot(slot_)) return;

        SyncedSlot.slot = (byte)slot_;
        SyncedSlot.leave = true;

         NetworkPunchAsync();
    }

    private bool IsValidSlot(int slot)
    {
        return slot >= 0 && slot <= MaxSlotIndex;
    }

    private void NetworkPunchAsync()
    {
     //   await TakeOwnershipAsync();

        byte[] bytes = SerializationUtility.SerializeValue(SyncedSlot, DataFormat.Binary);
        SendCustomNetworkEvent(bytes, DeliveryMethod.ReliableOrdered);

        OnDeserialization();
    }

    public override void OnNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
    {
        SyncedSlot = SerializationUtility.DeserializeValue<NetworkSyncPlayerSlot>(buffer, DataFormat.Binary);
        OnDeserialization();
    }

    public void OnDeserialization()
    {
        if (networkingManager == null || !IsValidSlot(SyncedSlot.slot))
        {
            return;
        }

        networkingManager._OnPlayerSlotChanged(this);
    }
}
