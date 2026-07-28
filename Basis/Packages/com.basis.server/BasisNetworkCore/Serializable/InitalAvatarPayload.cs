using Basis.Network.Core;
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
                if (!Writer.TryGetByte(out messageIndex))
                {
                    throw new ArgumentException("Failed to read messageIndex.");
                }
                if (!Writer.TryGetUShort(out WhoSentUsThis))
                {
                    throw new ArgumentException("Failed to read who sent us this!");
                }
                if (!Writer.TryGetUShort(out payloadSize))
                {
                    throw new ArgumentException("Failed to read payloadSize.");
                }
                if (payloadSize == 0)
                {
                    payload = null;
                    return;
                }
                if (payloadSize > Writer.AvailableBytes)
                {
                    throw new ArgumentException($"Invalid payloadSize: {payloadSize}");
                }
                if (payload == null || payload.Length != payloadSize)
                {
                    payload = new byte[payloadSize];
                }
                Writer.GetBytes(payload, payloadSize);
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
