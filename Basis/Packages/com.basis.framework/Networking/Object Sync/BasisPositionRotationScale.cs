using UnityEngine;
using System.Runtime.InteropServices;

[System.Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BasisPositionRotationScale
{
    // Position (quantized)
    public ushort x;
    public ushort y;
    public ushort z;

    // Rotation (already packed elsewhere as uint)
    public uint Rotation;

    // Scale (quantized)
    public ushort Scalex;
    public ushort Scaley;
    public ushort Scalez;

    // Position quantization
    public const float Precision = 0.05f;
    public const float Min = -1000f;
    public const float Max = 1000f;
    public const int MaxQuantizedValue = (int)((Max - Min) / Precision);

    // Scale quantization
    public const float ScalePrecision = 0.01f;
    public const float ScaleMin = -100f;
    public const float ScaleMax = 100f;
    public const int ScaleMaxQuantizedValue = (int)((ScaleMax - ScaleMin) / ScalePrecision);

    // Struct size in bytes (2*3 + 4 + 2*3)
    public const int Size = 16;

    // --- Position-only (kept for backwards compatibility)
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

    // --- Position + Scale
    public void Compress(Vector3 position, uint rotationPacked, Vector3 scale)
    {
        x = Quantize(position.x);
        y = Quantize(position.y);
        z = Quantize(position.z);
        Rotation = rotationPacked;

        Scalex = QuantizeScale(scale.x);
        Scaley = QuantizeScale(scale.y);
        Scalez = QuantizeScale(scale.z);
    }

    public Vector3 DecompressScale()
    {
        return new Vector3(
            DequantizeScale(Scalex),
            DequantizeScale(Scaley),
            DequantizeScale(Scalez)
        );
    }

    // --- Helpers: position
    private static ushort Quantize(float value)
    {
        int q = Mathf.Clamp(Mathf.RoundToInt((value - Min) / Precision), 0, MaxQuantizedValue);
        return (ushort)q;
    }

    private static float Dequantize(ushort value)
    {
        return Min + value * Precision;
    }

    // --- Helpers: scale
    private static ushort QuantizeScale(float value)
    {
        int q = Mathf.Clamp(Mathf.RoundToInt((value - ScaleMin) / ScalePrecision), 0, ScaleMaxQuantizedValue);
        return (ushort)q;
    }

    private static float DequantizeScale(ushort value)
    {
        return ScaleMin + value * ScalePrecision;
    }

    // --- Unsafe copy to byte[]
    public void ToBytes(byte[] buffer, int offset = 0)
    {
        if (buffer == null || buffer.Length < offset + Size)
        {
            if (buffer != null)
            {
                throw new System.ArgumentException($"Buffer too small. {buffer.Length < offset + Size}");
            }
            else
            {
                throw new System.ArgumentException($"Buffer was null");
            }
        }
        unsafe
        {
            fixed (byte* dst = &buffer[offset])
            {
                *(BasisPositionRotationScale*)dst = this;
            }
        }
    }

    // --- Unsafe copy from byte[]
    public static BasisPositionRotationScale FromBytes(byte[] buffer, int offset = 0)
    {
        if (buffer == null || buffer.Length < offset + Size)
        {
            if (buffer != null)
            {
                throw new System.ArgumentException($"Buffer too small. {buffer.Length < offset + Size}");
            }
            else
            {
                throw new System.ArgumentException($"Buffer was null");
            }
        }
        unsafe
        {
            fixed (byte* src = &buffer[offset])
            {
                return *(BasisPositionRotationScale*)src;
            }
        }
    }
}
