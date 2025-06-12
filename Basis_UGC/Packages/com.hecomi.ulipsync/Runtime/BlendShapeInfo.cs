namespace uLipSync
{
    [System.Serializable]
    public class BlendShapeInfo
    {
        public string phoneme;
        public int index = -1;
        public float weight { get; set; } = 0f;
        public float weightVelocity { get; set; } = 0f;
    }
}
