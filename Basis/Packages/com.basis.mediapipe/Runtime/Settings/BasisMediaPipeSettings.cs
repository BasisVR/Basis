using Basis.Scripts.Settings;

namespace Basis.MediaPipe
{
    /// <summary>Persistent settings for webcam tracking, surfaced in the Settings "Webcam Tracking" tab.</summary>
    public static class BasisMediaPipeSettings
    {
        // First touch can precede BasisSettingsSystem reading the settings file (static init via
        // RuntimeInitializeOnLoadMethod); re-load the bindings once the store is populated or
        // saved values never restore into RawValue.
        static BasisMediaPipeSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisMediaPipeSettings));
        }

        public static readonly BasisSettingsBinding<bool> Enable =
            new BasisSettingsBinding<bool>("mediapipe_enable", new BasisPlatformDefault<bool>(false));

        public static readonly BasisSettingsBinding<string> Camera =
            new BasisSettingsBinding<string>("mediapipe_camera", new BasisPlatformDefault<string>(string.Empty));

        public static readonly BasisSettingsBinding<bool> EnableFace =
            new BasisSettingsBinding<bool>("mediapipe_face", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> EnableHands =
            new BasisSettingsBinding<bool>("mediapipe_hands", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> Mirror =
            new BasisSettingsBinding<bool>("mediapipe_mirror", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> EnableHeadPosition =
            new BasisSettingsBinding<bool>("mediapipe_headposition", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> EnableHeadRotation =
            new BasisSettingsBinding<bool>("mediapipe_headrotation", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> EnableHandTracking =
            new BasisSettingsBinding<bool>("mediapipe_handtracking_v2", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> EnableBody =
            new BasisSettingsBinding<bool>("mediapipe_body", new BasisPlatformDefault<bool>(false));

        public static readonly BasisSettingsBinding<bool> SwapHands =
            new BasisSettingsBinding<bool>("mediapipe_swaphands", new BasisPlatformDefault<bool>(false));

        public static readonly BasisSettingsBinding<float> ArmHeadAnchor =
            new BasisSettingsBinding<float>("mediapipe_armheadanchor", new BasisPlatformDefault<float>(1f));

        public static readonly BasisSettingsBinding<bool> EnableArmElbowPole =
            new BasisSettingsBinding<bool>("mediapipe_armelbowpole", new BasisPlatformDefault<bool>(false));

        public static readonly BasisSettingsBinding<float> ElbowRestBias =
            new BasisSettingsBinding<float>("mediapipe_elbowrestbias", new BasisPlatformDefault<float>(0.5f));

        public static readonly BasisSettingsBinding<float> ChestMotion =
            new BasisSettingsBinding<float>("mediapipe_chestmotion", new BasisPlatformDefault<float>(0.6f));

        public static readonly BasisSettingsBinding<bool> InvertBlink =
            new BasisSettingsBinding<bool>("mediapipe_invertblink", new BasisPlatformDefault<bool>(false));

        // Keys bumped to _v2: a changed default is ignored by anyone who has already run the app, because the old
        // value is pinned on disk. Bumping the key is what actually ships the new default to existing installs.
        public static readonly BasisSettingsBinding<bool> InvertHeadYaw =
            new BasisSettingsBinding<bool>("mediapipe_invertheadyaw_v2", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> InvertHeadPitch =
            new BasisSettingsBinding<bool>("mediapipe_invertheadpitch_v2", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<bool> InvertHeadRoll =
            new BasisSettingsBinding<bool>("mediapipe_invertheadroll", new BasisPlatformDefault<bool>(false));

        public static readonly BasisSettingsBinding<float> HeadSmoothing =
            new BasisSettingsBinding<float>("mediapipe_headsmoothing_v2", new BasisPlatformDefault<float>(0.8f));

        public static readonly BasisSettingsBinding<float> FaceSmoothing =
            new BasisSettingsBinding<float>("mediapipe_facesmoothing_v2", new BasisPlatformDefault<float>(0.8f));

        public static readonly BasisSettingsBinding<float> HandSmoothing =
            new BasisSettingsBinding<float>("mediapipe_handsmoothing_v2", new BasisPlatformDefault<float>(0.8f));

        public static readonly BasisSettingsBinding<float> FingerSmoothing =
            new BasisSettingsBinding<float>("mediapipe_fingersmoothing_v2", new BasisPlatformDefault<float>(0.8f));

        public static readonly BasisSettingsBinding<bool> HandRotation =
            new BasisSettingsBinding<bool>("mediapipe_handrotation", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<float> HeadPositionStrength =
            new BasisSettingsBinding<float>("mediapipe_headpositionstrength_v2", new BasisPlatformDefault<float>(0.6f));

        public static readonly BasisSettingsBinding<float> HeadRotationStrength =
            new BasisSettingsBinding<float>("mediapipe_headrotationstrength_v2", new BasisPlatformDefault<float>(0.6f));

        public static readonly BasisSettingsBinding<float> HeadHeight =
            new BasisSettingsBinding<float>("mediapipe_headheight", new BasisPlatformDefault<float>(0f));

        public static readonly BasisSettingsBinding<int> ResolutionWidth =
            new BasisSettingsBinding<int>("mediapipe_reswidth", new BasisPlatformDefault<int>(640));

        public static readonly BasisSettingsBinding<int> ResolutionHeight =
            new BasisSettingsBinding<int>("mediapipe_resheight", new BasisPlatformDefault<int>(480));

        public static readonly BasisSettingsBinding<int> CameraFps =
            new BasisSettingsBinding<int>("mediapipe_camerafps", new BasisPlatformDefault<int>(30));

        public static readonly BasisSettingsBinding<bool> EnableTongue =
            new BasisSettingsBinding<bool>("mediapipe_tongue", new BasisPlatformDefault<bool>(true));

        public static readonly BasisSettingsBinding<float> TongueStrength =
            new BasisSettingsBinding<float>("mediapipe_tonguestrength", new BasisPlatformDefault<float>(1f));

        /// <summary>
        /// Re-reads every binding from the loaded settings dictionary. Must run after
        /// BasisSettingsSystem has loaded from disk (it replaces the dictionary), mirroring
        /// BasisSettingsDefaults.LoadAll. Otherwise bindings keep their construction-time defaults.
        /// </summary>
        public static void LoadAll()
        {
            Enable.LoadBindingValue();
            Camera.LoadBindingValue();
            EnableFace.LoadBindingValue();
            EnableHands.LoadBindingValue();
            EnableHeadPosition.LoadBindingValue();
            EnableHeadRotation.LoadBindingValue();
            EnableHandTracking.LoadBindingValue();
            EnableBody.LoadBindingValue();
            SwapHands.LoadBindingValue();
            ArmHeadAnchor.LoadBindingValue();
            EnableArmElbowPole.LoadBindingValue();
            ElbowRestBias.LoadBindingValue();
            ChestMotion.LoadBindingValue();
            Mirror.LoadBindingValue();
            InvertBlink.LoadBindingValue();
            InvertHeadYaw.LoadBindingValue();
            InvertHeadPitch.LoadBindingValue();
            InvertHeadRoll.LoadBindingValue();
            HeadSmoothing.LoadBindingValue();
            FaceSmoothing.LoadBindingValue();
            HandSmoothing.LoadBindingValue();
            FingerSmoothing.LoadBindingValue();
            HandRotation.LoadBindingValue();
            HeadPositionStrength.LoadBindingValue();
            HeadRotationStrength.LoadBindingValue();
            HeadHeight.LoadBindingValue();
            ResolutionWidth.LoadBindingValue();
            ResolutionHeight.LoadBindingValue();
            CameraFps.LoadBindingValue();
            EnableTongue.LoadBindingValue();
            TongueStrength.LoadBindingValue();
        }
    }
}
