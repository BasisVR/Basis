using UnityEngine;
using TMPro;

[RequireComponent(typeof(TMP_InputField))]
public class BasisTMP_InputSetting : MonoBehaviour
{
    public string settingKey;
    public BasisPlatformDefault<string> platformDefault;

    public TMP_InputField input;

    private void Awake()
    {
        string defaultValue = platformDefault.GetDefault();
        input.text = BasisSettingsSystem.LoadString(settingKey, defaultValue);

        input.onValueChanged.AddListener(async v =>
        {
            await BasisSettingsSystem.SaveString(settingKey, v);
        });

        BasisSettingsSystem.OnSettingChanged += HandleSettingChanged;
    }

    private void OnDestroy()
    {
        BasisSettingsSystem.OnSettingChanged -= HandleSettingChanged;
    }

    private void HandleSettingChanged(string key, string value)
    {
        if (key == settingKey)
            input.SetTextWithoutNotify(value);
    }
    private void OnValidate()
    {
        // Auto-assign in the editor if missing
        if (input == null)
            input = GetComponent<TMP_InputField>();
    }
}
