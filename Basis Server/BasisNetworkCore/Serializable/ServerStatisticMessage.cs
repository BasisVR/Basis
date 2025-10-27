using Basis.Network.Core;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        /// <summary>
        /// Snapshot of server/client stats. Fixed layout for easy wire format.
        /// </summary>
        public struct ServerStatisticMessage
        {
            public byte[] Data;
            public void Serialize(NetDataWriter w)
            {
                w.Put(Data);
            }

            public void Deserialize(NetDataReader r)
            {
                Data = r.GetRemainingBytes();
            }
        }
    }
}
