using Basis.Scripts.Settings;

namespace Basis.Developer.Recorder
{
    /// <summary>
    /// Persisted settings for the Avatar Recorder, relocated here from the framework's
    /// BasisSettingsDefaults. The binding keys are unchanged so existing saved values
    /// continue to load.
    /// </summary>
    public static class BasisRecorderSettings
    {
        static BasisRecorderSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisRecorderSettings));
        }

        public static BasisSettingsBinding<string> CountdownSeconds = new("recordercountdownseconds", new BasisPlatformDefault<string>("3"));
        public static BasisSettingsBinding<bool> AutoStop = new("recorderautostop", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> MaxDurationSeconds = new("recordermaxdurationseconds", new BasisPlatformDefault<string>("30"));

        public static void EnsureLoaded()
        {
            _ = CountdownSeconds;
            _ = AutoStop;
            _ = MaxDurationSeconds;
        }
    }
}
