using Basis.Network.Core;
public static partial class SerializableBasis
{
    public struct PlayerIdMessage
    {
        public ushort playerID;

        public void Deserialize(NetDataReader Writer)
        {
            Writer.Get(out playerID); // Read the entire ushort value
        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(playerID); // Write the entire ushort value
        }
    }
}
