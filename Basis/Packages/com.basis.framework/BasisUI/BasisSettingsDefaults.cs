namespace Basis.BasisUI
{
    public static class BasisSettingsDefaults
    {
        public static BasisSettingsBinding<float> MainVolume => new("main volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> MenuVolume => new("menuvolume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> WorldVolume => new("worldvolume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> PlayerVolume => new("playervolume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> MicrophoneVolume => new("microphonevolume", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<float> ControllerDeadZone => new("joystickdeadzone", new BasisPlatformDefault<float>(0.01f));

        public static BasisSettingsBinding<float> Basexdeadzone => new("basexdeadzone", new BasisPlatformDefault<float>(0.08f));

        public static BasisSettingsBinding<float> Extraxdeadzoneatfully => new("extraxdeadzoneatfully", new BasisPlatformDefault<float>(0.35f));

        public static BasisSettingsBinding<float> Ydeadzone => new("ydeadzone", new BasisPlatformDefault<float>(0.10f));

        public static BasisSettingsBinding<float> Wingexponent => new("wingexponent", new BasisPlatformDefault<float>(1.6f));

        public static BasisSettingsBinding<float> MicrophoneRange => new("microphonerange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> HearingRange => new("hearingrange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> SelectedHeight => new("selectedheight", new BasisPlatformDefault<float>(1.6f));

        public static BasisSettingsBinding<float> SelectedScale => new("selectedscale", new BasisPlatformDefault<float>(1.6f));

        public static BasisSettingsBinding<float> realworldeyeheight => new("realworldeyeheight", new BasisPlatformDefault<float>(1.61f));

        public static BasisSettingsBinding<bool> CustomScale => new("customscale", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> AvatarRange => new("avatarrange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> SnapTurnAngle => new("snapturnangle", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<float> mousesensitivty => new("mousesensitivty", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<bool> InvertMouse => new("invertmouse", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> usesnapturn => new("usesnapturn", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> QualityLevel => new("qualitylevel", new BasisPlatformDefault<string>
        {
            windows = "Ultra",
            android = "Very Low",
            linux = "Ultra",
            other = "Ultra"
        });

        public static BasisSettingsBinding<string> ShadowQuality => new("shadowquality", new BasisPlatformDefault<string>
        {
            windows = "Ultra",
            android = "Very Low",
            linux = "Ultra",
            other = "Ultra"
        });

        public static BasisSettingsBinding<string> HDRSupport => new("hdrsupport", new BasisPlatformDefault<string>
        {
            windows = "64bit",
            android = "Off",
            linux = "64bit",
            other = "64bit"
        });

        public static BasisSettingsBinding<bool> MicrophoneDenoiser => new("voicedenoiser", new BasisPlatformDefault<bool>
        {
            windows = true,
            android = false,
            linux = false,
            other = false
        });

        public static BasisSettingsBinding<string> Antialiasing => new("antialiasing", new BasisPlatformDefault<string>("msaa 2x"));

        public static BasisSettingsBinding<bool> DebugVisuals => new("debugvisuals", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> MemoryAllocation => new("memoryallocation", new BasisPlatformDefault<string>
        {
            windows = "Dynamic",
            android = "Dnamic",
            linux = "Dynamic",
            other = "Dynamic"
        });

        public static BasisSettingsBinding<string> MicrophoneIcon => new("microphoneicon", new BasisPlatformDefault<string>("alwaysvisible"));

        public static BasisSettingsBinding<string> VisualState => new("visualstate", new BasisPlatformDefault<string>("off"));

        public static BasisSettingsBinding<string> IKMode => new("ikmode", new BasisPlatformDefault<string>("eye height"));

        public static BasisSettingsBinding<string> SelectedBone => new("selectedbone", new BasisPlatformDefault<string>("selectedbone"));

        public static BasisSettingsBinding<float> FoveatedRendering => new("foveatedrendering", new BasisPlatformDefault<float>
        {
            windows = 0,
            android = 1,
            linux = 0,
            other = 0,
            ios = 0
        });

        public static BasisSettingsBinding<float> FieldOfView => new("fieldofview", new BasisPlatformDefault<float>(65));

        public const float FOV_MIN = 50;
        public const float FOV_MAX = 120;

        public static BasisSettingsBinding<float> AvatarDownloadSize => new("avatardownloadsize", new BasisPlatformDefault<float>(256));

        public static BasisSettingsBinding<float> AvatarMeshLOD => new("avatarmeshlod", new BasisPlatformDefault<float>
        {
            windows = 0.05f,
            android = 0.1f,
            linux = 0.05f,
            other = 0.05f
        });

        public static BasisSettingsBinding<float> GlobalMeshLOD => new("globalmeshlod", new BasisPlatformDefault<float>
        {
            windows = 0,
            android = 30,
            linux = 0,
            other = 0
        });

        public static BasisSettingsBinding<string> SitStand => new("seatedmode", new BasisPlatformDefault<string>(SettingsProviderIK.SeatedMode_Standing));

        public static BasisSettingsBinding<string> VSync => new("verticalsync", new BasisPlatformDefault<string>
        {
            windows = "On",
            android = "On",
            linux = "Capped",
            other = "On"
        });

        public static BasisSettingsBinding<float> RenderResolution => new("render resolution", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<string> MicrophoneMode => new("microphonemode", new BasisPlatformDefault<string>("onactivation"));

        public static BasisSettingsBinding<bool> UseAutomaticGain => new("agc", new BasisPlatformDefault<bool>
        {
            windows = true,
            android = false,
            linux = false,
            other = false
        });

        public static BasisSettingsBinding<bool> FalseBinding => new("falsebinding", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> TrueBinding => new("truebinding", new BasisPlatformDefault<bool>(false));

        // ---------------- GLOBAL ONE EURO PARAMS ----------------
        public static BasisSettingsBinding<float> FBIKMinCutoff => new("fbikmincutoff", new BasisPlatformDefault<float>(5.5f));

        public static BasisSettingsBinding<float> FBIKBeta => new("fbikbeta", new BasisPlatformDefault<float>(3.25f));

        public static BasisSettingsBinding<float> FBIKDerivativeCutoff => new("fbikderivativecutoff", new BasisPlatformDefault<float>(3f));

        public static BasisSettingsBinding<float> FBIKPositionSmoothingHz =>
            new("fbikpositionsmoothinghz", new BasisPlatformDefault<float>(20f));

        public static BasisSettingsBinding<float> FBIKRotationSmoothingHz =>
            new("fbikrotationsmoothinghz", new BasisPlatformDefault<float>(25f));

        // ---------------- HIPS ----------------
        public static BasisSettingsBinding<bool> FBIKHipsSmoothPos =>
            new("fbikhipssmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHipsSmoothRot =>
            new("fbikhipssmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHipsEuroPos =>
            new("fbikhipseuropos", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> FBIKHipsEuroRot =>
            new("fbikhipseurorot", new BasisPlatformDefault<bool>(true));

        // ---------------- HEAD ----------------
        public static BasisSettingsBinding<bool> FBIKHeadSmoothPos =>
            new("fbikheadsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHeadSmoothRot =>
            new("fbikheadsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHeadEuroPos =>
            new("fbikheadeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHeadEuroRot =>
            new("fbikheadeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT FOOT ----------------
        public static BasisSettingsBinding<bool> FBIKLeftFootSmoothPos =>
            new("fbikleftfootsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftFootSmoothRot =>
            new("fbikleftfootsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftFootEuroPos =>
            new("fbikleftfooteuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftFootEuroRot =>
            new("fbikleftfooteurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT FOOT ----------------
        public static BasisSettingsBinding<bool> FBIKRightFootSmoothPos =>
            new("fbikrightfootsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightFootSmoothRot =>
            new("fbikrightfootsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightFootEuroPos =>
            new("fbikrightfooteuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightFootEuroRot =>
            new("fbikrightfooteurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- CHEST ----------------
        public static BasisSettingsBinding<bool> FBIKChestSmoothPos =>
            new("fbikchestsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKChestSmoothRot =>
            new("fbikchestsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKChestEuroPos =>
            new("fbikchesteuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKChestEuroRot =>
            new("fbikchesteurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT LOWER LEG ----------------
        public static BasisSettingsBinding<bool> FBIKLeftLowerLegSmoothPos =>
            new("fbikleftlowerlegsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerLegSmoothRot =>
            new("fbikleftlowerlegsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerLegEuroPos =>
            new("fbikleftlowerlegeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerLegEuroRot =>
            new("fbikleftlowerlegeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT LOWER LEG ----------------
        public static BasisSettingsBinding<bool> FBIKRightLowerLegSmoothPos =>
            new("fbikrightlowerlegsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerLegSmoothRot =>
            new("fbikrightlowerlegsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerLegEuroPos =>
            new("fbikrightlowerlegeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerLegEuroRot =>
            new("fbikrightlowerlegeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT HAND ----------------
        public static BasisSettingsBinding<bool> FBIKLeftHandSmoothPos =>
            new("fbiklefthandsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftHandSmoothRot =>
            new("fbiklefthandsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftHandEuroPos =>
            new("fbikleftehandeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftHandEuroRot =>
            new("fbikleftehandeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT HAND ----------------
        public static BasisSettingsBinding<bool> FBIKRightHandSmoothPos =>
            new("fbikrighthandsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightHandSmoothRot =>
            new("fbikrighthandsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightHandEuroPos =>
            new("fbikrighthandeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightHandEuroRot =>
            new("fbikrighthandeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT LOWER ARM ----------------
        public static BasisSettingsBinding<bool> FBIKLeftLowerArmSmoothPos =>
            new("fbikleftlowerarmsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerArmSmoothRot =>
            new("fbikleftlowerarmsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerArmEuroPos =>
            new("fbikleftlowerarmeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerArmEuroRot =>
            new("fbikleftlowerarmeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT LOWER ARM ----------------
        public static BasisSettingsBinding<bool> FBIKRightLowerArmSmoothPos =>
            new("fbikrightlowerarmsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerArmSmoothRot =>
            new("fbikrightlowerarmsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerArmEuroPos =>
            new("fbikrightlowerarmeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerArmEuroRot =>
            new("fbikrightlowerarmeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT TOE ----------------
        public static BasisSettingsBinding<bool> FBIKLeftToeSmoothPos =>
            new("fbiklefttoesmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftToeSmoothRot =>
            new("fbiklefttoesmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftToeEuroPos =>
            new("fbiklefttoeeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftToeEuroRot =>
            new("fbiklefttoeeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT TOE ----------------
        public static BasisSettingsBinding<bool> FBIKRightToeSmoothPos =>
            new("fbikrighttoesmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightToeSmoothRot =>
            new("fbikrighttoesmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightToeEuroPos =>
            new("fbikrighttoeeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightToeEuroRot => new("fbikrighttoeeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT SHOULDER ----------------
        public static BasisSettingsBinding<bool> FBIKLeftShoulderSmoothPos => new("fbikleftshouldersmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftShoulderSmoothRot => new("fbikleftshouldersmoothrot", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> FBIKLeftShoulderEuroPos => new("fbikleftshouldereuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftShoulderEuroRot => new("fbikleftshouldereurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT SHOULDER ----------------
        public static BasisSettingsBinding<bool> FBIKRightShoulderSmoothPos => new("fbikrightshouldersmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightShoulderSmoothRot => new("fbikrightshouldersmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightShoulderEuroPos => new("fbikrightshouldereuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightShoulderEuroRot => new("fbikrightshouldereurorot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> VSyncCapFps => new("vsynccappedset", new BasisPlatformDefault<string>
        {
            windows = "120",
            android = "60",
            linux = "120",
            other = "120"
        });

        public static readonly BasisSettingsBinding<bool> FBIKEuroAll = new("euroall");

        // Limiter
        public static BasisSettingsBinding<float> LimitThreshold => new("limitthreshold", new BasisPlatformDefault<float>(0.95f)); // pre-clip

        public static BasisSettingsBinding<float> LimitKnee => new("limitknee", new BasisPlatformDefault<float>(0.05f)); // soft knee width

        // Denoise extra params (post gain + wet/dry)
        public static BasisSettingsBinding<float> DenoiseMakeupDb => new("denoisemakeupdb", new BasisPlatformDefault<float>(3f));

        public static BasisSettingsBinding<float> DenoiseWet => new("denoisewet", new BasisPlatformDefault<float>(1f)); // 0..1


        public static BasisSettingsBinding<float> AgcTargetRms => new("agctargetrms", new BasisPlatformDefault<float>(0.1f)); // ~ -24 dBFS

        public static BasisSettingsBinding<float> AgcMaxGainDb => new("agcdbgainmax", new BasisPlatformDefault<float>(8f));

        public static BasisSettingsBinding<float> AgcAttack => new("agcattack", new BasisPlatformDefault<float>(0.10f)); // 0..1

        public static BasisSettingsBinding<float> AgcRelease => new("agcrelease", new BasisPlatformDefault<float>(0.01f)); // 0..1

        public static void LoadAll()
        {
            // Audio
            MainVolume.LoadBindingValue();
            MenuVolume.LoadBindingValue();
            WorldVolume.LoadBindingValue();
            PlayerVolume.LoadBindingValue();
            MicrophoneVolume.LoadBindingValue();
            MicrophoneRange.LoadBindingValue();
            HearingRange.LoadBindingValue();
            MicrophoneDenoiser.LoadBindingValue();
            MicrophoneMode.LoadBindingValue();
            UseAutomaticGain.LoadBindingValue();
            DenoiseMakeupDb.LoadBindingValue();
            DenoiseWet.LoadBindingValue();
            AgcTargetRms.LoadBindingValue();
            AgcMaxGainDb.LoadBindingValue();
            AgcAttack.LoadBindingValue();
            AgcRelease.LoadBindingValue();

            // Input / Movement
            ControllerDeadZone.LoadBindingValue();
            Basexdeadzone.LoadBindingValue();
            Extraxdeadzoneatfully.LoadBindingValue();
            Ydeadzone.LoadBindingValue();
            Wingexponent.LoadBindingValue();
            SnapTurnAngle.LoadBindingValue();
            mousesensitivty.LoadBindingValue();
            InvertMouse.LoadBindingValue();
            usesnapturn.LoadBindingValue();

            // Avatar / IK / Body
            SelectedHeight.LoadBindingValue();
            SelectedScale.LoadBindingValue();
            realworldeyeheight.LoadBindingValue();
            CustomScale.LoadBindingValue();
            AvatarRange.LoadBindingValue();
            SelectedBone.LoadBindingValue();
            IKMode.LoadBindingValue();
            SitStand.LoadBindingValue();

            // Rendering / Graphics
            QualityLevel.LoadBindingValue();
            ShadowQuality.LoadBindingValue();
            HDRSupport.LoadBindingValue();
            Antialiasing.LoadBindingValue();
            DebugVisuals.LoadBindingValue();
            MemoryAllocation.LoadBindingValue();
            VisualState.LoadBindingValue();
            FoveatedRendering.LoadBindingValue();
            FieldOfView.LoadBindingValue();
            RenderResolution.LoadBindingValue();
            VSync.LoadBindingValue();
            VSyncCapFps.LoadBindingValue();

            // LOD / Download limits
            AvatarDownloadSize.LoadBindingValue();
            AvatarMeshLOD.LoadBindingValue();
            GlobalMeshLOD.LoadBindingValue();

            // UI
            MicrophoneIcon.LoadBindingValue();

            // Misc
            FalseBinding.LoadBindingValue();
            TrueBinding.LoadBindingValue();
            LimitThreshold.LoadBindingValue();
            LimitKnee.LoadBindingValue();

            // Global FBIK parameters
            FBIKMinCutoff.LoadBindingValue();
            FBIKBeta.LoadBindingValue();
            FBIKDerivativeCutoff.LoadBindingValue();
            FBIKPositionSmoothingHz.LoadBindingValue();
            FBIKRotationSmoothingHz.LoadBindingValue();

            // Hips
            FBIKHipsSmoothPos.LoadBindingValue();
            FBIKHipsSmoothRot.LoadBindingValue();
            FBIKHipsEuroPos.LoadBindingValue();
            FBIKHipsEuroRot.LoadBindingValue();

            // Head
            FBIKHeadSmoothPos.LoadBindingValue();
            FBIKHeadSmoothRot.LoadBindingValue();
            FBIKHeadEuroPos.LoadBindingValue();
            FBIKHeadEuroRot.LoadBindingValue();

            // Left Foot
            FBIKLeftFootSmoothPos.LoadBindingValue();
            FBIKLeftFootSmoothRot.LoadBindingValue();
            FBIKLeftFootEuroPos.LoadBindingValue();
            FBIKLeftFootEuroRot.LoadBindingValue();

            // Right Foot
            FBIKRightFootSmoothPos.LoadBindingValue();
            FBIKRightFootSmoothRot.LoadBindingValue();
            FBIKRightFootEuroPos.LoadBindingValue();
            FBIKRightFootEuroRot.LoadBindingValue();

            // Chest
            FBIKChestSmoothPos.LoadBindingValue();
            FBIKChestSmoothRot.LoadBindingValue();
            FBIKChestEuroPos.LoadBindingValue();
            FBIKChestEuroRot.LoadBindingValue();

            // Left Lower Leg
            FBIKLeftLowerLegSmoothPos.LoadBindingValue();
            FBIKLeftLowerLegSmoothRot.LoadBindingValue();
            FBIKLeftLowerLegEuroPos.LoadBindingValue();
            FBIKLeftLowerLegEuroRot.LoadBindingValue();

            // Right Lower Leg
            FBIKRightLowerLegSmoothPos.LoadBindingValue();
            FBIKRightLowerLegSmoothRot.LoadBindingValue();
            FBIKRightLowerLegEuroPos.LoadBindingValue();
            FBIKRightLowerLegEuroRot.LoadBindingValue();

            // Left Hand
            FBIKLeftHandSmoothPos.LoadBindingValue();
            FBIKLeftHandSmoothRot.LoadBindingValue();
            FBIKLeftHandEuroPos.LoadBindingValue();
            FBIKLeftHandEuroRot.LoadBindingValue();

            // Right Hand
            FBIKRightHandSmoothPos.LoadBindingValue();
            FBIKRightHandSmoothRot.LoadBindingValue();
            FBIKRightHandEuroPos.LoadBindingValue();
            FBIKRightHandEuroRot.LoadBindingValue();

            // Left Lower Arm
            FBIKLeftLowerArmSmoothPos.LoadBindingValue();
            FBIKLeftLowerArmSmoothRot.LoadBindingValue();
            FBIKLeftLowerArmEuroPos.LoadBindingValue();
            FBIKLeftLowerArmEuroRot.LoadBindingValue();

            // Right Lower Arm
            FBIKRightLowerArmSmoothPos.LoadBindingValue();
            FBIKRightLowerArmSmoothRot.LoadBindingValue();
            FBIKRightLowerArmEuroPos.LoadBindingValue();
            FBIKRightLowerArmEuroRot.LoadBindingValue();

            // Left Toe
            FBIKLeftToeSmoothPos.LoadBindingValue();
            FBIKLeftToeSmoothRot.LoadBindingValue();
            FBIKLeftToeEuroPos.LoadBindingValue();
            FBIKLeftToeEuroRot.LoadBindingValue();

            // Right Toe
            FBIKRightToeSmoothPos.LoadBindingValue();
            FBIKRightToeSmoothRot.LoadBindingValue();
            FBIKRightToeEuroPos.LoadBindingValue();
            FBIKRightToeEuroRot.LoadBindingValue();

            // Shoulders
            FBIKLeftShoulderSmoothPos.LoadBindingValue();
            FBIKLeftShoulderSmoothRot.LoadBindingValue();
            FBIKLeftShoulderEuroPos.LoadBindingValue();
            FBIKLeftShoulderEuroRot.LoadBindingValue();

            FBIKRightShoulderSmoothPos.LoadBindingValue();
            FBIKRightShoulderSmoothRot.LoadBindingValue();
            FBIKRightShoulderEuroPos.LoadBindingValue();
            FBIKRightShoulderEuroRot.LoadBindingValue();

            // Global toggle
            FBIKEuroAll.LoadBindingValue();
        }
    }
}
