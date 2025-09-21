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
    // Warmup: discard first chunk(s) after (re)start to avoid crackle/garbage frames.
    private static int warmupSamples = 0;
    private static bool inWarmup = false;
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
        // Prevent the processing thread from touching shared state while we reconfigure.
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
                newMicrophone = Microphone.devices[0]; // fallback to first device
            }
            // Ensure the selected device is not already recording
            if (Microphone.IsRecording(newMicrophone))
            {
                Microphone.End(newMicrophone);
            }
            // Stop the current device if any
            StopSelectedMicrophone_Internal();
            if (IsPaused)
            {
                BasisDebug.Log("Microphone Is Paused");
                // When paused, ensure all state is clean.
                ClearStateAfterStop();
                MicrophoneDevice = null;
                return;
            }
            BasisDebug.Log("Starting Microphone: " + newMicrophone);
            Microphone.GetDeviceCaps(newMicrophone, out minFreq, out maxFreq);
            // Some drivers return 0/0 for “any”. Default to 48000 for RNNoise compatibility.
            if (minFreq == 0 && maxFreq == 0)
            {
                minFreq = 48000;
                maxFreq = 48000;
            }
            LocalOpusSettings.SetDeviceAudioConfig(maxFreq);
            clip = Microphone.Start(newMicrophone, true, LocalOpusSettings.RecordingFullLength, LocalOpusSettings.MicrophoneSampleRate);

            // Reset ring buffer pointers and positions
            head = 0;
            position = 0;

            bufferLength = LocalOpusSettings.RecordingFullLength * LocalOpusSettings.MicrophoneSampleRate;

            LocalOpusSettings.CreateOrResizeArray(bufferLength, ref microphoneBufferArray);

            // Ensure process buffer and SampleRate (frame size) are set
            LocalOpusSettings.EnsureProcessBuffer(ref processBufferArray, out SampleRate);

            // Prepare the job's NativeArray to mirror process buffer
            HandleBasisVolumeAdjustmentJob();

            // Reset RMS window
            LocalOpusSettings.CreateOrResizeArray(LocalOpusSettings.rmsWindowSize, ref rmsValues);
            Array.Clear(rmsValues, 0, rmsValues.Length);
            rmsIndex = 0;
            averageRms = 0f;

            // Calculate a warmup (discard) period: two frames worth
            warmupSamples = SampleRate * 2;
            inWarmup = true;

            // Zero buffers so we never process garbage
            Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
            Array.Clear(processBufferArray, 0, processBufferArray.Length);

#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
            // Recreate the denoiser to clear internal state after restarts
            if (Denoiser == null) Denoiser = new RNNoise.NET.Denoiser();
#endif

            MicrophoneIsStarted = true;

            PacketSize = SampleRate * 4;

            ChangeMicrophoneVolume(SMDMicrophone.SelectedVolumeMicrophone);

            MicrophoneDevice = newMicrophone;

            // Allow processing again; it will only run when MicrophoneUpdate sets the event.
        }
    }

    // Only call while holding processingLock
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
            clip = null; // Make sure old clip is released
        }
    }
    private static void ClearStateAfterStop()
    {
        head = 0;
        position = 0;
        inWarmup = false;
        warmupSamples = 0;
        if (microphoneBufferArray != null)
        {
            Array.Clear(microphoneBufferArray, 0, microphoneBufferArray.Length);
        }
        if (processBufferArray != null)
        {
            Array.Clear(processBufferArray, 0, processBufferArray.Length);
        }
        if (rmsValues != null)
        {
            Array.Clear(rmsValues, 0, rmsValues.Length);
            rmsIndex = 0;
            averageRms = 0f;
        }
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
            // else: same size, keep NativeArray
        }
        else
        {
            VAJ.processBufferArray = new NativeArray<float>(processBufferArray, Allocator.Persistent);
        }

        VAJ.Volume = Volume;
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
        // Wait until the device actually starts feeding samples
        int currentPosition = Microphone.GetPosition(MicrophoneDevice);
        position = currentPosition; // volatile write
        if (position <= 0)
        {
            return;
        }

        // Copy the whole circular clip into our read buffer (fast in native)
        clip.GetData(microphoneBufferArray, 0);

        // Only signal processing when there's at least one full frame of new data
        int dataLength = GetDataLength(bufferLength, head, position);
        if (dataLength < SampleRate)
        {
            return;
        }

        // Signal processing thread
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
        // Discard initial warmup samples to avoid crackle/garbage frames after start
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
                // Not enough yet; skip processing this cycle
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
            AdjustVolume();
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
        // Mirror processBufferArray into NativeArray, run job, copy back.
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
    public static void ChangeMicrophoneVolume(float volume)
    {
        Volume = volume;
        VAJ.Volume = Volume;
        BasisDebug.Log($"Set Microphone Volume To {Volume}");
    }
    public static void ApplyDeNoise()
    {
#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
        // RNNoise expects 48k and short frames; we pass our frame-sized buffer here.
        Denoiser?.Denoise(processBufferArray);
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
}
