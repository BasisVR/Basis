using LiteNetLib.Utils;
using System;

namespace Basis.Network.Core.Serializable
{
    public static partial class SerializableBasis
    {
        /// <summary>
        /// Consists of a ushort length, followed by a byte array (of the same length).
        /// </summary>
        [System.Serializable]
        public struct BytesMessage
        {
            public bool Deserialize(NetDataReader reader, out byte[] Data)
            {
                if (reader.TryGetUShort(out ushort msgLength))
                {
                    Data = new byte[msgLength];
                    reader.GetBytes(Data, msgLength);
                    return true;
                }
                BNL.LogError("unable to read the size of the data");
                Data = null;
                return false;
            }

            public readonly void Serialize(NetDataWriter writer, byte[] Data)
            {
                ushort Length = (ushort)Data.Length;
                if (Length == 0)
                {
                    BNL.LogError("this data does not belong on the network! was size 0");
                }
                writer.Put(Length);
                writer.Put(Data);
            }
        }
    }
}
