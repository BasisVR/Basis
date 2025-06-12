using System;
using UnityEngine;
using TMPro;
using System.Globalization;

public class BasisSettingUIHandler<T> : MonoBehaviour
{
    public BasisISettingHandler<T> setting;
    public TMP_InputField inputField;
    public TMP_Dropdown dropdown;

    // For generic settings, this will update the value based on the input field or dropdown selection
    public void SetUp(SettingType type)
    {
        if (type == SettingType.InputField)
        {
            inputField.onValueChanged.AddListener(OnValueChangedFromInput);
            inputField.text = setting.GetCurrentValue();
        }
        else if (type == SettingType.Dropdown)
        {
            dropdown.onValueChanged.AddListener(OnValueChangedFromDropdown);

            dropdown.options.Clear();
            TMP_Dropdown.OptionData Data = new TMP_Dropdown.OptionData();
            dropdown.options.Add(Data);

            if (int.TryParse(setting.GetCurrentValue(), out int value))
            {
                dropdown.value = value;
            }
        }
    }

    private void OnValueChangedFromInput(string value)
    {
        setting.SetValue(value);
        setting.ApplySetting();
    }

    private void OnValueChangedFromDropdown(int value)
    {
        setting.SetValue(value.ToString(CultureInfo.InvariantCulture));
        setting.ApplySetting();
    }
    public enum SettingType
    {
        InputField,
        Dropdown
    }
}
