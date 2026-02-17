using Basis.Network.Core.Compression;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Compression
{
    public static class BasisUnityBitPackerExtensionsUnsafe
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EnsureSpace(byte[] bytes, int offset, int size)
        {
            return (uint)offset <= (uint)bytes.Length && offset + size <= bytes.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(float v) => math.isfinite(v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite3(float a, float b, float c) =>
            IsFinite(a) & IsFinite(b) & IsFinite(c);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite4(float a, float b, float c, float d) =>
            IsFinite(a) & IsFinite(b) & IsFinite(c) & IsFinite(d);

        /// <summary>
        /// Optional stricter validation: quaternions should be close-ish to unit length.
        /// Networking compression often expects normalized rotations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsReasonableQuaternion(float x, float y, float z, float w, float tolerance)
        {
            // length^2 should be near 1
            float lenSq = x * x + y * y + z * z + w * w;
            // Reject zeros / nonsense
            if (!(lenSq > 0f) || !IsFinite(lenSq)) return false;

            // Accept within tolerance (e.g. 0.01..0.05 depending on how noisy your source can be)
            return math.abs(lenSq - 1f) <= tolerance;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUShort(ref byte[] bytes, ref int offset, out ushort value)
        {
            value = default;
            if (!EnsureSpace(bytes, offset, 2)) return false;

            value = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;
            return true;
        }


        public unsafe static bool TryReadQuaternionFromBytes(
            ref byte[] bytes,
            ref int offset,
            out quaternion q,
            float unitLengthTolerance = 0.02f,
            bool requireUnitLength = true)
        {
            q = default;
            if (!EnsureSpace(bytes, offset, 16)) return false;

            float x, y, z, w;
            fixed (byte* ptr = &bytes[offset])
            {
                float* f = (float*)ptr;
                x = f[0];
                y = f[1];
                z = f[2];
                w = f[3];
            }

            // Validate without repairing
            if (!IsFinite4(x, y, z, w))
            {
                return false;
            }

            if (requireUnitLength && !IsReasonableQuaternion(x, y, z, w, unitLengthTolerance))
            {
                return false;
            }

            offset += 16;
            q = new quaternion(x, y, z, w);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadPosition(ref byte[] buffer, ref int offset, out Unity.Mathematics.float3 position)
        {
            position = default;
            if (!EnsureSpace(buffer, offset, 12)) return false;

            float x, y, z;
            unsafe
            {
                fixed (byte* src = &buffer[offset])
                {
                    float* fSrc = (float*)src;
                    x = fSrc[0];
                    y = fSrc[1];
                    z = fSrc[2];
                }
            }

            if (!IsFinite3(x, y, z)) return false;

            offset += 12;
            position = new Unity.Mathematics.float3(x, y, z);
            return true;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Sanitize(float v, float fallback)
        {
            // math.isfinite catches both NaN and ±Infinity.
            return math.isfinite(v) ? v : fallback;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUShort(ushort value, ref byte[] bytes, ref int offset)
        {
            EnsureSpace(bytes, offset, 2); bytes[offset++] = (byte)value; bytes[offset++] = (byte)(value >> 8);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUShort(ref byte[] bytes, ref int offset)
        {
            EnsureSpace(bytes, offset, 2);
            ushort result = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;
            return result;
        }
        public unsafe static void WriteQuaternionToBytes(quaternion q, ref byte[] bytes, ref int offset)
        {
            EnsureSpace(bytes, offset, 16); fixed (byte* ptr = &bytes[offset])
            {
                float* f = (float*)ptr; f[0] = Sanitize(q.value.x, 0f);
                f[1] = Sanitize(q.value.y, 0f);
                f[2] = Sanitize(q.value.z, 0f);
                f[3] = Sanitize(q.value.w, 1f);
            }
            offset += 16;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WritePosition(UnityEngine.Vector3 position, ref byte[] buffer, ref int offset)
        {
            EnsureSpace(buffer, offset, 12);
            unsafe
            {
                fixed (byte* dst = &buffer[offset])
                {
                    float* fDst = (float*)dst;
                    fDst[0] = Sanitize(position.x, 0f);
                    fDst[1] = Sanitize(position.y, 0f);
                    fDst[2] = Sanitize(position.z, 0f);
                }
            }
            offset += 12;
        }
        public static void CompressScale(float scale, ref LocalAvatarSyncMessage message, ref int offset)
        {

            float clamped = math.clamp(scale, BasisAvatarBitPacking.MinScale, BasisAvatarBitPacking.MaxScale);
            float normalized = (clamped - BasisAvatarBitPacking.MinScale) / BasisAvatarBitPacking.ComputedRange;

            ushort compressed = (ushort)(normalized * BasisAvatarBitPacking.UShortRangeDifference);
            WriteUShort(compressed, ref message.array, ref offset);
        }
        /// <summary>
        /// cant generate a nan unless min,max or floatrangedifference go bad (const cant)
        /// </summary>
        /// <param name="value"></param>
        /// <param name="minValue"></param>
        /// <param name="maxValue"></param>
        /// <returns></returns>
        public static float DecompressScale(ushort value)
        {
            float normalized = (float)value / BasisAvatarBitPacking.UShortRangeDifference;
            return normalized * (BasisAvatarBitPacking.MaxScale - BasisAvatarBitPacking.MinScale) + BasisAvatarBitPacking.MinScale;
        }
        private const float InvSqrt2 = 0.7071067811865475f; // 1/sqrt(2)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static quaternion NormalizeSafe(quaternion q)
        {
            float x = q.value.x, y = q.value.y, z = q.value.z, w = q.value.w;
            float lenSq = x * x + y * y + z * z + w * w;
            if (!(lenSq > 1e-12f) || !math.isfinite(lenSq))
                return new quaternion(0f, 0f, 0f, 1f);

            float inv = math.rsqrt(lenSq);
            return new quaternion(x * inv, y * inv, z * inv, w * inv);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetComp(in quaternion q, int i)
        {
            switch (i)
            {
                case 0: return q.value.x;
                case 1: return q.value.y;
                case 2: return q.value.z;
                default: return q.value.w;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetComp(ref quaternion q, int i, float v)
        {
            float4 f = q.value;
            switch (i)
            {
                case 0: f.x = v; break;
                case 1: f.y = v; break;
                case 2: f.z = v; break;
                default: f.w = v; break;
            }
            q.value = f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort QuantizeSmall(float v)
        {
            v = math.clamp(v, -InvSqrt2, InvSqrt2);
            // map [-InvSqrt2, +InvSqrt2] -> [0, 65535]
            float t = (v + InvSqrt2) / (2f * InvSqrt2); // [0..1]
            int qi = (int)math.round(t * 65535f);
            qi = math.clamp(qi, 0, 65535);
            return (ushort)qi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DequantizeSmall(ushort q)
        {
            float t = q / 65535f;                 // [0..1]
            return t * (2f * InvSqrt2) - InvSqrt2; // [-InvSqrt2..InvSqrt2]
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetSmallestThree(in quaternion q, int largest, out float a, out float b, out float c)
        {
            a = b = c = 0f;
            int k = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                float v = GetComp(q, i);
                if (k == 0) a = v;
                else if (k == 1) b = v;
                else c = v;
                k++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static quaternion BuildFromSmallestThree(int largest, float a, float b, float c, float missing)
        {
            quaternion q = new quaternion(0f, 0f, 0f, 0f);
            int k = 0;
            for (int i = 0; i < 4; i++)
            {
                float v;
                if (i == largest) v = missing;
                else
                {
                    v = (k == 0) ? a : (k == 1) ? b : c;
                    k++;
                }
                SetComp(ref q, i, v);
            }
            return q;
        }
        public static void WriteCompressedQuaternionToBytes(quaternion q, ref byte[] bytes, ref int offset)
        {
            EnsureSpace(bytes, offset, 7);

            q = NormalizeSafe(q);

            float ax = math.abs(q.value.x);
            float ay = math.abs(q.value.y);
            float az = math.abs(q.value.z);
            float aw = math.abs(q.value.w);

            int largest = 0;
            float max = ax;
            if (ay > max) { largest = 1; max = ay; }
            if (az > max) { largest = 2; max = az; }
            if (aw > max) { largest = 3; max = aw; }

            if (GetComp(q, largest) < 0f)
                q = new quaternion(-q.value.x, -q.value.y, -q.value.z, -q.value.w);

            GetSmallestThree(q, largest, out float a, out float b, out float c);

            ushort qa = QuantizeSmall(a);
            ushort qb = QuantizeSmall(b);
            ushort qc = QuantizeSmall(c);

            bytes[offset++] = (byte)largest;
            WriteUShort(qa, ref bytes, ref offset);
            WriteUShort(qb, ref bytes, ref offset);
            WriteUShort(qc, ref bytes, ref offset);
        }
        /// <summary>
        /// Safe-ish read: returns false if out of bounds or components invalid.
        /// </summary>
        public static bool TryReadCompressedQuaternionFromBytes(
            ref byte[] bytes,
            ref int offset,
            out quaternion q,
            float unitLengthTolerance = 0.02f,
            bool requireUnitLength = true)
        {
            q = default;
            if (!EnsureSpace(bytes, offset, 7)) return false;

            int largest = bytes[offset++];
            if ((uint)largest > 3u) return false;

            ushort qa = ReadUShort(ref bytes, ref offset);
            ushort qb = ReadUShort(ref bytes, ref offset);
            ushort qc = ReadUShort(ref bytes, ref offset);

            float a = DequantizeSmall(qa);
            float b = DequantizeSmall(qb);
            float c = DequantizeSmall(qc);

            if (!math.isfinite(a) | !math.isfinite(b) | !math.isfinite(c))
                return false;

            float sum = a * a + b * b + c * c;
            float missing = math.sqrt(math.max(0f, 1f - sum)); // >= 0, because largest forced positive

            q = BuildFromSmallestThree(largest, a, b, c, missing);
            q = NormalizeSafe(q);

            if (requireUnitLength)
            {
                float4 v = q.value;
                float lenSq = v.x * v.x + v.y * v.y + v.z * v.z + v.w * v.w;
                if (!(lenSq > 0f) || !math.isfinite(lenSq)) return false;
                if (math.abs(lenSq - 1f) > unitLengthTolerance) return false;
            }

            return true;
        }
    }
}
