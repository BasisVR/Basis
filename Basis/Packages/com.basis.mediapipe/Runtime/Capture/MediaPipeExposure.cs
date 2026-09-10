using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeExposure
    {
        public const float MaxGain = 4f, MinGamma = 0.55f, TargetHigh = 235f, TargetMean = 100f, BoostRamp = 40f, Rate = 0.15f, DarkLevel = 0.05f, BrightLevel = 0.3f;
        public readonly byte[] Lut = new byte[256];
        public float Gain = 1f, Gamma = 1f, Level = -1f, High = -1f;
        public bool Active;
        private readonly int[] histogram = new int[256];
        private long sum;
        private int count;
        private bool primed;
        public MediaPipeExposure()
        {
            BuildLut(Lut, 1f, 1f);
        }
        public void Begin()
        {
            System.Array.Clear(histogram, 0, histogram.Length);
            sum = 0;
            count = 0;
        }
        public void Add(byte r, byte g, byte b)
        {
            int luma = (r * 77 + g * 150 + b * 29) >> 8;
            histogram[luma]++;
            sum += luma;
            count++;
        }
        public bool End()
        {
            if (count == 0)
            {
                Level = -1f;
                High = -1f;
                return false;
            }
            Level = sum / (255f * count);
            int need = Mathf.Max(1, count / 50), seen = 0, bin = 255;
            while (bin > 0 && seen + histogram[bin] < need)
            {
                seen += histogram[bin];
                bin--;
            }
            High = bin / 255f;
            return true;
        }
        public void Update(bool boost)
        {
            float targetGain = 1f, targetGamma = 1f;
            if (boost && Level >= 0f)
            {
                float mean = Level * 255f, need = Mathf.Clamp01((TargetMean - mean) / BoostRamp);
                if (need > 0f)
                {
                    targetGain = Mathf.Lerp(1f, Mathf.Clamp(TargetHigh / Mathf.Max(High * 255f, 8f), 1f, MaxGain), need);
                    float lifted = Mathf.Max(mean * targetGain, 1f);
                    if (lifted < TargetMean) targetGamma = Mathf.Lerp(1f, Mathf.Clamp(Mathf.Log(TargetMean / 255f) / Mathf.Log(lifted / 255f), MinGamma, 1f), need);
                }
            }
            float rate = primed ? Rate : 1f;
            primed = true;
            Gain = Mathf.Lerp(Gain, targetGain, rate);
            Gamma = Mathf.Lerp(Gamma, targetGamma, rate);
            bool active = Gain > 1.02f || Gamma < 0.98f;
            if (active || Active) BuildLut(Lut, active ? Gain : 1f, active ? Gamma : 1f);
            Active = active;
        }
        public float Boost => Level > 0f ? Lut[Mathf.Clamp(Mathf.RoundToInt(Level * 255f), 1, 255)] / (Level * 255f) : 1f;
        public static void BuildLut(byte[] lut, float gain, float gamma)
        {
            for (int i = 0; i < 256; i++) lut[i] = (byte)Mathf.RoundToInt(255f * Mathf.Pow(Mathf.Min(1f, i * gain / 255f), gamma));
        }
        public static float Quality(float level) => level < 0f ? 1f : Mathf.Clamp01((level - DarkLevel) / (BrightLevel - DarkLevel));
    }
}
