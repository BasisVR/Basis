using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public class BasisAvatarBuffer
    {
        public Unity.Mathematics.quaternion rotation;
        public Unity.Mathematics.float3 Scale;
        public Unity.Mathematics.float3 Position;
        public float[] Muscles = new float[LocalAvatarSyncMessage.StoredBones];
        public double SecondsInterval;
    }
}
