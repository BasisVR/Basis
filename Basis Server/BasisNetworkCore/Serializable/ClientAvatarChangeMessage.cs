using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ClientAvatarChangeMessage
    {
        // Downloading - attempts to download from a URL, make sure a hash also exists.
        // BuiltIn - loads as an addressable in Unity.
        public byte loadMode;
        public byte[] byteArray;
        //we increment this and then wrap around when > 255
        public byte LocalAvatarIndex;
        public void Deserialize(NetDataReader Writer)
        {
            // Read the load mode
            loadMode = Writer.GetByte();
            // Initialize the byte array with the specified length
            ushort Length = Writer.GetUShort();
            if (byteArray == null || byteArray.Length != Length)
            {
                byteArray = new byte[Length];
            }

            // Read each byte manually into the array
            Writer.GetBytes(byteArray, 0, byteArray.Length);
            LocalAvatarIndex = Writer.GetByte();
        }
        public void Serialize(NetDataWriter Writer)
        {
            // Write the load mode
            Writer.Put(loadMode);
            if (byteArray == null)
            {
                Writer.Put((ushort)0);
            }
            else
            {
                Writer.Put((ushort)byteArray.Length);
                Writer.Put(byteArray);
            }
            Writer.Put(LocalAvatarIndex);
        }
    }
}
