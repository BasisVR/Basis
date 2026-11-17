using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Settings;
namespace Basis.Capture
{
    [NoAutoStaticsCleanup]
    public static class BasisCaptureSettings
    {
        static BasisCaptureSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisCaptureSettings));
        }
        public static BasisSettingsBinding<bool> IncludeRemotes = new("captureincluderemotes", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> IncludeScene = new("captureincludescene", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> ExportGeometry = new("captureexportgeometry", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> Microphone = new("capturemicrophone", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PeerMicrophone = new("capturepeermicrophone", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<string> RateHz = new("capturerate", new BasisPlatformDefault<string>("90"));
        public static BasisSettingsBinding<string> ConsentPolicy = new("captureconsentpolicy", new BasisPlatformDefault<string>("ask"));
        public static BasisSettingsBinding<string> MaxMinutes = new("capturemaxminutes", new BasisPlatformDefault<string>("30"));
        public static void EnsureLoaded()
        {
            _ = IncludeRemotes;
            _ = IncludeScene;
            _ = ExportGeometry;
            _ = Microphone;
            _ = PeerMicrophone;
            _ = MaxMinutes;
            _ = RateHz;
            _ = ConsentPolicy;
        }
        public static void ResetDefaults()
        {
            IncludeRemotes.ResetToDefault();
            IncludeScene.ResetToDefault();
            ExportGeometry.ResetToDefault();
            Microphone.ResetToDefault();
            PeerMicrophone.ResetToDefault();
            MaxMinutes.ResetToDefault();
            RateHz.ResetToDefault();
            ConsentPolicy.ResetToDefault();
        }
    }
}
