using HVR.Vixxy;
using UnityEditor;

namespace HVR.Basis.Comms.Editor
{
    [InitializeOnLoad]
    public class HVRBasisInit
    {
        static HVRBasisInit()
        {
            EditorApplication.delayCall += Next;
        }

        private static void Next()
        {
            var allOurTypes = new[]
            {
                typeof(HVRAvatarComms),
                typeof(HVRNetworkingCarrier),
                typeof(AutomaticFaceTracking),
                typeof(HVRVixxyControl),
                typeof(HVRVixxyMenuItem),
                typeof(HVRVixxyAggregator),
                // Not created by user
                typeof(HVRVariableNetworking),
                typeof(HVRVixxyOrchestrator),
                typeof(StreamedAvatarFeature),
                typeof(OSCAcquisition),
                typeof(FaceTrackingActivityRelay),
                typeof(EyeTrackingBoneActuation),
                typeof(BlendshapeActuation),
            };
            foreach (var type in allOurTypes) GizmoUtility.SetIconEnabled(type, false);
        }
    }
}
