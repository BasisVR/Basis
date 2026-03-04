using Basis.Network.Core;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct NetIDMessage
        {
            public string playerID;

            public void Deserialize(NetDataReader reader)
            {
                int bytes = reader.AvailableBytes;
                if (bytes != 0)
                {
                    playerID = reader.GetString();
                }
                else
                {
                  BNL.LogError($"Unable to read remaining bytes: {bytes}");
                }
            }

            public void Serialize(NetDataWriter writer)
            {
                if (!string.IsNullOrEmpty(playerID))
                {
                    writer.Put(playerID);
                }
                else
                {
                    BNL.LogError("Unable to serialize. Field was null or empty.");
                }
            }
        }
    }
}
