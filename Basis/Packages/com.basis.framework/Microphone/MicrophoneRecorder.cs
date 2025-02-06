using UnityEngine;
using System;
using System.Linq;
using Basis.Scripts.Device_Management;
using Unity.Collections;
using Unity.Jobs;
using Basis.Scripts.Networking.Transmitters;
public partial class MicrophoneRecorder : MicrophoneRecorderBase
{
    private int head = 0;
    private int bufferLength;
    public bool HasEvents = false;
    public int PacketSize;
    public bool UseDenoiser = false;
    public static Action<bool> OnPausedAction;
    private bool MicrophoneIsStarted = false;
    public bool isRunning = true;
    private int position;
    private NativeArray<float> PBA;
    private LogarithmicVolumeAdjustmentJob VAJ;
    private JobHandle handle;
    public bool TryInitialize()
    {
        if (!IsInitialize)
        {
            BasisOpusSettings = BasisDeviceManagement.Instance.BasisOpusSettings;
            processBufferArray = BasisOpusSettings.CalculateProcessBuffer();
            PBA = new NativeArray<float>(processBufferArray, Allocator.Persistent);
            VAJ = new LogarithmicVolumeAdjustmentJob
            {
                processBufferArray = PBA,
                Volume = Volume
            };
            ProcessBufferLength = processBufferArray.Length;
            samplingFrequency = BasisOpusSettings.GetSampleFreq();
            microphoneBufferArray = new float[BasisOpusSettings.RecordingFullLength * samplingFrequency];
            bufferLength = microphoneBufferArray.Length;
            rmsValues = new float[rmsWindowSize];
            PacketSize = ProcessBufferLength * 4;
            if (!HasEvents)
            {
                SMDMicrophone.OnMicrophoneChanged += ResetMicrophones;
                SMDMicrophone.OnMicrophoneVolumeChanged += ChangeMicrophoneVolume;
                SMDMicrophone.OnMicrophoneUseDenoiserChanged += ConfigureDenoiser;
                BasisDeviceManagement.Instance.OnBootModeChanged += OnBootModeChanged;
                HasEvents = true;
            }
            ChangeMicrophoneVolume(SMDMicrophone.SelectedVolumeMicrophone);
            ResetMicrophones(SMDMicrophone.SelectedMicrophone);
            ConfigureDenoiser(SMDMicrophone.SelectedDenoiserMicrophone);
            IsInitialize = true;
            return true;
        }
        return false;
    }
    public new void OnDestroy()
    {
        if (HasEvents)
        {
            SMDMicrophone.OnMicrophoneChanged -= ResetMicrophones;
            SMDMicrophone.OnMicrophoneVolumeChanged -= ChangeMicrophoneVolume;
            SMDMicrophone.OnMicrophoneUseDenoiserChanged -= ConfigureDenoiser;
            BasisDeviceManagement.Instance.OnBootModeChanged -= OnBootModeChanged;

            HasEvents = false;
        }
        // Dispose the NativeArray when done to avoid memory leaks
        if (VAJ.processBufferArray.IsCreated)
        {
            VAJ.processBufferArray.Dispose();
        }
        base.OnDestroy();
    }
    private void ConfigureDenoiser(bool useDenoiser)
    {
        UseDenoiser = useDenoiser;
        BasisDebug.Log("Setting Denoiser To " + UseDenoiser);
    }
    private void OnBootModeChanged(string mode)
    {
        ResetMicrophones(SMDMicrophone.SelectedMicrophone);
    }
    public void ResetMicrophones(string newMicrophone)
    {
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
            BasisDebug.LogError("Microphone " + newMicrophone + " not found!");
            return;
        }
        bool isRecording = Microphone.IsRecording(newMicrophone);
        BasisDebug.Log(isRecording ? $"Is Recording {MicrophoneDevice}" : $"Is not Recording {MicrophoneDevice}");
        if (MicrophoneDevice != newMicrophone)
        {
            StopMicrophone();
        }
        if (!isRecording)
        {
            if (!IsPaused)
            {
                BasisDebug.Log("Starting Microphone :" + newMicrophone);
                clip = Microphone.Start(newMicrophone, true, BasisOpusSettings.RecordingFullLength, samplingFrequency);
                MicrophoneIsStarted = true;
            }
            else
            {
                BasisDebug.Log("Microphone Change Stored");
            }
            MicrophoneDevice = newMicrophone;
        }
    }
    private void StopMicrophone()
    {
        if (string.IsNullOrEmpty(MicrophoneDevice))
        {
            return;
        }
        Microphone.End(MicrophoneDevice);
        BasisDebug.Log("Stopped Microphone " + MicrophoneDevice);
        MicrophoneDevice = null;
        MicrophoneIsStarted = false;
    }
    public void ToggleIsPaused()
    {
        IsPaused = !IsPaused;
    }
    public void SetPauseState(bool isPaused)
    {
        IsPaused = isPaused;
    }
    public bool GetPausedState()
    {
        return IsPaused;
    }
    public static bool isPaused = false;
    private bool IsPaused
    {
        get
        {
            return isPaused;
        }
        set
        {
            isPaused = value;
            if (isPaused)
            {
                StopMicrophone();
            }
            else
            {
                ResetMicrophones(SMDMicrophone.SelectedMicrophone);
            }
            OnPausedAction?.Invoke(isPaused);
        }
    }
    public void LateUpdate()
    {
        if (!MicrophoneIsStarted)
            return;

        position = Microphone.GetPosition(MicrophoneDevice);
        if (position == head)
        {
            // No new data has been recorded since the last update
            return;
        }
        //microphoneBufferArray.AsSpan();
        // Directly access the span over the buffer1
        Span<float> microphoneBufferSpan = microphoneBufferArray.AsSpan();
        clip.GetData(microphoneBufferSpan, 0);
        int dataLength = GetDataLength(bufferLength, head, position);

        while (dataLength >= ProcessBufferLength)
        {
            Span<float> processBufferSpan = processBufferArray.AsSpan();

            int remain = bufferLength - head;
            if (remain < ProcessBufferLength)
            {
                // Handle buffer wrapping more efficiently using slicing
                microphoneBufferSpan.Slice(head, remain).CopyTo(processBufferSpan);
                microphoneBufferSpan.Slice(0, ProcessBufferLength - remain)
                    .CopyTo(processBufferSpan.Slice(remain));
            }
            else
            {
                microphoneBufferSpan.Slice(head, ProcessBufferLength)
                    .CopyTo(processBufferSpan);
            }

            processBufferSpan = AdjustVolume(processBufferSpan);  // Adjust the volume of the audio data

            if (UseDenoiser)
            {
                ApplyDeNoise(processBufferSpan);  // Apply noise gate before processing
            }

            RollingRMS(processBufferSpan);

            if (IsTransmitWorthy())
            {
                OnHasAudio?.Invoke();
                BasisAudioTransmission.OnAudioReady(processBufferSpan);
            }
            else
            {
                OnHasSilence?.Invoke();
            }

            head = (head + ProcessBufferLength) % bufferLength;
            dataLength -= ProcessBufferLength;
        }
    }
    public Span<float> AdjustVolume(Span<float> processBufferArray)
    {
        processBufferArray.CopyTo(VAJ.processBufferArray);
        handle = VAJ.Schedule(processBufferArray.Length, 64);
        handle.Complete();

       return VAJ.processBufferArray.AsSpan();
    }
    public float GetRMS(Span<float> processBufferArray)
    {
        // Use a double for the sum to avoid overflow and precision issues
        double sum = 0.0;

        for (int Index = 0; Index < ProcessBufferLength; Index++)
        {
            float value = processBufferArray[Index];
            sum += value * value;
        }

        return Mathf.Sqrt((float)(sum / ProcessBufferLength));
    }
    public int GetDataLength(int bufferLength, int head, int position)
    {
        if (position < head)
        {
            return bufferLength - head + position;
        }
        else
        {
            return position - head;
        }
    }
    public void ChangeMicrophoneVolume(float volume)
    {
        Volume = volume;
        // Create the job
        VAJ.Volume = Volume;
        BasisDebug.Log("Set Microphone Volume To "+ Volume);
    }
    public void ApplyDeNoise(Span<float> processBufferArray)
    {
        Denoiser.Denoise(processBufferArray);
    }
    public void RollingRMS(Span<float> processBufferArray)
    {
        float rms = GetRMS(processBufferArray);
        rmsValues[rmsIndex] = rms;
        rmsIndex = (rmsIndex + 1) % rmsWindowSize;
        averageRms = rmsValues.Average();
    }
    public bool IsTransmitWorthy()
    {
        return averageRms > silenceThreshold;
    }
}
