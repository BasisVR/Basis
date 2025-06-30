using Basis.Scripts.BasisSdk.Players;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

public class BasisUIVolumeSampler : MonoBehaviour
{
    public Image[] MicrophoneSections;
    public int sampleSize = 512; // Number of samples per frame
    public float MicrophoneSectionsLength;

    public float UINormalizerValue = 2; // Scales the RMS value to make the UI more sensitive

    public float[] spectrumData;
    public float rms;
    public float peak;
    public bool HasEvent;
    public BasisRemotePlayer RemotePlayer;

    public void Initalize(BasisRemotePlayer Remoteplayer)
    {
        RemotePlayer = Remoteplayer;
        if (RemotePlayer == null || RemotePlayer.NetworkReceiver == null || RemotePlayer.NetworkReceiver.AudioReceiverModule == null || RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver == null)
        {
            return;
        }

        MicrophoneSectionsLength = MicrophoneSections.Length;

        if (!IsPowerOfTwo(sampleSize) || sampleSize < 64 || sampleSize > 8192)
        {
            Debug.LogError("Sample size must be a power of two between 64 and 8192. Defaulting to 1024.");
            sampleSize = 1024;
        }

        spectrumData = new float[sampleSize];
        RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver.AudioData += OnAudio;
        HasEvent = true;
    }

    public void OnDestroy()
    {
        if (HasEvent)
        {
            HasEvent = false;
            RemotePlayer.NetworkReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver.AudioData -= OnAudio;
        }
    }

    private bool IsPowerOfTwo(int value)
    {
        return (value & (value - 1)) == 0 && value > 0;
    }

    private void OnAudio(float[] data, int channels)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        System.Array.Copy(data, spectrumData, Mathf.Min(data.Length, spectrumData.Length));

        for (int i = 0; i < spectrumData.Length; i++)
        {
            if (float.IsNaN(spectrumData[i]) || float.IsInfinity(spectrumData[i]))
            {
                spectrumData[i] = 0f;
            }
        }

        rms = CalculateSpectrumRMS();
        peak = CalculateSpectrumPeak();
    }

    public void Update()
    {
        UpdateVolumeDisplay(rms, peak);
    }

    private void UpdateVolumeDisplay(float rms, float peak)
    {
        float normalizedRMS = Mathf.Clamp(rms * UINormalizerValue, 0f, 1.5f);
        float normalizedPeak = Mathf.Clamp(peak * UINormalizerValue, 0f, 1.5f);

        if (MicrophoneSections == null || MicrophoneSections.Length == 0)
        {
            Debug.LogError("No microphone sections assigned!");
            return;
        }

        float Maximum = Mathf.Max(normalizedRMS, normalizedPeak);

        for (int i = 0; i < MicrophoneSectionsLength; i++)
        {
            float sectionValue = (float)i / MicrophoneSectionsLength;
            Color barColor = GetColorGradient(sectionValue, Maximum);
            MicrophoneSections[i].color = barColor;
        }
    }

    // ✅ Modified function to return red when volume > 1
    private Color GetColorGradient(float sectionValue, float volumeLevel)
    {
        if (sectionValue < Mathf.Max(volumeLevel, 0))
        {
            if (volumeLevel > 1.0f)
            {
                return Color.red; // Overdriven red
            }
            else if (volumeLevel < 0.3f)
            {
                return Color.Lerp(Color.green, Color.yellow, volumeLevel * 3.33f);
            }
            else if (volumeLevel < 0.7f)
            {
                return Color.Lerp(Color.yellow, new Color(1f, 0.647f, 0f), (volumeLevel - 0.3f) * 3.33f); // Yellow to Orange
            }
            else
            {
                return Color.Lerp(new Color(1f, 0.647f, 0f), Color.red, (volumeLevel - 0.7f) * 3.33f); // Orange to Red
            }
        }

        return Color.gray;
    }

    private float CalculateSpectrumRMS()
    {
        if (spectrumData == null || spectrumData.Length == 0)
        {
            return 0f;
        }

        float sumOfSquares = spectrumData.Sum(value => value * value);
        float rmsValue = Mathf.Sqrt(sumOfSquares / spectrumData.Length);
        return float.IsNaN(rmsValue) || float.IsInfinity(rmsValue) ? 0f : rmsValue;
    }

    private float CalculateSpectrumPeak()
    {
        if (spectrumData == null || spectrumData.Length == 0)
        {
            return 0f;
        }

        float peakValue = spectrumData.Max();
        return float.IsNaN(peakValue) || float.IsInfinity(peakValue) ? 0f : peakValue;
    }
}
