using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeResultSlot
    {
        public readonly Vector3[] PoseWorld = new Vector3[MediaPipeSpace.PoseCount], Pose = new Vector3[MediaPipeSpace.PoseCount];
        public readonly float[] PoseVisibility = new float[MediaPipeSpace.PoseCount], Blendshapes = new float[(int)MediaPipeArkitBlendshape.Count];
        public readonly Vector3[][] HandImage = { new Vector3[MediaPipeSpace.HandCount], new Vector3[MediaPipeSpace.HandCount] };
        public readonly Vector3[][] HandWorld = { new Vector3[MediaPipeSpace.HandCount], new Vector3[MediaPipeSpace.HandCount] };
        public readonly bool[] HandHasWorld = new bool[2];
        public BasisMediaPipeResult Result;
    }
    public sealed class MediaPipeResultSlots
    {
        public const int Count = 3;
        private readonly MediaPipeResultSlot[] slots = new MediaPipeResultSlot[Count];
        private readonly object gate = new object();
        private int published = -1, consumed = -1, writing = -1;
        private bool hasPublished;
        public int Published => published;
        public int Consumed => consumed;
        public int Writing => writing;
        public MediaPipeResultSlots()
        {
            for (int i = 0; i < Count; i++) slots[i] = new MediaPipeResultSlot();
        }
        public MediaPipeResultSlot BeginWrite()
        {
            lock (gate)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (i == published || i == consumed) continue;
                    writing = i;
                    return slots[i];
                }
                writing = 0;
                return slots[0];
            }
        }
        public void Publish()
        {
            lock (gate)
            {
                if (writing < 0) return;
                published = writing;
                writing = -1;
                hasPublished = true;
            }
        }
        public bool TryTake(out BasisMediaPipeResult result)
        {
            lock (gate)
            {
                if (!hasPublished)
                {
                    result = default;
                    return false;
                }
                consumed = published;
                hasPublished = false;
                result = slots[consumed].Result;
                return true;
            }
        }
        public void Clear()
        {
            lock (gate)
            {
                hasPublished = false;
            }
        }
    }
}
