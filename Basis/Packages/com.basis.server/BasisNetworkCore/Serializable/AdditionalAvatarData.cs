using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct AdditionalAvatarData
    {
        public byte PayloadSize;
        public byte messageIndex;
        public byte[] array;

        public void Deserialize(NetDataReader reader)
        {
            if (reader.TryGetByte(out PayloadSize))
            {
                if (PayloadSize == 0)
                {
                    return;
                }
                if (reader.TryGetByte(out messageIndex))
                {
                    if (array == null || array.Length != PayloadSize)
                    {
                        array = new byte[PayloadSize];
                    }
                    reader.GetBytes(array, PayloadSize);
                }
                else
                {
                    BNL.LogError("trying to write data that does not exist! messageIndex");
                }
            }
            else
            {
                BNL.LogError("trying to write data that does not exist! PayloadSize");
            }
        }
        public void Serialize(NetDataWriter writer)
        {
            if (array == null)
            {
                PayloadSize = 0;
                writer.Put(PayloadSize);
                return;
            }

            if (array.Length > 255)
            {
                BNL.LogError("Larger than 255 cannot send this Additional Avatar Data");
                PayloadSize = 0;
                writer.Put(PayloadSize);
                return;
            }
            PayloadSize = (byte)array.Length;

            writer.Put(PayloadSize);
            writer.Put(messageIndex);

            if (PayloadSize > 0)
            {
                writer.Put(array, 0, PayloadSize);
            }
        }
    }
}
