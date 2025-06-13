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
        SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.CurrentMode);
        Dropdown.ClearOptions();
        List<TMP_Dropdown.OptionData> TmpOptions = new List<TMP_Dropdown.OptionData>();

        foreach (string device in SMDMicrophone.MicrophoneDevices)
        {
            TMP_Dropdown.OptionData option = new TMP_Dropdown.OptionData(device);
            TmpOptions.Add(option);
        }

        Dropdown.AddOptions(TmpOptions);
        Dropdown.value = MicrophoneToValue(SMDMicrophone.SelectedMicrophone);
        Volume.value = SMDMicrophone.SelectedVolumeMicrophone;
        UpdateMicrophoneVolumeText(SMDMicrophone.SelectedVolumeMicrophone);
    }

    public int MicrophoneToValue(string Active)
    {
        for (int Index = 0; Index < Dropdown.options.Count; Index++)
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
        SMDMicrophone.SaveVolumeSettings(BasisDeviceManagement.CurrentMode, value);
        UpdateMicrophoneVolumeText(value);
    }

    private void ApplyChanges(int index)
    {
        SMDMicrophone.SaveMicrophoneData(BasisDeviceManagement.CurrentMode, SMDMicrophone.MicrophoneDevices[index]);
    }

    private void UpdateMicrophoneVolumeText(float value)
    {
        int percentage = Mathf.RoundToInt(value * 100);
        MicrophoneVolume.text = percentage + "%";
    }
}
