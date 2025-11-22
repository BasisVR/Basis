namespace Basis.BasisUI
{
    public static class BasisSettingsDefaults
    {

        public static BasisSettingsBinding<float> MainVolume =>
            new("main volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> MenuVolume =>
            new("menu volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> WorldVolume =>
            new("world volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> PlayerVolume =>
            new("player volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> MicrophoneVolume =>
            new("Microphone Volume", new BasisPlatformDefault<float>(80));

        public static BasisSettingsBinding<float> ControllerDeadZone =>
            new("joystickdeadzone", new BasisPlatformDefault<float>(0.01f));

        public static BasisSettingsBinding<float> MicrophoneRange =>
            new("MicrophoneRange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> HearingRange =>
            new("HearingRange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> AvatarRange =>
            new("AvatarRange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> SnapTurnAngle =>
            new("snapturnangle", new BasisPlatformDefault<float>(-1));

        public static BasisSettingsBinding<bool> InvertMouse =>
            new("invertmouse", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> QualityLevel =>
            new("Quality Level", new BasisPlatformDefault<string>
            {
                windows = "Ultra",
                android = "Very Low",
                linux = "Ultra",
                other = "Ultra"
            });

        public static BasisSettingsBinding<string> ShadowQuality =>
            new("Shadow Quality", new BasisPlatformDefault<string>
            {
                windows = "Ultra",
                android = "Very Low",
                linux = "Ultra",
                other = "Ultra"
            });

        public static BasisSettingsBinding<string> HDRSupport =>
            new("HDR Support", new BasisPlatformDefault<string>
            {
                windows = "64bit",
                android = "off",
                linux = "64bit",
                other = "64bit"
            });

        public static BasisSettingsBinding<bool> MicrophoneDenoiser =>
            new("voicedenoiser", new BasisPlatformDefault<bool>
            {
                windows = true,
                android = false,
                linux = false,
                other = false
            });

        public static BasisSettingsBinding<string> Antialiasing =>
            new("Antialiasing", new BasisPlatformDefault<string>("MSAA 2X"));

        public static BasisSettingsBinding<bool> DebugVisuals =>
            new("Debug Visuals", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> MemoryAllocation =>
            new("HDR Support", new BasisPlatformDefault<string>
            {
                windows = "Dynamic",
                android = "Very Low",
                linux = "Dynamic",
                other = "Dynamic"
            });

        public static BasisSettingsBinding<string> MicrophoneIcon =>
            new("Microphone Icon", new BasisPlatformDefault<string>("AlwaysVisible"));

        public static BasisSettingsBinding<string> VisualState =>
            new("Visual State", new BasisPlatformDefault<string>("Off"));

        public static BasisSettingsBinding<float> FoveatedRendering =>
            new("Foveated Rendering", new BasisPlatformDefault<float>
            {
                windows = 0,
                android = 1,
                linux = 0,
                other = 0
            });

        public static BasisSettingsBinding<float> FieldOfView =>
            new("Field Of View", new BasisPlatformDefault<float>(65));

        public static BasisSettingsBinding<float> MeshLOD =>
            new("Mesh Lod", new BasisPlatformDefault<float>
            {
                windows = 0.05f,
                android = 0.1f,
                linux = 0.05f,
                other = 0.05f
            });

        public static BasisSettingsBinding<float> GlobalMeshLOD =>
            new("global meshlod", new BasisPlatformDefault<float>
            {
                windows = 0,
                android = 30,
                linux = 0,
                other = 0
            });

        public static BasisSettingsBinding<string> SeatedMode =>
            new("Seated Mode", new BasisPlatformDefault<string>("Standing Mode"));

        public static BasisSettingsBinding<string> VSync =>
            new("Vertical Sync", new BasisPlatformDefault<string>
            {
                windows = "On",
                android = "On",
                linux = "Capped",
                other = "On"
            });

        public static BasisSettingsBinding<string> Resolution => new("Resolution");

        public static BasisSettingsBinding<float> RenderResolution =>
            new("Resolution", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<string> Monitor => new("Monitor");

        public static BasisSettingsBinding<string> MicrophoneMode =>
            new("microphonemode", new BasisPlatformDefault<string>("OnActivation"));

        public static BasisSettingsBinding<bool> UseAutomaticGain =>
            new("AGC", new BasisPlatformDefault<bool>
            {
                windows = true,
                android = false,
                linux = false,
                other = false
            });
    }
}
