using LiteNetLib.Utils;
using System;

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
            if (array.Length > 256)
            {
                BNL.LogError("Larger then 256 cannot send this Additional Avatar Data");
                return;
            }
            PayloadSize = (array != null) ? (byte)array.Length : (byte)0;

            writer.Put(PayloadSize);
            writer.Put(messageIndex);

            if (PayloadSize > 0)
            {
                writer.Put(array, 0, PayloadSize);
            }
        }
    }
}
