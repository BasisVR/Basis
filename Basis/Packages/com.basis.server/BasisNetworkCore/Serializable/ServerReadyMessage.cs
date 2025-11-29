using Basis.Network.Core;
public static partial class SerializableBasis
{
    public struct ServerReadyMessage
    {
        public PlayerIdMessage playerIdMessage;//who this came from
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
