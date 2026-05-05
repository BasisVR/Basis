#if !BASIS_DISABLE_MICROPHONE
using UnityEngine;
using System;
using System.Linq;
using Basis.Scripts.Device_Management;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;

public static class BasisLocalMicrophoneDriver
{
    // Written under processingLock by ProcessAudioData (background thread) and by
    // the mic lifecycle methods (main). Read by MicrophoneUpdate on main without
    // the lock, so it must be volatile to prevent torn/cached reads.
    private static volatile int head = 0;
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

    private static volatile int position;

    private static BasisVolumeAdjustmentJob VAJ = new BasisVolumeAdjustmentJob();
    private static JobHandle handle;

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
    private static float agcGainDb = 0f;

    public const int ProcessFrameSize = 960;  // 20ms at 48kHz
    public const int DenoiserFrameSize = 480; // 10ms at 48kHz

    private static float _agcHoldTimer = 0f;
    private static float _noiseGateGain = 0f; // 0 = closed, 1 = open

    private static float[] _denoiseDry;
    private static float[] _tmp480;

    private static string _pendingDeviceWhenPaused = null;
    private static int channels = 1;
    // Small interleaved scratch sized to ProcessFrameSize * channels. Holds one chunk
    // pulled from the AudioClip ring per iteration, then downmixed into the mono ring.
    // Replaces a previous full-clip snapshot that copied ~192 KB every Unity tick.
    private static float[] _micDelta;
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

        if (!handle.IsCompleted) handle.Complete();
        if (VAJ.processBufferArray.IsCreated) VAJ.processBufferArray.Dispose();

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
        // 1) Update Volume mapping (affects VAJ.Volume too)
        ChangeMicrophoneVolume(s.Volume01);

        // 2) Update job params that are consumed during AdjustVolume()
        lock (processingLock)
        {
            VAJ.LimitThreshold = Mathf.Clamp01(s.LimitThreshold);
            VAJ.LimitKnee = Mathf.Clamp01(s.LimitKnee);

            // AGC internal state reset when disabled
            if (!s.UseAGC) agcGainDb = 0f;
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

    public static void ResetMicrophones(string newMicrophone)
    {
        lock (processingLock)
        {
            processingEvent.Reset();

            if (string.IsNullOrEmpty(newMicrophone))
            {
                BasisDebug.LogError("Microphone was empty or null");
                return;
            }
            if (Microphone.devices.Length == 0)
            {
                BasisDebug.LogError("No Microphones found!");
                return;
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
                return;
            }

            BasisDebug.Log("Starting Microphone: " + newMicrophone);

            Microphone.GetDeviceCaps(newMicrophone, out minFreq, out maxFreq);
            if (minFreq == 0 && maxFreq == 0)
            {
                minFreq = 48000;
                maxFreq = 48000;
            }

            LocalOpusSettings.SetDeviceAudioConfig(maxFreq);

            clip = Microphone.Start(newMicrophone,true, LocalOpusSettings.RecordingFullLength, LocalOpusSettings.MicrophoneSampleRate);

            head = 0;
            position = 0;

            // Unity clip samples are in FRAMES (per-channel samples at a time index)
            // GetData returns floats = frames * channels (interleaved)
            channels = (clip != null) ? clip.channels : 1;
            if (channels < 1)
            {
                channels = 1;
            }

            // circular buffer length in FRAMES (what your head/position math uses)
            bufferLength = LocalOpusSettings.RecordingFullLength * LocalOpusSettings.MicrophoneSampleRate;

            // small interleaved scratch sized to one process chunk (ProcessFrameSize * channels)
            CreateOrResizeArray(ProcessFrameSize * channels, ref _micDelta);

            // mono circular buffer (downmixed)
            LocalOpusSettings.CreateOrResizeArray(bufferLength, ref microphoneBufferArray);

            // processBufferArray is mono frame sized (your existing pipeline)
            LocalOpusSettings.EnsureProcessBuffer(ref processBufferArray, out ProcessFrameLength);

            CreateOrResizeArray(ProcessFrameLength, ref _denoiseDry);

            HandleBasisVolumeAdjustmentJob();

            LocalOpusSettings.CreateOrResizeArray(LocalOpusSettings.rmsWindowSize, ref rmsValues);
            Array.Clear(rmsValues, 0, rmsValues.Length);
            rmsIndex = 0;
            averageRms = 0f;

            warmupSamples = ProcessFrameLength * 2;
            inWarmup = true;

            if (_micDelta != null) Array.Clear(_micDelta, 0, _micDelta.Length);
            if (microphoneBufferArray != null) Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
            if (processBufferArray != null) Array.Clear(processBufferArray, 0, processBufferArray.Length);
            if (_denoiseDry != null) Array.Clear(_denoiseDry, 0, _denoiseDry.Length);

            Denoiser ??= new RNNoise.NET.Denoiser();

            MicrophoneIsStarted = true;
            PacketSize = ProcessFrameLength * 4;

            // Reapply snapshot volume after start
            ChangeMicrophoneVolume(SMDMicrophone.Current.Volume01);

            MicrophoneDevice = newMicrophone;
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
        head = 0;
        position = 0;
        inWarmup = false;
        warmupSamples = 0;
        _noiseGateGain = 0f;

        if (_micDelta != null) Array.Clear(_micDelta, 0, _micDelta.Length);
        if (microphoneBufferArray != null) Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
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

    public static void HandleBasisVolumeAdjustmentJob()
    {
        if (!handle.IsCompleted) handle.Complete();

        if (VAJ.processBufferArray.IsCreated)
        {
            if (VAJ.processBufferArray.Length != processBufferArray.Length)
            {
                VAJ.processBufferArray.Dispose();
                VAJ.processBufferArray = new NativeArray<float>(processBufferArray, Allocator.Persistent);
            }
        }
        else
        {
            VAJ.processBufferArray = new NativeArray<float>(processBufferArray, Allocator.Persistent);
        }

        VAJ.Volume = Volume;
        VAJ.VolumePrev = Volume; // prime so the first frame after (re)init doesn't ramp
        VAJ.FrameLength = processBufferArray.Length;
        _prevVolume = Volume;

        // Pull limiter settings from snapshot (authoritative)
        var s = SMDMicrophone.Current;
        VAJ.LimitThreshold = Mathf.Clamp01(s.LimitThreshold);
        VAJ.LimitKnee = Mathf.Clamp01(s.LimitKnee);
    }

    public static void MicrophoneUpdate()
    {
        if (!MicrophoneIsStarted || string.IsNullOrEmpty(MicrophoneDevice) || clip == null) return;

        int currentPosition = Microphone.GetPosition(MicrophoneDevice);
        position = currentPosition;
        if (position <= 0) return;

        int clipFrames = clip.samples;
        if (clipFrames <= 0) return;

        int ch = clip.channels;
        if (ch < 1) ch = 1;
        channels = ch;

        // Clamp to min(bufferLength, clipFrames) so a device restart that produced a
        // smaller clip mid-flight can't push reads out of range.
        int framesToUse = Mathf.Min(bufferLength, clipFrames);
        LocalOpusSettings.CreateOrResizeArray(framesToUse, ref microphoneBufferArray);

        int dataLength = GetDataLength(framesToUse, head, position);
        if (dataLength < ProcessFrameSize) return;

        // Pull only the new region [head .. head+dataLength) from the clip, in fixed
        // chunks of one process frame. Each chunk is downmixed into the mono ring at
        // its matching ring positions. Replaces a previous full-clip GetData per tick
        // (~192 KB → ~3.8 KB per chunk for mono).
        int chunkInterleaved = ProcessFrameSize * channels;
        if (_micDelta == null || _micDelta.Length != chunkInterleaved)
            _micDelta = new float[chunkInterleaved];

        // Serialize ring-buffer writes against the bg processor reading the same
        // region and advancing `head`. Without the lock, ProcessAudioData can read
        // half-written frames at the boundary = pop.
        lock (processingLock)
        {
            int readHead = head;
            int remaining = dataLength;
            while (remaining >= ProcessFrameSize)
            {
                clip.GetData(_micDelta, readHead);
                DownmixDeltaIntoRingMono(readHead, ProcessFrameSize, framesToUse, channels, _micDelta, microphoneBufferArray);
                readHead = (readHead + ProcessFrameSize) % framesToUse;
                remaining -= ProcessFrameSize;
            }
        }

        processingEvent.Set();

        if (Interlocked.Exchange(ref _scheduleMainHasAudio, 0) == 1)
            MainThreadOnHasAudio?.Invoke();
        else if (Interlocked.Exchange(ref _scheduleMainHasSilence, 0) == 1)
            MainThreadOnHasSilence?.Invoke();
    }

    /// <summary>
    /// Downmix an interleaved delta buffer (frames 0..frameCount in srcDelta) into the
    /// mono ring buffer dstMono at ring positions [headFrame, headFrame+frameCount),
    /// wrapping at ringFrames. Source is linear from index 0; destination is circular.
    /// </summary>
    private static void DownmixDeltaIntoRingMono(int headFrame, int frameCount, int ringFrames, int ch, float[] srcDelta, float[] dstMono)
    {
        if (srcDelta == null || dstMono == null || frameCount <= 0) return;
        if (ch < 1) ch = 1;

        int firstFrames = Mathf.Min(frameCount, ringFrames - headFrame);

        if (ch == 1)
        {
            Array.Copy(srcDelta, 0, dstMono, headFrame, firstFrames);
            if (firstFrames < frameCount)
                Array.Copy(srcDelta, firstFrames, dstMono, 0, frameCount - firstFrames);
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
                processingEvent.WaitOne();
                if (processingTokenSource.IsCancellationRequested) break;

                lock (processingLock)
                {
                    if (MicrophoneIsStarted && clip != null)
                        ProcessAudioData(position);
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

    public static void ProcessAudioData(int posSnapshot)
    {
        // Read snapshot ONCE per processing call so settings are consistent for the frame.
        // This assumes SMDMicrophone.Current changes on main thread; the lock makes it coherent with ApplyMicSettings.
        var s = SMDMicrophone.Current;

        if (inWarmup)
        {
            int available = GetDataLength(bufferLength, head, posSnapshot);
            if (available >= warmupSamples)
            {
                head = (head + warmupSamples) % bufferLength;
                inWarmup = false;
            }
            else
            {
                return;
            }
        }

        int dataLength = GetDataLength(bufferLength, head, posSnapshot);
        while (dataLength >= ProcessFrameSize)
        {
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

            // --- Optional AGC ---
            if (s.UseAGC)
            {
                float thisRms = GetRMS();
                UpdateAgc(thisRms, s.AgcTargetRms, s.AgcMaxGainDb, s.AgcAttack, s.AgcRelease);

                float agcAmp = DbToAmp(agcGainDb);
                if (!Mathf.Approximately(agcAmp, 1f))
                {
                    for (int i = 0; i < ProcessFrameSize; i++)
                        processBufferArray[i] *= agcAmp;
                }
            }

            // --- User gain + limiter in Burst job ---
            AdjustVolume(s);

            if (s.UseDenoiser)
            {
                ApplyDeNoise(s);
            }

            if (s.UseNoiseGate)
            {
                ApplyNoiseGate(s);
            }

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

            head = (head + ProcessFrameSize) % bufferLength;
            dataLength -= ProcessFrameSize;
        }
    }

    public static void AdjustVolume(SMDMicrophone.MicSettings s)
    {
        // Linearly ramp gain across the frame from the previous frame's end-of-frame
        // value to the current Volume, so a UI slider change does not step between
        // 20 ms frames (= click at the boundary).
        VAJ.VolumePrev = _prevVolume;
        VAJ.Volume = Volume;
        VAJ.FrameLength = processBufferArray.Length;
        _prevVolume = Volume;

        VAJ.LimitThreshold = Mathf.Clamp01(s.LimitThreshold);
        VAJ.LimitKnee = Mathf.Clamp01(s.LimitKnee);

        VAJ.processBufferArray.CopyFrom(processBufferArray);
        handle = VAJ.Schedule(processBufferArray.Length, 64);
        handle.Complete();
        VAJ.processBufferArray.CopyTo(processBufferArray);
    }

    public static float GetRMS()
    {
        double sum = 0.0;
        int len = processBufferArray.Length;
        for (int i = 0; i < len; i++)
        {
            float v = processBufferArray[i];
            sum += v * v;
        }
        return Mathf.Sqrt((float)(sum / len));
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
        VAJ.Volume = Volume;

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

        // Smoothing coefficients per frame (20ms frames)
        float attackCoeff = Mathf.Clamp01(s.NoiseGateAttack);
        float releaseCoeff = Mathf.Clamp01(s.NoiseGateRelease);

        if (frameRms > s.NoiseGateThreshold)
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
        int len = processBufferArray.Length;
        for (int i = 0; i < len; i++)
        {
            float v = processBufferArray[i];
            sumSq += v * v;
        }
        float currentMeanSq = (float)(sumSq / len);

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

    private static void UpdateAgc(float frameRms, float targetRms, float maxGainDb, float attack, float release)
    {
        const float agcDecaySpeed = 0.020f; // ProcessFrameSize / 48000;
        const float agcHoldTime   = 0.400f;

        if (frameRms <= 1e-6f) frameRms = 1e-6f;

        if (_agcHoldTimer > 0f) _agcHoldTimer -= agcDecaySpeed;

        // When input is very quiet (silence/pause), release gain back toward 0 dB
        // so the user isn't stuck at a reduced level when they start speaking again.
        if (frameRms < 0.003f)
        {
            if (agcGainDb < 0f)
            {
                agcGainDb = Mathf.Lerp(agcGainDb, 0f, Mathf.Clamp01(release));
            }
            return;
        }

        float neededDb = 20f * Mathf.Log10(Mathf.Max(1e-6f, targetRms) / frameRms);
        neededDb = Mathf.Clamp(neededDb, -maxGainDb, maxGainDb);

        // The timer provides a cooldown period when the audio hits a new peak volume before applying additional correction.
        if (neededDb < agcGainDb)
        {
            agcGainDb = Mathf.Lerp(agcGainDb, neededDb, Mathf.Clamp01(attack));
            _agcHoldTimer = agcHoldTime;
        }
        else
        {
            if (_agcHoldTimer <= 0f)
            {
                agcGainDb = Mathf.Lerp(agcGainDb, neededDb, Mathf.Clamp01(release));
            }
        }
    }

    private static void CreateOrResizeArray(int length, ref float[] arr)
    {
        if (arr == null || arr.Length != length) arr = new float[length];
    }
}

#endif
