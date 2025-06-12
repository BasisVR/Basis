public interface BasisISettingHandler<T>
{
    // Provides the default value of the setting
    string GetDefaultValue();

    // Retrieves the current value of the setting
    string GetCurrentValue();

    // Sets the new value for the setting
    void SetValue(string value);
    void ApplySetting();
}
