namespace HVR.Basis.Comms.HVRUtility
{
    public static class HVRLogging
    {
        public static void ProtocolError(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        public static void ProtocolWarning(string message) => BasisDebug.LogWarning(message, BasisDebug.LogTag.Avatar);
        public static void ProtocolAssetMismatch(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        public static void ProtocolDebug(string message) => BasisDebug.Log(message, BasisDebug.LogTag.Avatar);

        // Added by Vixxy
        public static void ProtocolAccident(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        public static void StateError(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
    }
}
