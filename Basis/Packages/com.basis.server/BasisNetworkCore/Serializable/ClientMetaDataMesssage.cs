using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    /// <summary>
    /// contains all necessary data to go along with the players return message locally.
    /// this message is more async and the goal is that you can use it to change things about a local player.
    /// </summary>
    public struct ServerMetaDataMessage
    {
        public ClientMetaDataMessage ClientMetaDataMessage;

        public int SyncInterval;
        public int BaseMultiplier;
        public float IncreaseRate;
        public float SlowestSendRate;

        public void Deserialize(NetDataReader Writer)
        {
            ClientMetaDataMessage.Deserialize(Writer);

            Writer.Get(out SyncInterval);
            Writer.Get(out BaseMultiplier);
            Writer.Get(out IncreaseRate);
            Writer.Get(out SlowestSendRate);
        }
        public void Serialize(NetDataWriter Writer)
        {
            ClientMetaDataMessage.Serialize(Writer);

            Writer.Put(SyncInterval);
            Writer.Put(BaseMultiplier);
            Writer.Put(IncreaseRate);
            Writer.Put(SlowestSendRate);
        }
    }
}
