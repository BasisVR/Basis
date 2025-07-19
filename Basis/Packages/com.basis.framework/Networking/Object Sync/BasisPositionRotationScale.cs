using UnityEngine;
using System.Runtime.InteropServices;

[System.Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BasisPositionRotationScale
{
    public ushort x;
    public ushort y;
    public ushort z;
    public uint Rotation;

    public const float Precision = 0.05f;
    public const float Min = -1000f;
    public const float Max = 1000f;
    public const int MaxQuantizedValue = (int)((Max - Min) / Precision);
    public const int Size = sizeof(ushort) * 3 + sizeof(uint); // 2 + 2 + 2 + 4 = 10 bytes

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

    // Unsafe copy to byte[]
    public void ToBytes(byte[] buffer, int offset = 0)
    {
        if (buffer == null || buffer.Length < offset + Size)
            throw new System.ArgumentException("Buffer too small.");

        unsafe
        {
            fixed (byte* dst = &buffer[offset])
            {
                *(BasisPositionRotationScale*)dst = this;
            }
        }
    }

    // Unsafe copy from byte[]
    public static BasisPositionRotationScale FromBytes(byte[] buffer, int offset = 0)
    {
        if (buffer == null || buffer.Length < offset + Size)
            throw new System.ArgumentException("Buffer too small.");

        unsafe
        {
            fixed (byte* src = &buffer[offset])
            {
                return *(BasisPositionRotationScale*)src;
            }
        }
    }
}
