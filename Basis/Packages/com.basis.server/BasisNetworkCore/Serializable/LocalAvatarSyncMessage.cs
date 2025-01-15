using LiteNetLib.Utils;
using System.Collections.Generic;
public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        public byte[] array;//position -> rotation -> rotation
        public const int AvatarSyncSize = 204;
        public const int StoredBones = 89;
        public AdditionalAvatarData[] AdditionalAvatarDatas;
        public bool hasAdditionalAvatarData;
        public void Deserialize(NetDataReader Writer,bool AttemptAdditionalData)
        {
            int Bytes = Writer.AvailableBytes;
            if (Bytes >= AvatarSyncSize)
            {
                //89 * 2 = 178 + 12 + 14 = 204
                //now 178 for muscles, 3*4 for position 12, 4*4 for rotation 16-2 (W is half) = 204
                array ??= new byte[AvatarSyncSize];
                Writer.GetBytes(array, AvatarSyncSize);
                if (AttemptAdditionalData)
                {
                    if (Writer.EndOfData)
                    {
                        AdditionalAvatarDatas = null;
                        hasAdditionalAvatarData = false;
                    }
                    else
                    {
                        List<AdditionalAvatarData> list = new List<AdditionalAvatarData>();
                        while (Writer.AvailableBytes != 0)
                        {
                            // BNL.Log("Deserialize AAD");
                            AdditionalAvatarData AAD = new AdditionalAvatarData();
                            AAD.Deserialize(Writer);
                            list.Add(AAD);
                        }
                        AdditionalAvatarDatas = list.ToArray();
                        hasAdditionalAvatarData = true;
                    }
                }
            }
            else
            {
                BNL.LogError($"Unable to read Remaing bytes where {Bytes}");
            }
        }
        public void Serialize(NetDataWriter Writer,bool AttemptAdditionalData)
        {
            if (array == null)
            {
                BNL.LogError("array was null!!");
            }
            else
            {
                Writer.Put(array);
            }
            if (AttemptAdditionalData && hasAdditionalAvatarData)
            {
                // BNL.Log("Serialize AAD");
                int count = AdditionalAvatarDatas.Length;
                for (int Index = 0; Index < count; Index++)
                {
                    AdditionalAvatarData AAD = AdditionalAvatarDatas[Index];
                    AAD.Serialize(Writer);
                }
            }
        }
    }
}
