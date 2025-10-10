using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Slider))]
public class BasisTMP_SliderSetting : MonoBehaviour
{
    public string settingKey;

    [SerializeField]
    public BasisPlatformDefault<float> platformDefault;

    public Slider slider;
    public TextMeshProUGUI Text;
    public Button applyButton;

    [Header("Display Options")]
    public DisplayMode displayMode = DisplayMode.Percentage;
    public int decimalPlaces = 2; // used for Raw and Meters

    [Header("Editor Auto Setup")]
    public bool autoValidate = true;

    private float pendingValue;

    public enum DisplayMode
    {
        Percentage,
        Raw,
        Meters
    }

    private void Awake()
    {
        Initialize();
    }

    public void Initialize()
    {
        float defaultValue = platformDefault.GetDefault();
        float savedValue = BasisSettingsSystem.LoadFloat(settingKey, defaultValue);

        slider.value = savedValue;
        pendingValue = savedValue;

        UpdateText(slider.value);

        slider.onValueChanged.AddListener(v =>
        {
            pendingValue = v;
            UpdateText(v);
        });

        if (applyButton != null)
            applyButton.onClick.AddListener(ApplySetting);

        BasisSettingsSystem.OnSettingChanged += HandleSettingChanged;
    }

    private void OnDestroy()
    {
        BasisSettingsSystem.OnSettingChanged -= HandleSettingChanged;

        if (applyButton != null)
            applyButton.onClick.RemoveListener(ApplySetting);
    }

    private void ApplySetting()
    {
         BasisSettingsSystem.SetFloat(settingKey, pendingValue);
        // Debug.Log($"[Settings] Applied {settingKey} = {pendingValue}");
    }

    private void HandleSettingChanged(string key, string value)
    {
        if (key == settingKey && float.TryParse(value, out float f))
        {
            slider.SetValueWithoutNotify(f);
            pendingValue = f;
            UpdateText(f);
        }
    }

    private void UpdateText(float value)
    {
        if (Text == null || slider == null) return;

        switch (displayMode)
        {
            case DisplayMode.Percentage:
                float range = slider.maxValue - slider.minValue;
                float normalized = (range > 0f) ? (value - slider.minValue) / range : 0f;
                Text.text = $"{Mathf.RoundToInt(normalized * 100f)}%";
                break;

            case DisplayMode.Raw:
                Text.text = value.ToString("0." + new string('#', decimalPlaces));
                break;

            case DisplayMode.Meters:
                Text.text = value.ToString("0." + new string('#', decimalPlaces)) + " m";
                break;
        }
    }

    private void OnValidate()
    {
        if (slider == null)
            slider = GetComponent<Slider>();

        if (Text == null)
            Text = GetComponentInChildren<TextMeshProUGUI>();

        if (string.IsNullOrEmpty(settingKey))
            settingKey = this.gameObject.name;

        if (autoValidate && Mathf.Approximately(platformDefault.GetDefault(), 0f) && slider != null)
        {
            platformDefault.android = slider.value;
            platformDefault.windows = slider.value;
            platformDefault.linux = slider.value;
            platformDefault.other = slider.value;
        }

        if (slider != null)
            UpdateText(slider.value);
    }
}
