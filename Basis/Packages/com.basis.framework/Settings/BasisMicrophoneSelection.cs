using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;

public class BasisMicrophoneSelection : MonoBehaviour
{
    public TMP_Dropdown Dropdown;
    public Slider Volume;
    public TMP_Text MicrophoneVolume;

    public void Start()
    {
        Volume.maxValue = 1;
        Volume.minValue = 0;
        Volume.wholeNumbers = false;
        Dropdown.onValueChanged.AddListener(ApplyChanges);
        Volume.onValueChanged.AddListener(VolumeChanged);
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        GenerateUI();
    }

    public void OnDestroy()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
    }

    public void GenerateUI()
    {
        SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
        Dropdown.ClearOptions();
        List<TMP_Dropdown.OptionData> TmpOptions = new List<TMP_Dropdown.OptionData>();

        foreach (string device in SMDMicrophone.MicrophoneDevices)
        {
            TMP_Dropdown.OptionData option = new TMP_Dropdown.OptionData(device);
            TmpOptions.Add(option);
        }

        Dropdown.AddOptions(TmpOptions);
        Dropdown.value = MicrophoneToValue(SMDMicrophone.Current.Microphone);
        Volume.value = SMDMicrophone.Current.Volume01;
        UpdateMicrophoneVolumeText(SMDMicrophone.Current.Volume01);
    }

    public int MicrophoneToValue(string Active)
    {
        int Count = Dropdown.options.Count;
        for (int Index = 0; Index < Count; Index++)
        {
            TMP_Dropdown.OptionData optionData = Dropdown.options[Index];
            if (Active == optionData.text)
            {
                return Index;
            }
        }
        return 0;
    }

    private void OnBootModeChanged(string obj)
    {
        GenerateUI();
    }

    private void VolumeChanged(float value)
    {
        // Ensure we’re operating on the active mode snapshot (covers mode swaps while UI is open)
        if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
        {
            SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
        }

        // Single source of truth (updates prefs + emits ONE settings-changed event)
        SMDMicrophone.SetVolume(value);

        UpdateMicrophoneVolumeText(value);
    }

    private void ApplyChanges(int index)
    {
        if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
        {
            SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
        }

        var devices = SMDMicrophone.MicrophoneDevices;
        if (devices == null || devices.Length == 0) return;

        index = Mathf.Clamp(index, 0, devices.Length - 1);

        // Single source of truth (updates prefs + emits ONE settings-changed event)
        SMDMicrophone.SetMicrophone(devices[index]);
    }
    private void UpdateMicrophoneVolumeText(float value)
    {
        int percentage = Mathf.RoundToInt(value * 100);
        MicrophoneVolume.text = $"{percentage}%";
    }
}
