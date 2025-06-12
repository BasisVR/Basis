using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct VoiceReceiversMessage
    {
        public ushort[] users;
        public void Deserialize(NetDataReader Writer)
        {
            // Calculate the number of ushorts based on the remaining bytes
            int remainingBytes = Writer.AvailableBytes;
            int ushortCount = remainingBytes / sizeof(ushort);

            // Initialize the array with the calculated size
            if (users == null || users.Length != ushortCount)
            {
                users = new ushort[ushortCount];
            }
            // Read each ushort value into the array
            for (int index = 0; index < ushortCount; index++)
            {
                users[index] = Writer.GetUShort();
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            if (users != null)
            {
                int Count = users.Length;
                for (int Index = 0; Index < Count; Index++)
                {
                    Writer.Put(users[Index]);
                }
            }
        }
    }
}
