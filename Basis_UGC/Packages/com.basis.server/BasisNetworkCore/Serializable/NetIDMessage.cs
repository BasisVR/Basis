using LiteNetLib.Utils;
namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct NetIDMessage
        {
            public string UniqueID;

            public void Deserialize(NetDataReader reader)
            {
                int bytes = reader.AvailableBytes;
                if (bytes != 0)
                {
                    UniqueID = reader.GetString();
                }
                else
                {
                  BNL.LogError($"Unable to read remaining bytes: {bytes}");
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                if (!string.IsNullOrEmpty(UniqueID))
                {
                    writer.Put(UniqueID);
                }
                else
                {
                    BNL.LogError("Unable to serialize. Field was null or empty.");
                }
            }
        }
    }
}
