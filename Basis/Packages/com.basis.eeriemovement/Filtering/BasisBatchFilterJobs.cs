using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Scripts.Drivers
{
    // Per-slot smoothing mode — matches Pos/Rot enable toggles.
    // 0 = passthrough (no smoothing), 1 = exponential fallback, 2 = one-euro filter.
    public enum BasisFilterMode : byte
    {
        Passthrough = 0,
        Fallback = 1,
        Euro = 2,
    }

    /// <summary>Burst-friendly one-euro state for a Vector3 signal.</summary>
    public struct BasisEuroVec3State
    {
        public bool xHasPrev;   // has the low-pass on value been initialized
        public bool dxHasPrev;  // has the low-pass on derivative been initialized
        public float3 hatX;
        public float3 hatDx;
    }

    /// <summary>Burst-friendly log-map quaternion one-euro state (shortest-path, log/exp via axis-angle).</summary>
    public struct BasisEuroQuatState
    {
        public bool hasPrev;
        public quaternion prev;
        public BasisEuroVec3State logVecState;
    }

    /// <summary>
    /// IJobParallelFor: smooth N independent Vector3 bone positions each frame.
    /// Each index selects passthrough, fallback exponential, or one-euro filter via <c>mode</c>.
    /// State arrays are accessed by ref (ArrayElementAsRef) to skip the indexer's full struct copy.
    /// </summary>
    [BurstCompile]
    public struct BasisBatchPositionFilterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> mode;
        [ReadOnly] public NativeArray<float3> rawInputs;
        [ReadOnly] public NativeArray<float4> tuning;

        public NativeArray<BasisEuroVec3State> euroStates;
        public NativeArray<float3> fallbackStates;

        [WriteOnly] public NativeArray<float3> outputs;

        public float dt;

        public unsafe void Execute(int i)
        {
            byte m = UnsafeUtility.ReadArrayElement<byte>(mode.GetUnsafeReadOnlyPtr(), i);
            float3 x = UnsafeUtility.ReadArrayElement<float3>(rawInputs.GetUnsafeReadOnlyPtr(), i);
            void* outPtr = outputs.GetUnsafePtr();

            if (m == (byte)BasisFilterMode.Passthrough)
            {
                UnsafeUtility.WriteArrayElement(outPtr, i, x);
                return;
            }

            float4 t = UnsafeUtility.ReadArrayElement<float4>(tuning.GetUnsafeReadOnlyPtr(), i);

            if (m == (byte)BasisFilterMode.Fallback)
            {
                ref float3 fs = ref UnsafeUtility.ArrayElementAsRef<float3>(fallbackStates.GetUnsafePtr(), i);
                fs = math.lerp(fs, x, t.w);
                UnsafeUtility.WriteArrayElement(outPtr, i, fs);
                return;
            }

            // Euro — operate on array slot by ref, avoiding read/write struct copies.
            ref BasisEuroVec3State st = ref UnsafeUtility.ArrayElementAsRef<BasisEuroVec3State>(euroStates.GetUnsafePtr(), i);
            float3 result = BasisFilterMath.EuroVec3(ref st, x, math.max(dt, 1e-6f), t.x, t.y, t.z);
            UnsafeUtility.WriteArrayElement(outPtr, i, result);
        }
    }

    /// <summary>
    /// IJobParallelFor: smooth N independent Quaternion bone rotations each frame.
    /// Euro path uses a log-map (axis-angle) representation so delta rotations are filtered as float3.
    /// State arrays are accessed by ref (ArrayElementAsRef) to skip the indexer's full struct copy.
    /// </summary>
    [BurstCompile]
    public struct BasisBatchRotationFilterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> mode;
        [ReadOnly] public NativeArray<quaternion> rawInputs;
        [ReadOnly] public NativeArray<float4> tuning;

        public NativeArray<BasisEuroQuatState> euroStates;
        public NativeArray<quaternion> fallbackStates;

        [WriteOnly] public NativeArray<quaternion> outputs;

        public float dt;

        public unsafe void Execute(int i)
        {
            byte m = UnsafeUtility.ReadArrayElement<byte>(mode.GetUnsafeReadOnlyPtr(), i);
            quaternion q = UnsafeUtility.ReadArrayElement<quaternion>(rawInputs.GetUnsafeReadOnlyPtr(), i);
            void* outPtr = outputs.GetUnsafePtr();

            if (m == (byte)BasisFilterMode.Passthrough)
            {
                UnsafeUtility.WriteArrayElement(outPtr, i, q);
                return;
            }

            float4 t = UnsafeUtility.ReadArrayElement<float4>(tuning.GetUnsafeReadOnlyPtr(), i);

            if (m == (byte)BasisFilterMode.Fallback)
            {
                ref quaternion fs = ref UnsafeUtility.ArrayElementAsRef<quaternion>(fallbackStates.GetUnsafePtr(), i);
                fs = BasisFilterMath.SlerpShortest(fs, q, t.w);
                UnsafeUtility.WriteArrayElement(outPtr, i, fs);
                return;
            }

            // Euro — ref the slot; EuroQuat mutates st.logVecState in-place too.
            ref BasisEuroQuatState st = ref UnsafeUtility.ArrayElementAsRef<BasisEuroQuatState>(euroStates.GetUnsafePtr(), i);
            quaternion result = BasisFilterMath.EuroQuat(ref st, q, math.max(dt, 1e-6f), t.x, t.y, t.z);
            UnsafeUtility.WriteArrayElement(outPtr, i, result);
        }
    }

    /// <summary>IJobParallelForTransform that batch-reads each bone's solved world pose into parallel arrays.</summary>
    [BurstCompile]
    public struct BasisReadBoneWorldPoseJob : IJobParallelForTransform
    {
        public NativeArray<float3> Positions;
        public NativeArray<quaternion> Rotations;

        public void Execute(int index, TransformAccess transform)
        {
            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Positions[index] = position;
            Rotations[index] = rotation;
        }
    }

    /// <summary>Shared Burst math helpers for the filter jobs.</summary>
    [BurstCompile]
    public static class BasisFilterMath
    {
        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1.0f / (2.0f * math.PI * cutoff);
            return 1.0f / (1.0f + tau / math.max(dt, 1e-6f));
        }

        public static float3 EuroVec3(ref BasisEuroVec3State st, float3 x, float dt, float minCutoff, float beta, float dCutoff)
        {
            // Mirrors BasisOneEuroFilterVector3 semantics: each LowPassFilter initializes on its first call
            // by storing the current value and returning it; subsequent calls blend.
            float3 prevHatX = st.xHasPrev ? st.hatX : x;
            float3 dx = (prevHatX - x) / dt;

            float ad = Alpha(dCutoff, dt);
            if (st.dxHasPrev) st.hatDx = math.lerp(st.hatDx, dx, ad);
            else { st.hatDx = dx; st.dxHasPrev = true; }

            float cutoff = minCutoff + beta * math.length(st.hatDx);
            float a = Alpha(cutoff, dt);

            if (st.xHasPrev) st.hatX = math.lerp(st.hatX, x, a);
            else { st.hatX = x; st.xHasPrev = true; }

            return st.hatX;
        }

        public static quaternion EuroQuat(ref BasisEuroQuatState st, quaternion q, float dt, float minCutoff, float beta, float dCutoff)
        {
            // First frame: seed state, pass through.
            if (!st.hasPrev)
            {
                st.hasPrev = true;
                st.prev = q;
                return q;
            }

            // Shortest-path: flip q if needed so dot(prev, q) >= 0.
            float4 pv = st.prev.value;
            float4 qv = q.value;
            if (math.dot(pv, qv) < 0f) qv = -qv;
            q = new quaternion(qv);

            // delta = q * inverse(prev) — for unit quats, inverse == conjugate
            quaternion prevInv = math.conjugate(st.prev);
            quaternion delta = math.mul(q, prevInv);

            // Convert delta to axis-angle (the "log" of the quaternion, scaled by 2)
            float4 dv = delta.value;
            float w = math.clamp(dv.w, -1f, 1f);
            float halfAngle = math.acos(w);
            float angle = 2f * halfAngle;
            if (angle > math.PI) angle -= 2f * math.PI;

            float sinHalf = math.sqrt(math.max(0f, 1f - w * w));
            float3 axis = sinHalf > 1e-6f ? dv.xyz / sinHalf : new float3(0f, 0f, 0f);
            float3 logVec = axis * angle;

            // Low-pass the log vector.
            float3 filteredLog = EuroVec3(ref st.logVecState, logVec, dt, minCutoff, beta, dCutoff);

            // exp back to quaternion.
            float mag = math.length(filteredLog);
            quaternion filteredDelta;
            if (mag < 1e-6f)
            {
                filteredDelta = quaternion.identity;
            }
            else
            {
                float3 unit = filteredLog / mag;
                float halfMag = mag * 0.5f;
                float s = math.sin(halfMag);
                filteredDelta = new quaternion(unit.x * s, unit.y * s, unit.z * s, math.cos(halfMag));
            }

            quaternion outQ = math.mul(filteredDelta, st.prev);
            st.prev = outQ;
            return outQ;
        }

        public static quaternion SlerpShortest(quaternion a, quaternion b, float t)
        {
            float4 av = a.value;
            float4 bv = b.value;
            float cosHalf = math.dot(av, bv);
            if (cosHalf < 0f) { bv = -bv; cosHalf = -cosHalf; }

            // Fall back to nlerp when nearly colinear to avoid division by near-zero sin.
            if (cosHalf > 0.9995f)
            {
                float4 r = math.normalize(math.lerp(av, bv, t));
                return new quaternion(r);
            }

            float halfAngle = math.acos(math.min(cosHalf, 1f));
            float sinHalf = math.sin(halfAngle);
            float wa = math.sin((1f - t) * halfAngle) / sinHalf;
            float wb = math.sin(t * halfAngle) / sinHalf;
            return new quaternion(av * wa + bv * wb);
        }
    }
}
