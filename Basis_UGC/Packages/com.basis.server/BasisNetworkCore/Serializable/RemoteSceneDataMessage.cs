using LiteNetLib.Utils;
using System;

public static partial class SerializableBasis
{
    public struct RemoteSceneDataMessage
    {
        public ushort messageIndex;
        public byte[] payload;

        public void Deserialize(NetDataReader Writer)
        {
            // Read the messageIndex safely
            if (!Writer.TryGetUShort(out messageIndex))
            {
                throw new ArgumentException("Failed to read messageIndex.");
            }

            // Read remaining bytes as payload
            if (Writer.AvailableBytes > 0)
            {
                payload = Writer.GetRemainingBytes();
            }
        }

        public void Serialize(NetDataWriter Writer)
        {
            // Write the messageIndex
            Writer.Put(messageIndex);
            // Write the payload if present
            if (payload != null && payload.Length > 0)
            {
                Writer.Put(payload);
            }
        }
    }
}
