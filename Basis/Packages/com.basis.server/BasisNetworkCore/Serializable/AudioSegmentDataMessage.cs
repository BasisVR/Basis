using Basis.Network.Core;

public static partial class SerializableBasis
{
    [System.Serializable]
    public struct AudioSegmentDataMessage
    {
        public byte SequenceNumber;
        public byte TotalPlayedInSilence;
        public byte[] buffer;
        public int TotalLength;
        public int LengthUsed;
        public void Deserialize(NetDataReader Writer)
        {
            SequenceNumber = Writer.GetByte();
            TotalPlayedInSilence = Writer.GetByte();
            if (Writer.EndOfData)
            {
                // Keep the retained buffer; consumers gate on LengthUsed, and nulling here would
                // throw the reused allocation away across a talk → silence → talk cycle.
                TotalLength = 0;
                LengthUsed = 0;
            }
            else
            {
                // Copied into a retained buffer rather than GetRemainingBytes(): this struct is
                // recycled (client queue, server pool) and every consumer copies out via
                // LengthUsed, so a fresh byte[] per voice packet was pure garbage. Grow-only, so
                // a steady stream settles on one allocation.
                int available = Writer.AvailableBytes;
                if (buffer == null || buffer.Length < available)
                {
                    buffer = new byte[available];
                }
                Writer.GetBytes(buffer, available);
                TotalLength = available;
                LengthUsed = available;
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(SequenceNumber);
            Writer.Put(TotalPlayedInSilence);
            if (LengthUsed != 0)
            {
                Writer.Put(buffer, 0, LengthUsed);
            }
        }
    }
}