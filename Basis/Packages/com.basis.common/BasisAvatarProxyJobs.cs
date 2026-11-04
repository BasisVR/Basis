using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// The per frame half of <see cref="BasisAvatarProxy"/>: every limb's matrix for the room, in one flat
/// array that consumers index into rather than each keeping a copy.
///
/// ⚠️ A JOB VERSION CANNOT BE SCHEDULED FROM INSIDE THE RENDER PIPELINE. An earlier attempt scheduled
/// the gather from RenderPipelineManager.beginFrameRendering and crashed the editor:
///
///     InvalidOperationException: The previously scheduled job ZBinningJob writes to the
///     NativeArray`1[System.UInt32] ZBinningJob.bins. You must call JobHandle.Complete() on the job
///     ZBinningJob, before you can write to it safely.
///
/// ZBinningJob is URP's OWN light binning job — scheduling and completing there is a sync point in the
/// middle of somebody else's job graph, and the safety system is right to refuse it.
///
/// So the split is: <see cref="ScheduleBeforeRender"/> runs on Application.onBeforeRender at
/// BeforeRenderOrder int.MaxValue — after Basis has run its IK on the default-order handler, before URP
/// exists for the frame — and <see cref="Run"/> (beginContextRendering) only joins and publishes. The poses
/// are still one sample at one instant for every consumer; the sample just happens a hair earlier, at the
/// last onBeforeRender slot instead of the first pipeline callback, with nothing writing bones in between.
/// A destroyed bone is skipped by the transform job and keeps its last matrix until the next rebuild,
/// exactly as the managed loop's null-skip did.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisAvatarProxyJobs
{
    private static Transform[] bones;
    private static float2[] shape;
    private static Matrix4x4[] matrices;
    private static Vector4[] normalRows;
    private static int limbCount;
    [NoAutoStaticsCleanup] private static NativeArray<float4> outNormals;

    [NoAutoStaticsCleanup] private static TransformAccessArray access;
    [NoAutoStaticsCleanup] private static NativeArray<float3> positions;
    [NoAutoStaticsCleanup] private static NativeArray<float2> shapeNative;
    [NoAutoStaticsCleanup] private static NativeArray<float4x4> outMatrices;
    private static JobHandle handle;
    private static bool scheduled;
    private static bool hooked;

    [BurstCompile]
    private struct GatherJob : IJobParallelForTransform
    {
        public NativeArray<float3> Positions;

        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid) { return; }
            Positions[index] = transform.position;
        }
    }

    [BurstCompile]
    private struct BuildJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float2> Shape;
        public NativeArray<float4x4> Matrices;
        [NativeDisableParallelForRestriction] public NativeArray<float4> Normals;

        public void Execute(int index)
        {
            float2 s = Shape[index];
            float4x4 m = Build(Positions[index * 2], Positions[index * 2 + 1], s.x, s.y);
            Matrices[index] = m;
            NormalRows(m, out float4 n0, out float4 n1, out float4 n2);
            Normals[index * 3] = n0; Normals[index * 3 + 1] = n1; Normals[index * 3 + 2] = n2;
        }
    }

    public static void NormalRows(in float4x4 m, out float4 n0, out float4 n1, out float4 n2)
    {
        float4 c0 = m.c0, c1 = m.c1, c2 = m.c2;
        float s0 = c0.x * c0.x + c0.y * c0.y + c0.z * c0.z, s1 = c1.x * c1.x + c1.y * c1.y + c1.z * c1.z, s2 = c2.x * c2.x + c2.y * c2.y + c2.z * c2.z;
        if (s0 < 1e-12f || s1 < 1e-12f || s2 < 1e-12f) { n0 = new float4(float.NaN); n1 = n2 = default; return; }
        float i0 = 1f / s0, i1 = 1f / s1, i2 = 1f / s2;
        n0 = new float4(c0.x * i0, c1.x * i1, c2.x * i2, 0f);
        n1 = new float4(c0.y * i0, c1.y * i1, c2.y * i2, 0f);
        n2 = new float4(c0.z * i0, c1.z * i1, c2.z * i2, 0f);
    }

    public static bool TryNormalRowsAt(int index, out Vector4 n0, out Vector4 n1, out Vector4 n2)
    {
        if (normalRows == null || index < 0 || index >= limbCount || float.IsNaN(normalRows[index * 3].x)) { n0 = n1 = n2 = default; return false; }
        n0 = normalRows[index * 3]; n1 = normalRows[index * 3 + 1]; n2 = normalRows[index * 3 + 2];
        return true;
    }

    public static bool IsAllocated => matrices != null && limbCount > 0;

    /// <summary>
    /// The matrix for a limb, by its global index. Consumers hold an offset into this rather than their
    /// own copy, so nothing is duplicated per tracer and nothing is copied per frame.
    /// </summary>
    public static Matrix4x4 MatrixAt(int index)
    {
        if (matrices == null || index < 0 || index >= limbCount) { return Matrix4x4.identity; }
        return matrices[index];
    }

    /// <summary>
    /// Rebuilds the flat arrays from the given limbs. Called when an avatar joins or leaves, never per
    /// frame - the transforms an avatar's capsules read do not change while it is standing there.
    /// </summary>
    public static void Rebuild(List<BasisAvatarProxy.ResolvedLimb> limbs)
    {
        CompleteScheduled();
        DisposeNative();

        limbCount = limbs != null ? limbs.Count : 0;
        if (limbCount == 0)
        {
            bones = null;
            shape = null;
            matrices = null;
            normalRows = null;
            return;
        }

        bones = new Transform[limbCount * 2];
        shape = new float2[limbCount];
        matrices = new Matrix4x4[limbCount];
        normalRows = new Vector4[limbCount * 3];
        for (int index = 0; index < limbCount; index++) { normalRows[index * 3] = new Vector4(1f, 0f, 0f, 0f); normalRows[index * 3 + 1] = new Vector4(0f, 1f, 0f, 0f); normalRows[index * 3 + 2] = new Vector4(0f, 0f, 1f, 0f); }

        bool everyBoneAlive = true;
        for (int index = 0; index < limbCount; index++)
        {
            BasisAvatarProxy.ResolvedLimb limb = limbs[index];
            // A null here would desynchronise every index after it, so a dead bone keeps its slot and is
            // caught by the radius being zero instead.
            Transform from = limb.From != null ? limb.From : limb.To;
            bones[index * 2] = from;
            bones[index * 2 + 1] = limb.To != null ? limb.To : from;
            shape[index] = new float2(limb.IsValid ? limb.Radius : 0f, limb.Extend);
            matrices[index] = Matrix4x4.identity;
            if (from == null) { everyBoneAlive = false; }
        }

        if (!everyBoneAlive) { return; }

        access = new TransformAccessArray(bones);
        positions = new NativeArray<float3>(limbCount * 2, Allocator.Persistent);
        for (int index = 0; index < limbCount * 2; index++) { positions[index] = bones[index].position; }
        shapeNative = new NativeArray<float2>(shape, Allocator.Persistent);
        outMatrices = new NativeArray<float4x4>(limbCount, Allocator.Persistent);
        for (int index = 0; index < limbCount; index++) { outMatrices[index] = float4x4.identity; }
        outNormals = new NativeArray<float4>(limbCount * 3, Allocator.Persistent);
        outNormals.Reinterpret<Vector4>().CopyFrom(normalRows);
        EnsureHook();
    }

    private static void EnsureHook()
    {
        if (hooked) { return; }
        hooked = true;
        Application.onBeforeRender += ScheduleBeforeRender;
        Application.quitting += Unhook;
    }

    private static void Unhook()
    {
        Application.quitting -= Unhook;
        Application.onBeforeRender -= ScheduleBeforeRender;
        hooked = false;
    }

    [BeforeRenderOrder(int.MaxValue)]
    private static void ScheduleBeforeRender()
    {
        if (scheduled || limbCount == 0 || !access.isCreated) { return; }
        JobHandle gather = new GatherJob { Positions = positions }.Schedule(access);
        handle = new BuildJob { Positions = positions, Shape = shapeNative, Matrices = outMatrices, Normals = outNormals }.Schedule(limbCount, 16, gather);
        scheduled = true;
        JobHandle.ScheduleBatchedJobs();
    }

    private static void CompleteScheduled()
    {
        if (!scheduled) { return; }
        handle.Complete();
        scheduled = false;
    }

    /// <summary>
    /// Publishes this frame's matrices. Joins the pre-render job when one is in flight; falls back to the
    /// managed read loop when it is not (the frame a layout rebuild landed on, or when nothing hooked yet).
    /// </summary>
    public static void Run()
    {
        if (matrices == null || limbCount == 0) { return; }

        if (scheduled)
        {
            handle.Complete();
            scheduled = false;
            outMatrices.Reinterpret<Matrix4x4>().CopyTo(matrices);
            outNormals.Reinterpret<Vector4>().CopyTo(normalRows);
            return;
        }

        for (int index = 0; index < limbCount; index++)
        {
            Transform from = bones[index * 2];
            Transform to = bones[index * 2 + 1];
            if (from == null || to == null) { continue; }
            float4x4 m = Build((float3)from.position, (float3)to.position, shape[index].x, shape[index].y);
            matrices[index] = m;
            NormalRows(m, out float4 n0, out float4 n1, out float4 n2);
            normalRows[index * 3] = n0; normalRows[index * 3 + 1] = n1; normalRows[index * 3 + 2] = n2;
        }
    }

    /// <summary>
    /// Where the shared unit capsule has to be put to become this limb. The capsule is authored along +Y
    /// with radius 1 and its ends at y = +/-2, so the scale is (radius, half length, radius) and the
    /// second column is the bone direction.
    /// </summary>
    public static Matrix4x4 Build(Vector3 start, Vector3 end, float radius, float extend) => Build((float3)start, (float3)end, radius, extend);
    public static float4x4 Build(float3 start, float3 end, float radius, float extend)
    {
        float3 axis = end - start;
        float length = math.length(axis);

        if (length <= 0.0001f)
        {
            // A collapsed joint still has a body part sitting on it, so it stays a ball rather than
            // vanishing - which is what stops a degenerate rig punching holes in the occlusion.
            return new float4x4(new float4(radius, 0f, 0f, 0f), new float4(0f, radius, 0f, 0f), new float4(0f, 0f, radius, 0f), new float4(start, 1f));
        }

        float3 direction = axis / length;
        if (extend > 0f)
        {
            end += direction * extend;
            length += extend;
        }

        // The capsule is radially symmetric, so any orthonormal basis with +Y down the bone is the right
        // one. Built directly rather than through FromToRotation, which needs its own antiparallel case.
        float3 reference = math.abs(direction.y) < 0.99f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
        float3 x = math.normalize(math.cross(reference, direction));
        float3 z = math.cross(direction, x);

        float halfLength = length * 0.5f;
        float3 centre = (start + end) * 0.5f;
        return new float4x4(new float4(x * radius, 0f), new float4(direction * halfLength, 0f), new float4(z * radius, 0f), new float4(centre, 1f));
    }

    private static void DisposeNative()
    {
        if (access.isCreated) { access.Dispose(); }
        if (positions.IsCreated) { positions.Dispose(); }
        if (shapeNative.IsCreated) { shapeNative.Dispose(); }
        if (outMatrices.IsCreated) { outMatrices.Dispose(); }
        if (outNormals.IsCreated) { outNormals.Dispose(); }
    }

    public static void Release()
    {
        CompleteScheduled();
        DisposeNative();
        bones = null;
        shape = null;
        matrices = null;
        normalRows = null;
        limbCount = 0;
    }
}
