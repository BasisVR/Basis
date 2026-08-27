using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// The per frame half of <see cref="BasisAvatarProxy"/>: every limb's matrix for the room, in one flat
/// array that consumers index into rather than each keeping a copy.
///
/// ⚠️ THIS RUNS ON THE MAIN THREAD, DELIBERATELY, AND A JOB VERSION CANNOT LIVE AT THIS CALL SITE.
/// It was an IJobParallelForTransform gather plus a Burst IJobParallelFor, and it crashed the editor:
///
///     InvalidOperationException: The previously scheduled job ZBinningJob writes to the
///     NativeArray`1[System.UInt32] ZBinningJob.bins. You must call JobHandle.Complete() on the job
///     ZBinningJob, before you can write to it safely.
///
/// ZBinningJob is URP's OWN light binning job. The poses are sampled from
/// RenderPipelineManager.beginFrameRendering, which is the only point late enough that every pose write
/// for the frame has landed and early enough that nothing has started drawing - but it sits inside URP's
/// frame setup, with URP's jobs in flight. Scheduling and completing there is a sync point in the middle
/// of somebody else's job graph, and the safety system is right to refuse it.
///
/// The work is small enough that this is not the tradeoff it sounds like: a dozen limbs per avatar, two
/// transform reads and a basis construction each. What it is NOT is the old per frame SkinnedMeshRenderer
/// bake, which is the cost the proxy exists to remove. If this ever does show up in a profile, the job
/// version has to be scheduled from outside the render pipeline - a MonoBehaviour LateUpdate that
/// schedules and a beginFrameRendering that only reads - not resurrected here.
/// </summary>
public static class BasisAvatarProxyJobs
{
    private static Transform[] bones;
    private static float2[] shape;
    private static Matrix4x4[] matrices;
    private static int limbCount;

    /// <summary>How many limbs the shared arrays currently hold. For tests and diagnostics.</summary>
    public static int LimbCount => limbCount;

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
        limbCount = limbs != null ? limbs.Count : 0;
        if (limbCount == 0)
        {
            bones = null;
            shape = null;
            matrices = null;
            return;
        }

        bones = new Transform[limbCount * 2];
        shape = new float2[limbCount];
        matrices = new Matrix4x4[limbCount];

        for (int index = 0; index < limbCount; index++)
        {
            BasisAvatarProxy.ResolvedLimb limb = limbs[index];
            // A null here would desynchronise every index after it, so a dead bone keeps its slot and is
            // caught by the radius being zero instead.
            bones[index * 2] = limb.From;
            bones[index * 2 + 1] = limb.To != null ? limb.To : limb.From;
            shape[index] = new float2(limb.IsValid ? limb.Radius : 0f, limb.Extend);
            matrices[index] = Matrix4x4.identity;
        }
    }

    /// <summary>Reads every bone and rebuilds every matrix. One pass for the whole room.</summary>
    public static void Run()
    {
        if (matrices == null || limbCount == 0) { return; }

        for (int index = 0; index < limbCount; index++)
        {
            Transform from = bones[index * 2];
            Transform to = bones[index * 2 + 1];
            if (from == null || to == null) { continue; }
            matrices[index] = Build(from.position, to.position, shape[index].x, shape[index].y);
        }
    }

    /// <summary>
    /// Where the shared unit capsule has to be put to become this limb. The capsule is authored along +Y
    /// with radius 1 and its ends at y = +/-2, so the scale is (radius, half length, radius) and the
    /// second column is the bone direction.
    /// </summary>
    public static Matrix4x4 Build(Vector3 start, Vector3 end, float radius, float extend)
    {
        Vector3 axis = end - start;
        float length = axis.magnitude;

        if (length <= 0.0001f)
        {
            // A collapsed joint still has a body part sitting on it, so it stays a ball rather than
            // vanishing - which is what stops a degenerate rig punching holes in the occlusion.
            return Matrix4x4.TRS(start, Quaternion.identity, new Vector3(radius, radius, radius));
        }

        Vector3 direction = axis / length;
        if (extend > 0f)
        {
            end += direction * extend;
            length += extend;
        }

        // The capsule is radially symmetric, so any orthonormal basis with +Y down the bone is the right
        // one. Built directly rather than through FromToRotation, which needs its own antiparallel case.
        Vector3 reference = Mathf.Abs(direction.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 x = Vector3.Normalize(Vector3.Cross(reference, direction));
        Vector3 z = Vector3.Cross(direction, x);

        float halfLength = length * 0.5f;
        Vector3 centre = (start + end) * 0.5f;

        Matrix4x4 matrix = new Matrix4x4();
        matrix.SetColumn(0, new Vector4(x.x * radius, x.y * radius, x.z * radius, 0f));
        matrix.SetColumn(1, new Vector4(direction.x * halfLength, direction.y * halfLength, direction.z * halfLength, 0f));
        matrix.SetColumn(2, new Vector4(z.x * radius, z.y * radius, z.z * radius, 0f));
        matrix.SetColumn(3, new Vector4(centre.x, centre.y, centre.z, 1f));
        return matrix;
    }

    public static void Release()
    {
        bones = null;
        shape = null;
        matrices = null;
        limbCount = 0;
    }
}
