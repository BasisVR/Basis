using LiteNetLib.Utils;
namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct ServerUniqueIDMessages
        {
            public ushort MessageCount;
            public ServerNetIDMessage[] Messages;

            public void Deserialize(NetDataReader reader)
            {
                int bytes = reader.AvailableBytes;
                if (bytes >= sizeof(ushort))
                {
                    MessageCount = reader.GetUShort();
                    if (Messages == null || Messages.Length != MessageCount)
                    {
                        Messages = new ServerNetIDMessage[MessageCount];
                    }
                    for (int Index = 0; Index < MessageCount; Index++)
                    {
                        Messages[Index] = new ServerNetIDMessage();
                        Messages[Index].Deserialize(reader);
                    }
                }
                else
                {
                    Messages = null;
                    BNL.LogError($"Unable to read remaining bytes for MessageCount. Available: {bytes}");
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                if (Messages != null)
                {
                    MessageCount = (ushort)Messages.Length;
                    writer.Put(MessageCount);
                    for (int Index = 0; Index < MessageCount; Index++)
                    {
                        ServerNetIDMessage message = Messages[Index];
                        message.Serialize(writer);
                    }
                }
                else
                {
                    BNL.LogError("Unable to serialize. Messages array was null.");
                }
            }
        }
    }
}
