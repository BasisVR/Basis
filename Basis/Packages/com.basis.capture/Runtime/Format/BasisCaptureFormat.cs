namespace Basis.Capture
{
    public static class BasisCaptureFormat
    {
        public const int Version = 1;
        public const uint FramesMagic = 0x50414342;
        public const byte RecordActor = 1, RecordSceneTransforms = 2, RecordLights = 3, RecordCamera = 4;
        public const byte ActorVisible = 1;
        public const int BoneFloats = 10, MatrixFloats = 16;
        public const string ManifestFile = "capture.json", FramesFile = "frames.bin", MaterialsFile = "materials.json", AssetsFolder = "assets", AudioFolder = "audio", PeersFolder = "peers";
        public const string MeshMagic = "BCMESH01";
        public static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.ToString();
        }
    }
}
