using UnityEngine;

namespace HVR.Basis.Comms.HVRUtility
{
    public static class HVRLogging
    {
        [HideInCallstack] public static void ProtocolError(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        [HideInCallstack] public static void ProtocolWarning(string message) => BasisDebug.LogWarning(message, BasisDebug.LogTag.Avatar);
        [HideInCallstack] public static void ProtocolAssetMismatch(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        [HideInCallstack] public static void ProtocolDebug(string message) => BasisDebug.Log(message, BasisDebug.LogTag.Avatar);

        // Added by Vixxy
        [HideInCallstack] public static void ProtocolAccident(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
        [HideInCallstack] public static void StateError(string message) => BasisDebug.LogError(message, BasisDebug.LogTag.Avatar);
    }
}
