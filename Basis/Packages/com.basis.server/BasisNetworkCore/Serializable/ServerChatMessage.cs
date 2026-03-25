using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// Server-to-client chat message. Wraps the chat payload with the sender's player ID.
    /// </summary>
    public struct ServerChatMessage
    {
        public PlayerIdMessage playerIdMessage;
        public ChatMessage chatMessage;

        public void Deserialize(NetDataReader reader)
        {
            playerIdMessage.Deserialize(reader);
            chatMessage.Deserialize(reader);
        }

        public void Serialize(NetDataWriter writer)
        {
            playerIdMessage.Serialize(writer);
            chatMessage.Serialize(writer);
        }
    }
}
