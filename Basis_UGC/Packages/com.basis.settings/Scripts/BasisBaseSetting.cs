public abstract class BasisBaseSetting<T> : BasisISettingHandler<string>
{
    public string Identifier { get; private set; }
    protected string defaultValue;
    protected string currentValue;

    public BasisBaseSetting(string identifier, string defaultValue)
    {
        Identifier = identifier;
        this.defaultValue = defaultValue;
        currentValue = defaultValue; // Initializing with the default value
    }

    // Retrieve the default value
    public string GetDefaultValue() => defaultValue;

    // Retrieve the current value
    public string GetCurrentValue() => currentValue;

    // Set a new value
    public void SetValue(string value)
    {
        currentValue = value;
        // Optionally, trigger an event to notify other systems about the change
    }

    // To be overridden in derived classes if additional save/load logic is needed
    public virtual void ApplySetting()
    {
        // Here you can handle saving the value or triggering other behavior
    }
}
