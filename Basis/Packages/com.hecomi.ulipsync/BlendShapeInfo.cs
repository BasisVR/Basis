namespace uLipSync
{
    [System.Serializable]
    public class BlendShapeInfo
    {
        public string phoneme;
        public int index = -1;

        // Cached at setup time:
        public int phonemeIndex = -1;

        public float weight;
        public float weightVelocity;
    }
}
