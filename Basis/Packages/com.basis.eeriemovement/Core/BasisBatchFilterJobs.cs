using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
namespace Basis.Scripts.Drivers
{
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
        public float4x4 playspaceToWorld;
        // Indexers, not UnsafeUtility: six arrays are indexed by the same i and nothing here checks
        // they are the same length, so a caller scheduling on one array's count while another is
        // short read/wrote off the end of a heap block with no diagnostic in any build. Burst strips
        // the container bounds check when ENABLE_UNITY_COLLECTIONS_CHECKS is off, so a player build
        // emits the same loads and stores it did before and the editor now catches the mismatch.
        public void Execute(int i)
        {
            byte m = mode[i];
            float3 x = rawInputs[i];

            if (m == (byte)BasisFilterMode.Passthrough)
            {
                outputs[i] = math.transform(playspaceToWorld, x);
                return;
            }

            float4 t = tuning[i];

            if (m == (byte)BasisFilterMode.Fallback)
            {
                float3 fs = math.lerp(fallbackStates[i], x, t.w);
                fallbackStates[i] = fs;
                outputs[i] = math.transform(playspaceToWorld, fs);
                return;
            }

            BasisEuroVec3State st = euroStates[i];
            float3 result = BasisFilterMath.EuroVec3(ref st, x, math.max(dt, 1e-6f), t.x, t.y, t.z);
            euroStates[i] = st;
            outputs[i] = math.transform(playspaceToWorld, result);
        }
    }
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
        public quaternion playspaceRotation;
        // See BasisBatchPositionFilterJob.Execute for why these are indexers and not UnsafeUtility.
        public void Execute(int i)
        {
            byte m = mode[i];
            quaternion q = rawInputs[i];

            if (m == (byte)BasisFilterMode.Passthrough)
            {
                outputs[i] = math.mul(playspaceRotation, q);
                return;
            }

            float4 t = tuning[i];

            if (m == (byte)BasisFilterMode.Fallback)
            {
                quaternion fs = BasisFilterMath.SlerpShortest(fallbackStates[i], q, t.w);
                fallbackStates[i] = fs;
                outputs[i] = math.mul(playspaceRotation, fs);
                return;
            }

            BasisEuroQuatState st = euroStates[i];
            quaternion result = BasisFilterMath.EuroQuat(ref st, q, math.max(dt, 1e-6f), t.x, t.y, t.z);
            euroStates[i] = st;
            outputs[i] = math.mul(playspaceRotation, result);
        }
    }
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
            float3 prevHatX = st.xHasPrev ? st.hatX : x, dx = (prevHatX - x) / dt;
            float ad = Alpha(dCutoff, dt);
            if (st.dxHasPrev) st.hatDx = math.lerp(st.hatDx, dx, ad);
            else { st.hatDx = dx; st.dxHasPrev = true; }

            float cutoff = minCutoff + beta * math.length(st.hatDx), a = Alpha(cutoff, dt);

            if (st.xHasPrev) st.hatX = math.lerp(st.hatX, x, a);
            else { st.hatX = x; st.xHasPrev = true; }

            return st.hatX;
        }
        public static quaternion EuroQuat(ref BasisEuroQuatState st, quaternion q, float dt, float minCutoff, float beta, float dCutoff)
        {
            if (!st.hasPrev)
            {
                st.hasPrev = true;
                st.prev = q;
                return q;
            }

            float4 pv = st.prev.value, qv = q.value;
            if (math.dot(pv, qv) < 0f) qv = -qv;
            q = new quaternion(qv);

            quaternion prevInv = math.conjugate(st.prev), delta = math.mul(q, prevInv);
            float4 dv = delta.value;
            float w = math.clamp(dv.w, -1f, 1f), halfAngle = math.acos(w), angle = 2f * halfAngle;
            if (angle > math.PI) angle -= 2f * math.PI;

            float sinHalf = math.sqrt(math.max(0f, 1f - w * w));
            float3 axis = sinHalf > 1e-6f ? dv.xyz / sinHalf : new float3(0f, 0f, 0f), logVec = axis * angle;
            float3 filteredLog = EuroVec3(ref st.logVecState, logVec, dt, minCutoff, beta, dCutoff);
            float mag = math.length(filteredLog);
            quaternion filteredDelta;
            if (mag < 1e-6f)
            {
                filteredDelta = quaternion.identity;
            }
            else
            {
                float3 unit = filteredLog / mag;
                float halfMag = mag * 0.5f, s = math.sin(halfMag);
                filteredDelta = new quaternion(unit.x * s, unit.y * s, unit.z * s, math.cos(halfMag));
            }

            quaternion outQ = math.mul(filteredDelta, st.prev);
            st.prev = outQ;
            return outQ;
        }
        public static quaternion SlerpShortest(quaternion a, quaternion b, float t)
        {
            float4 av = a.value, bv = b.value;
            float cosHalf = math.dot(av, bv);
            if (cosHalf < 0f) { bv = -bv; cosHalf = -cosHalf; }

            if (cosHalf > 0.9995f)
            {
                float4 r = math.normalize(math.lerp(av, bv, t));
                return new quaternion(r);
            }

            float halfAngle = math.acos(math.min(cosHalf, 1f)), sinHalf = math.sin(halfAngle);
            float wa = math.sin((1f - t) * halfAngle) / sinHalf, wb = math.sin(t * halfAngle) / sinHalf;
            return new quaternion(av * wa + bv * wb);
        }
    }
}
