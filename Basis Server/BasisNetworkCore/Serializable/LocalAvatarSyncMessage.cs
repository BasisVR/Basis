using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        public byte[] array;//position -> rotation -> rotation -> scale
        public const int AvatarSyncSize = 206;//plus a additional 1 byte after this for additional avatar data
        public const int StoredBones = 89;

        public AdditionalAvatarData[] AdditionalAvatarDatas;
        public byte AdditionalAvatarDataSize;
        //when we swap avatars additional avatar data could be wrong for a few frames when that occurs linked avatar index will update to match.
        public byte LinkedAvatarIndex;
        public LocalAvatarSyncMessage(byte[] array) : this()
        {
            this.array = array;
        }

        public void Deserialize(NetDataReader Writer)
        {
            if (Writer == null)
            {
                BNL.LogError("Deserialize failed: NetDataReader was null!");
                return;
            }

            int Bytes = Writer.AvailableBytes;
            if (Bytes >= AvatarSyncSize)
            {
                array ??= new byte[AvatarSyncSize];
                Writer.GetBytes(array, AvatarSyncSize);

                if (Writer.TryGetByte(out AdditionalAvatarDataSize))
                {
                    if (AdditionalAvatarDataSize != 0)
                    {
                        if (!Writer.TryGetByte(out LinkedAvatarIndex))
                        {
                            BNL.LogError("Missing LinkedAvatarIndex!");
                        }

                        AdditionalAvatarDatas = new AdditionalAvatarData[AdditionalAvatarDataSize];
                        for (int Index = 0; Index < AdditionalAvatarDataSize; Index++)
                        {
                            AdditionalAvatarDatas[Index] = new AdditionalAvatarData();
                            AdditionalAvatarDatas[Index].Deserialize(Writer);
                        }
                    }
                }
                else
                {
                    BNL.LogError("Fundamental error: missing Additional Avatar Data Byte");
                }
            }
            else
            {
                BNL.LogError($"Unable to read LocalAvatarSyncMessage. Remaining bytes: {Bytes}, expected: {AvatarSyncSize}+");
            }
        }

        public void Serialize(NetDataWriter Writer)
        {
            if (Writer == null)
            {
                BNL.LogError("Serialize failed: NetDataWriter was null!");
                return;
            }

            if (array == null)
            {
                BNL.LogError("Serialize failed: array was null!");
                Writer.Put(new byte[AvatarSyncSize]); // fail-safe: put empty array
            }
            else
            {
                Writer.Put(array);
            }

            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length == 0 || AdditionalAvatarDatas.Length > 256)
            {
                Writer.Put((byte)0);
                return;
            }

            AdditionalAvatarDataSize = (byte)AdditionalAvatarDatas.Length;
            Writer.Put(AdditionalAvatarDataSize);

            if (AdditionalAvatarDataSize != 0)
            {
                Writer.Put(LinkedAvatarIndex); // include linked avatar if we have additional avatar data
            }
            else
            {
                return;
            }

            for (int Index = 0; Index < AdditionalAvatarDataSize; Index++)
            {
                AdditionalAvatarData AAD = AdditionalAvatarDatas[Index];
                AAD.Serialize(Writer);
            }
        }
    }
}
