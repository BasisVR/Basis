using System;

namespace OpenLipSync.Inference.OVRCompat
{
    [Serializable]
    public sealed class Frame
    {
        public int frameNumber;
        public int frameDelay;
        public float[] Visemes = new float[VisemeCount];
        public float laughterScore;

        public void Reset()
        {
            frameNumber = 0;
            frameDelay = 0;
            Array.Clear(Visemes, 0, Visemes.Length);
            laughterScore = 0f;
        }

        public const int VisemeCount = 15;
    }
}
