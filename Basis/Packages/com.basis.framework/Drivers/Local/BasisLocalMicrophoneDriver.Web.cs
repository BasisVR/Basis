#if !BASIS_DISABLE_MICROPHONE && UNITY_WEBGL && !UNITY_EDITOR
using System;
using Basis.Scripts.Platform;
using OpenLipSync.Inference.Audio;
using Unity.Scripting.LifecycleManagement;

public static partial class BasisLocalMicrophoneDriver
{
    public const string WebMicrophoneDevice = "Browser Microphone";
    private const int WebReadCapacity = 24000;
    [NoAutoStaticsCleanup] private static AudioResampler webResampler, webLipSyncResampler;
    private static float[] webRead, webPending, webLipSync;
    private static int webPendingCount, webResamplerRate, webLastState, webDeviceVersion;
    private static string[] webDevices = new[] { WebMicrophoneDevice };
    private static readonly byte[] webDeviceNames = new byte[8192];

    public static string[] WebMicrophoneDevices
    {
        get
        {
            int version = BasisWebAudio.BasisWebAudio_MicDeviceVersion();
            if (version == webDeviceVersion) return webDevices;
            webDeviceVersion = version;
            int length = BasisWebAudio.BasisWebAudio_MicDevices(webDeviceNames, webDeviceNames.Length);
            string[] labels = length > 0 ? System.Text.Encoding.UTF8.GetString(webDeviceNames, 0, length).Split('\n') : Array.Empty<string>();
            string[] devices = new string[labels.Length + 1];
            devices[0] = WebMicrophoneDevice;
            Array.Copy(labels, 0, devices, 1, labels.Length);
            webDevices = devices;
            BasisDebug.Log($"[WebAudio] microphones: {string.Join(" | ", devices)}", BasisDebug.LogTag.Voice);
            return webDevices;
        }
    }

    private static bool ResetWebMicrophone(string requested)
    {
        lock (processingLock)
        {
            bool retryAfterError = webLastState == BasisWebAudio.MicError;
            StopSelectedMicrophone_Internal();
            if (IsPaused)
            {
                BasisDebug.Log("Microphone Is Paused");
                ClearStateAfterStop();
                MicrophoneDevice = null;
                return false;
            }
            int deviceIndex = Array.IndexOf(WebMicrophoneDevices, requested) - 1;
            string device = deviceIndex >= 0 ? requested : WebMicrophoneDevice;
            int state = BasisWebAudio.BasisWebAudio_MicStart(ProcessFrameSize, 1, 0, 0, deviceIndex);
            webLastState = state;
            if (state >= BasisWebAudio.MicDenied)
            {
                BasisDebug.LogWarningOnce($"Browser microphone unavailable (state {state}).");
                MicrophoneIsStarted = false;
                MicrophoneDevice = null;
                ClearStateAfterStop();
                return false;
            }
            BasisDebug.Log("Starting Microphone: " + device);
            channels = 1;
            LocalOpusSettings.EnsureProcessBuffer(ref processBufferArray, out ProcessFrameLength);
            CreateOrResizeArray(ProcessFrameLength, ref _denoiseDry);
            PrimeVolumeRamp();
            LocalOpusSettings.CreateOrResizeArray(LocalOpusSettings.rmsWindowSize, ref rmsValues);
            Array.Clear(rmsValues, 0, rmsValues.Length);
            rmsIndex = 0;
            averageRms = 0f;
            lock (ringLock)
            {
                head = 0;
                captured = 0;
                written = 0;
                bufferLength = LocalOpusSettings.RecordingFullLength * LocalOpusSettings.MicrophoneSampleRate;
                CreateOrResizeArray(ProcessFrameSize, ref _micDelta);
                LocalOpusSettings.CreateOrResizeArray(bufferLength, ref microphoneBufferArray);
                warmupSamples = ProcessFrameLength * 2;
                inWarmup = true;
                Array.Clear(_micDelta, 0, _micDelta.Length);
                Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
            }
            lock (stagingLock)
            {
                stagingReadChunk = 0;
                stagingWriteChunk = 0;
                stagingCount = 0;
            }
            Array.Clear(processBufferArray, 0, processBufferArray.Length);
            Array.Clear(_denoiseDry, 0, _denoiseDry.Length);
            Denoiser ??= new RNNoise.NET.Denoiser();
            webPendingCount = 0;
            webResampler?.Reset();
            webLipSyncResampler?.Reset();
            MicrophoneIsStarted = true;
            PacketSize = ProcessFrameLength * 4;
            ChangeMicrophoneVolume(SMDMicrophone.Current.Volume01);
            MicrophoneDevice = device;
            return !retryAfterError;
        }
    }

    private static void StopWebMicrophone()
    {
        BasisWebAudio.BasisWebAudio_MicStop();
        webPendingCount = 0;
        BasisDebug.Log("Stopped Microphone " + MicrophoneDevice);
    }

    private static bool WebMicrophoneNeedsRestart(string target)
    {
        if (!MicrophoneIsStarted || !string.Equals(MicrophoneDevice, target, StringComparison.Ordinal)) return webLastState != BasisWebAudio.MicDenied;
        int state = BasisWebAudio.BasisWebAudio_MicState();
        if (state != webLastState)
        {
            webLastState = state;
            if (state == BasisWebAudio.MicRunning)
            {
                _recoveryBackoffUntil = 0f;
                _recoveryBackoffSeconds = 0f;
                BasisDebug.Log($"Browser microphone running at {BasisWebAudio.BasisWebAudio_MicSampleRate()} Hz");
            }
            else if (state == BasisWebAudio.MicDenied) BasisDebug.LogWarning("Browser microphone permission was denied. Allow microphone access for this site and unmute to retry.");
        }
        return state == 0 || state == BasisWebAudio.MicError;
    }

    public static float[] WebLipSyncAudio(out int count)
    {
        float[] frame = processBufferArray;
        count = frame != null ? Math.Min(ProcessFrameSize, frame.Length) : 0;
        int rate = BasisOpenLipSyncDriver.InputSampleRate, source = LocalOpusSettings.MicrophoneSampleRate;
        if (count <= 0 || rate <= 0 || rate == source) return frame;
        if (webLipSyncResampler == null || webLipSyncResampler.InputSampleRate != source || webLipSyncResampler.OutputSampleRate != rate)
        {
            webLipSyncResampler?.Dispose();
            webLipSyncResampler = new AudioResampler(source, rate, filterTaps: 32, numPhases: 512);
        }
        ReadOnlySpan<float> samples = webLipSyncResampler.Resample(new ReadOnlySpan<float>(frame, 0, count));
        if (webLipSync == null || webLipSync.Length < samples.Length) webLipSync = new float[samples.Length + ProcessFrameSize];
        samples.CopyTo(webLipSync);
        count = samples.Length;
        return webLipSync;
    }

    public static int WebLipSyncSilence()
    {
        float[] frame = processBufferArray;
        int count = frame != null ? Math.Min(ProcessFrameSize, frame.Length) : 0, rate = BasisOpenLipSyncDriver.InputSampleRate, source = LocalOpusSettings.MicrophoneSampleRate;
        if (count <= 0 || rate <= 0 || rate == source) return count;
        webLipSyncResampler?.Reset();
        return (int)((long)count * rate / source);
    }

    private static void PumpWebCapture()
    {
        if (!MicrophoneIsStarted || microphoneBufferArray == null || bufferLength <= 0) return;
        int rate = BasisWebAudio.BasisWebAudio_MicSampleRate();
        int available = BasisWebAudio.BasisWebAudio_MicAvailable();
        if (rate <= 0 || available <= 0) return;
        webRead ??= new float[WebReadCapacity];
        webPending ??= new float[WebReadCapacity * 2 + ProcessFrameSize];
        int target = LocalOpusSettings.MicrophoneSampleRate;
        while (available > 0)
        {
            int read = BasisWebAudio.BasisWebAudio_MicRead(webRead, Math.Min(available, WebReadCapacity));
            if (read <= 0) break;
            available -= read;
            ReadOnlySpan<float> samples = webRead.AsSpan(0, read);
            if (rate != target)
            {
                if (webResampler == null || webResamplerRate != rate)
                {
                    webResampler?.Dispose();
                    webResampler = new AudioResampler(rate, target, filterTaps: 32, numPhases: 512);
                    webResamplerRate = rate;
                }
                samples = webResampler.Resample(samples);
            }
            if (webPendingCount + samples.Length > webPending.Length) webPendingCount = 0;
            samples.CopyTo(webPending.AsSpan(webPendingCount));
            webPendingCount += samples.Length;
            StageWebChunks();
        }
    }

    private static void StageWebChunks()
    {
        int consumed = 0;
        while (webPendingCount - consumed >= ProcessFrameSize)
        {
            lock (stagingLock)
            {
                if (stagingBuffer == null || stagingChunkStride != ProcessFrameSize)
                {
                    stagingBuffer = new float[StagingChunkCapacity * ProcessFrameSize];
                    stagingChunkStride = ProcessFrameSize;
                    stagingReadChunk = 0;
                    stagingWriteChunk = 0;
                    stagingCount = 0;
                }
                stagingChannels = 1;
                while (webPendingCount - consumed >= ProcessFrameSize && stagingCount < StagingChunkCapacity)
                {
                    Array.Copy(webPending, consumed, stagingBuffer, stagingWriteChunk * ProcessFrameSize, ProcessFrameSize);
                    stagingWriteChunk = (stagingWriteChunk + 1) % StagingChunkCapacity;
                    stagingCount++;
                    captured = (captured + ProcessFrameSize) % bufferLength;
                    consumed += ProcessFrameSize;
                }
            }
            ProcessPendingFrames();
        }
        if (consumed == 0) return;
        webPendingCount -= consumed;
        if (webPendingCount > 0) Array.Copy(webPending, consumed, webPending, 0, webPendingCount);
    }
}
#endif
