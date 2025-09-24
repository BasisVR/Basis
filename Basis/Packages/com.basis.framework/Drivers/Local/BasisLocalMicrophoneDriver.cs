using UnityEngine;
using System;
using System.Linq;
using Basis.Scripts.Device_Management;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;

/// <summary>
/// Captures microphone audio into a ring buffer, optionally denoises it, adjusts volume via a Burst job,
/// computes rolling RMS to detect voice activity, and raises main-thread callbacks for UI/UX.
/// Uses a background processing thread and a ManualResetEvent to decouple capture from processing.
/// </summary>
public static class BasisLocalMicrophoneDriver
{
    /// <summary>Ring-buffer head index into the circular <see cref="clip"/> samples.</summary>
    private static int head = 0;

    /// <summary>Total number of samples in the circular recording clip.</summary>
    private static int bufferLength;

    /// <summary>Whether event hooks (device/volume/denoiser/bootmode) are registered.</summary>
    public static bool HasEvents = false;

    /// <summary>Network packet size in samples (derived from <see cref="SampleRate"/>).</summary>
    public static int PacketSize;

    /// <summary>Flag for enabling the RNNoise denoiser on processed audio frames.</summary>
    public static bool UseDenoiser = false;

    /// <summary>Invoked when pause state changes; argument is the new paused state.</summary>
    public static Action<bool> OnPausedAction;

    /// <summary>True when the selected microphone has successfully started recording.</summary>
    private static bool MicrophoneIsStarted = false;

    /// <summary>Background thread that processes audio frames when signaled.</summary>
    private static Thread processingThread;

    /// <summary>Global running flag (not strictly required with <see cref="processingTokenSource"/>).</summary>
    public static bool isRunning = true;

    /// <summary>Signal used to wake the processing thread once a full frame is available.</summary>
    private static ManualResetEvent processingEvent = new ManualResetEvent(false);

    /// <summary>Lock for protecting shared state during reconfiguration (device resets, stops).</summary>
    private static readonly object processingLock = new object();

    /// <summary>Volatile write of current microphone cursor (sample index) from <see cref="MicrophoneUpdate"/>.</summary>
    private static volatile int position;

    /// <summary>Burst job that applies volume scalar to the current process buffer.</summary>
    private static BasisVolumeAdjustmentJob VAJ = new BasisVolumeAdjustmentJob();

    /// <summary>Handle for the in-flight volume adjustment job.</summary>
    private static JobHandle handle;

    /// <summary>PlayerPrefs key storing paused state across sessions.</summary>
    public const string MicrophoneState = "MicrophoneState";

    /// <summary>Raised on the processing thread when a frame is considered voice-active.</summary>
    public static Action OnHasAudio;

    /// <summary>Raised on the processing thread when a frame is considered silence.</summary>
    public static Action OnHasSilence;

    /// <summary>Unity microphone recording clip (circular buffer).</summary>
    public static AudioClip clip;

    /// <summary>Whether the driver has been initialized.</summary>
    public static bool IsInitialize = false;

    /// <summary>Name of the active microphone device.</summary>
    public static string MicrophoneDevice = null;

    /// <summary>Current volume gain applied by the Burst job.</summary>
    public static float Volume = 1f;

    /// <summary>Backing buffer that mirrors data read from <see cref="clip"/> each update.</summary>
    [HideInInspector] public static float[] microphoneBufferArray;

    /// <summary>Scratch buffer for the current frame (size == <see cref="SampleRate"/>).</summary>
    [HideInInspector] public static float[] processBufferArray;

    /// <summary>Rolling RMS window values.</summary>
    [HideInInspector] public static float[] rmsValues;

    /// <summary>Current index into <see cref="rmsValues"/>.</summary>
    public static int rmsIndex = 0;

    /// <summary>Mean of the rolling RMS window.</summary>
    public static float averageRms;

#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
    /// <summary>RNNoise denoiser instance (requires 48 kHz input).</summary>
    public static RNNoise.NET.Denoiser Denoiser = new RNNoise.NET.Denoiser();
#endif

    /// <summary>Device-reported minimum frequency; 0/0 indicates "any". Defaults to 48 kHz.</summary>
    public static int minFreq = 48000;

    /// <summary>Device-reported maximum frequency; defaults to 48 kHz if unspecified.</summary>
    public static int maxFreq = 48000;

    /// <summary>Frame size in samples used by processing/transmit (derived by <see cref="LocalOpusSettings.EnsureProcessBuffer"/>).</summary>
    public static int SampleRate;

    /// <summary>Flag to schedule <see cref="MainThreadOnHasAudio"/> on the next <see cref="MicrophoneUpdate"/> call.</summary>
    private static bool ScheduleMainHasAudio;

    /// <summary>Flag to schedule <see cref="MainThreadOnHasSilence"/> on the next <see cref="MicrophoneUpdate"/> call.</summary>
    private static bool ScheduleMainHasSilence;

    /// <summary>Main-thread callback mirroring <see cref="OnHasAudio"/> for UI/gameplay logic.</summary>
    public static Action MainThreadOnHasAudio;

    /// <summary>Main-thread callback mirroring <see cref="OnHasSilence"/> for UI/gameplay logic.</summary>
    public static Action MainThreadOnHasSilence;

    /// <summary>Coarse-grain lock for init/deinit and scheduling of main-thread events.</summary>
    private static readonly object _lock = new object();

    /// <summary>Latched paused state (persisted to <see cref="PlayerPrefs"/> via <see cref="MicrophoneState"/>).</summary>
    public static bool isPaused = false;

    /// <summary>Property wrapper that persists pause, resets microphones, and invokes <see cref="OnPausedAction"/>.</summary>
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

    /// <summary>Cancellation token for the processing thread loop.</summary>
    private static CancellationTokenSource processingTokenSource;

    /// <summary>Samples to discard after (re)start to avoid crackle/garbage frames.</summary>
    private static int warmupSamples = 0;

    /// <summary>True while warmup frames are being discarded.</summary>
    private static bool inWarmup = false;

    /// <summary>
    /// Initializes the driver: registers events, chooses a device, configures denoiser, and starts the processing thread.
    /// Safe to call multiple times.
    /// </summary>
    /// <returns>True if initialization succeeded or was already initialized.</returns>
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

    /// <summary>
    /// Shuts down the driver: stops processing thread, unregisters events, stops microphone,
    /// completes/cleans Burst resources, disposes denoiser, and clears buffers.
    /// </summary>
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

    /// <summary>
    /// Subscribes to microphone device/volume/denoiser and boot-mode events once.
    /// </summary>
    private static void RegisterEvents()
    {
        if (HasEvents) return;

        SMDMicrophone.OnMicrophoneChanged += ResetMicrophones;
        SMDMicrophone.OnMicrophoneVolumeChanged += ChangeMicrophoneVolume;
        SMDMicrophone.OnMicrophoneUseDenoiserChanged += ConfigureDenoiser;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        HasEvents = true;
    }

    /// <summary>
    /// Unsubscribes from all events if previously registered.
    /// </summary>
    private static void UnregisterEvents()
    {
        if (!HasEvents) return;

        SMDMicrophone.OnMicrophoneChanged -= ResetMicrophones;
        SMDMicrophone.OnMicrophoneVolumeChanged -= ChangeMicrophoneVolume;
        SMDMicrophone.OnMicrophoneUseDenoiserChanged -= ConfigureDenoiser;
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        HasEvents = false;
    }

    /// <summary>
    /// Enables or disables the denoiser flag; actual denoiser instance is managed on (re)start.
    /// </summary>
    private static void ConfigureDenoiser(bool useDenoiser)
    {
        UseDenoiser = useDenoiser;
        BasisDebug.Log("Setting Denoiser To " + UseDenoiser);
    }

    /// <summary>
    /// Reacts to boot-mode changes by reselecting and restarting the microphone.
    /// </summary>
    private static void OnBootModeChanged(string mode)
    {
        ResetMicrophones(SMDMicrophone.SelectedMicrophone);
    }

    /// <summary>
    /// (Re)starts the selected microphone safely, reinitializing buffers and processing state.
    /// When paused, it cleans state and bails without starting recording.
    /// </summary>
    /// <param name="newMicrophone">Device name to select; falls back to the first device if not found.</param>
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

    /// <summary>
    /// Stops the current microphone device (if any). Call only under <see cref="processingLock"/>.
    /// </summary>
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

    /// <summary>
    /// Clears ring-buffer pointers and zeroes arrays after stopping recording.
    /// </summary>
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

    /// <summary>
    /// Public stop wrapper that acquires <see cref="processingLock"/> and fully clears state.
    /// </summary>
    private static void StopSelectedMicrophone()
    {
        lock (processingLock)
        {
            processingEvent.Reset();
            StopSelectedMicrophone_Internal();
            ClearStateAfterStop();
        }
    }

    /// <summary>
    /// Ensures the volume adjustment job has a correctly sized persistent NativeArray and sets gain.
    /// Completes any in-flight job before resizing.
    /// </summary>
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

    /// <summary>
    /// Toggles paused state (persists preference, restarts/cleans devices accordingly).
    /// </summary>
    public static void ToggleIsPaused()
    {
        IsPaused = !IsPaused;
    }

    /// <summary>
    /// Called on the main thread to poll the microphone ring buffer position,
    /// copy fresh data, and signal the processing thread when at least one frame is available.
    /// Also dispatches scheduled main-thread audio/silence callbacks.
    /// </summary>
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

    /// <summary>
    /// Starts the background processing thread that consumes frames when signaled by <see cref="processingEvent"/>.
    /// </summary>
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

    /// <summary>
    /// Requests the processing thread to stop and waits for it to join.
    /// </summary>
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

    /// <summary>
    /// Consumes available audio frames from the ring buffer, applies volume gain and optional denoise,
    /// updates RMS windows, and raises audio/silence events. Advances the head index by <see cref="SampleRate"/> per frame.
    /// </summary>
    /// <param name="posSnapshot">Snapshot of the current microphone position to compute available samples.</param>
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

    /// <summary>
    /// Runs the Burst volume adjustment job over <see cref="processBufferArray"/>.
    /// </summary>
    public static void AdjustVolume()
    {
        // Mirror processBufferArray into NativeArray, run job, copy back.
        VAJ.processBufferArray.CopyFrom(processBufferArray);
        handle = VAJ.Schedule(processBufferArray.Length, 64);
        handle.Complete();
        VAJ.processBufferArray.CopyTo(processBufferArray);
    }

    /// <summary>
    /// Computes RMS of the current frame buffer.
    /// </summary>
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

    /// <summary>
    /// Computes available samples between ring-buffer head and the device write position.
    /// </summary>
    public static int GetDataLength(int len, int h, int pos)
    {
        return (pos < h) ? (len - h + pos) : (pos - h);
    }

    /// <summary>
    /// Adjusts microphone gain and updates the Burst job parameter; persists to logs.
    /// </summary>
    public static void ChangeMicrophoneVolume(float volume)
    {
        Volume = volume;
        VAJ.Volume = Volume;
        BasisDebug.Log($"Set Microphone Volume To {Volume}");
    }

    /// <summary>
    /// Applies RNNoise denoising to the current frame buffer (48 kHz expected).
    /// No-op on Android/Linux where the binding is unavailable.
    /// </summary>
    public static void ApplyDeNoise()
    {
#if !UNITY_ANDROID && !UNITY_STANDALONE_LINUX
        // RNNoise expects 48k and short frames; we pass our frame-sized buffer here.
        Denoiser?.Denoise(processBufferArray);
#endif
    }

    /// <summary>
    /// Updates a rolling RMS window and computes <see cref="averageRms"/>.
    /// </summary>
    public static void RollingRMS()
    {
        float rms = GetRMS();
        rmsValues[rmsIndex] = rms;
        rmsIndex = (rmsIndex + 1) % LocalOpusSettings.rmsWindowSize;
        averageRms = rmsValues.Average();
    }

    /// <summary>
    /// Determines whether the current rolling window indicates voice activity above the silence threshold.
    /// </summary>
    public static bool IsTransmitWorthy()
    {
        return averageRms > LocalOpusSettings.silenceThreshold;
    }
}
