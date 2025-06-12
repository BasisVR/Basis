using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ServerAvatarChangeMessage
    {
        public PlayerIdMessage uShortPlayerId;
        public ClientAvatarChangeMessage clientAvatarChangeMessage;
        public void Deserialize(NetDataReader Writer)
        {
            uShortPlayerId.Deserialize(Writer);
            clientAvatarChangeMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            uShortPlayerId.Serialize(Writer);
            clientAvatarChangeMessage.Serialize(Writer);
        }
    }
}
