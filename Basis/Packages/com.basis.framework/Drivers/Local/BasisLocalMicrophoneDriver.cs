#if !BASIS_DISABLE_MICROPHONE
using UnityEngine;
using System;
using System.Linq;
using Basis.Scripts.Device_Management;
using System.Threading;

public static class BasisLocalMicrophoneDriver
{
    private static int head = 0;
    private static int captured = 0;
    private static int bufferLength;

    public static bool HasEvents = false;
    public static int PacketSize;

    public static Action<bool> OnPausedAction;
    public static Action<bool> OnInitializedAction;

    private static bool MicrophoneIsStarted = false;
    private static Thread processingThread;
    // AutoResetEvent so WaitOne consumes the signal atomically. Avoids a lost-wakeup
    // race where MicrophoneUpdate.Set() lands between the worker's WaitOne return and
    // a manual Reset(), stalling one tick of processing.
    private static AutoResetEvent processingEvent = new AutoResetEvent(false);
    private static readonly object processingLock = new object();
    private static readonly object ringLock = new object();

    public const string MicrophoneState = "MicrophoneState";
    public const string SettingStartOff = "Muted";
    public const string SettingStartOn = "Unmuted";
    public const string SettingStartRememberLast = "Remember Last State";

    public const string SettingMuteShutdown = "Shutdown Microphone";
    public const string SettingMuteSuppress = "Keep Microphone Open";

    private static bool IsSuppressMuteMode =>
        Basis.BasisUI.BasisSettingsDefaults.MicMuteBehavior?.RawValue == SettingMuteSuppress;

    public static Action OnHasAudio;
    public static Action OnHasSilence;

    public static AudioClip clip;
    public static bool IsInitialize = false;
    public static string MicrophoneDevice = null;

    private const float DeviceScanIntervalSeconds = 2f;
    private static float _deviceScanTimer;
    private static string[] _knownDevices;

    private const float RecoveryBackoffMaxSeconds = 15f;
    private static float _recoveryBackoffUntil;
    private static float _recoveryBackoffSeconds;

    /// <summary>Linear amplitude multiplier (from dB mapping in ChangeMicrophoneVolume).</summary>
    public static float Volume = 1f;

    /// <summary>End-of-frame volume from the previous processed frame; used as the
    /// ramp start for the current frame so UI volume changes don't step between frames.</summary>
    private static float _prevVolume = 1f;

    [HideInInspector] public static float[] microphoneBufferArray;
    [HideInInspector] public static float[] processBufferArray;

    [HideInInspector] public static float[] rmsValues;
    public static int rmsIndex = 0;
    public static float averageRms;

    public static RNNoise.NET.Denoiser Denoiser = new RNNoise.NET.Denoiser();
    public static int minFreq = 48000;
    public static int maxFreq = 48000;

    /// <summary>
    /// Number of mono samples per process frame (e.g. 960 = 20ms at 48 kHz). NOT the
    /// audio sample rate in Hz — that lives in <see cref="LocalOpusSettings.MicrophoneSampleRate"/>.
    /// Used as the Opus encoder's frame_size argument and to size derived buffers.
    /// </summary>
    public static int ProcessFrameLength;

    public static Action MainThreadOnHasAudio;
    public static Action MainThreadOnHasSilence;

    private static int _scheduleMainHasAudio;   // 0/1
    private static int _scheduleMainHasSilence; // 0/1

    public static bool isPaused = false;

    private static CancellationTokenSource processingTokenSource;

    private static int warmupSamples = 0;
    private static bool inWarmup = false;

    public const int ProcessFrameSize = 960;  // 20ms at 48kHz
    public const int DenoiserFrameSize = 480; // 10ms at 48kHz

    private static readonly BasisMicrophoneAgc _agc = new BasisMicrophoneAgc();
    private static BasisMicrophoneAgc.Settings _agcSettings;
    private static float _prevAgcAmp = 1f;

    private static float AgcFrameSeconds => ProcessFrameSize / (float)LocalOpusSettings.MicrophoneSampleRate;

    /// <summary>Live AGC gain in dB, for the audio debug readout. 0 when AGC is off.</summary>
    public static float AgcGainDb => SMDMicrophone.Current.UseAGC ? _agc.GainDb : 0f;

    /// <summary>Live estimate of the talker's speech level, for the audio debug readout.</summary>
    public static float AgcSpeechLevel => _agc.SpeechLevel;

    /// <summary>Whether the AGC currently believes it has heard this talker speak.</summary>
    public static bool AgcHasSpeech => _agc.HasSpeechEstimate;

    /// <summary>The noise floor the gate is currently working against.</summary>
    public static float GateNoiseFloor => _gateNoiseFloor.NoiseFloor;

    /// <summary>The threshold the gate last used, whether auto-derived or manual.</summary>
    public static float GateThreshold => _lastGateThreshold;

    private const float AutoGateOverNoise = 2.5f;

    private static readonly BasisNoiseFloorTracker _gateNoiseFloor = new BasisNoiseFloorTracker();
    private static float _lastGateThreshold;

    private static float _noiseGateGain = 0f; // 0 = closed, 1 = open

    private static float[] _denoiseDry;
    private static float[] _tmp480;

    private static string _pendingDeviceWhenPaused = null;
    private static int channels = 1;
    // Small interleaved scratch sized to ProcessFrameSize * channels. Holds one chunk
    // pulled from the AudioClip ring per iteration, then staged raw for the worker.
    // Replaces a previous full-clip snapshot that copied ~192 KB every Unity tick.
    private static float[] _micDelta;

    // Raw interleaved capture staging. PumpCapture (main thread, top of the frame — the
    // Unity Microphone/AudioClip APIs are main-thread-only) enqueues whole 20 ms chunks
    // straight off the clip; the processing thread downmixes them into the mono ring and
    // carries everything from there (AGC, limiter, denoise, gate, Opus encode, network
    // send). This keeps the capture read as the ONLY main-thread stage of the pipeline.
    private static readonly object stagingLock = new object();
    private static float[] stagingBuffer;
    private static int stagingChunkStride; // ProcessFrameSize * channels at alloc time
    private static int stagingChannels = 1;
    private static int stagingReadChunk;
    private static int stagingWriteChunk;
    private static int stagingCount;
    private const int StagingChunkCapacity = 16; // 320 ms of worker-stall tolerance
    // Worker-only scratch a staged chunk is copied into, so the downmix runs outside stagingLock.
    private static float[] _stagingScratch;
    // Mono-ring write head, advanced by the worker as staged chunks are downmixed in.
    // `captured` stays the clip-read cursor and total-in-flight tail: the free-space
    // check against it naturally accounts for staged-but-not-yet-downmixed chunks.
    private static int written;
    private static bool IsPaused
    {
        get => isPaused;
        set
        {
            isPaused = value;
            PlayerPrefs.SetInt(MicrophoneState, isPaused ? 1 : 0);

            bool suppress = IsSuppressMuteMode;

            if (isPaused)
            {
                if (!suppress)
                    StopSelectedMicrophone();
            }
            else if (!suppress || !MicrophoneIsStarted)
            {
                string desired = SMDMicrophone.Current.Microphone;
                if (string.IsNullOrEmpty(desired)) desired = _pendingDeviceWhenPaused;
                if (string.IsNullOrEmpty(desired)) desired = MicrophoneDevice;

                if (!string.IsNullOrEmpty(desired))
                    ResetMicrophones(desired);

                _pendingDeviceWhenPaused = null;
            }

            OnPausedAction?.Invoke(isPaused);

#if UNITY_IOS && !UNITY_EDITOR
            Basis.Scripts.Platform.BasisIOSAudioSession.ReapplySettings();
#endif
        }
    }

    public static bool ResolvePausedFromSettings()
    {
        string behavior = Basis.BasisUI.BasisSettingsDefaults.MicStartBehavior.RawValue;
        switch (behavior)
        {
            case SettingStartOn:
                return false;
            case SettingStartRememberLast:
                return PlayerPrefs.GetInt(MicrophoneState, 1) == 1;
            case SettingStartOff:
            default:
                return true;
        }
    }

    public static bool Initialize()
    {
        if (IsInitialize) return true;
        try
        {
            isPaused = ResolvePausedFromSettings();
            RegisterEvents();

            // Load emits one change event; ApplyMicSettings reacts.
            SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
            _knownDevices = SMDMicrophone.MicrophoneDevices;
            _deviceScanTimer = 0f;

            StartProcessingThread();
            IsInitialize = true;
            OnInitializedAction?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Microphone Initialization Failed: {ex}");
            DeInitialize();
            return false;
        }
    }

    public static void DeInitialize()
    {
        if (!IsInitialize) return;

        StopProcessingThread();
        UnregisterEvents();
        StopSelectedMicrophone();

        Denoiser?.Dispose();
        Denoiser = null;

        _tmp480 = null;
        clip = null;

        _micDelta = null;
        microphoneBufferArray = null;
        processBufferArray = null;

        rmsValues = null;
        _denoiseDry = null;

        channels = 1;
        IsInitialize = false;
        OnInitializedAction?.Invoke(false);
        BasisDebug.Log("Microphone Driver Deinitialized.");
    }

    private static void RegisterEvents()
    {
        if (HasEvents) return;

        SMDMicrophone.OnMicrophoneSettingsChanged += ApplyMicSettings;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;

        HasEvents = true;
    }

    private static void UnregisterEvents()
    {
        if (!HasEvents) return;

        SMDMicrophone.OnMicrophoneSettingsChanged -= ApplyMicSettings;
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;

        HasEvents = false;
    }

    private static void OnBootModeChanged(string mode)
    {
        // Emits new snapshot
        SMDMicrophone.LoadInMicrophoneData(mode);
    }

    /// <summary>
    /// “Poke” handler: update job params + restart mic if device changed.
    /// No copying of settings into driver fields.
    /// </summary>
    private static void ApplyMicSettings(SMDMicrophone.MicSettings s)
    {
        // 1) Update Volume mapping
        ChangeMicrophoneVolume(s.Volume01);

        // 2) AdjustVolume reads the limiter values straight off the settings snapshot it
        //    is handed each frame, so only the AGC state needs touching here.
        lock (processingLock)
        {
            // AGC internal state reset when disabled
            if (!s.UseAGC)
            {
                _agc.Reset();
                _prevAgcAmp = 1f;
            }
        }

        // 3) Device switch
        if (IsPaused)
        {
            _pendingDeviceWhenPaused = s.Microphone;
            return;
        }

        if (!string.Equals(MicrophoneDevice, s.Microphone, StringComparison.Ordinal))
        {
            ResetMicrophones(s.Microphone);
        }
    }

    public static void ToggleIsPaused()
    {
        IsPaused = !IsPaused;
    }

    public static bool ResetMicrophones(string newMicrophone)
    {
        lock (processingLock)
        {
            processingEvent.Reset();

            if (string.IsNullOrEmpty(newMicrophone))
            {
                BasisDebug.LogError("Microphone was empty or null");
                return false;
            }
            if (Microphone.devices.Length == 0)
            {
                BasisDebug.LogError("No Microphones found!");
                return false;
            }
            if (!Microphone.devices.Contains(newMicrophone))
            {
                newMicrophone = Microphone.devices[0];
            }

            if (Microphone.IsRecording(newMicrophone))
            {
                Microphone.End(newMicrophone);
            }

            StopSelectedMicrophone_Internal();

            if (IsPaused)
            {
                BasisDebug.Log("Microphone Is Paused");
                ClearStateAfterStop();
                MicrophoneDevice = null;
                return false;
            }

            BasisDebug.Log("Starting Microphone: " + newMicrophone);

            try
            {
                Microphone.GetDeviceCaps(newMicrophone, out minFreq, out maxFreq);
            }
            catch
            {
                minFreq = 0;
                maxFreq = 0;
            }
            if (minFreq == 0 && maxFreq == 0)
            {
                minFreq = 48000;
                maxFreq = 48000;
            }

            LocalOpusSettings.SetDeviceAudioConfig(maxFreq);

            try
            {
                clip = Microphone.Start(newMicrophone, true, LocalOpusSettings.RecordingFullLength, LocalOpusSettings.MicrophoneSampleRate);
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarningOnce($"Microphone.Start threw for '{newMicrophone}': {ex.Message}");
                clip = null;
            }

            if (clip == null || !Microphone.IsRecording(newMicrophone))
            {
                BasisDebug.LogWarningOnce($"Microphone '{newMicrophone}' failed to start; will retry with backoff.");
                if (Microphone.IsRecording(newMicrophone)) Microphone.End(newMicrophone);
                clip = null;
                MicrophoneIsStarted = false;
                MicrophoneDevice = null;
                ClearStateAfterStop();
                return false;
            }

            // Unity clip samples are in FRAMES (per-channel samples at a time index)
            // GetData returns floats = frames * channels (interleaved)
            channels = (clip != null) ? clip.channels : 1;
            if (channels < 1)
            {
                channels = 1;
            }

            // processBufferArray is mono frame sized (your existing pipeline)
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
                if (clip.samples > 0 && clip.samples < bufferLength)
                {
                    bufferLength = clip.samples;
                }

                // small interleaved scratch sized to one process chunk (ProcessFrameSize * channels)
                CreateOrResizeArray(ProcessFrameSize * channels, ref _micDelta);

                // mono circular buffer (downmixed)
                LocalOpusSettings.CreateOrResizeArray(bufferLength, ref microphoneBufferArray);

                warmupSamples = ProcessFrameLength * 2;
                inWarmup = true;

                if (_micDelta != null) Array.Clear(_micDelta, 0, _micDelta.Length);
                if (microphoneBufferArray != null) Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
            }

            // A restart without a full stop-clear must not carry raw chunks from the old config.
            lock (stagingLock)
            {
                stagingReadChunk = 0;
                stagingWriteChunk = 0;
                stagingCount = 0;
            }

            if (processBufferArray != null) Array.Clear(processBufferArray, 0, processBufferArray.Length);
            if (_denoiseDry != null) Array.Clear(_denoiseDry, 0, _denoiseDry.Length);

            Denoiser ??= new RNNoise.NET.Denoiser();

            MicrophoneIsStarted = true;
            PacketSize = ProcessFrameLength * 4;

            // Reapply snapshot volume after start
            ChangeMicrophoneVolume(SMDMicrophone.Current.Volume01);

            MicrophoneDevice = newMicrophone;
            return true;
        }
    }

    private static void StopSelectedMicrophone_Internal()
    {
        if (string.IsNullOrEmpty(MicrophoneDevice)) return;

        if (Microphone.IsRecording(MicrophoneDevice))
        {
            Microphone.End(MicrophoneDevice);
            BasisDebug.Log("Stopped Microphone " + MicrophoneDevice);
        }

        MicrophoneDevice = null;
        MicrophoneIsStarted = false;

        if (clip != null) clip = null;
    }

    private static void ClearStateAfterStop()
    {
        lock (ringLock)
        {
            head = 0;
            captured = 0;
            written = 0;
            inWarmup = false;
            warmupSamples = 0;

            if (_micDelta != null) Array.Clear(_micDelta, 0, _micDelta.Length);
            if (microphoneBufferArray != null) Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
        }

        // Drop any raw chunks captured for the old device/config.
        lock (stagingLock)
        {
            stagingReadChunk = 0;
            stagingWriteChunk = 0;
            stagingCount = 0;
        }

        _noiseGateGain = 0f;

        _agc.Reset();
        _gateNoiseFloor.Reset();
        _lastGateThreshold = 0f;
        _prevAgcAmp = 1f;

        if (processBufferArray != null) Array.Clear(processBufferArray, 0, processBufferArray.Length);

        if (rmsValues != null)
        {
            Array.Clear(rmsValues, 0, rmsValues.Length);
            rmsIndex = 0;
            averageRms = 0f;
        }

        if (_denoiseDry != null) Array.Clear(_denoiseDry, 0, _denoiseDry.Length);
    }

    private static void StopSelectedMicrophone()
    {
        lock (processingLock)
        {
            processingEvent.Reset();
            StopSelectedMicrophone_Internal();
            ClearStateAfterStop();
        }
    }

    /// <summary>Primes the gain ramp so the first frame after (re)init doesn't ramp.</summary>
    public static void PrimeVolumeRamp()
    {
        _prevVolume = Volume;
    }

    private static void PollDeviceChanges()
    {
        if (!IsInitialize) return;

        _deviceScanTimer += Time.unscaledDeltaTime;
        if (_deviceScanTimer < DeviceScanIntervalSeconds) return;
        _deviceScanTimer = 0f;

        string[] devices = Microphone.devices;

        if (!DeviceListsMatch(_knownDevices, devices))
        {
            _knownDevices = devices;
            SMDMicrophone.SetDeviceList(devices);
            _recoveryBackoffUntil = 0f;
            _recoveryBackoffSeconds = 0f;
        }

        bool micShouldRun = !IsPaused || IsSuppressMuteMode;
        if (!micShouldRun) return;

        string preferred = SMDMicrophone.Current.Microphone;

        string target;
        if (!string.IsNullOrEmpty(preferred) && Array.IndexOf(devices, preferred) >= 0)
            target = preferred;
        else if (!string.IsNullOrEmpty(MicrophoneDevice) && Array.IndexOf(devices, MicrophoneDevice) >= 0)
            target = MicrophoneDevice;
        else if (devices.Length > 0)
            target = devices[0];
        else
        {
            StopSelectedMicrophone();
            return;
        }

        bool needsRestart =
            !MicrophoneIsStarted ||
            !string.Equals(MicrophoneDevice, target, StringComparison.Ordinal) ||
            !Microphone.IsRecording(target);

        if (!needsRestart) return;

        if (Time.unscaledTime < _recoveryBackoffUntil) return;

        if (ResetMicrophones(target))
        {
            _recoveryBackoffUntil = 0f;
            _recoveryBackoffSeconds = 0f;
        }
        else
        {
            _recoveryBackoffSeconds = Mathf.Clamp(_recoveryBackoffSeconds <= 0f ? 2f : _recoveryBackoffSeconds * 2f, 2f, RecoveryBackoffMaxSeconds);
            _recoveryBackoffUntil = Time.unscaledTime + _recoveryBackoffSeconds;
        }
    }

    private static bool DeviceListsMatch(string[] a, string[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Main-thread capture pump. Call at the top of the frame: reads every complete 20 ms
    /// chunk the clip has captured (Microphone.GetPosition / AudioClip.GetData are
    /// main-thread-only APIs) into the raw staging ring and wakes the processing thread,
    /// which then owns downmix, filtering, encode and the network send for the rest of
    /// the frame. Nothing joins back to the main thread except the mic-icon flags below.
    /// </summary>
    public static void PumpCapture()
    {
        PollDeviceChanges();

        if (Interlocked.Exchange(ref _scheduleMainHasAudio, 0) == 1)
        {
            MainThreadOnHasAudio?.Invoke();
        }
        else if (Interlocked.Exchange(ref _scheduleMainHasSilence, 0) == 1)
        {
            MainThreadOnHasSilence?.Invoke();
        }

        if (!MicrophoneIsStarted || string.IsNullOrEmpty(MicrophoneDevice) || clip == null) return;

        int currentPosition = Microphone.GetPosition(MicrophoneDevice);
        if (currentPosition <= 0)
        {
            return;
        }

        // channels is latched from the clip at StartLocalMicrophone and cannot change
        // without a restart — re-reading clip.channels here was a native call per frame.
        int ch = channels;
        int chunkInterleaved = ProcessFrameSize * ch;
        if (_micDelta == null || _micDelta.Length != chunkInterleaved)
        {
            _micDelta = new float[chunkInterleaved];
        }

        if (microphoneBufferArray == null || bufferLength <= 0)
        {
            return;
        }

        // `head` advances monotonically on the worker; a stale read only under-estimates
        // free space, so no lock is needed for a conservative trim.
        int headSnapshot = Volatile.Read(ref head);

        int newFrames = GetDataLength(bufferLength, captured, currentPosition);
        int backlog = GetDataLength(bufferLength, headSnapshot, captured);
        int freeFrames = bufferLength - backlog - ProcessFrameSize;
        if (newFrames > freeFrames)
        {
            newFrames = freeFrames > 0 ? freeFrames - (freeFrames % ProcessFrameSize) : 0;
        }

        bool queuedAny = false;
        lock (stagingLock)
        {
            if (stagingBuffer == null || stagingChunkStride != chunkInterleaved)
            {
                stagingBuffer = new float[StagingChunkCapacity * chunkInterleaved];
                stagingChunkStride = chunkInterleaved;
                stagingReadChunk = 0;
                stagingWriteChunk = 0;
                stagingCount = 0;
            }
            stagingChannels = ch;

            while (newFrames >= ProcessFrameSize && stagingCount < StagingChunkCapacity)
            {
                clip.GetData(_micDelta, captured);
                Array.Copy(_micDelta, 0, stagingBuffer, stagingWriteChunk * chunkInterleaved, chunkInterleaved);
                stagingWriteChunk = (stagingWriteChunk + 1) % StagingChunkCapacity;
                stagingCount++;

                captured = (captured + ProcessFrameSize) % bufferLength;
                newFrames -= ProcessFrameSize;
                queuedAny = true;
            }
        }

        if (queuedAny)
        {
            processingEvent.Set();
        }
    }

    /// <summary>
    /// Downmix an interleaved delta buffer (frames 0..frameCount in srcDelta) into the
    /// mono ring buffer dstMono at ring positions [headFrame, headFrame+frameCount),
    /// wrapping at ringFrames. Source is linear from index 0; destination is circular.
    /// </summary>
    private static void DownmixDeltaIntoRingMono(int headFrame, int frameCount, int ringFrames, int ch, float[] srcDelta, float[] dstMono)
    {
        if (srcDelta == null || dstMono == null || frameCount <= 0)
        {
            return;
        }

        if (ch < 1)
        {
            ch = 1;
        }

        int firstFrames = Mathf.Min(frameCount, ringFrames - headFrame);

        if (ch == 1)
        {
            Array.Copy(srcDelta, 0, dstMono, headFrame, firstFrames);
            if (firstFrames < frameCount)
            {
                Array.Copy(srcDelta, firstFrames, dstMono, 0, frameCount - firstFrames);
            }

            return;
        }

        for (int i = 0; i < firstFrames; i++)
        {
            int baseIdx = i * ch;
            float sum = 0f;
            for (int c = 0; c < ch; c++) sum += srcDelta[baseIdx + c];
            dstMono[headFrame + i] = sum / ch;
        }
        int wrapCount = frameCount - firstFrames;
        for (int i = 0; i < wrapCount; i++)
        {
            int baseIdx = (firstFrames + i) * ch;
            float sum = 0f;
            for (int c = 0; c < ch; c++) sum += srcDelta[baseIdx + c];
            dstMono[i] = sum / ch;
        }
    }

    private static void StartProcessingThread()
    {
        processingTokenSource = new CancellationTokenSource();
        processingThread = new Thread(() =>
        {
            while (!processingTokenSource.IsCancellationRequested)
            {
                try
                {
                    processingEvent.WaitOne();
                    if (processingTokenSource.IsCancellationRequested) break;

                    ProcessPendingFrames();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogErrorOnce($"Microphone processing thread: {ex}", BasisDebug.LogTag.Voice);
                }
            }
        });

        processingThread.IsBackground = true;
        processingThread.Start();
    }

    public static void StopProcessingThread()
    {
        processingTokenSource?.Cancel();
        processingEvent?.Set();

        if (processingThread != null && processingThread.IsAlive)
            processingThread.Join();

        processingThread = null;
        processingTokenSource?.Dispose();
        processingTokenSource = null;
    }

    public static void ProcessPendingFrames()
    {
        while (true)
        {
            lock (processingLock)
            {
                DrainStagingIntoRing();

                if (!TryDequeueCapturedFrame())
                {
                    return;
                }

                ProcessCurrentFrame();
            }
        }
    }

    // Downmixes every staged raw chunk into the mono ring, advancing `written`. Runs on
    // the processing thread; each chunk is copied out to worker scratch first so the
    // per-sample downmix never holds stagingLock against the main-thread pump.
    private static void DrainStagingIntoRing()
    {
        while (true)
        {
            int chunkStride;
            int ch;
            lock (stagingLock)
            {
                if (stagingCount == 0 || stagingBuffer == null)
                {
                    return;
                }
                chunkStride = stagingChunkStride;
                ch = stagingChannels;
                if (_stagingScratch == null || _stagingScratch.Length != chunkStride)
                {
                    _stagingScratch = new float[chunkStride];
                }
                Array.Copy(stagingBuffer, stagingReadChunk * chunkStride, _stagingScratch, 0, chunkStride);
                stagingReadChunk = (stagingReadChunk + 1) % StagingChunkCapacity;
                stagingCount--;
            }

            lock (ringLock)
            {
                if (microphoneBufferArray == null || bufferLength <= 0)
                {
                    return;
                }
                DownmixDeltaIntoRingMono(written, ProcessFrameSize, bufferLength, ch, _stagingScratch, microphoneBufferArray);
                written = (written + ProcessFrameSize) % bufferLength;
            }
        }
    }

    private static bool TryDequeueCapturedFrame()
    {
        lock (ringLock)
        {
            if (!MicrophoneIsStarted || microphoneBufferArray == null || processBufferArray == null)
            {
                return false;
            }

            // Gate on `written` (data actually downmixed into the ring), not `captured`
            // (the clip cursor, which is ahead by whatever is still in raw staging).
            if (inWarmup)
            {
                if (GetDataLength(bufferLength, head, written) < warmupSamples)
                {
                    return false;
                }
                head = (head + warmupSamples) % bufferLength;
                inWarmup = false;
            }

            if (GetDataLength(bufferLength, head, written) < ProcessFrameSize)
            {
                return false;
            }

            int remain = bufferLength - head;
            if (remain < ProcessFrameSize)
            {
                Array.Copy(microphoneBufferArray, head, processBufferArray, 0, remain);
                Array.Copy(microphoneBufferArray, 0, processBufferArray, remain, ProcessFrameSize - remain);
            }
            else
            {
                Array.Copy(microphoneBufferArray, head, processBufferArray, 0, ProcessFrameSize);
            }

            head = (head + ProcessFrameSize) % bufferLength;
            return true;
        }
    }

    private static void ProcessCurrentFrame()
    {
        // Read snapshot ONCE per processing call so settings are consistent for the frame.
        // This assumes SMDMicrophone.Current changes on main thread; the lock makes it coherent with ApplyMicSettings.
        var s = SMDMicrophone.Current;

        ApplyUserGain();

        if (s.UseDenoiser)
        {
            ApplyDeNoise(s);
        }

        if (s.UseNoiseGate)
        {
            ApplyNoiseGate(s);
        }

        if (s.UseAGC)
        {
            ApplyAgc(s);
        }
        else
        {
            _prevAgcAmp = 1f;
        }

        ApplyLimiter(s);

        RollingRMS();

        if (!isPaused && IsTransmitWorthy())
        {
            OnHasAudio?.Invoke();
            Interlocked.Exchange(ref _scheduleMainHasAudio, 1);
            Interlocked.Exchange(ref _scheduleMainHasSilence, 0);
        }
        else
        {
            OnHasSilence?.Invoke();
            Interlocked.Exchange(ref _scheduleMainHasSilence, 1);
            Interlocked.Exchange(ref _scheduleMainHasAudio, 0);
        }
    }

    public static void AdjustVolume(SMDMicrophone.MicSettings s)
    {
        ApplyUserGain();
        ApplyLimiter(s);
    }

    public static void ApplyUserGain()
    {
        float[] buffer = processBufferArray;
        int frameLength = buffer.Length;

        // Linearly ramp gain across the frame from the previous frame's end-of-frame
        // value to the current Volume, so a UI slider change does not step between
        // 20 ms frames (= click at the boundary).
        float volumeStart = _prevVolume;
        float volumeEnd = Volume;
        _prevVolume = volumeEnd;

        if (Mathf.Approximately(volumeStart, 1f) && Mathf.Approximately(volumeEnd, 1f))
        {
            return;
        }

        float rampStep = frameLength > 1 ? 1f / (frameLength - 1) : 0f;
        for (int index = 0; index < frameLength; index++)
        {
            float gain = frameLength > 1 ? Mathf.Lerp(volumeStart, volumeEnd, index * rampStep) : volumeEnd;
            buffer[index] *= gain;
        }
    }

    private static void ApplyAgc(SMDMicrophone.MicSettings s)
    {
        float[] buffer = processBufferArray;

        double sumSq = 0.0;
        float peak = 0f;
        for (int i = 0; i < ProcessFrameSize; i++)
        {
            float v = buffer[i];
            sumSq += (double)v * v;
            float magnitude = v < 0f ? -v : v;
            if (magnitude > peak) peak = magnitude;
        }
        float frameRms = Mathf.Sqrt((float)(sumSq / ProcessFrameSize));

        _agcSettings.TargetRms = s.AgcTargetRms;
        _agcSettings.MaxBoostDb = s.AgcMaxGainDb;
        _agcSettings.Attack01 = s.AgcAttack;
        _agcSettings.Release01 = s.AgcRelease;
        _agcSettings.Headroom = Mathf.Clamp01(s.LimitThreshold);

        float ampEnd = _agc.Process(frameRms, peak, _agcSettings, AgcFrameSeconds);
        float ampStart = _prevAgcAmp;
        _prevAgcAmp = ampEnd;

        if (Mathf.Approximately(ampStart, 1f) && Mathf.Approximately(ampEnd, 1f))
        {
            return;
        }

        float rampStep = ProcessFrameSize > 1 ? 1f / (ProcessFrameSize - 1) : 0f;
        for (int i = 0; i < ProcessFrameSize; i++)
        {
            buffer[i] *= ProcessFrameSize > 1 ? Mathf.Lerp(ampStart, ampEnd, i * rampStep) : ampEnd;
        }
    }

    public static void ApplyLimiter(SMDMicrophone.MicSettings s)
    {
        float[] buffer = processBufferArray;
        int frameLength = buffer.Length;

        float limitT = Mathf.Clamp01(s.LimitThreshold);
        float limitK = Mathf.Max(1e-6f, Mathf.Clamp01(s.LimitKnee));
        float capped = limitT + limitK;

        for (int index = 0; index < frameLength; index++)
        {
            float x = buffer[index];

            // Soft limiter: passthrough below the threshold, smooth cubic knee within
            // [threshold, threshold + knee], hard cap above.
            float ax = Mathf.Abs(x);
            if (ax >= capped)
            {
                x = Mathf.Sign(x) * capped;
            }
            else if (ax > limitT)
            {
                float t = (ax - limitT) / limitK;
                x = Mathf.Sign(x) * (limitT + limitK * (1f - Mathf.Pow(1f - t, 3f)));
            }
            else
            {
                continue;
            }

            buffer[index] = x;
        }
    }

    public static float GetRMS()
    {
        double sum = 0.0;
        for (int i = 0; i < ProcessFrameSize; i++)
        {
            float v = processBufferArray[i];
            sum += v * v;
        }
        return Mathf.Sqrt((float)(sum / ProcessFrameSize));
    }

    public static int GetDataLength(int len, int h, int pos)
    {
        return (pos < h) ? (len - h + pos) : (pos - h);
    }

    /// <summary>UI volume [0..1] mapped to dB then linear amp.</summary>
    public static void ChangeMicrophoneVolume(float ui)
    {
        ui = Mathf.Clamp01(ui);
        const float minDb = -60f;
        const float maxDb = 0f;
        float db = Mathf.Lerp(minDb, maxDb, ui);

        Volume = DbToAmp(db);

        BasisDebug.Log($"Set Microphone Gain To {db:F1} dB (amp {Volume:F3})", BasisDebug.LogTag.Voice);
    }

    public static void ApplyDeNoise(SMDMicrophone.MicSettings s)
    {
        if (_denoiseDry == null || _denoiseDry.Length != processBufferArray.Length)
            CreateOrResizeArray(processBufferArray.Length, ref _denoiseDry);

        Array.Copy(processBufferArray, _denoiseDry, ProcessFrameSize);

        int offset = 0;

        while (offset < ProcessFrameSize)
        {
            // Copy from process buffer to denoiser buffer
            // Todo: This is a little fragile since it relies on DenoiserFrameSize being 480
            if (_tmp480 == null || _tmp480.Length != DenoiserFrameSize)
                _tmp480 = new float[DenoiserFrameSize];

            Array.Copy(processBufferArray, offset, _tmp480, 0, DenoiserFrameSize);

            Denoiser?.Denoise(_tmp480);

            Array.Copy(_tmp480, 0, processBufferArray, offset, DenoiserFrameSize);

            offset += DenoiserFrameSize;
        }

        float makeup = DbToAmp(s.DenoiseMakeupDb);
        float wet = Mathf.Clamp01(s.DenoiseWet);

        if (!Mathf.Approximately(wet, 1f) || !Mathf.Approximately(s.DenoiseMakeupDb, 0f))
        {
            for (int i = 0; i < ProcessFrameSize; i++)
            {
                float den = processBufferArray[i] * makeup;
                processBufferArray[i] = Mathf.Lerp(_denoiseDry[i], den, wet);
            }
        }
    }

    public static void ApplyNoiseGate(SMDMicrophone.MicSettings s)
    {
        // Compute frame RMS
        double sum = 0.0;
        for (int i = 0; i < ProcessFrameSize; i++)
        {
            float v = processBufferArray[i];
            sum += v * v;
        }
        float frameRms = Mathf.Sqrt((float)(sum / ProcessFrameSize));

        float gateFloor = _gateNoiseFloor.Update(frameRms, AgcFrameSeconds);
        float threshold = s.AutoNoiseGate
            ? Mathf.Max(BasisNoiseFloorTracker.MinNoiseFloor, gateFloor * AutoGateOverNoise)
            : s.NoiseGateThreshold;

        _lastGateThreshold = threshold;

        // Smoothing coefficients per frame (20ms frames)
        float attackCoeff = Mathf.Clamp01(s.NoiseGateAttack);
        float releaseCoeff = Mathf.Clamp01(s.NoiseGateRelease);

        if (frameRms > threshold)
        {
            // Open gate
            _noiseGateGain = Mathf.Lerp(_noiseGateGain, 1f, attackCoeff);
        }
        else
        {
            // Close gate
            _noiseGateGain = Mathf.Lerp(_noiseGateGain, 0f, releaseCoeff);
        }

        // Apply gate gain to samples
        if (_noiseGateGain < 0.999f)
        {
            for (int i = 0; i < ProcessFrameSize; i++)
            {
                processBufferArray[i] *= _noiseGateGain;
            }
        }
    }

    public static void RollingRMS()
    {
        double sumSq = 0.0;
        for (int i = 0; i < ProcessFrameSize; i++)
        {
            float v = processBufferArray[i];
            sumSq += v * v;
        }
        float currentMeanSq = (float)(sumSq / ProcessFrameSize);

        rmsValues[rmsIndex] = currentMeanSq;
        rmsIndex = (rmsIndex + 1) % LocalOpusSettings.rmsWindowSize;

        float averagePower = 0f;
        for (int i = 0; i < rmsValues.Length; i++)
            averagePower += rmsValues[i];
        averagePower /= rmsValues.Length;

        averageRms = Mathf.Sqrt(averagePower);
    }

    public static bool IsTransmitWorthy()
    {
        return averageRms > LocalOpusSettings.silenceThreshold;
    }

    private static float DbToAmp(float db) => Mathf.Pow(10f, db / 20f);

    private static void CreateOrResizeArray(int length, ref float[] arr)
    {
        if (arr == null || arr.Length != length) arr = new float[length];
    }
}

#endif
