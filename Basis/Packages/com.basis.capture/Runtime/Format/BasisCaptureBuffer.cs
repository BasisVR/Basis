using System;
using System.Buffers.Binary;
using UnityEngine;
namespace Basis.Capture
{
    public sealed class BasisCaptureBuffer
    {
        public byte[] Data;
        public int Length;
        public BasisCaptureBuffer(int capacity = 1 << 16) { Data = new byte[capacity]; }
        public void Reset(byte[] data) { Data = data; Length = 0; }
        public void Ensure(int extra)
        {
            int need = Length + extra;
            if (need <= Data.Length) return;
            int size = Data.Length * 2;
            while (size < need) size *= 2;
            Array.Resize(ref Data, size);
        }
        public void Byte(byte v) { Ensure(1); Data[Length++] = v; }
        public void Int(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(Length), v); Length += 4; }
        public void Long(long v) { Ensure(8); BinaryPrimitives.WriteInt64LittleEndian(Data.AsSpan(Length), v); Length += 8; }
        public void Float(float v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(Length), BitConverter.SingleToInt32Bits(v)); Length += 4; }
        public void Double(double v) { Ensure(8); BinaryPrimitives.WriteInt64LittleEndian(Data.AsSpan(Length), BitConverter.DoubleToInt64Bits(v)); Length += 8; }
        public void Matrix(Matrix4x4 m) { for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) Float(m[r, c]); }
        public int BeginRecord(byte type, int id)
        {
            Byte(type);
            Int(id);
            Int(0);
            return Length;
        }
        public void EndRecord(int start) => BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(start - 4), Length - start);
        public int Reserve(int bytes)
        {
            Ensure(bytes);
            int at = Length;
            Array.Clear(Data, at, bytes);
            Length += bytes;
            return at;
        }
    }
}
