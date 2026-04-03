#if UNITY_SERVER
using System;
using System.IO;
using System.Threading;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using OpusSharp.Core;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// Headless audio clip player for stress testing. Loads .wav files from a directory,
/// picks one randomly, Opus-encodes it, and sends it over the network as voice audio.
/// Self-contained: has its own Opus encoder and sends directly via the network peer.
///
/// Place .wav files in: {Application.dataPath}/AudioClips/
/// If the directory is missing or empty, no audio is sent (silent headless as usual).
///
/// Designed for testing what 1000+ simultaneous audio sources sound and look like.
/// Each headless client picks a random clip and loops it over the network.
/// </summary>
public static class BasisAudioClipPlayer
{
    public static bool IsActive { get; private set; }

    private static float[] clipSamples;
    private static int clipPosition;
    private static Thread playbackThread;
    private static volatile bool shouldRun;

    private static OpusEncoder encoder;
    private static AudioSegmentDataMessage segment;
    private static NetDataWriter writer;
    private static byte sequenceNumber;

    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const float FrameDurationSeconds = 0.02f; // 20ms
    private static readonly int FrameSize = (int)(FrameDurationSeconds * SampleRate); // 960

    /// <summary>
    /// Directory to scan for .wav files. Defaults to {Application.dataPath}/AudioClips/
    /// </summary>
    public static string ClipDirectory;

    /// <summary>
    /// Attempts to initialize the clip player. If the AudioClips directory exists and
    /// contains .wav files, a random clip is loaded and streamed as voice audio.
    /// If the directory is missing or empty, this is a no-op (silent headless as usual).
    /// </summary>
    public static bool TryInitialize()
    {
        if (IsActive)
        {
            return true;
        }

        string dir = ClipDirectory ?? Path.Combine(Application.dataPath, "AudioClips");
        BasisDebug.Log($"[AudioClipPlayer] Booting up. AudioClips directory: {dir}", BasisDebug.LogTag.Device);

        if (!Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                BasisDebug.Log($"[AudioClipPlayer] Created AudioClips directory: {dir}", BasisDebug.LogTag.Device);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[AudioClipPlayer] Failed to create AudioClips directory: {dir} - {ex.Message}", BasisDebug.LogTag.Device);
            }
            return false;
        }

        string[] files = Directory.GetFiles(dir, "*.wav");
        if (files.Length == 0)
        {
            BasisDebug.LogError($"[AudioClipPlayer] failed to find and .wav", BasisDebug.LogTag.Device);
            return false;
        }

        string chosen = files[UnityEngine.Random.Range(0, files.Length)];
        BasisDebug.Log($"[AudioClipPlayer] Loading: {Path.GetFileName(chosen)}", BasisDebug.LogTag.Device);

        clipSamples = LoadWavAsMono48k(chosen);
        if (clipSamples == null || clipSamples.Length == 0)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Failed to load: {chosen}", BasisDebug.LogTag.Device);
            return false;
        }

        // Initialize Opus encoder
        encoder = new OpusEncoder(SampleRate, Channels, OpusPredefinedValues.OPUS_APPLICATION_AUDIO, use_static: false);
        encoder.Ctl(EncoderCTL.OPUS_SET_BITRATE, 32000);
        encoder.Ctl(EncoderCTL.OPUS_SET_COMPLEXITY, 5);

        // Initialize send buffers
        int packetSize = FrameSize * 4;
        segment = new AudioSegmentDataMessage
        {
            buffer = new byte[packetSize],
            TotalLength = packetSize
        };
        writer = new NetDataWriter();
        sequenceNumber = 0;

        clipPosition = 0;
        shouldRun = true;
        IsActive = true;

        playbackThread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "HeadlessAudioClipPlayer"
        };
        playbackThread.Start();

        BasisDebug.Log($"[AudioClipPlayer] Active: {Path.GetFileName(chosen)} ({clipSamples.Length} samples, looping at {SampleRate}Hz)", BasisDebug.LogTag.Device);
        return true;
    }

    /// <summary>
    /// Stop the clip player and clean up resources.
    /// </summary>
    public static void DeInitialize()
    {
        shouldRun = false;
        IsActive = false;

        if (playbackThread != null && playbackThread.IsAlive)
        {
            playbackThread.Join(500);
        }
        playbackThread = null;
        clipSamples = null;
        clipPosition = 0;

        encoder?.Dispose();
        encoder = null;
    }

    /// <summary>
    /// Background thread that encodes one audio frame (20ms / 960 samples) with Opus
    /// and sends it directly over the network as voice data every interval.
    /// Waits for the network peer to be available before sending.
    /// </summary>
    private static void PlaybackLoop()
    {
        BasisDebug.Log("[AudioClipPlayer] Playback thread started", BasisDebug.LogTag.Device);
        try
        {
            long intervalTicks = (long)(FrameDurationSeconds * System.Diagnostics.Stopwatch.Frequency);
            float[] frameBuffer = new float[FrameSize];
            bool loggedFirstSend = false;
            int peerNullCount = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextFrameTick = sw.ElapsedTicks;

            while (shouldRun)
            {
                // Sleep until the next frame boundary, compensating for encode/send time
                long now = sw.ElapsedTicks;
                long waitTicks = nextFrameTick - now;
                if (waitTicks > 0)
                {
                    int waitMs = (int)(waitTicks * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (waitMs > 0)
                        Thread.Sleep(waitMs);
                }
                nextFrameTick += intervalTicks;

                // If we fell behind by more than 5 frames, reset to avoid burst-sending
                if (sw.ElapsedTicks - nextFrameTick > intervalTicks * 5)
                    nextFrameTick = sw.ElapsedTicks;

                if (!shouldRun || clipSamples == null || encoder == null)
                    break;

                // Fill frame from clip (looping)
                for (int i = 0; i < FrameSize; i++)
                {
                    frameBuffer[i] = clipSamples[clipPosition];
                    clipPosition++;
                    if (clipPosition >= clipSamples.Length)
                    {
                        clipPosition = 0;
                    }
                }

                NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
                if (peer == null)
                {
                    peerNullCount++;
                    if (peerNullCount % 250 == 1)
                    {
                        BasisDebug.LogWarning($"[AudioClipPlayer] Waiting for network peer... ({peerNullCount} frames skipped)", BasisDebug.LogTag.Device);
                    }

                    continue;
                }

                // Encode with Opus
                segment.LengthUsed = encoder.Encode(frameBuffer, FrameSize, segment.buffer, segment.TotalLength);
                segment.SequenceNumber = sequenceNumber++;
                segment.TotalPlayedInSilence = 0;

                if (!loggedFirstSend)
                {
                    loggedFirstSend = true;
                    BasisDebug.Log($"[AudioClipPlayer] First packet sent. Encoded {segment.LengthUsed} bytes, seq={segment.SequenceNumber}, peer={peer.Id}", BasisDebug.LogTag.Device);
                }

                // Send on voice channel
                writer.Reset();
                segment.Serialize(writer);
                peer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Unreliable);
            }

            BasisDebug.Log("[AudioClipPlayer] Playback thread exiting normally", BasisDebug.LogTag.Device);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Playback thread crashed: {ex}", BasisDebug.LogTag.Device);
        }
    }

    /// <summary>
    /// Loads a PCM WAV file and returns 48kHz mono float samples.
    /// Supports 8-bit, 16-bit, 24-bit, and 32-bit PCM WAV formats.
    /// Non-48kHz files are resampled via linear interpolation.
    /// </summary>
    private static float[] LoadWavAsMono48k(string path)
    {
        try
        {
            byte[] fileBytes = File.ReadAllBytes(path);
            if (fileBytes.Length < 44)
                return null;

            if (fileBytes[0] != 'R' || fileBytes[1] != 'I' || fileBytes[2] != 'F' || fileBytes[3] != 'F')
                return null;
            if (fileBytes[8] != 'W' || fileBytes[9] != 'A' || fileBytes[10] != 'V' || fileBytes[11] != 'E')
                return null;

            int pos = 12;
            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = 0;
            int dataSize = 0;

            while (pos < fileBytes.Length - 8)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(fileBytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);

                if (chunkId == "fmt ")
                {
                    int audioFormat = BitConverter.ToInt16(fileBytes, pos + 8);
                    if (audioFormat != 1)
                    {
                        BasisDebug.LogWarning("[AudioClipPlayer] Only PCM WAV files are supported.", BasisDebug.LogTag.Device);
                        return null;
                    }
                    channels = BitConverter.ToInt16(fileBytes, pos + 10);
                    sampleRate = BitConverter.ToInt32(fileBytes, pos + 12);
                    bitsPerSample = BitConverter.ToInt16(fileBytes, pos + 22);
                }
                else if (chunkId == "data")
                {
                    dataOffset = pos + 8;
                    dataSize = chunkSize;
                    break;
                }

                pos += 8 + chunkSize;
                if (chunkSize % 2 != 0) pos++;
            }

            if (dataOffset == 0 || dataSize == 0 || channels == 0 || sampleRate == 0 || bitsPerSample == 0)
                return null;

            int bytesPerSample = bitsPerSample / 8;
            int totalFrames = dataSize / (bytesPerSample * channels);

            float[] monoSamples = new float[totalFrames];
            for (int i = 0; i < totalFrames; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int offset = dataOffset + (i * channels + ch) * bytesPerSample;
                    if (offset + bytesPerSample > fileBytes.Length) break;

                    float sample = bitsPerSample switch
                    {
                        8 => (fileBytes[offset] - 128) / 128f,
                        16 => BitConverter.ToInt16(fileBytes, offset) / 32768f,
                        24 => DecodeInt24(fileBytes, offset) / 8388608f,
                        32 => BitConverter.ToInt32(fileBytes, offset) / 2147483648f,
                        _ => 0f
                    };
                    sum += sample;
                }
                monoSamples[i] = sum / channels;
            }

            if (sampleRate != SampleRate)
            {
                BasisDebug.Log($"[AudioClipPlayer] Resampling from {sampleRate}Hz to {SampleRate}Hz", BasisDebug.LogTag.Device);
                monoSamples = Resample(monoSamples, sampleRate, SampleRate);
            }

            return monoSamples;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[AudioClipPlayer] WAV load error: {ex.Message}", BasisDebug.LogTag.Device);
            return null;
        }
    }

    private static int DecodeInt24(byte[] data, int offset)
    {
        int val = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
        return val;
    }

    private static float[] Resample(float[] source, int sourceSampleRate, int targetSampleRate)
    {
        double ratio = (double)sourceSampleRate / targetSampleRate;
        int targetLength = (int)(source.Length / ratio);
        float[] result = new float[targetLength];

        for (int i = 0; i < targetLength; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            float s0 = source[Mathf.Min(srcIndex, source.Length - 1)];
            float s1 = source[Mathf.Min(srcIndex + 1, source.Length - 1)];
            result[i] = (float)(s0 + (s1 - s0) * frac);
        }

        return result;
    }
}
#endif
