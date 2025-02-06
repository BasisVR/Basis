using System;
using Basis.Scripts.Device_Management;
using Concentus;

public class BasisAudioDecoder
{
    public event Action OnDecoded;
    IOpusDecoder decoder;
    public BasisOpusSettings Settings;
    public float[] pcmBuffer;
    public int ActualPcmLength;
    public int FakePcmLength;
    public void Initialize()
    {
        FakePcmLength = 2048;
        ActualPcmLength = 2048;
        Settings = BasisDeviceManagement.Instance.BasisOpusSettings;
        pcmBuffer = new float[FakePcmLength];//AudioDecoder.maximumPacketDuration now its 2048
        decoder = OpusCodecFactory.CreateDecoder(Settings.GetSampleFreq(),1);
    }
    public void Deinitalize()
    {
        if (decoder != null)
        {
            decoder.Dispose();
            decoder = null;
        }
    }

    /// <summary>
    /// decodes data into the pcm buffer
    /// note that the pcm buffer is always going to have more data then submited.
    /// the pcm length is how much was actually encoded.
    /// </summary>
    /// <param name="incomingSpan"></param>
    /*
    public void OnDecode(ReadOnlySpan<byte> incomingSpan, int length,Span<float> OutgoingData)
    {
        ActualPcmLength = decoder.Decode(incomingSpan, OutgoingData, length);
        OnDecoded?.Invoke();
    }
    */
    public void OnDecode(byte[] incomingSpan, int length)
    {
        Span<float> OutgoingData = pcmBuffer.AsSpan();
        ActualPcmLength = decoder.Decode(incomingSpan, OutgoingData, pcmBuffer.Length);
        pcmBuffer = OutgoingData.ToArray();
        OnDecoded?.Invoke();
    }
}
