using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        public byte[] array;//position -> rotation -> rotation
        public const int AvatarSyncSize = 204 + 2;//plus a additional 1 byte after this for additional avatar data
        public const int StoredBones = 89;

        public AdditionalAvatarData[] AdditionalAvatarDatas;
        public byte AdditionalAvatarDataSize;
        public void Deserialize(NetDataReader Writer)
        {
            int Bytes = Writer.AvailableBytes;
            if (Bytes >= AvatarSyncSize)
            {
                //89 * 2 = 178 + 12 + 14 = 204
                //now 178 for muscles, 3*4 for position 12, 4*4 for rotation 16-2 (W is half) = 204
                array ??= new byte[AvatarSyncSize];
                Writer.GetBytes(array, AvatarSyncSize);
                if (Writer.TryGetByte(out AdditionalAvatarDataSize))
                {
                    if (AdditionalAvatarDataSize != 0)
                    {
                        AdditionalAvatarDatas = new AdditionalAvatarData[AdditionalAvatarDataSize];
                        for (int Index = 0; Index < AdditionalAvatarDataSize; Index++)
                        {
                            AdditionalAvatarDatas[Index] = new AdditionalAvatarData();
                            AdditionalAvatarDatas[Index].Deserialize(Writer);
                        }
                        //  BNL.Log("found additional message " + AdditionalAvatarDatas.Length);
                    }
                }
                else
                {
                    BNL.LogError("fundamental error missing Additional Avatar Data Byte");
                }
            }
            else
            {
                BNL.LogError($"Unable to read Remaining bytes where {Bytes} in LocalAvatarSyncMessage");
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            if (array == null)
            {
                BNL.LogError("array was null!!");
            }
            else
            {
                Writer.Put(array);
            }
            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length == 0 || AdditionalAvatarDatas.Length > 256)
            {
                Writer.Put((byte)0);
            }
            else
            {
                AdditionalAvatarDataSize = (byte)AdditionalAvatarDatas.Length;
                Writer.Put(AdditionalAvatarDataSize);
                for (int Index = 0; Index < AdditionalAvatarDataSize; Index++)
                {
                    AdditionalAvatarData AAD = AdditionalAvatarDatas[Index];
                    AAD.Serialize(Writer);
                }
                //   BNL.Log("sending additional message " + AdditionalAvatarDatas.Length);
            }
        }
    }
}
