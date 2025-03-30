using LiteNetLib.Utils;

public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        public byte[] array; // Position -> Rotation -> Rotation
        public const int AvatarSyncSize = 204;
        public const int TotalSafeSize = 205;
        public const int StoredBones = 89;

        public byte AdditionalAvatarDataSize;
        public AdditionalAvatarData[] AdditionalAvatarDatas;

        public void Deserialize(NetDataReader reader)
        {
            int availableBytes = reader.AvailableBytes;
            if (availableBytes < TotalSafeSize) // Ensure there's at least AvatarSyncSize + 1 byte
            {
                BNL.LogError($"Insufficient bytes ({availableBytes}) to deserialize LocalAvatarSyncMessage.");
                return;
            }


            if (!reader.TryGetByte(out AdditionalAvatarDataSize))
            {
                BNL.LogError("Missing AdditionalAvatarDataSize byte in LocalAvatarSyncMessage. this should never occur as we validate size above!");
                return;
            }
            if (AdditionalAvatarDataSize == 0)
            {
                AdditionalAvatarDatas = null;
            }
            else
            {
                AdditionalAvatarDatas = new AdditionalAvatarData[AdditionalAvatarDataSize];
                for (int Index = 0; Index < AdditionalAvatarDataSize; Index++)
                {
                    AdditionalAvatarDatas[Index] = new AdditionalAvatarData();
                    AdditionalAvatarDatas[Index].Deserialize(reader);
                }
            }

            //89 * 2  =178
            //3 * 4 = 12
            //3 * 4 + 1*2 = 14
            // = 204
            if (array == null || array.Length == 0)
            {
                array = new byte[AvatarSyncSize];
            }
            reader.GetBytes(array, AvatarSyncSize);
        }

        public void Serialize(NetDataWriter writer)
        {
            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length == 0)
            {
                writer.Put((byte)0);
            }
            else
            {
                if (AdditionalAvatarDatas.Length > 256)
                {
                    BNL.LogError("Too many AdditionalAvatarDatas, exceeding maximum allowed size (256).");
                    return;
                }
                AdditionalAvatarDataSize = (byte)AdditionalAvatarDatas.Length;
                writer.Put(AdditionalAvatarDataSize);
                for (int Index = 0; Index < AdditionalAvatarDataSize; Index++)
                {
                    AdditionalAvatarData aad = AdditionalAvatarDatas[Index];
                    if (aad.array == null)
                    {
                        BNL.LogError($"AdditionalAvatarData[{Index}] is null and cannot be serialized.");
                        continue;
                    }
                    aad.Serialize(writer);
                }
            }

            if (array == null)
            {
                BNL.LogError("Array is null while serializing LocalAvatarSyncMessage.");
                return;
            }

            writer.Put(array);
        }
    }
}
