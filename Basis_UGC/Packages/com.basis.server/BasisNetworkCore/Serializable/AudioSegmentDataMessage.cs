using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    [System.Serializable]
    public struct AudioSegmentDataMessage
    {
        public byte[] buffer;
        public int TotalLength;
        public int LengthUsed;
        public AudioSegmentDataMessage(byte[] buffer) : this()
        {
            this.buffer = buffer;
            TotalLength = buffer.Length;
        }
        public void Deserialize(NetDataReader Writer)
        {
            if (Writer.EndOfData)
            {
                LengthUsed = 0;
            }
            else
            {
                if (TotalLength == Writer.AvailableBytes)
                {
                    Writer.GetBytes(buffer, Writer.AvailableBytes);
                    LengthUsed = TotalLength;
                }
                else
                {
                    buffer = Writer.GetRemainingBytes();
                    TotalLength = buffer.Length;
                    LengthUsed = TotalLength;
                }
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            if (LengthUsed != 0)
            {
                Writer.Put(buffer, 0, LengthUsed);
                //  BNL.Log("Put Length was " + LengthUsed);
            }
        }
    }
}
