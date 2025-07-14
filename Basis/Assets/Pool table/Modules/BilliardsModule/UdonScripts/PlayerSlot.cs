using Basis;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using System;
public class PlayerSlot : BasisNetworkBehaviour
{
    private NetworkingManager networkingManager;
    public NetworkSyncPlayerSlot SyncedSlot = new NetworkSyncPlayerSlot();
    public class NetworkSyncPlayerSlot
    {
        [NonSerialized]
        public byte slot = byte.MaxValue;
        [NonSerialized]
        public bool leave = false;
    }
    public void _Init(NetworkingManager networkingManager_)
    {
        networkingManager = networkingManager_;
    }

    public void JoinSlot(byte slot_)
    {
        if (slot_ > 3) return;
        SyncedSlot.slot = slot_;
        SyncedSlot.leave = false;
        NetworkPunch();
    }

    public void LeaveSlot(int slot_)
    {
        if (slot_ > 3) return;
        SyncedSlot.slot = (byte)slot_;
        SyncedSlot.leave = true;
        NetworkPunch();
    }
    public async void NetworkPunch()
    {
        await TakeOwnershipAsync();

        byte[] bytes = SerializationUtility.SerializeValue(SyncedSlot, DataFormat.Binary);
        SendCustomNetworkEvent(bytes, DeliveryMethod.ReliableOrdered);

        OnDeserialization();
    }
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        SyncedSlot = SerializationUtility.DeserializeValue<NetworkSyncPlayerSlot>(buffer, DataFormat.Binary);
        OnDeserialization();
    }
    public void OnDeserialization()
    {
        if (networkingManager == null) return;
        if (SyncedSlot.slot > 3) return;

        networkingManager._OnPlayerSlotChanged(this);
    }
}
