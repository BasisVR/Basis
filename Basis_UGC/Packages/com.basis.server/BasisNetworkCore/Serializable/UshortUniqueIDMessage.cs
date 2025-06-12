using LiteNetLib.Utils;
namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct UshortUniqueIDMessage
        {
            public ushort UniqueIDUshort;

            public void Deserialize(NetDataReader reader)
            {
                int bytes = reader.AvailableBytes;
                if (bytes != 0)
                {
                    UniqueIDUshort = reader.GetUShort();
                }
                else
                {
                    BNL.LogError($"Unable to read remaining bytes: {bytes}");
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(UniqueIDUshort);
            }
        }
    }
}
