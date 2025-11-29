using BasisNetworkCore.Pooling;
using Basis.Network.Core;
using System;

public static partial class SerializableBasis
{
    public struct RemoteSceneDataMessage
    {
        public ushort messageIndex;
        public byte[] payload;

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
                // Return previous payload to the pool if needed
                if (payload != null)
                {
                    BasisByteArrayPooling.Return(payload);
                }

                payload = BasisByteArrayPooling.Rent(payloadSize);
                reader.GetBytes(payload, payloadSize);
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(messageIndex);

            if (payload != null && payload.Length > 0)
            {
                writer.Put(payload);
            }
        }

        public void Release()
        {
            if (payload != null)
            {
                BasisByteArrayPooling.Return(payload);
                payload = null;
            }
        }
    }
}
