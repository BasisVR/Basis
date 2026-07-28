using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct AdditionalAvatarData
    {
        public byte PayloadSize;
        public byte messageIndex;
        public byte[] array;

        // Wire form: [PayloadSize:1][messageIndex:1][data:PayloadSize]. The 2-byte header is
        // written for EVERY entry, including empty/suppressed ones (PayloadSize 0) — a size-0
        // entry that omitted messageIndex would be ambiguous and desync every entry after it,
        // corrupting all additional data (face tracking) riding the same frame.
        public void Deserialize(NetDataReader reader)
        {
            if (reader.TryGetByte(out PayloadSize))
            {
                if (reader.TryGetByte(out messageIndex))
                {
                    if (PayloadSize == 0)
                    {
                        array = null;
                        return;
                    }
                    if (PayloadSize > reader.AvailableBytes)
                    {
                        BNL.LogError("AdditionalAvatarData payload exceeds available data!");
                        // Deserialized in place over a retained slot — drop the stale buffer so a
                        // corrupt entry can never be dispatched or re-serialized as valid data.
                        array = null;
                        return;
                    }
                    if (array == null || array.Length != PayloadSize)
                    {
                        array = new byte[PayloadSize];
                    }
                    reader.GetBytes(array, PayloadSize);
                }
                else
                {
                    BNL.LogError("trying to write data that does not exist! messageIndex");
                    array = null;
                }
            }
            else
            {
                BNL.LogError("trying to write data that does not exist! PayloadSize");
                array = null;
            }
        }
        public void Serialize(NetDataWriter writer)
        {
            if (array != null && array.Length > 255)
            {
                BNL.LogError("Larger than 255 cannot send this Additional Avatar Data");
                PayloadSize = 0;
                writer.Put(PayloadSize);
                writer.Put(messageIndex);
                return;
            }

            PayloadSize = (byte)(array?.Length ?? 0);
            writer.Put(PayloadSize);
            writer.Put(messageIndex);

            if (PayloadSize > 0)
            {
                writer.Put(array, 0, PayloadSize);
            }
        }
    }
}
