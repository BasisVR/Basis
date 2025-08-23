using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Toggle))]
public class BasisTMP_ToggleSetting : MonoBehaviour
{
    public string settingKey;
    public BasisPlatformDefault<bool> platformDefault;

    public Toggle toggle;

    private void Awake()
    {
        bool defaultValue = platformDefault.GetDefault();
        toggle.isOn = BasisSettingsSystem.LoadBool(settingKey, defaultValue);

        toggle.onValueChanged.AddListener(async v =>
        {
            await BasisSettingsSystem.SetBoolAsync(settingKey, v);
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
            toggle.SetIsOnWithoutNotify(BasisSettingsSystem.LoadBool(settingKey));
    }
    private void OnValidate()
    {
        // Auto-assign in the editor if missing
        if (toggle == null)
            toggle = GetComponent<Toggle>();
    }
}
