using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct BasisAvatarCloneRequest
    {
        public ushort requestingUser;
        public void Deserialize(NetDataReader NetDataReader)
        {
            requestingUser = NetDataReader.GetUShort();
        }
        public void Serialize(NetDataWriter NetDataWriter)
        {
            NetDataWriter.Put(requestingUser);
        }
    }
    public struct BasisAvatarCloneResponse
    {
        public ushort requestingUser;
        public void Deserialize(NetDataReader NetDataReader)
        {
            requestingUser = NetDataReader.GetUShort();
        }
        public void Serialize(NetDataWriter NetDataWriter)
        {
            NetDataWriter.Put(requestingUser);
        }
    }
}
