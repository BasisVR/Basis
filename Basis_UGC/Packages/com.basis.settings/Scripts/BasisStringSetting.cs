public class BasisStringSetting : BasisBaseSetting<string>
{
    public BasisStringSetting(string identifier, string defaultValue) : base(identifier, defaultValue) { }

    // Custom logic to apply the setting, such as saving to a file
    public override async void ApplySetting()
    {
        // For example, you can save the value to the global settings data
        var existingItem = BasisSettingsSaver.SettingsData.FindItemByIdentifier(Identifier);
        if (existingItem == null)
        {
            BasisSettingsSaver.SettingsData.itemsDictionary.Add(Identifier, new BasisDataItem { identifier = Identifier, value = currentValue });
        }
        else
        {
            existingItem.value = currentValue;
        }

      await  BasisSettingsSaver.SaveDataAsync();
    }
}
