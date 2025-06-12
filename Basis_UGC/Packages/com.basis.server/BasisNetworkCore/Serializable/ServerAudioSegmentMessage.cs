using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ServerAudioSegmentMessage
    {
        public PlayerIdMessage playerIdMessage;
        public AudioSegmentDataMessage audioSegmentData;
        public void Deserialize(NetDataReader Writer)
        {
            playerIdMessage.Deserialize(Writer);
            audioSegmentData.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerIdMessage.Serialize(Writer);
            audioSegmentData.Serialize(Writer);
        }
    }
}
