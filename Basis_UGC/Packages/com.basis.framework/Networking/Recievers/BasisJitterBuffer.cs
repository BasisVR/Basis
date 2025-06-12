using System.Collections.Concurrent;
using System.Collections.Generic;

[System.Serializable]
public class BasisJitterBuffer
{
    public ConcurrentDictionary<byte, BasisSequencedVoiceData> voiceData = new ConcurrentDictionary<byte, BasisSequencedVoiceData>();

    public void Insert(BasisSequencedVoiceData sequencedVoiceData, byte lastReadIndex)
    {
        if (voiceData.Count > 5)
        {
            voiceData.Clear();
        }
        // Insert the new data
        voiceData[sequencedVoiceData.SequenceNumber] = sequencedVoiceData;
    }
    public bool DumpIndividual(out BasisSequencedVoiceData SequencedVoiceData, byte LastReadIndex)
    {
        while (voiceData.Count != 0)
        {
            LastReadIndex += (byte)(LastReadIndex + 1);

            if (LastReadIndex > 63)
            {
                LastReadIndex = 0;
            }
            if (voiceData.Remove(LastReadIndex, out SequencedVoiceData))
            {

                return true;
            }
        }
        SequencedVoiceData = new BasisSequencedVoiceData(LastReadIndex, null, 0, true);
        return false;
    }
    public bool IsBufferFull()
    {
        if (voiceData.Count >= RemoteOpusSettings.JitterBufferSize)
        {
            return true;
        }
        return false;
    }
    public static bool IsAheadOf(byte current, byte next, out string Error)
    {
        // Calculate the difference with proper wraparound
        int diff = (next - current + 64) % 64;

        // Debugging output to help track the difference
        //  Console.WriteLine($"current: {current}, next: {next}, diff: {diff}");

        // Check if next is ahead of current and not too far ahead in the circular buffer
        if (diff > 0 && diff <= 31)
        {
            Error = string.Empty;  // No error, next is ahead
            return true;
        }
        else if (diff == 63)  // Allow diff == 63 as valid ahead (just before wrapping around)
        {
            Error = string.Empty;  // No error, next is ahead
            return true;
        }
        else
        {
            // Determine why it is behind or too far ahead
            if (diff == 0)
            {
                Error = "next is the same as current";
            }
            else if (diff > 31)
            {
                Error = "next is too far ahead (more than halfway around)";
            }
            else
            {
                Error = "next is behind current";
            }

            return false;  // next is behind or too far ahead
        }
    }
    /*
     *         public void OnDecode(byte SequenceNumber, byte[] data, int length)
        {
            byte[] CopiedData = new byte[length];
            Array.Copy(data, CopiedData, length);
            SequencedVoiceData Seq = new SequencedVoiceData(SequenceNumber, CopiedData, length, false);

         //   BasisJitterBuffer.Insert(Seq, lastReadIndex);
         //   CollectReadySamples();
        }
        public void OnDecodeSilence(byte SequenceNumber)
        {
            SequencedVoiceData Seq = new SequencedVoiceData(SequenceNumber, null, 0, true);
           // BasisJitterBuffer.Insert(Seq, lastReadIndex);
           // CollectReadySamples();
        }
     */
    /*
public void CollectReadySamples()
{
    if (BasisJitterBuffer.voiceData.Count >= 5)
    {
        if (BasisJitterBuffer.DumpIndividual(out SequencedVoiceData SVD, lastReadIndex))
        {
            lastReadIndex = SVD.SequenceNumber;
            if (SVD.IsInsertedSilence)
            {
                InOrderRead.Add(silentData, silentData.Length);
            }
            else
            {
                pcmLength = decoder.Decode(SVD.Array, SVD.Length, pcmBuffer, RemoteOpusSettings.NetworkSampleRate, false);
                InOrderRead.Add(pcmBuffer, pcmLength);
            }
        }
        else
        {
            InOrderRead.Add(silentData, silentData.Length);

        }
    }
}
*/
}
