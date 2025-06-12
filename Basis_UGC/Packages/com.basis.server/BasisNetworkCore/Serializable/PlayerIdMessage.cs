using LiteNetLib.Utils;

public static partial class SerializableBasis
{
    public struct PlayerIdMessage
    {
        private ushort data; // Encodes both playerID and additional data.

        /// <summary>
        /// 0 to 1023
        /// </summary>
        public ushort playerID// (0-1023)
        {
            get => (ushort)(data & 0x03FF); // Extract the lower 10 bits for PlayerID
            set => data = (ushort)((data & 0xFC00) | (value & 0x03FF)); // Set PlayerID while preserving upper 6 bits
        }

        /// <summary>
        /// 0 to 63
        /// </summary>
        public byte AdditionalData// (0–63)
        {
            get => (byte)((data >> 10) & 0x3F); // Extract the upper 6 bits for AdditionalData
            set => data = (ushort)((data & 0x03FF) | ((value & 0x3F) << 10)); // Set AdditionalData while preserving lower 10 bits
        }

        public void Deserialize(NetDataReader Writer)
        {
            Writer.Get(out data); // Read the entire ushort value
        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(data); // Write the entire ushort value
        }
    }
}
