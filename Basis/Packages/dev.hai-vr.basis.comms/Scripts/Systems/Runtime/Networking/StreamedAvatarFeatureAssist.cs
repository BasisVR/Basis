using UnityEngine;
namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Assist/Streamed Avatar Feature Assist")]
    public class StreamedAvatarFeatureAssist : MonoBehaviour
    {
        public float deltaTime = 0.1f;
        public StreamedAvatarFeature[] features;
    }
}
