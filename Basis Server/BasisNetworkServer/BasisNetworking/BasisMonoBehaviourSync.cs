using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace BasisNetworkServer.BasisNetworking
{
    public static class BasisMonoBehaviourSync
    {
        public static List<StoredBehaviour> StoredBehaviours = new List<StoredBehaviour>();
        public static void AddBehaviour()
        {

        }
        public static void RemoveBehaviour()
        {

        }
    }
    public struct StoredBehaviour
    {
        public string PairedString;
        public ushort NetId;
        public StoredData[] StoredData;
    }
    public struct StoredData
    {
        public ushort PayloadSize;
        public byte messageIndex;
        public byte[] array;

        public void Deserialize(NetDataReader reader)
        {
            if (reader.TryGetUShort(out PayloadSize))
            {
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
            if (array.Length > ushort.MaxValue)
            {
                BNL.LogError($"Larger then {ushort.MaxValue} cannot send this Additional Avatar Data");
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
