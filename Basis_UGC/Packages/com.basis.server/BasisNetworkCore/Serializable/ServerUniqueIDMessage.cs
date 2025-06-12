using LiteNetLib.Utils;
namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct ServerNetIDMessage
        {
            public NetIDMessage NetIDMessage;
            public UshortUniqueIDMessage UshortUniqueIDMessage;
            public void Deserialize(NetDataReader reader)
            {
                NetIDMessage.Deserialize(reader);
                UshortUniqueIDMessage.Deserialize(reader);
            }

            public void Serialize(NetDataWriter writer)
            {
                NetIDMessage.Serialize(writer);
                UshortUniqueIDMessage.Serialize(writer);
            }
        }
    }
}
