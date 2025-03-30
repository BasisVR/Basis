using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ServerSideSyncPlayerMessage
    {
        public PlayerIdMessage playerIdMessage;
        public byte interval;
        public LocalAvatarSyncMessage avatarSerialization;
        public void Deserialize(NetDataReader NetDataReader)
        {
            playerIdMessage.Deserialize(NetDataReader);//2bytes
            NetDataReader.Get(out interval);//1 bytes
            avatarSerialization.Deserialize(NetDataReader);//205 = 208
        }
        public void Serialize(NetDataWriter NetDataWriter)
        {
            playerIdMessage.Serialize(NetDataWriter);
            NetDataWriter.Put(interval);
            avatarSerialization.Serialize(NetDataWriter);
        }
    }
}
