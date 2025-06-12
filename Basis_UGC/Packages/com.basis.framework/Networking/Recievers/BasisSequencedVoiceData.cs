[System.Serializable]
public struct BasisSequencedVoiceData
{
    public byte SequenceNumber;
    public byte[] Array;
    public int Length;
    public bool IsInsertedSilence;

    public BasisSequencedVoiceData(byte sequenceNumber, byte[] array, int length, bool isInsertedSilence)
    {
        SequenceNumber = sequenceNumber;
        Array = array;
        Length = length;
        IsInsertedSilence = isInsertedSilence;
    }
}
