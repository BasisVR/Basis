using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// Interpolates every remote synced object in parallel. One index per object slot; each slot
    /// owns contiguous ranges in the shared SoA pools. Continuous → lerp, rotation → nlerp,
    /// discrete → snap to newest.
    /// </summary>
    [BurstCompile]
    public struct InterpolateSyncObjectsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Active;
        [ReadOnly] public NativeArray<float> T;

        [ReadOnly] public NativeArray<int> ContBase;
        [ReadOnly] public NativeArray<int> ContCount;
        [ReadOnly] public NativeArray<int> RotBase;
        [ReadOnly] public NativeArray<int> RotCount;
        [ReadOnly] public NativeArray<int> DiscBase;
        [ReadOnly] public NativeArray<int> DiscCount;

        [ReadOnly] public NativeArray<float> ContCur;
        [ReadOnly] public NativeArray<float> ContNext;
        [ReadOnly] public NativeArray<byte> ContMode;
        [ReadOnly] public NativeArray<quaternion> RotCur;
        [ReadOnly] public NativeArray<quaternion> RotNext;
        [ReadOnly] public NativeArray<byte> RotMode;
        [ReadOnly] public NativeArray<int> DiscNext;

        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float> ContOut;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<quaternion> RotOut;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<int> DiscOut;

        public void Execute(int slot)
        {
            if (Active[slot] == 0) return;
            float t = T[slot];

            int cb = ContBase[slot];
            int cc = ContCount[slot];
            for (int i = 0; i < cc; i++)
            {
                int idx = cb + i;
                byte mode = ContMode[idx];
                if (mode == 0)
                {
                    ContOut[idx] = ContNext[idx];
                }
                else if (mode == 2)
                {
                    float a = ContCur[idx];
                    float delta = math.fmod(ContNext[idx] - a + 540f, 360f) - 180f;
                    ContOut[idx] = a + delta * t;
                }
                else
                {
                    ContOut[idx] = math.lerp(ContCur[idx], ContNext[idx], t);
                }
            }

            int rb = RotBase[slot];
            int rc = RotCount[slot];
            for (int i = 0; i < rc; i++)
            {
                int idx = rb + i;
                if (RotMode[idx] == 0)
                {
                    RotOut[idx] = RotNext[idx];
                    continue;
                }
                quaternion a = RotCur[idx];
                quaternion b = RotNext[idx];
                if (math.dot(a.value, b.value) < 0f) b.value = -b.value;
                RotOut[idx] = math.nlerp(a, b, t);
            }

            int db = DiscBase[slot];
            int dc = DiscCount[slot];
            for (int i = 0; i < dc; i++)
            {
                int idx = db + i;
                DiscOut[idx] = DiscNext[idx];
            }
        }
    }

    /// <summary>
    /// One transform-apply binding for <see cref="ApplySyncTransformsJob"/>. Indices are pool positions
    /// (-1 = channel not synced: position/scale axes hold the transform's live value, euler axes hold
    /// <see cref="HeldEuler"/>). Objects fill it with their own schema offsets; the driver rebases them
    /// into the shared pools at layout rebuild. Mirrors BasisSyncedTransform.ComposeSyncedPose.
    /// </summary>
    public struct BasisSyncApplyBinding
    {
        public int Slot;
        public int PosX, PosY, PosZ;
        public int RotQuat;
        public int RotCont;
        public int EulerX, EulerY, EulerZ;
        public float3 HeldEuler;
        public int ScaleUniform;
        public int ScaleX, ScaleY, ScaleZ;
        public byte World;

        public static BasisSyncApplyBinding Empty => new BasisSyncApplyBinding
        {
            Slot = -1,
            PosX = -1, PosY = -1, PosZ = -1,
            RotQuat = -1, RotCont = -1,
            EulerX = -1, EulerY = -1, EulerZ = -1,
            ScaleUniform = -1, ScaleX = -1, ScaleY = -1, ScaleZ = -1,
        };

        public bool HasPosition => PosX >= 0 || PosY >= 0 || PosZ >= 0;
        public bool HasRotation => RotQuat >= 0 || RotCont >= 0 || EulerX >= 0 || EulerY >= 0 || EulerZ >= 0;
        public bool HasScale => ScaleUniform >= 0 || ScaleX >= 0 || ScaleY >= 0 || ScaleZ >= 0;
        public bool HasAny => HasPosition || HasRotation || HasScale;

        public void OffsetPools(int contBase, int rotBase)
        {
            if (PosX >= 0) PosX += contBase;
            if (PosY >= 0) PosY += contBase;
            if (PosZ >= 0) PosZ += contBase;
            if (RotQuat >= 0) RotQuat += rotBase;
            if (RotCont >= 0) RotCont += contBase;
            if (EulerX >= 0) EulerX += contBase;
            if (EulerY >= 0) EulerY += contBase;
            if (EulerZ >= 0) EulerZ += contBase;
            if (ScaleUniform >= 0) ScaleUniform += contBase;
            if (ScaleX >= 0) ScaleX += contBase;
            if (ScaleY >= 0) ScaleY += contBase;
            if (ScaleZ >= 0) ScaleZ += contBase;
        }
    }

    /// <summary>
    /// Writes interpolated position/rotation/scale onto bound transforms, composing partially-synced
    /// channels against the transform's live values the same way the main-thread apply does. Rotation
    /// comes from the quaternion pool, four continuous floats (normalized), or euler angles
    /// (quaternion.EulerZXY == Quaternion.Euler), whichever the binding declares.
    /// </summary>
    [BurstCompile]
    public struct ApplySyncTransformsJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<float> ContOut;
        [ReadOnly] public NativeArray<quaternion> RotOut;
        [ReadOnly] public NativeArray<byte> Active;
        [ReadOnly] public NativeArray<BasisSyncApplyBinding> Bindings;

        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid) return;
            BasisSyncApplyBinding b = Bindings[index];
            if (Active[b.Slot] == 0) return;
            bool world = b.World == 1;

            bool hasPos = b.HasPosition;
            bool hasRot = b.HasRotation;
            if (hasPos || hasRot)
            {
                float3 p = float3.zero;
                quaternion r = quaternion.identity;
                bool fullPos = b.PosX >= 0 && b.PosY >= 0 && b.PosZ >= 0;
                if (!fullPos || !hasRot)
                {
                    UnityEngine.Vector3 lp;
                    UnityEngine.Quaternion lr;
                    if (world) transform.GetPositionAndRotation(out lp, out lr);
                    else transform.GetLocalPositionAndRotation(out lp, out lr);
                    p = lp;
                    r = lr;
                }

                if (b.PosX >= 0) p.x = ContOut[b.PosX];
                if (b.PosY >= 0) p.y = ContOut[b.PosY];
                if (b.PosZ >= 0) p.z = ContOut[b.PosZ];

                if (b.RotQuat >= 0)
                {
                    r = RotOut[b.RotQuat];
                }
                else if (b.RotCont >= 0)
                {
                    float4 q = new float4(ContOut[b.RotCont], ContOut[b.RotCont + 1], ContOut[b.RotCont + 2], ContOut[b.RotCont + 3]);
                    float mag = math.length(q);
                    if (mag > 1e-6f) r = new quaternion(q / mag);
                }
                else if (b.EulerX >= 0 || b.EulerY >= 0 || b.EulerZ >= 0)
                {
                    float3 e = b.HeldEuler;
                    if (b.EulerX >= 0) e.x = ContOut[b.EulerX];
                    if (b.EulerY >= 0) e.y = ContOut[b.EulerY];
                    if (b.EulerZ >= 0) e.z = ContOut[b.EulerZ];
                    r = quaternion.EulerZXY(math.radians(e));
                }

                if (hasPos && hasRot)
                {
                    if (world) transform.SetPositionAndRotation(p, r);
                    else transform.SetLocalPositionAndRotation(p, r);
                }
                else if (hasPos)
                {
                    if (world) transform.position = p;
                    else transform.localPosition = p;
                }
                else
                {
                    if (world) transform.rotation = r;
                    else transform.localRotation = r;
                }
            }

            if (b.ScaleUniform >= 0)
            {
                float u = ContOut[b.ScaleUniform];
                transform.localScale = new float3(u, u, u);
            }
            else if (b.ScaleX >= 0 || b.ScaleY >= 0 || b.ScaleZ >= 0)
            {
                float3 s = transform.localScale;
                if (b.ScaleX >= 0) s.x = ContOut[b.ScaleX];
                if (b.ScaleY >= 0) s.y = ContOut[b.ScaleY];
                if (b.ScaleZ >= 0) s.z = ContOut[b.ScaleZ];
                transform.localScale = s;
            }
        }
    }
}
