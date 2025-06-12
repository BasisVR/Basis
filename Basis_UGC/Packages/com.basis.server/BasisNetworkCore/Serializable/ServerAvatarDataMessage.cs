using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ServerAvatarDataMessage
    {
        public PlayerIdMessage playerIdMessage;
        public RemoteAvatarDataMessage avatarDataMessage;
        public void Deserialize(NetDataReader Writer)
        {
            playerIdMessage.Deserialize(Writer);
            avatarDataMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerIdMessage.Serialize(Writer);
            avatarDataMessage.Serialize(Writer);
        }
    }
}
