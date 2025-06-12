using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ServerReadyMessage
    {
        public PlayerIdMessage playerIdMessage;
        public ReadyMessage localReadyMessage;
        public void Deserialize(NetDataReader Writer)
        {
            playerIdMessage.Deserialize(Writer);
            localReadyMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerIdMessage.Serialize(Writer);
            localReadyMessage.Serialize(Writer);
        }
    }
}
