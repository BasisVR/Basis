using Basis.Network.Core;
public static partial class SerializableBasis
{
    public struct PlayerIdMessage
    {
        public ushort playerID;

        public void Deserialize(NetDataReader Writer)
        {
            Writer.Get(out playerID);
        }
        /// <param name="largeId">false = read byte, true = read ushort.</param>
        public void Deserialize(NetDataReader Writer, bool largeId)
        {
            playerID = largeId ? Writer.GetUShort() : Writer.GetByte();
        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(playerID);
        }
        /// <param name="largeId">false = write byte, true = write ushort.</param>
        public void Serialize(NetDataWriter Writer, bool largeId)
        {
            if (largeId)
                Writer.Put(playerID);
            else
                Writer.Put((byte)playerID);
        }
    }
}
