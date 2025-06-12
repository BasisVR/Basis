using LiteNetLib.Utils;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct ConsoleData
        {
            public byte messageIndex;
            public byte[] array;

            public void Deserialize(NetDataReader reader)
            {
                int bytesAvailable = reader.AvailableBytes;
                if (bytesAvailable > 0)
                {
                    messageIndex = reader.GetByte();

                    ushort payloadSize = reader.GetUShort();

                    if (payloadSize > 0)
                    {
                        if (array == null || array.Length != payloadSize)
                        {
                            array = new byte[payloadSize];
                        }
                        reader.GetBytes(array, payloadSize);
                    }
                    else
                    {
                        array = new byte[0]; // Handle zero-length array case
                    }
                }
                else
                {
                    BNL.LogError($"Unable to read remaining bytes, available: {bytesAvailable}");
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(messageIndex);

                ushort size = (array != null) ? (ushort)array.Length : (ushort)0;
                writer.Put(size);

                if (size > 0)
                {
                    writer.Put(array);
                }
            }
        }
    }
}
