using UnityEngine;
using System;
using System.Linq;
using Basis.Scripts.Device_Management;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using Basis.Scripts.BasisSdk.Players;

//
// BasisLocalMicrophoneDriver (revised)
//
public static class BasisLocalMicrophoneDriver
{
    private static int head = 0;
    private static int bufferLength;

    public static bool HasEvents = false;
    public static int PacketSize;
    public static bool UseDenoiser = false;

    public static Action<bool> OnPausedAction;

    private static bool MicrophoneIsStarted = false;
    private static Thread processingThread;
    public static bool isRunning = true;
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

    /// <summary>
    /// Linear amplitude multiplier (derived from dB mapping in ChangeMicrophoneVolume).
    /// May be > 1.0 to boost quiet mics.
    /// </summary>
    public static float Volume = 1f;

    [HideInInspector] public static float[] microphoneBufferArray;
    [HideInInspector] public static float[] processBufferArray;

    [HideInInspector] public static float[] rmsValues;
    public static int rmsIndex = 0;
    public static float averageRms;

#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
    public static RNNoise.NET.Denoiser Denoiser = new RNNoise.NET.Denoiser();
#endif

    public static int minFreq = 48000;
    public static int maxFreq = 48000;

    public static int SampleRate;

    private static bool ScheduleMainHasAudio;
    private static bool ScheduleMainHasSilence;

    public static Action MainThreadOnHasAudio;
    public static Action MainThreadOnHasSilence;

    private static readonly object _lock = new object();

    public static bool isPaused = false;

    private static bool IsPaused
    {
        get => isPaused;
        set
        {
            isPaused = value;
            PlayerPrefs.SetInt(MicrophoneState, isPaused ? 1 : 0);
            ResetMicrophones(SMDMicrophone.SelectedMicrophone);
            OnPausedAction?.Invoke(isPaused);
        }
    }

    private static CancellationTokenSource processingTokenSource;

    private static int warmupSamples = 0;
    private static bool inWarmup = false;

    // ---------- New knobs (tweak safely at runtime) ----------

    /// <summary>Limiter threshold and knee for safety when Volume/AGC > 1.0.</summary>
    public static float LimitThreshold = 0.95f; // start compressing before clip
    public static float LimitKnee = 0.05f;      // soft knee width

    /// <summary>Denoiser makeup and wet/dry.</summary>
    public static float DenoiseMakeupDb = 3f;   // +3 dB after denoise
    public static float DenoiseWet = 1f;        // 0..1 (1 = fully denoised)

    /// <summary>Optional AGC.</summary>
    public static bool UseAGC = false;
    public static float AgcTargetRms = 0.06f;   // ≈ −24 dBFS
    public static float AgcMaxGainDb = 18f;
    public static float AgcAttack = 0.10f;      // towards needed gain when too quiet
    public static float AgcRelease = 0.01f;     // when too loud (reduce gain slowly)
    private static float agcGainDb = 0f;

    // Temp buffers for denoiser wet/dry and chunking
    private static float[] _denoiseDry; // copy of pre-denoise frame
    private static float[] _tmp480;     // 480-sample scratch (allocated on demand)
    // ---------------------------------------------------------

    public static bool Initialize()
    {
        if (IsInitialize) return true;

        lock (_lock)
        {
            if (IsInitialize) return true;

            try
            {
                RegisterEvents();
                SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
                ResetMicrophones(SMDMicrophone.SelectedMicrophone);
                ConfigureDenoiser(SMDMicrophone.SelectedDenoiserMicrophone);
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
    }

    public static void DeInitialize()
    {
        lock (_lock)
        {
            if (!IsInitialize) return;

            StopProcessingThread();
            UnregisterEvents();
            StopSelectedMicrophone();
            if (handle.IsCompleted == false)
            {
                handle.Complete();
            }
            if (VAJ.processBufferArray.IsCreated)
            {
                VAJ.processBufferArray.Dispose();
            }
#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
            Denoiser?.Dispose();
            Denoiser = null;
#endif
            clip = null;
            microphoneBufferArray = null;
            processBufferArray = null;
            rmsValues = null;
            _denoiseDry = null;
            _tmp480 = null;

            IsInitialize = false;
            BasisDebug.Log("Microphone Driver Deinitialized.");
        }
    }

    private static void RegisterEvents()
    {
        if (HasEvents) return;

        SMDMicrophone.OnMicrophoneChanged += ResetMicrophones;
        SMDMicrophone.OnMicrophoneVolumeChanged += ChangeMicrophoneVolume;
        SMDMicrophone.OnMicrophoneUseDenoiserChanged += ConfigureDenoiser;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        HasEvents = true;
    }

    private static void UnregisterEvents()
    {
        if (!HasEvents) return;

        SMDMicrophone.OnMicrophoneChanged -= ResetMicrophones;
        SMDMicrophone.OnMicrophoneVolumeChanged -= ChangeMicrophoneVolume;
        SMDMicrophone.OnMicrophoneUseDenoiserChanged -= ConfigureDenoiser;
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        HasEvents = false;
    }

    private static void ConfigureDenoiser(bool useDenoiser)
    {
        UseDenoiser = useDenoiser;
        BasisDebug.Log("Setting Denoiser To " + UseDenoiser);
    }

    private static void OnBootModeChanged(string mode)
    {
        ResetMicrophones(SMDMicrophone.SelectedMicrophone);
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

            // allocate denoise helpers
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

#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
            if (Denoiser == null) Denoiser = new RNNoise.NET.Denoiser();
#endif

            MicrophoneIsStarted = true;

            PacketSize = SampleRate * 4;

            // Re-apply current UI volume with dB mapping
            ChangeMicrophoneVolume(SMDMicrophone.SelectedVolumeMicrophone);

            MicrophoneDevice = newMicrophone;
        }
    }

    private static void StopSelectedMicrophone_Internal()
    {
        if (string.IsNullOrEmpty(MicrophoneDevice))
            return;

        if (Microphone.IsRecording(MicrophoneDevice))
        {
            Microphone.End(MicrophoneDevice);
            BasisDebug.Log("Stopped Microphone " + MicrophoneDevice);
        }

        MicrophoneDevice = null;
        MicrophoneIsStarted = false;

        if (clip != null)
        {
            clip = null;
        }
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
        if (handle.IsCompleted == false)
        {
            handle.Complete();
        }

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
        VAJ.LimitThreshold = LimitThreshold;
        VAJ.LimitKnee = LimitKnee;
    }

    public static void ToggleIsPaused()
    {
        IsPaused = !IsPaused;
    }

    public static void MicrophoneUpdate()
    {
        if (!MicrophoneIsStarted || string.IsNullOrEmpty(MicrophoneDevice) || clip == null)
        {
            return;
        }

        int currentPosition = Microphone.GetPosition(MicrophoneDevice);
        position = currentPosition;
        if (position <= 0)
        {
            return;
        }

        clip.GetData(microphoneBufferArray, 0);

        int dataLength = GetDataLength(bufferLength, head, position);
        if (dataLength < SampleRate)
        {
            return;
        }

        processingEvent.Set();

        lock (_lock)
        {
            if (ScheduleMainHasAudio)
            {
                MainThreadOnHasAudio?.Invoke();
                ScheduleMainHasAudio = false;
            }
            else if (ScheduleMainHasSilence)
            {
                MainThreadOnHasSilence?.Invoke();
                ScheduleMainHasSilence = false;
            }
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
                    {
                        ProcessAudioData(position);
                    }
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
        {
            processingThread.Join();
        }
        processingThread = null;
        processingTokenSource?.Dispose();
        processingTokenSource = null;
    }

    public static void ProcessAudioData(int posSnapshot)
    {
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

            // --- Optional AGC (pre-fader, before explicit Volume & limiter) ---
            if (UseAGC)
            {
                float thisRms = GetRMS();
                UpdateAgc(thisRms);
                float agcAmp = DbToAmp(agcGainDb);
                if (!Mathf.Approximately(agcAmp, 1f))
                {
                    // simple scalar multiply
                    for (int i = 0; i < SampleRate; i++)
                        processBufferArray[i] *= agcAmp;
                }
            }

            // --- User gain + limiter in Burst job ---
            AdjustVolume(); // uses VAJ.Volume (linear) and limiter

            // --- Optional denoise with wet/dry + makeup ---
            if (UseDenoiser)
            {
                ApplyDeNoise();
            }

            RollingRMS();

            if (IsTransmitWorthy())
            {
                OnHasAudio?.Invoke();
                lock (_lock) { ScheduleMainHasAudio = true; }
            }
            else
            {
                OnHasSilence?.Invoke();
                lock (_lock) { ScheduleMainHasSilence = true; }
            }

            head = (head + SampleRate) % bufferLength;
            dataLength -= SampleRate;
        }
    }

    public static void AdjustVolume()
    {
        // keep VAJ fields up to date (in case UI changed them at runtime)
        VAJ.Volume = Volume;
        VAJ.LimitThreshold = LimitThreshold;
        VAJ.LimitKnee = LimitKnee;

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

    /// <summary>
    /// UI volume in [0..1] is mapped to dB, then converted to linear. Range: −60 dB … +18 dB.
    /// </summary>
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
    public static void ApplyDeNoise()
    {
#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
        if (_denoiseDry == null || _denoiseDry.Length != processBufferArray.Length)
            CreateOrResizeArray(processBufferArray.Length, ref _denoiseDry);

        // copy dry
        Array.Copy(processBufferArray, _denoiseDry, SampleRate);

        // RNNoise is 48k/10ms friendly. If frame != 480, chunk in 480-sample hops.
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
                // copy chunk to temp, zero-pad if last chunk shorter
                Array.Clear(_tmp480, 0, hop);
                Array.Copy(processBufferArray, o, _tmp480, 0, n);

                Denoiser?.Denoise(_tmp480);

                Array.Copy(_tmp480, 0, processBufferArray, o, n);
                o += n;
            }
        }

        // wet/dry + makeup
        float makeup = DbToAmp(DenoiseMakeupDb);
        float wet = Mathf.Clamp01(DenoiseWet);
        if (!Mathf.Approximately(wet, 1f) || !Mathf.Approximately(DenoiseMakeupDb, 0f))
        {
            for (int i = 0; i < SampleRate; i++)
            {
                float den = processBufferArray[i] * makeup;
                processBufferArray[i] = Mathf.Lerp(_denoiseDry[i], den, wet);
            }
        }
#endif
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

    // ---------- Helpers ----------

    private static float DbToAmp(float db) => Mathf.Pow(10f, db / 20f);

    private static void UpdateAgc(float frameRms)
    {
        if (frameRms <= 1e-6f) frameRms = 1e-6f;
        float neededDb = 20f * Mathf.Log10(AgcTargetRms / frameRms);
        neededDb = Mathf.Clamp(neededDb, -AgcMaxGainDb, AgcMaxGainDb);
        float k = (neededDb > agcGainDb) ? AgcAttack : AgcRelease;
        agcGainDb = Mathf.Lerp(agcGainDb, neededDb, k);
    }

    private static void CreateOrResizeArray(int length, ref float[] arr)
    {
        if (arr == null || arr.Length != length) arr = new float[length];
    }
}
