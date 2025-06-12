using LiteNetLib.Utils;
using System;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct AvatarLoadDataMessage
        {
            public byte messageIndex;
            public ushort payloadSize;
            public byte[] payload;
            public ushort WhoSentUsThis;
            public void Deserialize(NetDataReader Writer)
            {
                // Read the messageIndex safely
                if (!Writer.TryGetByte(out messageIndex))
                {
                    throw new ArgumentException("Failed to read messageIndex.");
                }
                if (Writer.TryGetUShort(out WhoSentUsThis))
                {
                    throw new ArgumentException("Failed to read who sent us this!");
                }
                // Read the recipientsSize safely
                if (Writer.TryGetUShort(out payloadSize))
                {
                    // Guard against negative or absurd sizes
                    if (payloadSize > Writer.AvailableBytes / sizeof(ushort))
                    {
                        throw new ArgumentException($"Invalid recipientsSize: {payloadSize}");
                    }
                    if (payload == null || payload.Length != payloadSize)
                    {
                        payload = new byte[payloadSize];
                    }
                    if (!Writer.TryGetBytesWithLength(out payload))
                    {
                        throw new ArgumentException($"Failed to read payload!.");
                    }
                }
                else
                {
                    payload = null;
                }
            }

            public void Serialize(NetDataWriter Writer)
            {
                // Write the messageIndex
                Writer.Put(messageIndex);
                Writer.Put(WhoSentUsThis);
                // Determine and write the recipientsSize
                if (payload == null || payload.Length == 0)
                {
                    payloadSize = 0;
                }
                else
                {
                    payloadSize = (ushort)payload.Length;
                }
                Writer.Put(payloadSize);
                // Write the recipients array if present
                if (payload != null && payload.Length > 0)
                {
                    Writer.Put(payload);
                }
            }
        }
    }
}
