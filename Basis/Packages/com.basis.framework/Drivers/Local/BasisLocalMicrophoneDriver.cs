using UnityEngine;
using System;
using System.Linq;
using Basis.Scripts.Device_Management;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;

public static class BasisLocalMicrophoneDriver
{
    private static int head = 0;
    private static int bufferLength;

    public static bool HasEvents = false;
    public static int PacketSize;

    public static Action<bool> OnPausedAction;

    private static bool MicrophoneIsStarted = false;
    private static Thread processingThread;
    private static ManualResetEvent processingEvent = new ManualResetEvent(false);
    private static readonly object processingLock = new object();

    private static volatile int position;

    private static BasisVolumeAdjustmentJob VAJ = new BasisVolumeAdjustmentJob();
    private static JobHandle handle;

    public const string MicrophoneState = "MicrophoneState";

    public static Action OnHasAudio;
    public static Action OnHasSilence;

    public static AudioClip clip;
    public static bool IsInitialize = false;
    public static string MicrophoneDevice = null;

    /// <summary>Linear amplitude multiplier (from dB mapping in ChangeMicrophoneVolume).</summary>
    public static float Volume = 1f;

    [HideInInspector] public static float[] microphoneBufferArray;
    [HideInInspector] public static float[] processBufferArray;

    [HideInInspector] public static float[] rmsValues;
    public static int rmsIndex = 0;
    public static float averageRms;

    public static RNNoise.NET.Denoiser Denoiser = new RNNoise.NET.Denoiser();
    public static int minFreq = 48000;
    public static int maxFreq = 48000;

    public static int SampleRate;

    public static Action MainThreadOnHasAudio;
    public static Action MainThreadOnHasSilence;

    private static int _scheduleMainHasAudio;   // 0/1
    private static int _scheduleMainHasSilence; // 0/1

    public static bool isPaused = false;

    private static CancellationTokenSource processingTokenSource;

    private static int warmupSamples = 0;
    private static bool inWarmup = false;
    private static float agcGainDb = 0f;
    private static float[] _denoiseDry;
    private static float[] _tmp480;

    private static string _pendingDeviceWhenPaused = null;

    private static bool IsPaused
    {
        get => isPaused;
        set
        {
            isPaused = value;
            PlayerPrefs.SetInt(MicrophoneState, isPaused ? 1 : 0);

            if (isPaused)
            {
                StopSelectedMicrophone();
            }
            else
            {
                // Prefer snapshot device
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

    public static bool Initialize()
    {
        if (IsInitialize) return true;
        try
        {
            RegisterEvents();

            // Load emits one change event; ApplyMicSettings reacts.
            SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

            StartProcessingThread();
            IsInitialize = true;
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
        microphoneBufferArray = null;
        processBufferArray = null;
        rmsValues = null;
        _denoiseDry = null;

        IsInitialize = false;
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
            clip = Microphone.Start(newMicrophone, true, LocalOpusSettings.RecordingFullLength, LocalOpusSettings.MicrophoneSampleRate);

            head = 0;
            position = 0;

            bufferLength = LocalOpusSettings.RecordingFullLength * LocalOpusSettings.MicrophoneSampleRate;
            LocalOpusSettings.CreateOrResizeArray(bufferLength, ref microphoneBufferArray);

            LocalOpusSettings.EnsureProcessBuffer(ref processBufferArray, out SampleRate);

            CreateOrResizeArray(SampleRate, ref _denoiseDry);

            HandleBasisVolumeAdjustmentJob();

            LocalOpusSettings.CreateOrResizeArray(LocalOpusSettings.rmsWindowSize, ref rmsValues);
            Array.Clear(rmsValues, 0, rmsValues.Length);
            rmsIndex = 0;
            averageRms = 0f;

            warmupSamples = SampleRate * 2;
            inWarmup = true;

            Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
            Array.Clear(processBufferArray, 0, processBufferArray.Length);
            Array.Clear(_denoiseDry, 0, _denoiseDry.Length);

            Denoiser ??= new RNNoise.NET.Denoiser();

            MicrophoneIsStarted = true;
            PacketSize = SampleRate * 4;

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

        clip.GetData(microphoneBufferArray, 0);

        int dataLength = GetDataLength(bufferLength, head, position);
        if (dataLength < SampleRate) return;

        processingEvent.Set();

        if (Interlocked.Exchange(ref _scheduleMainHasAudio, 0) == 1)
            MainThreadOnHasAudio?.Invoke();
        else if (Interlocked.Exchange(ref _scheduleMainHasSilence, 0) == 1)
            MainThreadOnHasSilence?.Invoke();
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

                processingEvent.Reset();
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
        while (dataLength >= SampleRate)
        {
            int remain = bufferLength - head;
            if (remain < SampleRate)
            {
                Array.Copy(microphoneBufferArray, head, processBufferArray, 0, remain);
                Array.Copy(microphoneBufferArray, 0, processBufferArray, remain, SampleRate - remain);
            }
            else
            {
                Array.Copy(microphoneBufferArray, head, processBufferArray, 0, SampleRate);
            }

            // --- Optional AGC ---
            if (s.UseAGC)
            {
                float thisRms = GetRMS();
                UpdateAgc(thisRms, s.AgcTargetRms, s.AgcMaxGainDb, s.AgcAttack, s.AgcRelease);

                float agcAmp = DbToAmp(agcGainDb);
                if (!Mathf.Approximately(agcAmp, 1f))
                {
                    for (int i = 0; i < SampleRate; i++)
                        processBufferArray[i] *= agcAmp;
                }
            }

            // --- User gain + limiter in Burst job ---
            AdjustVolume(s);

            if (s.UseDenoiser)
            {
                ApplyDeNoise(s);
            }

            RollingRMS();

            if (IsTransmitWorthy())
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

            head = (head + SampleRate) % bufferLength;
            dataLength -= SampleRate;
        }
    }

    public static void AdjustVolume(SMDMicrophone.MicSettings s)
    {
        VAJ.Volume = Volume;
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
        for (int i = 0; i < SampleRate; i++)
        {
            float v = processBufferArray[i];
            sum += v * v;
        }
        return Mathf.Sqrt((float)(sum / SampleRate));
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

        Array.Copy(processBufferArray, _denoiseDry, SampleRate);

        const int hop = 480;
        if (SampleRate == hop)
        {
            Denoiser?.Denoise(processBufferArray);
        }
        else
        {
            if (_tmp480 == null || _tmp480.Length != hop) _tmp480 = new float[hop];

            int o = 0;
            while (o < SampleRate)
            {
                int n = Math.Min(hop, SampleRate - o);
                Array.Clear(_tmp480, 0, hop);
                Array.Copy(processBufferArray, o, _tmp480, 0, n);
                Denoiser?.Denoise(_tmp480);
                Array.Copy(_tmp480, 0, processBufferArray, o, n);
                o += n;
            }
        }

        float makeup = DbToAmp(s.DenoiseMakeupDb);
        float wet = Mathf.Clamp01(s.DenoiseWet);

        if (!Mathf.Approximately(wet, 1f) || !Mathf.Approximately(s.DenoiseMakeupDb, 0f))
        {
            for (int i = 0; i < SampleRate; i++)
            {
                float den = processBufferArray[i] * makeup;
                processBufferArray[i] = Mathf.Lerp(_denoiseDry[i], den, wet);
            }
        }
    }

    public static void RollingRMS()
    {
        float rms = GetRMS();
        rmsValues[rmsIndex] = rms;
        rmsIndex = (rmsIndex + 1) % LocalOpusSettings.rmsWindowSize;
        averageRms = rmsValues.Average();
    }

    public static bool IsTransmitWorthy()
    {
        return averageRms > LocalOpusSettings.silenceThreshold;
    }

    private static float DbToAmp(float db) => Mathf.Pow(10f, db / 20f);

    private static void UpdateAgc(float frameRms, float targetRms, float maxGainDb, float attack, float release)
    {
        if (frameRms <= 1e-6f) frameRms = 1e-6f;

        float neededDb = 20f * Mathf.Log10(Mathf.Max(1e-6f, targetRms) / frameRms);
        neededDb = Mathf.Clamp(neededDb, -maxGainDb, maxGainDb);

        float k = (neededDb > agcGainDb) ? Mathf.Clamp01(attack) : Mathf.Clamp01(release);
        agcGainDb = Mathf.Lerp(agcGainDb, neededDb, k);
    }

    private static void CreateOrResizeArray(int length, ref float[] arr)
    {
        if (arr == null || arr.Length != length) arr = new float[length];
    }
}
