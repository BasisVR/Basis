using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ServerSideSyncPlayerMessage
    {
        public PlayerIdMessage playerIdMessage;
        public byte interval;
        public LocalAvatarSyncMessage avatarSerialization;
        public void Deserialize(NetDataReader Writer)
        {
            playerIdMessage.Deserialize(Writer);//2bytes
            Writer.Get(out interval);//1 bytes
            avatarSerialization.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerIdMessage.Serialize(Writer);
            Writer.Put(interval);
            avatarSerialization.Serialize(Writer);
        }
    }
}
