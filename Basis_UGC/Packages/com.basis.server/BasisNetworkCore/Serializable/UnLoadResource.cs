using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct UnLoadResource
    {
        /// <summary>
        /// 0 = Game object, 1 = Scene,
        /// </summary>
        public byte Mode;
        public string LoadedNetID;
        public bool Deserialize(NetDataReader Writer)
        {
            int Bytes = Writer.AvailableBytes;
            if (Writer.TryGetByte(out Mode) == false)
            {
                return false;
            }

            if (Writer.TryGetString(out LoadedNetID) == false)
            {
                return false;
            }
            return true;
        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(Mode);
            Writer.Put(LoadedNetID);
        }
    }
}
