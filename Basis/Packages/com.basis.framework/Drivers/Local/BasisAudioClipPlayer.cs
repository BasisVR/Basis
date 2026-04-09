#if UNITY_SERVER
using Basis.Network.Core;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// Headless audio clip player for stress testing. Loads .wav or .opus files from a directory,
/// picks one randomly, Opus-encodes it, and sends it over the network as voice audio.
/// Self-contained: has its own Opus encoder and sends directly via the network peer.
///
/// Place .wav or .opus files in: {Application.dataPath}/AudioClips/
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

    private static OpusSharp.Core.Interfaces.IOpusEncoder encoder;
    private static AudioSegmentDataMessage segment;
    private static NetDataWriter writer;
    private static byte sequenceNumber;

    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const float FrameDurationSeconds = 0.02f; // 20ms
    private const float PlaybackGain = 0.3f;
    private static readonly int FrameSize = (int)(FrameDurationSeconds * SampleRate); // 960

    /// <summary>
    /// Directory to scan for .wav or .opus files. Defaults to {Application.dataPath}/AudioClips/
    /// </summary>
    public static string ClipDirectory;


    public static OpusSharp.Core.Interfaces.IOpusDecoder decoder;

    /// <summary>
    /// Attempts to initialize the clip player. If the AudioClips directory exists and
    /// contains supported audio files, a random clip is loaded and streamed as voice audio.
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

        string[] files = FindSupportedAudioFiles(dir);
        if (files.Length == 0)
        {
            BasisDebug.LogError("[AudioClipPlayer] Failed to find a supported audio file (.wav or .opus).", BasisDebug.LogTag.Device);
            return false;
        }

        string chosen = files[UnityEngine.Random.Range(0, files.Length)];
        BasisDebug.Log($"[AudioClipPlayer] Loading: {Path.GetFileName(chosen)}", BasisDebug.LogTag.Device);

        clipSamples = LoadAudioAsMono48k(chosen);
        if (clipSamples == null || clipSamples.Length == 0)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Failed to load: {chosen}", BasisDebug.LogTag.Device);
            return false;
        }
#if UNITY_IOS && !UNITY_EDITOR
            encoder = new OpusSharp.Core.Static.OpusEncoder(
    LocalOpusSettings.MicrophoneSampleRate,
    LocalOpusSettings.Channels,
    LocalOpusSettings.OpusApplication
);
        encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, 32000);
        encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
#else

        encoder = new OpusSharp.Core.Dynamic.OpusEncoder(
            LocalOpusSettings.MicrophoneSampleRate,
            LocalOpusSettings.Channels,
            LocalOpusSettings.OpusApplication
        );
        encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, 32000);
        encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
#endif

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
                    frameBuffer[i] = clipSamples[clipPosition] * PlaybackGain;
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
    /// Loads a supported audio file and returns 48kHz mono float samples.
    /// </summary>
    private static float[] LoadAudioAsMono48k(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return LoadWavAsMono48k(path);
        }

        if (string.Equals(extension, ".opus", StringComparison.OrdinalIgnoreCase))
        {
            return LoadOpusAsMono48k(path);
        }

        BasisDebug.LogWarning($"[AudioClipPlayer] Unsupported audio file extension: {extension}", BasisDebug.LogTag.Device);
        return null;
    }

    private static string[] FindSupportedAudioFiles(string directory)
    {
        List<string> files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string extension = Path.GetExtension(file);
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".opus", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        return files.ToArray();
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

    /// <summary>
    /// Loads an Ogg Opus file and returns 48kHz mono float samples.
    /// Supports mono and stereo Opus streams using channel mapping family 0.
    /// </summary>
    private static float[] LoadOpusAsMono48k(string path)
    {
        try
        {
            byte[] fileBytes = File.ReadAllBytes(path);
            if (fileBytes.Length < 64)
            {
                return null;
            }

            List<byte[]> packets = ReadOggPackets(fileBytes);
            if (packets == null || packets.Count == 0)
            {
                return null;
            }

            if (!TryParseOpusHead(packets[0], out int channels, out int preSkipSamples))
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Invalid OpusHead packet.", BasisDebug.LogTag.Device);
                return null;
            }

            if (channels < 1 || channels > 2)
            {
                BasisDebug.LogWarning($"[AudioClipPlayer] Only mono and stereo Opus streams are supported. Channels={channels}", BasisDebug.LogTag.Device);
                return null;
            }

            int audioPacketStart = 1;
            if (packets.Count > 1 && IsPacketNamed(packets[1], "OpusTags"))
            {
                audioPacketStart = 2;
            }

            if (audioPacketStart >= packets.Count)
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Opus file contains headers but no audio packets.", BasisDebug.LogTag.Device);
                return null;
            }

            const int MaxOpusFrameSize = 5760; // 120ms at 48kHz
            float[] decodeBuffer = new float[MaxOpusFrameSize * channels];
            List<float> monoSamples = new List<float>();
            int remainingPreSkip = preSkipSamples;
#if UNITY_IOS && !UNITY_EDITOR
            // iOS requires statically linked Opus library
            decoder = new OpusSharp.Core.Static.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#else
            decoder = new OpusSharp.Core.Dynamic.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#endif
            for (int packetIndex = audioPacketStart; packetIndex < packets.Count; packetIndex++)
            {
                byte[] packet = packets[packetIndex];
                if (packet.Length == 0)
                {
                    continue;
                }

                int decodedSamples = decoder.Decode(packet, packet.Length, decodeBuffer, MaxOpusFrameSize, false);
                int sampleStart = 0;
                if (remainingPreSkip > 0)
                {
                    sampleStart = Math.Min(decodedSamples, remainingPreSkip);
                    remainingPreSkip -= sampleStart;
                }

                for (int sample = sampleStart; sample < decodedSamples; sample++)
                {
                    if (channels == 1)
                    {
                        monoSamples.Add(decodeBuffer[sample]);
                    }
                    else
                    {
                        int offset = sample * channels;
                        monoSamples.Add((decodeBuffer[offset] + decodeBuffer[offset + 1]) * 0.5f);
                    }
                }
            }

            if (remainingPreSkip > 0)
            {
                BasisDebug.LogWarning($"[AudioClipPlayer] Opus pre-skip exceeded decoded audio by {remainingPreSkip} samples.", BasisDebug.LogTag.Device);
            }

            return monoSamples.ToArray();
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Opus load error: {ex.Message}", BasisDebug.LogTag.Device);
            return null;
        }
    }

    private static List<byte[]> ReadOggPackets(byte[] fileBytes)
    {
        List<byte[]> packets = new List<byte[]>();
        List<byte> packetBuffer = null;
        int position = 0;

        while (position < fileBytes.Length)
        {
            if (position + 27 > fileBytes.Length)
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Truncated Ogg page header.", BasisDebug.LogTag.Device);
                return null;
            }

            if (fileBytes[position] != 'O' ||
                fileBytes[position + 1] != 'g' ||
                fileBytes[position + 2] != 'g' ||
                fileBytes[position + 3] != 'S')
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Invalid Ogg page signature.", BasisDebug.LogTag.Device);
                return null;
            }

            if (fileBytes[position + 4] != 0)
            {
                BasisDebug.LogWarning($"[AudioClipPlayer] Unsupported Ogg bitstream version: {fileBytes[position + 4]}", BasisDebug.LogTag.Device);
                return null;
            }

            byte headerType = fileBytes[position + 5];
            int pageSegments = fileBytes[position + 26];
            int segmentTableOffset = position + 27;
            int payloadOffset = segmentTableOffset + pageSegments;
            if (payloadOffset > fileBytes.Length)
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Invalid Ogg segment table.", BasisDebug.LogTag.Device);
                return null;
            }

            int payloadSize = 0;
            for (int index = 0; index < pageSegments; index++)
            {
                payloadSize += fileBytes[segmentTableOffset + index];
            }

            if (payloadOffset + payloadSize > fileBytes.Length)
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Truncated Ogg page payload.", BasisDebug.LogTag.Device);
                return null;
            }

            bool continuedPacket = (headerType & 0x01) != 0;
            if (continuedPacket && packetBuffer == null)
            {
                packetBuffer = new List<byte>();
            }
            else if (!continuedPacket && packetBuffer != null && packetBuffer.Count > 0)
            {
                BasisDebug.LogWarning("[AudioClipPlayer] Encountered a non-continued page while a packet was still open.", BasisDebug.LogTag.Device);
                return null;
            }

            packetBuffer ??= new List<byte>();
            int payloadPosition = payloadOffset;

            for (int index = 0; index < pageSegments; index++)
            {
                int segmentSize = fileBytes[segmentTableOffset + index];
                if (segmentSize > 0)
                {
                    packetBuffer.AddRange(new ArraySegment<byte>(fileBytes, payloadPosition, segmentSize));
                }

                payloadPosition += segmentSize;
                if (segmentSize < 255)
                {
                    packets.Add(packetBuffer.ToArray());
                    packetBuffer.Clear();
                }
            }

            position = payloadOffset + payloadSize;
        }

        if (packetBuffer != null && packetBuffer.Count > 0)
        {
            BasisDebug.LogWarning("[AudioClipPlayer] Ogg stream ended with an incomplete packet.", BasisDebug.LogTag.Device);
            return null;
        }

        return packets;
    }

    private static bool TryParseOpusHead(byte[] packet, out int channels, out int preSkipSamples)
    {
        channels = 0;
        preSkipSamples = 0;

        if (!IsPacketNamed(packet, "OpusHead") || packet.Length < 19)
        {
            return false;
        }

        channels = packet[9];
        preSkipSamples = BitConverter.ToUInt16(packet, 10);
        int mappingFamily = packet[18];
        if (mappingFamily != 0)
        {
            BasisDebug.LogWarning($"[AudioClipPlayer] Unsupported Opus channel mapping family: {mappingFamily}", BasisDebug.LogTag.Device);
            return false;
        }

        return true;
    }

    private static bool IsPacketNamed(byte[] packet, string name)
    {
        if (packet == null || packet.Length < name.Length)
        {
            return false;
        }

        for (int index = 0; index < name.Length; index++)
        {
            if (packet[index] != name[index])
            {
                return false;
            }
        }

        return true;
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
