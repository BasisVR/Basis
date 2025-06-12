using LiteNetLib.Utils;

public static partial class SerializableBasis
{
    public struct ServerSceneDataMessage
    {
        public PlayerIdMessage playerIdMessage;
        public RemoteSceneDataMessage sceneDataMessage;

        public void Deserialize(NetDataReader Writer)
        {
            // Read the playerIdMessage
            playerIdMessage.Deserialize(Writer);
            sceneDataMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            // Write the playerIdMessage and sceneDataMessage
            playerIdMessage.Serialize(Writer);
            sceneDataMessage.Serialize(Writer);
        }
    }
}
