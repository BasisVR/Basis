using TMPro;
using UnityEngine;

[RequireComponent(typeof(TMP_Dropdown))]
public class BasisTMP_DropdownSetting : MonoBehaviour
{
    public string settingKey;

    [SerializeField]
    public BasisPlatformDefault<string> platformDefault; // use string instead of int

    public TMP_Dropdown dropdown;
    public TextMeshProUGUI Text;

    [Header("Display Options")]
    public bool showSelectedLabel = true; // default true, shows selected option text

    [Header("Editor Auto Setup")]
    public bool autoValidate = true; // when true, OnValidate can set defaults automatically

    private void Awake()
    {
        Initialize();
    }

    public void Initialize()
    {
        string defaultValue = platformDefault.GetDefault();

        // Try to load saved string
        string savedValue = BasisSettingsSystem.LoadString(settingKey, defaultValue);

        // Find index for saved value
        int index = FindOptionIndex(savedValue);
        if (index < 0 && dropdown.options.Count > 0)
            index = 0; // fallback to first option if not found

        dropdown.value = index;
        UpdateText(index);

        dropdown.onValueChanged.AddListener(v =>
        {
            string optionName = GetOptionName(v);
             BasisSettingsSystem.SaveString(settingKey, optionName);
            UpdateText(v);
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
        {
            int index = FindOptionIndex(value);
            if (index >= 0)
            {
                dropdown.SetValueWithoutNotify(index);
                UpdateText(index);
            }
        }
    }

    private void UpdateText(int index)
    {
        if (Text == null || dropdown == null) return;

        if (showSelectedLabel && index >= 0 && index < dropdown.options.Count)
        {
            Text.text = dropdown.options[index].text;
        }
        else
        {
            // fallback to index number
            Text.text = index.ToString();
        }
    }

    private int FindOptionIndex(string value)
    {
        if (string.IsNullOrEmpty(value) || dropdown == null) return -1;

        for (int i = 0; i < dropdown.options.Count; i++)
        {
            if (dropdown.options[i].text == value)
                return i;
        }
        return -1;
    }

    private string GetOptionName(int index)
    {
        if (dropdown == null || index < 0 || index >= dropdown.options.Count)
            return string.Empty;

        return dropdown.options[index].text;
    }

    private void OnValidate()
    {
        // Auto-assign dropdown if missing
        if (dropdown == null)
            dropdown = GetComponent<TMP_Dropdown>();

        // Auto-assign Text if missing (search in children, excluding dropdown.label itself to allow external label)
        if (Text == null)
        {
            foreach (var tmp in GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp.gameObject != dropdown.captionText?.gameObject)
                {
                    Text = tmp;
                    break;
                }
            }
        }

        // Auto-assign setting key if empty
        if (string.IsNullOrEmpty(settingKey))
            settingKey = this.gameObject.name;

        // If AutoValidate is enabled and default is empty → match dropdown's current option
        if (autoValidate && string.IsNullOrEmpty(platformDefault.GetDefault()) && dropdown != null && dropdown.options.Count > 0)
        {
            string currentName = GetOptionName(dropdown.value);
            platformDefault.android = currentName;
            platformDefault.windows = currentName;
            platformDefault.linux = currentName;
            platformDefault.other = currentName;
        }

        // Keep label preview updated in editor
        if (dropdown != null)
            UpdateText(dropdown.value);
    }
}
