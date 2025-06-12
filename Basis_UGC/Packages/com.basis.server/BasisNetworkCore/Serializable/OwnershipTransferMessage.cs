using LiteNetLib.Utils;
using static SerializableBasis;
namespace DarkRift.Basis_Common.Serializable
{
    public static partial class SerializableBasis
    {
        public struct OwnershipTransferMessage
        {
            public PlayerIdMessage playerIdMessage;
            public string ownershipID;
            public void Deserialize(NetDataReader Writer)
            {
                playerIdMessage.Deserialize(Writer);
                Writer.Get(out ownershipID);
            }
            public void Serialize(NetDataWriter Writer)
            {
                playerIdMessage.Serialize(Writer);
                Writer.Put(ownershipID);
            }
        }
    }
}
