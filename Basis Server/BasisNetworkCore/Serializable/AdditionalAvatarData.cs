using LiteNetLib.Utils;

public static partial class SerializableBasis
{
    public struct AdditionalAvatarData
    {
        public byte PayloadSize;
        public byte messageIndex;
        public byte[] array;

        public void Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetByte(out messageIndex))
            {
                BNL.LogError("Failed to read messageIndex from NetDataReader.");
                return;
            }

            if (!reader.TryGetByte(out PayloadSize))
            {
                BNL.LogError("Failed to read PayloadSize from NetDataReader.");
                return;
            }

            if (PayloadSize == 0)
            {
                array = null; // Ensure it's cleared if no data
                return;
            }
            if (array == null || array.Length != PayloadSize)
            {
                array = new byte[PayloadSize];
            }

            reader.GetBytes(array, PayloadSize);
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(messageIndex);

            if (array == null || array.Length == 0)
            {
                PayloadSize = 0;
                writer.Put(PayloadSize);
                return;
            }

            if (array.Length > 256)
            {
                BNL.LogError("PayloadSize exceeds the maximum allowed size (256). Serialization aborted.");
                return;
            }
            PayloadSize = (byte)array.Length;
            writer.Put(PayloadSize);
            writer.Put(array, 0, PayloadSize);
        }
    }
}
