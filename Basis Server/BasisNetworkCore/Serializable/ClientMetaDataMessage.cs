using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct ClientMetaDataMessage
    {
        public string playerUUID;
        public string playerDisplayName;
        public string playerPlatform;
        public void Deserialize(NetDataReader Writer)
        {
            Writer.Get(out playerUUID);
            Writer.Get(out playerDisplayName);
            Writer.Get(out playerPlatform);

        }
        public void Serialize(NetDataWriter Writer)
        {
            if (string.IsNullOrEmpty(playerUUID) == false)
            {
                Writer.Put(playerUUID);
            }
            else
            {
                Writer.Put("Failure");
            }
            if (string.IsNullOrEmpty(playerDisplayName) == false)
            {
                Writer.Put(playerDisplayName);
            }
            else
            {
                Writer.Put("Failure");
            }
            if (string.IsNullOrEmpty(playerPlatform) == false)
            {
                Writer.Put(playerPlatform);
            }
            else
            {
                Writer.Put("Failure");
            }
        }
    }
}
