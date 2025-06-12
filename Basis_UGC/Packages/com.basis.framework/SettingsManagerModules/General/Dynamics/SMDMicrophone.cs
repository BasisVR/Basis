using BattlePhaze.SettingsManager;
using System.Collections.Generic;
using UnityEngine;

public class SMDMicrophone : SettingsManagerOption
{
    public SettingsManager Manager;
    // Define a delegate for the callback
    public delegate void MicrophoneChangedHandler(string newMicrophone);

    // Create an event of the delegate type
    public static event MicrophoneChangedHandler OnMicrophoneChanged;

    // Backing field for the SelectedMicrophone property
    private static string selectedMicrophone;

    // Property with a callback in the set accessor
    public static string SelectedMicrophone
    {
        get
        {
            return selectedMicrophone;
        }

        set
        {
            selectedMicrophone = value;
            // Invoke the callback event
            OnMicrophoneChanged?.Invoke(selectedMicrophone);
        }
    }
    public delegate void MicrophoneVolumeChangedHandler(float Volume);

    // Create an event of the delegate type
    public static event MicrophoneVolumeChangedHandler OnMicrophoneVolumeChanged;
    // Backing field for the SelectedMicrophone property
    private static float selectedVolumeMicrophone = 1;

    // Property with a callback in the set accessor
    public static float SelectedVolumeMicrophone
    {
        get
        {
            return selectedVolumeMicrophone;
        }

        set
        {
            selectedVolumeMicrophone = value;
            // Invoke the callback event
            OnMicrophoneVolumeChanged?.Invoke(selectedVolumeMicrophone);
        }
    }
    public delegate void MicrophoneUseDenoiserChangedHandler(bool useDenoiser);

    // Create an event of the delegate type
    public static event MicrophoneUseDenoiserChangedHandler OnMicrophoneUseDenoiserChanged;
    // Backing field for the SelectedMicrophone property
    private static bool selectedDenoiserMicrophone;

    // Property with a callback in the set accessor
    public static bool SelectedDenoiserMicrophone
    {
        get => selectedDenoiserMicrophone;
        set
        {
            selectedDenoiserMicrophone = value;
            // Invoke the callback event
            OnMicrophoneUseDenoiserChanged?.Invoke(selectedDenoiserMicrophone);
        }
    }
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager = null)
    {
        if (Manager == null)
        {
            Manager = SettingsManager.Instance;
        }
        if (NameReturn(0, Option))
        {
            SelectedDenoiserMicrophone = CheckIsOn(Option.SelectedValue);
        }
    }
    public static string[] MicrophoneDevices;
    public static Dictionary<string, string> MicrophoneSelections = new Dictionary<string, string>();
    public static Dictionary<string, float> VolumeSettings = new Dictionary<string, float>();

    public static void LoadInMicrophoneData(string mode)
    {
        BasisDebug.Log($"Loading microphone and volume for mode: {mode}");
        MicrophoneDevices = Microphone.devices;

        if (string.IsNullOrEmpty(mode))
        {
            BasisDebug.LogError("Missing Device Mode!");
            return;
        }

        string savedMicrophone = PlayerPrefs.GetString(mode + "_Microphone", "");
        float savedVolume = PlayerPrefs.GetFloat(mode + "_Volume", 1.0f);

        if (string.IsNullOrEmpty(savedMicrophone) && MicrophoneDevices.Length > 0)
        {
            savedMicrophone = MicrophoneDevices[0];
        }

        MicrophoneSelections[mode] = savedMicrophone;
        VolumeSettings[mode] = savedVolume;

        SelectedMicrophone = savedMicrophone;
        SelectedVolumeMicrophone = savedVolume;
    }

    public static void SaveMicrophoneData(string mode, string selectedMicrophone)
    {
        if (string.IsNullOrEmpty(mode))
        {
            BasisDebug.LogError("Missing Device Mode!");
            return;
        }

        BasisDebug.Log($"Saving selected microphone for mode: {mode}");
        MicrophoneSelections[mode] = selectedMicrophone;
        PlayerPrefs.SetString(mode + "_Microphone", selectedMicrophone);
        PlayerPrefs.Save();
        SelectedMicrophone = selectedMicrophone;
    }

    public static void SaveVolumeSettings(string mode, float volume)
    {
        if (string.IsNullOrEmpty(mode))
        {
            BasisDebug.LogError("Missing Device Mode!");
            return;
        }

        BasisDebug.Log($"Saving volume settings for mode: {mode}");
        VolumeSettings[mode] = volume;
        PlayerPrefs.SetFloat(mode + "_Volume", volume);
        PlayerPrefs.Save();
        SelectedVolumeMicrophone = volume;
    }
}
