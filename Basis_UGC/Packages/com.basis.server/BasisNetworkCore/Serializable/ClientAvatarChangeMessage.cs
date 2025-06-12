using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct ClientAvatarChangeMessage
    {
        // Downloading - attempts to download from a URL, make sure a hash also exists.
        // BuiltIn - loads as an addressable in Unity.
        public byte loadMode;
        public byte[] byteArray;
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
        }
    }
}

/*
if (AvatarLoadMessages == null)
{
TotalStoredMessages = 0;
Writer.Put(TotalStoredMessages);
}
else
{
TotalStoredMessages = (byte)AvatarLoadMessages.Length;
Writer.Put(AvatarLoadMessages.Length);
for (int Index = 0; Index < TotalStoredMessages; Index++)
{
AvatarLoadMessages[Index].Serialize(Writer);
}
}
   */
/*
TotalStoredMessages = Writer.GetByte();

AvatarLoadMessages = new AvatarLoadDataMessage[TotalStoredMessages];
for (int Index = 0; Index < TotalStoredMessages; Index++)
{
    AvatarLoadMessages[Index].Deserialize(Writer);
}
*/
//  public byte TotalStoredMessages;
//  public AvatarLoadDataMessage[] AvatarLoadMessages;
