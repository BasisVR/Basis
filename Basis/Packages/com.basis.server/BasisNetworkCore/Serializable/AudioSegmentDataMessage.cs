using Basis.Network.Core;

public static partial class SerializableBasis
{
    [System.Serializable]
    public struct AudioSegmentDataMessage
    {
        public byte TotalPlayedInSilence;
        public byte[] buffer;
        public int TotalLength;
        public int LengthUsed;
        public void Deserialize(NetDataReader Writer)
        {
            TotalPlayedInSilence = Writer.GetByte();
            if (Writer.EndOfData)
            {
                LengthUsed = 0;
            }
            else
            {
                if (TotalLength == Writer.AvailableBytes)
                {
                    Writer.GetBytes(buffer, 0, Writer.AvailableBytes);
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
            Writer.Put(TotalPlayedInSilence);
            if (LengthUsed != 0)
            {
                Writer.Put(buffer, 0, LengthUsed);
                //  BNL.Log("Put Length was " + LengthUsed);
            }
        }
    }
}
