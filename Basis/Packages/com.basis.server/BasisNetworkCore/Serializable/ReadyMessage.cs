using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ReadyMessage
    {
        public ClientMetaDataMessage playerMetaDataMessage;
        public ClientAvatarChangeMessage clientAvatarChangeMessage;
        public LocalAvatarSyncMessage localAvatarSyncMessage;
        public void Deserialize(NetDataReader Writer)
        {
            playerMetaDataMessage.Deserialize(Writer);
            clientAvatarChangeMessage.Deserialize(Writer);
            localAvatarSyncMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerMetaDataMessage.Serialize(Writer);
            clientAvatarChangeMessage.Serialize(Writer);
            localAvatarSyncMessage.Serialize(Writer);
        }
        public bool WasDeserializedCorrectly()
        {
            if(clientAvatarChangeMessage.byteArray == null)
            {
                return false;
            }
            if(localAvatarSyncMessage.array == null)
            {
                return false;
            }
            return true;
        }
    }
}
