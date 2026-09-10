using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeNeutralSampler
    {
        public const int Required = 20;
        public const float StillDegrees = 2f, StillTranslation = 1.5f;
        public Quaternion Rotation = Quaternion.identity;
        public Vector3 Position;
        public int Count { get; private set; }
        private Quaternion lastRotation = Quaternion.identity;
        private Vector3 lastPosition, positionSum;
        private Vector4 rotationSum;
        public bool Add(Quaternion rotation, Vector3 position)
        {
            if (Count > 0 && (Quaternion.Angle(lastRotation, rotation) > StillDegrees || Vector3.Distance(lastPosition, position) > StillTranslation)) Reset();
            lastRotation = rotation;
            lastPosition = position;
            Vector4 q = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
            if (Count > 0 && Vector4.Dot(q, rotationSum) < 0f) q = -q;
            rotationSum += q;
            positionSum += position;
            Count++;
            if (Count < Required) return false;
            Vector4 mean = rotationSum.normalized;
            Rotation = new Quaternion(mean.x, mean.y, mean.z, mean.w);
            Position = positionSum / Count;
            Reset();
            return true;
        }
        public void Reset()
        {
            Count = 0;
            rotationSum = Vector4.zero;
            positionSum = Vector3.zero;
        }
    }
}
