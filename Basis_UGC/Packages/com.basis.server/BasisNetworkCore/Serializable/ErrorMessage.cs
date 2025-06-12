using LiteNetLib.Utils;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct ErrorMessage
        {
            public string Message;

            public void Deserialize(NetDataReader reader)
            {
                int bytes = reader.AvailableBytes;
                if (bytes >= sizeof(ushort))
                {
                    Message = reader.GetString();
                }
                else
                {
                    BNL.LogError("Not Enough Data Remains!");
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(Message);
            }
        }
    }
}
