using UnityEngine;
using Vector3 = UnityEngine.Vector3;
[System.Serializable]
public struct BasisPositionRotationScale
{
    public ushort x;
    public ushort y;
    public ushort z;
    public uint Rotation;

    // Parameters
    public const float Precision = 0.05f;
    public const float Min = -1000f;
    public const float Max = 1000f;
    public const int MaxQuantizedValue = (int)((Max - Min) / Precision);

    public void Compress(Vector3 input, uint value)
    {
        x = Quantize(input.x);
        y = Quantize(input.y);
        z = Quantize(input.z);
        Rotation = value;
    }

    public Vector3 DeCompress()
    {
        return new Vector3(
            Dequantize(x),
            Dequantize(y),
            Dequantize(z)
        );
    }
    private static ushort Quantize(float value)
    {
        int quantized = Mathf.Clamp(Mathf.RoundToInt((value - Min) / Precision), 0, MaxQuantizedValue);
        return (ushort)quantized;
    }

    private static float Dequantize(ushort value)
    {
        return Min + value * Precision;
    }
}
