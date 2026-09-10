namespace Basis.MediaPipe
{
    /// <summary>Runtime configuration for the webcam tracker. Bound to Basis settings in M4.</summary>
    public struct BasisMediaPipeConfig
    {
        public const string PoseModelLite = "lite", PoseModelFull = "full", PoseModelHeavy = "heavy";
        public bool EnableFace;
        public bool EnableHands;
        public bool EnablePose;
        public bool EnableChest;
        public bool EnableHeadPosition;
        public bool EnableHeadRotation;
        public bool EnableHandTracking;
        public bool EnableArmElbowPole;
        public bool SwapHands;
        public bool MirrorHorizontally;
        public bool LowLightBoost;
        public bool CameraFpsAuto;
        public string PoseModel;
        public int TargetFps;
        public int CameraWidth;
        public int CameraHeight;

        public static string NormalizePoseModel(string model) => model == PoseModelFull || model == PoseModelHeavy ? model : PoseModelLite;

        public static BasisMediaPipeConfig Default => new BasisMediaPipeConfig
        {
            EnableFace = true,
            EnableHands = true,
            EnablePose = false,
            EnableChest = false,
            EnableHeadPosition = true,
            EnableHeadRotation = true,
            EnableHandTracking = false,
            EnableArmElbowPole = false,
            SwapHands = false,
            MirrorHorizontally = true,
            LowLightBoost = true,
            CameraFpsAuto = true,
            PoseModel = PoseModelLite,
            TargetFps = 30,
            CameraWidth = 640,
            CameraHeight = 480,
        };
    }
}
