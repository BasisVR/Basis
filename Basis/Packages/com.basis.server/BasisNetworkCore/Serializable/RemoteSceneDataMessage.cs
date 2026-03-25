using Basis.Network.Core;
using System;

public static partial class SerializableBasis
{
    public struct RemoteSceneDataMessage
    {
        public ushort messageIndex;
        public byte[] payload;
        public int payloadLength;

        public void Deserialize(NetDataReader reader)
        {
            // Read the messageIndex safely
            if (!reader.TryGetUShort(out messageIndex))
            {
                throw new ArgumentException("Failed to read messageIndex.");
            }

            int payloadSize = reader.AvailableBytes;

            if (payloadSize > 0)
            {
                payload = new byte[payloadSize];
                reader.GetBytes(payload, payloadSize);
                payloadLength = payloadSize;
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(messageIndex);

            int len = payloadLength > 0 ? payloadLength : (payload != null ? payload.Length : 0);
            if (payload != null && len > 0)
            {
                writer.Put(payload, 0, len);
            }
        }

        public void Release()
        {
            if (payload != null)
            {
                payload = null;
                payloadLength = 0;
            }
        }
    }
}
