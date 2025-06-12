using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.Networking.Compression
{

    [System.Serializable]
    public class CompressionArraysRangedUshort
    {
        public NativeArray<float> Floats;
        public NativeArray<ushort> Ushorts;
        public float Precision;
        public float InversePrecision;
        public float MinValue;
        public float MaxValue;
        public int RequiredBits;
        public ushort Mask;
        public DecompressJob Decompression;
        public CompressJob Compression;
        public JobHandle handle;
        public int InnerBatchCount = 64;

        public byte[] byteArray;
        public ushort[] ushortArray;
        public int ByteCount;
        public float[] FloatArray;
        public CompressionArraysRangedUshort(int length, float minValue, float maxValue, float precision, bool isDecompression)
        {
            FloatArray = new float[length];
            ushortArray = new ushort[length];
            Floats = new NativeArray<float>(FloatArray, Allocator.Persistent);
            Ushorts = new NativeArray<ushort>(ushortArray, Allocator.Persistent);
            MinValue = minValue;
            MaxValue = maxValue;
            Precision = precision;
            InversePrecision = 1.0f / precision;
            RequiredBits = CalculateRequiredBits();
            Mask = (ushort)((1 << RequiredBits) - 1);
            ByteCount = length * 2;
            byteArray = new byte[ByteCount];
            if (isDecompression)
            {
                Decompression = new DecompressJob
                {
                    InputValues = Ushorts,
                    OutputValues = Floats,
                    MinValue = MinValue,
                    Precision = Precision
                };
            }
            else
            {
                Compression = new CompressJob
                {
                    InputValues = Floats,
                    OutputValues = Ushorts,
                    MinValue = MinValue,
                    InversePrecision = InversePrecision,
                    Mask = Mask
                };
            }
        }

        private int CalculateRequiredBits()
        {
            float range = MaxValue - MinValue;
            float maxValueInRange = range * InversePrecision;
            return FastLog2((uint)(maxValueInRange + 0.5f)) + 1;
        }
        public static int FastLog2(uint value)
        {
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return deBruijnLookup[(value * 0x07C4ACDDU) >> 27];
        }

        private static readonly int[] deBruijnLookup = new int[32]
        {
            0, 9, 1, 10, 13, 21, 2, 29, 11, 14, 16, 18, 22, 25, 3, 30,
            8, 12, 20, 28, 15, 17, 24, 7, 19, 27, 23, 6, 26, 5, 4, 31
        };
        public void CompressArray(float[] values, int length, ref ushort[] Output)
        {
            Compression.InputValues.CopyFrom(values);
            handle = Compression.Schedule(length, InnerBatchCount);
            handle.Complete();

            Compression.OutputValues.CopyTo(Output);
        }

        public void DecompressArray(ushort[] compressedValues, int length, ref float[] Output)
        {
            Decompression.InputValues.CopyFrom(compressedValues);
            handle = Decompression.Schedule(length, InnerBatchCount);
            handle.Complete();

            Decompression.OutputValues.CopyTo(Output);
        }

        public void Dispose()
        {
            if (Floats.IsCreated) Floats.Dispose();
            if (Ushorts.IsCreated) Ushorts.Dispose();
        }

        [BurstCompile]
        public struct CompressJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> InputValues;
            [WriteOnly] public NativeArray<ushort> OutputValues;
            public float MinValue;
            public float InversePrecision;
            public ushort Mask;

            public void Execute(int index)
            {
                float normalizedValue = (InputValues[index] - MinValue) * InversePrecision;
                OutputValues[index] = (ushort)(math.min((int)normalizedValue, Mask));
            }
        }

        [BurstCompile]
        public struct DecompressJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ushort> InputValues;
            [WriteOnly] public NativeArray<float> OutputValues;
            public float MinValue;
            public float Precision;

            public void Execute(int index)
            {
                OutputValues[index] = InputValues[index] * Precision + MinValue;
            }
        }
    }
}