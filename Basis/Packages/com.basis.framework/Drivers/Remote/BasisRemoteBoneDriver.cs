using Basis.Network.Core.Compression;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Indices used to address bones in flat SoA/arrays for jobs.
/// </summary>
public static class BoneIdx
{
    /// <summary>Head bone index.</summary>
    public const int Head = 0;
    /// <summary>Neck bone index.</summary>
    public const int Neck = 1;
    /// <summary>Chest bone index.</summary>
    public const int Chest = 2;
    /// <summary>Spine bone index.</summary>
    public const int Spine = 3;
    /// <summary>Hips/root bone index.</summary>
    public const int Hips = 4;
    /// <summary>Center-eye (between the eyes) index.</summary>
    public const int CenterEye = 5;
    /// <summary>Mouth anchor index.</summary>
    public const int Mouth = 6;
    /// <summary>Total number of bones supported.</summary>
    public const int BoneCount = 7;
}

/// <summary>
/// Authoring-time TPose data and local offsets (unscaled) used by the bone solver.
/// Values are in avatar-local space relative to <c>rootWorld</c>.
/// </summary>
public struct TposeAndOffsetDataJob
{
    /// <summary>Unscaled TPose local position of the neck.</summary>
    public float3 tposeLocal_unscaled_Neck;
    /// <summary>Unscaled TPose local position of the chest.</summary>
    public float3 tposeLocal_unscaled_Chest;
    /// <summary>Unscaled TPose local position of the spine.</summary>
    public float3 tposeLocal_unscaled_Spine;
    /// <summary>Unscaled TPose local position of the hips.</summary>
    public float3 tposeLocal_unscaled_Hips;
    /// <summary>Unscaled TPose local position of the center eye.</summary>
    public float3 tposeLocal_unscaled_CenterEye;
    /// <summary>Unscaled TPose local position of the mouth.</summary>
    public float3 tposeLocal_unscaled_Mouth;

    /// <summary>Unscaled offset from head to neck.</summary>
    public float3 offsets_unscaled_Neck;
    /// <summary>Unscaled offset from neck to chest.</summary>
    public float3 offsets_unscaled_Chest;
    /// <summary>Unscaled offset from chest to spine (down-chain).</summary>
    public float3 offsets_unscaled_Spine;      // Chest→Spine in this chain
    /// <summary>Unscaled offset from head to center-eye.</summary>
    public float3 offsets_unscaled_CenterEye;
    /// <summary>Unscaled offset from head to mouth.</summary>
    public float3 offsets_unscaled_Mouth;


    /// <summary>
    /// default scale
    /// </summary>
    public float3 TposeScale;
}

/// <summary>
/// Per-frame scale cache (scaled TPose and offsets) to avoid recomputing in downstream passes.
/// </summary>
public struct RemoteScaleCache
{
    /// <summary>Scaled TPose local hips.</summary>
    public float3 tposeLocal_scaled_Hips;
    /// <summary>Scaled TPose local mouth.</summary>
    public float3 tposeLocal_scaled_Mouth;
    /// <summary>Scaled head→neck offset.</summary>
    public float3 offsets_scaled_Neck;
    /// <summary>Scaled neck→chest offset.</summary>
    public float3 offsets_scaled_Chest;
    /// <summary>Scaled chest→spine offset.</summary>
    public float3 offsets_scaled_Spine;
    /// <summary>Scaled head→center-eye offset.</summary>
    public float3 offsets_scaled_CenterEye;
    /// <summary>Scaled head→mouth offset.</summary>
    public float3 offsets_scaled_Mouth;
}

/// <summary>
/// Final pose outputs per bone used by apply passes (nameplate/mouth/etc).
/// </summary>
public struct RemoteFrameOutput
{
    /// <summary>World positions for the pose.</summary>
    public float3 pos_Head, pos_Neck, pos_Spine, pos_Hips, pos_CenterEye, pos_Mouth;
    /// <summary>World rotations for the pose.</summary>
    public quaternion rot_Head, rot_Neck, rot_Chest, rot_Spine, rot_Hips, rot_CenterEye, rot_Mouth;
    /// <summary>
    /// Vertical delta between hips and mouth in scaled TPose space (used for UI placement).
    /// </summary>
    public float HeightAvatarHipCoord;
}

/// <summary>
/// Core remote bone job: reads gathered transform samples directly, scales authoring
/// offsets, composes head/hips transforms, computes derived joint positions, and writes
/// a <see cref="RemoteFrameOutput"/>. Folds in the SoA→AoS aggregation step so the
/// gather→sim chain is one job shorter on the critical path.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct BasisRemoteBoneJob : IJobParallelFor
{
    /// <summary>Authoring-time TPose and offset data (unscaled).</summary>
    [ReadOnly] public NativeArray<TposeAndOffsetDataJob> Authoring;

    // Per-frame gather outputs, consumed directly (no aggregation step).
    [ReadOnly] public NativeArray<float3> RootPos;
    [ReadOnly] public NativeArray<float3> RootScale;
    [ReadOnly] public NativeArray<float3> HeadPos;
    [ReadOnly] public NativeArray<quaternion> HeadRot;
    [ReadOnly] public NativeArray<float3> HipsPos;
    [ReadOnly] public NativeArray<quaternion> HipsRot;
    [ReadOnly] public NativeArray<quaternion> TposeHeadRot;
    [ReadOnly] public NativeArray<quaternion> TposeHipsRot;

    /// <summary>Per-frame pose outputs.</summary>
    [WriteOnly]
    public NativeArray<RemoteFrameOutput> Out;
    /// <summary>Separate mouth-only positions for fast lookup (avoids full RemoteFrameOutput copy).</summary>
    [WriteOnly]
    public NativeArray<float3> MouthPositions;
    /// <summary>
    /// Mouth facing direction, same lookup rationale as <see cref="MouthPositions"/>.
    /// Voice directivity needs the axis the mouth radiates along, and reading it
    /// off the Transform on the main thread would stall on this very job.
    /// </summary>
    [WriteOnly]
    public NativeArray<float3> MouthForwards;
    /// <summary>Writable per-frame scale cache (scaled TPose and offsets).</summary>
    public NativeArray<RemoteScaleCache> GeneratedScales;

    /// <summary>
    /// Executes the bone solve for one avatar.
    /// </summary>
    /// <param name="i">Avatar index.</param>
    public void Execute(int i)
    {
        var a = Authoring[i];
        float3 nowScale = RootScale[i];
        var sc = GeneratedScales[i];

        // Scale TPose + offsets by current world scale
        sc.tposeLocal_scaled_Hips = a.tposeLocal_unscaled_Hips * nowScale;
        sc.tposeLocal_scaled_Mouth = a.tposeLocal_unscaled_Mouth * nowScale;
        sc.offsets_scaled_Neck = a.offsets_unscaled_Neck * nowScale;
        sc.offsets_scaled_Chest = a.offsets_unscaled_Chest * nowScale;
        sc.offsets_scaled_Spine = a.offsets_unscaled_Spine * nowScale;
        sc.offsets_scaled_CenterEye = a.offsets_unscaled_CenterEye * nowScale;
        sc.offsets_scaled_Mouth = a.offsets_unscaled_Mouth * nowScale;
        GeneratedScales[i] = sc;

        // Compose world rotations (TPose→current). headR is the avatar's own root frame carried by
        // the head: it takes the root-space offsets below into world, and is the frame the eye/mouth
        // anchors are handed as their rotation. See BasisRemoteBoneMath.HeadWorldFrame — the
        // conjugate is what makes it collapse to the root rotation at T-pose, and dropping it is
        // invisible on rigs whose head binds axis-aligned and wrong on every rig that doesn't.
        quaternion headR = BasisRemoteBoneMath.HeadWorldFrame(HeadRot[i], TposeHeadRot[i]);
        quaternion hipsR = math.mul(TposeHipsRot[i], HipsRot[i]);

        // World positions for downstream apply jobs. Previously this code
        // subtracted rootWorld, which silently worked only because root
        // never moved off origin in the old hips-as-world design. With the
        // current root-as-derived design root actually moves, so subtracting
        // would shift every consumer (mouth/nameplate/center-eye lookups —
        // all of which feed SetPositionAndRotation) by -rootWorld. Use the
        // gathered world positions directly.
        float3 headP = HeadPos[i];
        float3 hipsP = HipsPos[i];

        // Forward chain from head using headR and scaled offsets — already in
        // world space because headP is world.
        BasisRemoteBoneMath.ComposeHeadChain(headP, headR,
            sc.offsets_scaled_Neck, sc.offsets_scaled_Chest, sc.offsets_scaled_Spine, sc.offsets_scaled_CenterEye, sc.offsets_scaled_Mouth,
            out float3 neckP, out float3 chestP, out float3 spineP, out float3 eyeP, out float3 mouthP);


        float3 difference = SafeDivide(nowScale, a.TposeScale);

        Out[i] = new RemoteFrameOutput
        {
            pos_Head = headP,
            pos_Neck = neckP,
            pos_Spine = spineP,
            pos_Hips = hipsP,
            pos_CenterEye = eyeP,
            pos_Mouth = mouthP,

            rot_Head = headR,
            rot_Neck = headR,
            rot_Chest = headR,
            rot_Spine = headR,
            rot_Hips = hipsR,
            rot_CenterEye = headR,
            rot_Mouth = headR,


            // Used for vertical offsetting of the nameplate UI
            HeightAvatarHipCoord = difference.y * 1.2f,
        };
        MouthPositions[i] = mouthP;
        MouthForwards[i] = math.mul(headR, new float3(0f, 0f, 1f));
    }
    private readonly float3 SafeDivide(float3 numerator, float3 denominator)
    {
        const float eps = 1e-6f;

        float3 safeDenom = math.select(denominator,math.sign(denominator) * eps, math.abs(denominator) < eps);

        return numerator / safeDenom;
    }
}

/// <summary>
/// Gathers world root position and approximated lossy scale for each avatar root
/// (computed from the local-to-world matrix inside jobs).
/// </summary>
[BurstCompile]
struct GatherRootJob : IJobParallelForTransform
{
    /// <summary>Output world positions for roots.</summary>
    [WriteOnly] public NativeArray<float3> rootPos;
    /// <summary>Output lossy scales for roots.</summary>
    [WriteOnly] public NativeArray<float3> rootScale;

    /// <summary>Executes per-transform sampling for the root.</summary>
    public void Execute(int index, TransformAccess tx)
    {
        rootPos[index] = tx.position;

        // derive world scale from matrix (no API call to lossyScale in jobs)
        var m = tx.localToWorldMatrix;
        float3 sx = new float3(m.m00, m.m10, m.m20);
        float3 sy = new float3(m.m01, m.m11, m.m21);
        float3 sz = new float3(m.m02, m.m12, m.m22);
        rootScale[index] = new float3(math.length(sx), math.length(sy), math.length(sz));
    }
}

/// <summary>
/// Gathers head world-space position and rotation.
/// </summary>
[BurstCompile]
struct GatherHeadJob : IJobParallelForTransform
{
    /// <summary>Output head positions.</summary>
    [WriteOnly] public NativeArray<float3> headPos;
    /// <summary>Output head rotations.</summary>
    [WriteOnly] public NativeArray<quaternion> headRot;

    /// <summary>Executes per-head sampling.</summary>
    public void Execute(int index, TransformAccess tx)
    {
        tx.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        headPos[index] = position;
        headRot[index] = rotation;
    }
}

/// <summary>
/// Gathers hips world-space position and rotation.
/// </summary>
[BurstCompile]
struct GatherHipsJob : IJobParallelForTransform
{
    /// <summary>Output hips positions.</summary>
    [WriteOnly] public NativeArray<float3> hipsPos;
    /// <summary>Output hips rotations.</summary>
    [WriteOnly] public NativeArray<quaternion> hipsRot;

    /// <summary>Executes per-hip sampling.</summary>
    public void Execute(int index, TransformAccess tx)
    {
        tx.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        hipsPos[index] = position;
        hipsRot[index] = rotation;
    }
}

/// <summary>
/// Applies the mouth transform directly from the computed <see cref="RemoteFrameOutput"/>.
/// </summary>
[BurstCompile]
struct ApplyMouthJob : IJobParallelForTransform
{
    /// <summary>Read-only pose data to apply.</summary>
    [ReadOnly]
    public NativeArray<RemoteFrameOutput> MouthRotation;

    /// <summary>Applies position and rotation to the bound mouth transform.</summary>
    public void Execute(int index, TransformAccess tx)
    {
        tx.SetPositionAndRotation(MouthRotation[index].pos_Mouth, MouthRotation[index].rot_Mouth);
    }
}

/// <summary>
/// Positions the floating nameplate relative to the avatar and rotates it to face the camera (yaw only).
/// Uses derived TPose vertical delta to place the plate above the head.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct MappedNameplateApplyJob : IJobParallelForTransform
{
    /// <summary>Camera world position used to bill-board the plate (yaw-only).</summary>
    public float3 CameraPosition;

    /// <summary>Input pose data (per-avatar) for nameplate placement.</summary>
    [ReadOnly] public NativeArray<RemoteFrameOutput> NamePlateIn;

    /// <summary>Computes position above hips and rotates toward camera.</summary>
    public void Execute(int jobIndex, TransformAccess tx)
    {
        var data = NamePlateIn[jobIndex];
        float3 hips = data.pos_Hips;

        // y = hips.y + diff * 1.8
        float3 nameplatePos = new float3(hips.x, hips.y + data.HeightAvatarHipCoord, hips.z);

        // Face the camera (yaw only) with zero-distance guard.
        float3 toCam = CameraPosition - nameplatePos;
        float2 xz = new float2(toCam.x, toCam.z);
        float yaw = math.lengthsq(xz) > 1e-12f ? math.atan2(xz.x, xz.y) : 0f;
        quaternion rot = quaternion.RotateY(yaw);

        tx.SetPositionAndRotation(nameplatePos, rot);
    }
}

/// <summary>
/// Burst job that writes precomputed bone localRotations to transforms. Does only the
/// transform side of the work — the quaternion multiply lives in the merged
/// ComputeSkeletonRotationsFromNetworkJob (BasisRemoteNetworkDriver), which also decides which
/// bones actually moved. Runs across ALL remote players' bones in a single flat
/// TransformAccessArray.
///
/// WriteMask is what keeps this affordable. The write it guards is orders of magnitude more
/// expensive than the byte test — it dirties the bone's entire subtree and feeds
/// TransformChangeDispatch — while a large share of bones are bit-identical to last frame
/// (PoseLOD-skipped players, untracked fingers, settled filters). It also subsumes the old
/// ValidMask, since a null bone slot can never set it, so this reads one array instead of two.
/// </summary>
[BurstCompile]
public struct ApplySkeletonRotationsJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<quaternion> Rotations;
    [ReadOnly] public NativeArray<byte> WriteMask;

    public void Execute(int index, TransformAccess transform)
    {
        if (WriteMask[index] == 0) return;
        transform.localRotation = Rotations[index];
    }
}

// ApplyRootJob was folded into ApplyRootAndScaleJob below — it wrote the same one transform per
// player that the scale pass did, so keeping them separate cost a dispatch and a dependency stage
// for nothing.

// ComputeRootFromHipsJob was folded into BasisRemoteNetworkDriver.BulkCopyHipsAndDeriveJob.
// One Burst dispatch per frame fans out all per-player network state AND derives
// the root pose in a single pass, instead of three separate jobs round-tripping
// through temp buffers. See ScheduleBulkCopyHipsAndDerive on the network driver.

/// <summary>
/// Burst job that writes the hips bone's WORLD position and rotation directly
/// via <see cref="TransformAccess.SetPositionAndRotation"/>. The wire carries
/// hips world at high precision (Position/Rotation slots), and applying it
/// world-side bypasses any assumptions about the rig hierarchy — Unity's
/// SetPositionAndRotation walks the actual parent chain (Animator → Armature →
/// Hips, or any other layout) and computes the correct localPosition/local
/// Rotation to land hips exactly at the received world pose.
///
/// Must be scheduled AFTER the root apply (rootApplyJob): the localPosition
/// Unity derives for hips depends on the current parent transform, so the
/// parent must already be in its final per-frame pose when this runs.
///
/// Hips is excluded from the bone-rotations packet (BONE_WRITE_ORDER), so this
/// is the only writer of hips orientation. Other bones (chest, spine, etc.)
/// are written by ApplySkeletonRotationsJob and chain off whatever local
/// rotation Unity computes here.
/// </summary>
[BurstCompile]
public struct ApplyHipsWorldJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<float3> Positions;
    [ReadOnly] public NativeArray<quaternion> Rotations;

    public void Execute(int index, TransformAccess transform)
    {
        transform.SetPositionAndRotation(Positions[index], Rotations[index]);
    }
}

/// <summary>
/// Burst job that writes avatar scale AND the derived root world pose for all remote players in a
/// single pass over sRoots.
///
/// These were two jobs (ApplyAvatarScaleJob then ApplyRootJob) over what is physically the same
/// transform — <c>BasisRemoteAvatarDriver</c> registers <c>animatorRoot</c> as both
/// <c>remotePlayerRoot</c> and <c>AvatarScale</c> — so the job system had to serialise them. At
/// one entry per player that bought two dispatches and an extra dependency stage on the critical
/// path for a single transform's worth of work, which is pure overhead: a write-mode
/// IJobParallelForTransform batches by root hierarchy, and remote players are separate scene
/// roots, so each of these passes is N batches of exactly ONE transform.
///
/// Scale is still written first. SetPositionAndRotation on a DESCENDANT (mouth, nameplate) bakes
/// the parent lossyScale into the child's localPosition, so everything downstream of this job has
/// to see the final scale — doing both here preserves that without a second pass.
/// </summary>
[BurstCompile]
public struct ApplyRootAndScaleJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<float3> Scales;
    [ReadOnly] public NativeArray<byte> HasScaleChange;
    [ReadOnly] public NativeArray<float3> Positions;
    [ReadOnly] public NativeArray<quaternion> Rotations;

    public void Execute(int index, TransformAccess transform)
    {
        if (HasScaleChange[index] != 0)
        {
            transform.localScale = Scales[index];
        }
        transform.SetPositionAndRotation(Positions[index], Rotations[index]);
    }
}

/// <summary>
/// Reads the FK-posed world pose (+ root local rotation) of the bones the end-effector IK needs, into
/// flat buffers parallel to the skeleton TAA, and clears the per-bone override mask. Only the 12 effector
/// slots (LHand/RHand/LFoot/RFoot × root/joint/tip) of players anchored THIS frame do the expensive
/// transform read — every other bone just clears its (cheap byte) mask and returns. Scheduled after the
/// skeleton FK + hips-world apply so the world poses are final.
/// </summary>
[BurstCompile]
public struct ReadBonePoseJob : IJobParallelForTransform
{
    // Bits set for BONE_WRITE_ORDER slots 5..12 (roots+joints) and 15..18 (tips). See EffectorIKComputeJob.
    const uint EffectorSlotMask = 0x79FE0u;

    [ReadOnly] public NativeArray<int> PlayerKeys;
    [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<byte> EffMask;
    [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> ReadPos;
    [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> ReadWorldRot;
    [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<quaternion> ReadLocalRot;
    [WriteOnly] public NativeArray<byte> OverrideMask;
    public int BoneCount;
    public int CapacityFixed;

    public void Execute(int index, TransformAccess transform)
    {
        OverrideMask[index] = 0;

        int dense = index / BoneCount;
        int boneSlot = index - dense * BoneCount;
        if (((EffectorSlotMask >> boneSlot) & 1u) == 0) return;

        int key = PlayerKeys[dense];
        if ((uint)key >= (uint)CapacityFixed || EffMask[key] == 0) return;

        transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        ReadPos[index] = position;
        ReadWorldRot[index] = rotation;
        ReadLocalRot[index] = transform.localRotation;
    }
}

/// <summary>
/// Writes the end-effector IK's LOCAL rotation overrides back onto anchored limbs' bones. Only bones
/// the compute pass flagged (OverrideMask) are touched; local writes are order-independent, so this
/// runs as a single parallel pass. Scheduled after the compute pass, last to touch the skeleton TAA.
/// </summary>
[BurstCompile]
public struct WriteBoneRotationJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<quaternion> OverrideRot;
    [ReadOnly] public NativeArray<byte> OverrideMask;
    /// <summary>Kept in step with what the transform now holds — this pass is the other writer of
    /// these bones. Without it the IK'd rotation would still look like the FK value to the next
    /// frame's compute pass, which would then skip the write and leave the limb stuck at its last
    /// anchored pose once the anchor drops.</summary>
    [WriteOnly] public NativeArray<quaternion> LastWritten;

    public void Execute(int index, TransformAccess transform)
    {
        if (OverrideMask[index] != 0)
        {
            quaternion rot = OverrideRot[index];
            transform.localRotation = rot;
            LastWritten[index] = rot;
        }
    }
}

/// <summary>
/// Static orchestration layer for remote bone simulation.
/// Manages persistent SoA buffers, TransformAccessArrays, scheduling, and disposal.
/// </summary>
public static class RemoteBoneJobSystem
{
    // Persistent SoA
    /// <summary>Authoring TPose/offsets per avatar.</summary>
    static NativeList<TposeAndOffsetDataJob> sAuthoring;
    /// <summary>Per-frame scale caches per avatar.</summary>
    static NativeList<RemoteScaleCache> sScale;
    /// <summary>Per-frame pose outputs per avatar.</summary>
    static NativeList<RemoteFrameOutput> sOut;
    /// <summary>Separate mouth-only world positions for fast lookup (avoids full RemoteFrameOutput copy).</summary>
    static NativeList<float3> sMouthPositions;
    /// <summary>Mouth facing directions, kept beside <see cref="sMouthPositions"/>.</summary>
    static NativeList<float3> sMouthForwards;

    // Cached TPose quats (job friendly)
    /// <summary>TPose head quaternions per avatar.</summary>
    static NativeList<quaternion> sTPoseHeadRot;
    /// <summary>TPose hips quaternions per avatar.</summary>
    static NativeList<quaternion> sTPoseHipsRot;
    /// <summary>TPose hips localPosition per avatar — base for the hips delta apply.</summary>
    static NativeList<float3> sTPoseHipsLocalPos;
    /// <summary>TPose hips localRotation per avatar — base for the hips rotation delta apply
    /// (final hips.localRotation = this × received rotation delta).</summary>
    static NativeList<quaternion> sTPoseHipsLocalRot;

    // Transform access arrays (roots / heads / hips)
    /// <summary>Root transforms per avatar.</summary>
    static TransformAccessArray sRoots;
    /// <summary>Head transforms per avatar.</summary>
    static TransformAccessArray sHeads;
    /// <summary>Hips transforms per avatar.</summary>
    static TransformAccessArray sHips;

    /// <summary>Nameplate transforms per avatar.</summary>
    static TransformAccessArray sNamePlate;
    /// <summary>Avatar scale proxy transforms per avatar.</summary>
    static TransformAccessArray sAvatarScale;
    /// <summary>Mouth transforms per avatar.</summary>
    static TransformAccessArray sMouth;

    // ─── Skeleton bone rotation job data ───
    // Flat TAA holding ALL bone transforms for ALL remote players.
    // Layout: [player0_bone0..bone(N-1), player1_bone0..bone(N-1), ...]
    // where N = BasisBoneRotationCompression.SyncBoneCount (51).
    static TransformAccessArray sSkeletonBones;
    /// <summary>T-pose local rotations, flat parallel to sSkeletonBones.</summary>
    static NativeList<quaternion> sSkeletonTpose;
    /// <summary>Valid mask (1 = bone exists, 0 = null/skip), flat parallel to sSkeletonBones.</summary>
    static NativeList<byte> sSkeletonValid;
    /// <summary>Precomputed local rotations (T-pose × network delta) consumed by <see cref="ApplySkeletonRotationsJob"/>.</summary>
    static NativeArray<quaternion> sSkeletonRotations;
    /// <summary>Last rotation actually written to each bone transform, parallel to
    /// <see cref="sSkeletonRotations"/>. Owned by the compute pass and refreshed by the
    /// effector-IK write-back; (0,0,0,0) reads as "no known value" and forces a write.</summary>
    static NativeArray<quaternion> sSkeletonLastWritten;
    /// <summary>Per-bone write gate produced by the compute pass: 1 = the rotation changed and the
    /// transform must be written, 0 = skip. Already accounts for <see cref="sSkeletonValid"/>.</summary>
    static NativeArray<byte> sSkeletonWriteMask;

    /// <summary>
    /// Capacity to allocate for a required length: rounds up so a steadily growing instance
    /// reallocates O(log n) times instead of on every single join.
    /// </summary>
    static int GrowCapacity(int required)
    {
        if (required <= 0) return 0;
        int capacity = 64;
        while (capacity < required) capacity <<= 1;
        return capacity;
    }
    /// <summary>End-effector IK flat buffers, parallel to sSkeletonBones: read-pose outputs + local overrides.</summary>
    static NativeArray<float3> sIkReadPos;
    static NativeArray<quaternion> sIkReadWorldRot;
    static NativeArray<quaternion> sIkReadLocalRot;
    static NativeArray<quaternion> sIkOverrideRot;
    static NativeArray<byte> sIkOverrideMask;
    /// <summary>Dummy transform for null bone slots in the TAA.</summary>
    static Transform sDummyBone;

    // Temp per-frame buffers (reused)
    /// <summary>Temp root positions.</summary>
    static NativeArray<float3> sTmpRootPos, sTmpHeadPos, sTmpHipsPos;
    /// <summary>Temp root scales.</summary>
    static NativeArray<float3> sTmpRootScale;
    /// <summary>Temp head rotations.</summary>
    static NativeArray<quaternion> sTmpHeadRot, sTmpHipsRot;

    // Hips world pose (populated from BasisRemoteNetworkDriver each frame).
    // The wire's Position/Rotation slots carry hips world directly so the server
    // reduction system reads hips for distance and the visually anchored bone
    // gets the high-precision channel.
    static NativeArray<float3> sTmpHipsWorldPos;
    static NativeArray<quaternion> sTmpHipsWorldRot;
    static NativeArray<float3> sTmpAvatarScales;
    static NativeArray<byte> sTmpScaleChanged;
    // Per-frame derived root world pose — written by the combined
    // BulkCopyHipsAndDeriveJob in one pass alongside the hips world copy and
    // scale. Input to ApplyRootAndScaleJob. Computed such that
    //   root.world × hips.local = hips.world (received).
    static NativeArray<float3> sTmpRootDerivedPos;
    static NativeArray<quaternion> sTmpRootDerivedRot;

    // Bookkeeping
    /// <summary>Map from external key → internal SoA index. Flat array indexed by ushort player ID; -1 = absent.</summary>
    static int[] sKeyToIndex;
    /// <summary>Reverse map: internal SoA index → external key. Maintained directly by Add/Remove,
    /// so Schedule does not need to snapshot a managed List each frame. Sized to the high-water
    /// mark of <see cref="AuthoringLength"/>; consumers always pair it with an explicit count.</summary>
    static NativeArray<int> sKeyArray;
    /// <summary>Pending job handle chain.</summary>
    static JobHandle sPending;
    static JobHandle sGatherRoot;
    static JobHandle sGatherHead;
    static JobHandle sGatherHips;
    static bool sGathersScheduled;

    // Pre-scheduled dependency-free work. Both jobs used to be scheduled inside Schedule(), at
    // the tail of SimulateNetworkApply, even though neither depends on anything that runs
    // between BeginRead() and there — so the workers sat idle through the receiver loop waiting
    // on the MAIN THREAD to reach the schedule call, not on data. They are now kicked at the
    // earliest point each is legal and Schedule() consumes the handles.
    static JobHandle sSkeletonCompute;
    static bool sSkeletonComputeScheduled;
    /// <summary>Bone count the pre-scheduled compute was sized for; a mismatch at consume time discards it.</summary>
    static int sSkeletonComputeBones;
    static JobHandle sHipsDerive;
    static bool sHipsDeriveScheduled;

    /// <summary>
    /// Drops every pre-scheduled handle. Callers must have run <see cref="CompletePending"/>
    /// first — the handles are folded into sPending, so that fences them — otherwise a layout
    /// change would land under a job still in flight.
    /// </summary>
    static void ClearPreScheduled()
    {
        sGathersScheduled = false;
        sSkeletonComputeScheduled = false;
        sHipsDeriveScheduled = false;
    }
    /// <summary>Initialization flag.</summary>
    static bool sInitialized;

    /// <summary>
    /// A registration captured at calibration time (while the avatar is held in TPose),
    /// committed to the SoA/TAA later at a point where no bone job is reading the containers.
    /// </summary>
    struct PendingAdd
    {
        public int Key;
        public TposeAndOffsetDataJob Authoring;
        public quaternion TposeHeadRot;
        public quaternion TposeHipsRot;
        public float3 TposeHipsLocalPos;
        public quaternion TposeHipsLocalRot;
        public Transform Root;
        public Transform Head;
        public Transform Hips;
        public Transform NamePlate;
        public Transform AvatarScale;
        public Transform Mouth;
        public quaternion[] BoneTpose;
        public Transform[] BoneTransforms;
    }

    /// <summary>
    /// Registrations deferred out of the avatar-load path and committed at the top of
    /// <see cref="Schedule"/>. Adding to the SoA/TAA mid-frame forces a job sync; folding the
    /// commits into the sync the frame already does removes that per-add stall, which is what
    /// hits when many avatars calibrate in the same window (e.g. a large simultaneous join).
    /// Removal stays synchronous in <see cref="RemoveRemotePlayer"/>: teardown must drop the TAA
    /// entry while the transform is still alive (Unity auto-removes destroyed transforms from a
    /// TransformAccessArray, which would desync the parallel SoA), so it can't be deferred.
    /// </summary>
    static readonly List<PendingAdd> sPendingAdds = new List<PendingAdd>();

    public static int AuthoringLength;
    /// <summary>
    /// Allocates persistent containers and sets initial capacities for all arrays.
    /// Safe to call multiple times; subsequent calls are ignored once initialized.
    /// </summary>
    /// <param name="initialCapacity">Optional starting capacity hint.</param>
    public static void Initialize(int initialCapacity = 0)
    {
        if (sInitialized) return;

        sAuthoring = new NativeList<TposeAndOffsetDataJob>(initialCapacity, Allocator.Persistent);
        sScale = new NativeList<RemoteScaleCache>(initialCapacity, Allocator.Persistent);
        sOut = new NativeList<RemoteFrameOutput>(initialCapacity, Allocator.Persistent);
        sMouthPositions = new NativeList<float3>(initialCapacity, Allocator.Persistent);
        sMouthForwards = new NativeList<float3>(initialCapacity, Allocator.Persistent);

        sTPoseHeadRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);
        sTPoseHipsRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);
        sTPoseHipsLocalPos = new NativeList<float3>(initialCapacity, Allocator.Persistent);
        sTPoseHipsLocalRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);

        sRoots = new TransformAccessArray(initialCapacity);
        sHeads = new TransformAccessArray(initialCapacity);
        sHips = new TransformAccessArray(initialCapacity);

        sNamePlate = new TransformAccessArray(initialCapacity);
        sAvatarScale = new TransformAccessArray(initialCapacity);
        sMouth = new TransformAccessArray(initialCapacity);

        sSkeletonBones = new TransformAccessArray(initialCapacity * BasisBoneRotationCompression.SyncBoneCount);
        sSkeletonTpose = new NativeList<quaternion>(initialCapacity * BasisBoneRotationCompression.SyncBoneCount, Allocator.Persistent);
        sSkeletonValid = new NativeList<byte>(initialCapacity * BasisBoneRotationCompression.SyncBoneCount, Allocator.Persistent);

        // Create a dummy transform for null bone slots (TAA can't hold null)
        var dummyGO = new GameObject("[BoneJobDummy]") { hideFlags = HideFlags.HideAndDontSave };
        dummyGO.SetActive(false);
        sDummyBone = dummyGO.transform;

        sKeyToIndex = new int[65536];
        Array.Fill(sKeyToIndex, -1);
        EnsureKeyArrayCapacity(math.max(initialCapacity, 16));

        sPendingAdds.Clear();
        sInitialized = true;
    }

    /// <summary>
    /// Disposes all persistent containers and temp buffers, and clears bookkeeping.
    /// </summary>
    public static void Dispose()
    {
        CompletePending();
        ClearPreScheduled();

        if (sAuthoring.IsCreated) sAuthoring.Dispose();
        if (sScale.IsCreated) sScale.Dispose();
        if (sOut.IsCreated) sOut.Dispose();
        if (sMouthPositions.IsCreated) sMouthPositions.Dispose();
        if (sMouthForwards.IsCreated) sMouthForwards.Dispose();

        if (sTPoseHeadRot.IsCreated) sTPoseHeadRot.Dispose();
        if (sTPoseHipsRot.IsCreated) sTPoseHipsRot.Dispose();
        if (sTPoseHipsLocalPos.IsCreated) sTPoseHipsLocalPos.Dispose();
        if (sTPoseHipsLocalRot.IsCreated) sTPoseHipsLocalRot.Dispose();

        if (sRoots.isCreated) sRoots.Dispose();
        if (sHeads.isCreated) sHeads.Dispose();
        if (sHips.isCreated) sHips.Dispose();

        if (sNamePlate.isCreated) sNamePlate.Dispose();
        if (sAvatarScale.isCreated) sAvatarScale.Dispose();
        if (sMouth.isCreated) sMouth.Dispose();

        if (sSkeletonBones.isCreated) sSkeletonBones.Dispose();
        if (sSkeletonTpose.IsCreated) sSkeletonTpose.Dispose();
        if (sSkeletonValid.IsCreated) sSkeletonValid.Dispose();
        if (sSkeletonRotations.IsCreated) sSkeletonRotations.Dispose();
        if (sSkeletonLastWritten.IsCreated) sSkeletonLastWritten.Dispose();
        if (sSkeletonWriteMask.IsCreated) sSkeletonWriteMask.Dispose();
        if (sIkReadPos.IsCreated) sIkReadPos.Dispose();
        if (sIkReadWorldRot.IsCreated) sIkReadWorldRot.Dispose();
        if (sIkReadLocalRot.IsCreated) sIkReadLocalRot.Dispose();
        if (sIkOverrideRot.IsCreated) sIkOverrideRot.Dispose();
        if (sIkOverrideMask.IsCreated) sIkOverrideMask.Dispose();
        if (sDummyBone != null) { UnityEngine.Object.Destroy(sDummyBone.gameObject); sDummyBone = null; }

        DisposeTempBuffers();

        if (sKeyArray.IsCreated) sKeyArray.Dispose();

        if (sKeyToIndex != null) Array.Fill(sKeyToIndex, -1);
        sPendingAdds.Clear();
        sInitialized = false;
    }

    /// <summary>
    /// Completes any pending scheduled jobs and resets the pending handle.
    /// </summary>
    static void CompletePending()
    {
        sPending.Complete();
        sPending = default;
    }

    /// <summary>
    /// True when nothing this system scheduled is still running, so a caller about to mutate the
    /// SoA/TAA containers would not actually block in <see cref="CompletePending"/>. Non-blocking
    /// (JobHandle.IsCompleted) — used to decide whether deferring an avatar install to the frame's
    /// safe window buys anything, since deferring one that would not have fenced is pure latency.
    /// </summary>
    public static bool IsQuiet => sPending.IsCompleted;

    /// <summary>
    /// Registers a remote avatar into the job system and returns the same key for convenience.
    /// Computes authoring TPose data/offsets in avatar-local space and caches TPose quats.
    /// </summary>
    /// <param name="key">External key identifying the avatar.</param>
    /// <param name="remotePlayerRoot">Avatar root transform.</param>
    /// <param name="head">Head transform.</param>
    /// <param name="hips">Hips/root transform.</param>
    /// <param name="tposeHead">Head TPose calibrated coordinates.</param>
    /// <param name="tposeHips">Hips TPose calibrated coordinates.</param>
    /// <param name="authoredCenterEyeLocal">Center-eye position from authoring, root-relative rendered metres.</param>
    /// <param name="authoredMouthLocal">Mouth position from authoring, root-relative rendered metres.</param>
    /// <param name="tposeHeadWorld">Head T-pose position in the same frame — BasisTransformMapping.TposeWorld.</param>
    /// <param name="tposeRootScale">Root world scale those metres were recorded at.</param>
    /// <param name="NamePlate">Nameplate transform to be driven.</param>
    /// <param name="AvatarScale">Transform used for avatar scaling (if any).</param>
    /// <param name="MouthTransform">Mouth transform to be driven.</param>
    /// <returns>The provided <paramref name="key"/>.</returns>
    public static int AddRemotePlayer(int key, Transform remotePlayerRoot, Transform head, Transform hips,BasisCalibratedCoords tposeHead, BasisCalibratedCoords tposeHips, float3 tposeHipsLocalPos, quaternion tposeHipsLocalRot, float3 authoredCenterEyeLocal,float3 authoredMouthLocal, float3 tposeHeadWorld, float3 tposeRootScale, Transform NamePlate, Transform AvatarScale, Transform MouthTransform,float3 TposedScale,
        NativeArray<quaternion> boneTPoseLocal = default, Transform[] boneTransforms = null)
    {
        if (!sInitialized) Initialize();

        float3 rootWorld = remotePlayerRoot.position;
        float3 ToAvatarLocal(float3 world) => world - rootWorld;

        // Assemble TPose local positions (in avatar-local space)
        float3 tHead = ToAvatarLocal(head.position);
        float3 tNeck = float3.zero;
        float3 tChest = float3.zero;
        float3 tSpine = float3.zero;
        float3 tHips = ToAvatarLocal(hips.position);

        // The eye/mouth pair is resolved entirely in the T-pose root frame, never through world. The
        // head-position subtraction above cannot be used for them: it is a world-axes delta, and the
        // head-carried root frame in the job would rotate it a second time. tposeHeadWorld and the
        // authored points are both root-relative rendered metres (TposeWorld's frame, which is also
        // what the SDK bakes), and tposeRootScale converts the pair into the model units the job's
        // per-frame scale multiply expects.
        float3 tEye = authoredCenterEyeLocal;
        float3 tMouth = authoredMouthLocal;

        // Compute unscaled offsets
        float3 offNeck = tNeck - tHead;
        float3 offChest = tChest - tNeck;
        float3 offSpine = tSpine - tChest;
        float3 offEye = BasisRemoteBoneMath.HeadAnchorOffset(tEye, tposeHeadWorld, tposeRootScale);
        float3 offMouth = BasisRemoteBoneMath.HeadAnchorOffset(tMouth, tposeHeadWorld, tposeRootScale);

        var a = new TposeAndOffsetDataJob
        {
            tposeLocal_unscaled_Neck = tNeck,
            tposeLocal_unscaled_Chest = tChest,
            tposeLocal_unscaled_Spine = tSpine,
            tposeLocal_unscaled_Hips = tHips,
            tposeLocal_unscaled_CenterEye = tEye,
            tposeLocal_unscaled_Mouth = tMouth,

            offsets_unscaled_Neck = offNeck,
            offsets_unscaled_Chest = offChest,
            offsets_unscaled_Spine = offSpine,
            offsets_unscaled_CenterEye = offEye,
            offsets_unscaled_Mouth = offMouth,
             TposeScale = TposedScale
        };

        bool hasBoneSource = boneTransforms != null && boneTPoseLocal.IsCreated;
        bool isUpdate = sInitialized && (uint)key < (uint)sKeyToIndex.Length && sKeyToIndex[key] >= 0;

        // Snapshot the bone TPose rotations for the DEFERRED path only: the source NativeArray is
        // owned by the receiver and may be disposed/recreated on a recalibration before the add
        // commits, so the reference can't be held across the defer. The in-place update below
        // consumes it synchronously and reads the source directly, skipping a SyncBoneCount
        // managed array per avatar swap.
        quaternion[] boneTpose = (hasBoneSource && !isUpdate) ? boneTPoseLocal.ToArray() : null;

        PendingAdd pending = new PendingAdd
        {
            Key = key,
            Authoring = a,
            TposeHeadRot = (quaternion)tposeHead.rotation,
            TposeHipsRot = (quaternion)tposeHips.rotation,
            TposeHipsLocalPos = tposeHipsLocalPos,
            TposeHipsLocalRot = tposeHipsLocalRot,
            Root = remotePlayerRoot,
            Head = head,
            Hips = hips,
            NamePlate = NamePlate,
            AvatarScale = AvatarScale,
            Mouth = MouthTransform,
            BoneTpose = boneTpose,
            BoneTransforms = boneTransforms,
        };

        // Already registered — this is a re-registration (avatar swap, far LOD install, range
        // re-entry), not a join. The row keeps the same index and the same SyncBoneCount skeleton
        // slots, so it is re-pointed at the new transforms in place. The old path removed the
        // entry and re-added it, which cost SyncBoneCount RemoveAtSwapBack calls against
        // sSkeletonBones — the largest TransformAccessArray in the system at SyncBoneCount x
        // players — and measured 11.28ms on a crowded instance.
        //
        // Synchronous rather than queued, for the same reason RemoveRemotePlayer is: the caller's
        // OLD avatar transforms are destroyed at end of frame, and Unity auto-drops destroyed
        // transforms from a TransformAccessArray. Deferring past that would let the TAA shrink out
        // from under the parallel SoA lists. Writing the live replacements now closes that window
        // instead of opening it. CompletePending fences the bone jobs first, exactly as the remove
        // path did.
        if (isUpdate)
        {
            CompletePending();
            ClearPreScheduled();
            // Any add still queued for this key is an older calibration than this one.
            RemovePendingAdd(key);
            if (!CommitUpdateInternal(pending, sKeyToIndex[key], hasBoneSource ? boneTPoseLocal : default))
            {
                // The incoming transforms are unusable, so the row cannot be re-pointed and
                // would otherwise keep pointing at the outgoing avatar's dying hierarchy.
                RemoveRemotePlayerInternal(key);
            }
            return key;
        }

        sPendingAdds.Add(pending);
        return key;
    }

    /// <summary>
    /// Re-points an already-registered row at a new avatar's transforms without touching the
    /// container lengths: same index, same skeleton slots, no add/remove churn and no reshuffle of
    /// sKeyToIndex. Caller must have fenced the bone jobs. Returns false when the incoming
    /// transforms are unusable, in which case nothing was written.
    /// </summary>
    static bool CommitUpdateInternal(in PendingAdd p, int idx, NativeArray<quaternion> boneTpose)
    {
        if (p.Root == null || p.Head == null || p.Hips == null)
        {
            return false;
        }
        if ((uint)idx >= (uint)sAuthoring.Length)
        {
            return false;
        }

        sAuthoring[idx] = p.Authoring;
        sScale[idx] = new RemoteScaleCache();
        sOut[idx] = default;
        sMouthPositions[idx] = default;
        sMouthForwards[idx] = new float3(0f, 0f, 1f);

        sTPoseHeadRot[idx] = p.TposeHeadRot;
        sTPoseHipsRot[idx] = p.TposeHipsRot;
        sTPoseHipsLocalPos[idx] = p.TposeHipsLocalPos;
        sTPoseHipsLocalRot[idx] = p.TposeHipsLocalRot;

        sRoots[idx] = p.Root;
        sHeads[idx] = p.Head;
        sHips[idx] = p.Hips;
        sNamePlate[idx] = p.NamePlate;
        sAvatarScale[idx] = p.AvatarScale;
        sMouth[idx] = p.Mouth;

        int boneCount = BasisBoneRotationCompression.SyncBoneCount;
        int baseIdx = idx * boneCount;
        bool hasBones = p.BoneTransforms != null && boneTpose.IsCreated;
        for (int b = 0; b < boneCount; b++)
        {
            Transform bone = hasBones ? p.BoneTransforms[b] : null;
            int slot = baseIdx + b;
            if (bone != null)
            {
                sSkeletonBones[slot] = bone;
                sSkeletonTpose[slot] = boneTpose[b];
                sSkeletonValid[slot] = 1;
            }
            else
            {
                sSkeletonBones[slot] = sDummyBone;
                sSkeletonTpose[slot] = quaternion.identity;
                sSkeletonValid[slot] = 0;
            }
        }
        // These slots now point at a different avatar's bones; the write-skip cache still
        // describes the outgoing one.
        InvalidateSkeletonCache(baseIdx, boneCount);

        // sKeyToIndex[p.Key] and sKeyArray[idx] already hold this key — the row never moved.
        return true;
    }

    /// <summary>
    /// Commits a queued <see cref="PendingAdd"/> into the SoA/TAA containers. Must run only at a
    /// point where no bone job is reading the containers (see <see cref="DrainPendingAdds"/>).
    /// Skips silently if the avatar's transforms were destroyed before the commit.
    /// </summary>
    static void CommitAddInternal(in PendingAdd p)
    {
        if (p.Root == null || p.Head == null || p.Hips == null)
        {
            return;
        }

        // Already registered (e.g. a duplicate calibration queued the same frame) — the
        // synchronous remove path keeps the live entry; don't append a second copy.
        if ((uint)p.Key < (uint)sKeyToIndex.Length && sKeyToIndex[p.Key] >= 0)
        {
            return;
        }

        int idx = sAuthoring.Length;
        EnsureTaaCapacity(idx + 1);

        sAuthoring.Add(p.Authoring);
        sScale.Add(new RemoteScaleCache());
        sOut.Add(default);
        sMouthPositions.Add(default);
        sMouthForwards.Add(new float3(0f, 0f, 1f));

        sTPoseHeadRot.Add(p.TposeHeadRot);
        sTPoseHipsRot.Add(p.TposeHipsRot);
        sTPoseHipsLocalPos.Add(p.TposeHipsLocalPos);
        sTPoseHipsLocalRot.Add(p.TposeHipsLocalRot);

        sRoots.Add(p.Root);

        sNamePlate.Add(p.NamePlate);
        sAvatarScale.Add(p.AvatarScale);
        sMouth.Add(p.Mouth);

        sHeads.Add(p.Head);
        sHips.Add(p.Hips);

        // Register skeleton bones for the parallel apply job
        int boneCount = BasisBoneRotationCompression.SyncBoneCount;
        if (p.BoneTransforms != null && p.BoneTpose != null)
        {
            for (int b = 0; b < boneCount; b++)
            {
                Transform bone = p.BoneTransforms[b];
                if (bone != null)
                {
                    sSkeletonBones.Add(bone);
                    sSkeletonTpose.Add(p.BoneTpose[b]);
                    sSkeletonValid.Add(1);
                }
                else
                {
                    sSkeletonBones.Add(sDummyBone);
                    sSkeletonTpose.Add(quaternion.identity);
                    sSkeletonValid.Add(0);
                }
            }
        }
        else
        {
            // No bone data provided — fill with dummies
            for (int b = 0; b < boneCount; b++)
            {
                sSkeletonBones.Add(sDummyBone);
                sSkeletonTpose.Add(quaternion.identity);
                sSkeletonValid.Add(0);
            }
        }
        // Fresh slots (or slots a departed player left behind) must not inherit a write-skip
        // cache. Usually redundant — the buffer grows and zero-fills on the next Ensure — but not
        // when the grow-with-slack policy already covers this length.
        InvalidateSkeletonCache(idx * boneCount, boneCount);

        sKeyToIndex[p.Key] = idx;
        EnsureKeyArrayCapacity(idx + 1);
        sKeyArray[idx] = p.Key;
        AuthoringLength = sAuthoring.Length;
    }

    /// <summary>
    /// Unregisters a remote avatar by key. Runs synchronously (completing in-flight bone jobs
    /// first) because the TAA entry must be dropped while the transform is still alive — Unity
    /// auto-removes destroyed transforms from a TransformAccessArray, which would otherwise
    /// desync the parallel SoA lists if removal were deferred past destruction.
    /// </summary>
    /// <param name="key">The external key previously used to add the avatar.</param>
    /// <returns><c>true</c> if found and removed; otherwise <c>false</c>.</returns>
    public static bool RemoveRemotePlayer(int key)
    {
        if (!sInitialized) return false;
        CompletePending();
        ClearPreScheduled();
        RemovePendingAdd(key);
        return RemoveRemotePlayerInternal(key);
    }

    /// <summary>
    /// Unregisters a remote avatar by key, removing it from all SoA containers and TAA sets.
    /// Uses swap-back removal to keep arrays dense. Caller must have completed in-flight bone jobs.
    /// </summary>
    /// <param name="key">The external key previously used to add the avatar.</param>
    /// <returns><c>true</c> if found and removed; otherwise <c>false</c>.</returns>
    static bool RemoveRemotePlayerInternal(int key)
    {
        if ((uint)key >= (uint)sKeyToIndex.Length) return false;
        int idx = sKeyToIndex[key];
        if (idx < 0) return false;

        int last = sAuthoring.Length - 1;
        if (idx != last)
        {
            // Swap-back SoA
            sAuthoring[idx] = sAuthoring[last];
            sScale[idx] = sScale[last];
            sOut[idx] = sOut[last];
            sMouthPositions[idx] = sMouthPositions[last];
            sMouthForwards[idx] = sMouthForwards[last];
            sTPoseHeadRot[idx] = sTPoseHeadRot[last];
            sTPoseHipsRot[idx] = sTPoseHipsRot[last];
            sTPoseHipsLocalPos[idx] = sTPoseHipsLocalPos[last];
            sTPoseHipsLocalRot[idx] = sTPoseHipsLocalRot[last];

            sNamePlate.RemoveAtSwapBack(idx);
            sAvatarScale.RemoveAtSwapBack(idx);
            sMouth.RemoveAtSwapBack(idx);

            sRoots.RemoveAtSwapBack(idx);
            sHeads.RemoveAtSwapBack(idx);
            sHips.RemoveAtSwapBack(idx);

            // O(1) reverse lookup instead of iterating the dictionary
            int movedKey = sKeyArray[last];
            sKeyToIndex[movedKey] = idx;
            sKeyArray[idx] = movedKey;
        }
        else
        {
            sRoots.RemoveAtSwapBack(last);
            sHeads.RemoveAtSwapBack(last);
            sHips.RemoveAtSwapBack(last);

            sNamePlate.RemoveAtSwapBack(last);
            sAvatarScale.RemoveAtSwapBack(last);
            sMouth.RemoveAtSwapBack(last);
        }

        // Swap-back skeleton bone entries (flat: boneCount contiguous entries per player).
        // Strategy: copy last player's block over removed player's block in the NativeLists,
        // then truncate. For the TAA, overwrite slots then remove from the tail.
        int boneCount = BasisBoneRotationCompression.SyncBoneCount;
        int boneIdxStart = idx * boneCount;
        int boneLastStart = last * boneCount;
        if (idx != last)
        {
            // Copy last player's data over the removed player's slots
            for (int b = 0; b < boneCount; b++)
            {
                int dst = boneIdxStart + b;
                int src = boneLastStart + b;
                sSkeletonTpose[dst] = sSkeletonTpose[src];
                sSkeletonValid[dst] = sSkeletonValid[src];
                // TAA: overwrite the removed slot's transform with the last player's transform
                sSkeletonBones[dst] = sSkeletonBones[src];
            }
            // The moved player's bones now live at a different index, where the write-skip cache
            // still holds the departing player's rotations.
            InvalidateSkeletonCache(boneIdxStart, boneCount);
        }
        // Truncate the tail block (last player's entries are now duplicated or are the ones being removed)
        for (int b = boneCount - 1; b >= 0; b--)
        {
            sSkeletonBones.RemoveAtSwapBack(sSkeletonBones.length - 1);
            sSkeletonTpose.RemoveAt(sSkeletonTpose.Length - 1);
            sSkeletonValid.RemoveAt(sSkeletonValid.Length - 1);
        }

        sAuthoring.RemoveAt(last);
        sScale.RemoveAt(last);
        sOut.RemoveAt(last);
        sMouthPositions.RemoveAt(last);
        sMouthForwards.RemoveAt(last);
        sTPoseHeadRot.RemoveAt(last);
        sTPoseHipsRot.RemoveAt(last);
        sTPoseHipsLocalPos.RemoveAt(last);
        sTPoseHipsLocalRot.RemoveAt(last);
        sKeyToIndex[key] = -1;
        // sKeyArray's slot at `last` is now stale, but consumers gate on AuthoringLength
        // (count), so the unused tail slot is harmless. No truncation needed.
        AuthoringLength = sAuthoring.Length;
        return true;
    }

    /// <summary>
    /// Commits all queued registrations. Caller must have completed any in-flight bone jobs first
    /// so the SoA/TAA mutations are safe.
    /// </summary>
    static readonly ProfilerMarker sMarkerCommitAdds = new ProfilerMarker("BasisDriver.Network.CommitAvatarAdds");

    static void DrainPendingAdds()
    {
        int n = sPendingAdds.Count;
        if (n == 0) return;
        // The registration marker on the calibration side only covers QUEUEING an add; the
        // SyncBoneCount TransformAccessArray Adds per avatar actually land here, one frame stage
        // later and under a completely different parent marker. Attributed so a load-in spike
        // isn't split between two places that look unrelated.
        using (sMarkerCommitAdds.Auto())
        {
            for (int i = 0; i < n; i++)
            {
                CommitAddInternal(sPendingAdds[i]);
            }
        }
        sPendingAdds.Clear();
    }

    /// <summary>
    /// Drops any queued (uncommitted) registration for a key, so a player that leaves
    /// before its add commits can't later commit a ghost entry into the SoA/TAA.
    /// </summary>
    static void RemovePendingAdd(int key)
    {
        for (int i = sPendingAdds.Count - 1; i >= 0; i--)
        {
            if (sPendingAdds[i].Key == key)
            {
                sPendingAdds.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Ensures temporary per-frame buffers exist and match the current avatar count.
    /// </summary>
    /// <param name="count">Number of avatars to accommodate.</param>
    static void EnsureTempBuffers(int count)
    {
        if (count <= 0)
        {
            return;
        }

        // Grow-only with slack. An exact-length check reallocated every one of these buffers on any
        // join or leave, because the count changes by one — a free+malloc storm on the main thread
        // inside Schedule() at exactly the moment a crowd is forming. Every consumer is dimensioned
        // by a TransformAccessArray length or an explicit count, never by this array's Length, so
        // trailing slack is simply unused. Shrinks only when massively oversized, to bound the
        // footprint after a large instance drains.
        void AllocOrResize<T>(ref NativeArray<T> arr, int len) where T : struct
        {
            if (arr.IsCreated)
            {
                if (arr.Length < len || arr.Length > len * 4 + 64)
                {
                    arr.Dispose();
                    arr = new NativeArray<T>(GrowCapacity(len), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
            }
            else
            {
                arr = new NativeArray<T>(GrowCapacity(len), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }
        AllocOrResize(ref sTmpRootPos, count);
        AllocOrResize(ref sTmpRootScale, count);
        AllocOrResize(ref sTmpHeadPos, count);
        AllocOrResize(ref sTmpHeadRot, count);
        AllocOrResize(ref sTmpHipsPos, count);
        AllocOrResize(ref sTmpHipsRot, count);
        AllocOrResize(ref sTmpHipsWorldPos, count);
        AllocOrResize(ref sTmpHipsWorldRot, count);
        AllocOrResize(ref sTmpRootDerivedPos, count);
        AllocOrResize(ref sTmpRootDerivedRot, count);
        AllocOrResize(ref sTmpAvatarScales, count);
        AllocOrResize(ref sTmpScaleChanged, count);
    }

    /// <summary>
    /// Disposes temporary per-frame buffers if allocated.
    /// </summary>
    static void DisposeTempBuffers()
    {
        if (sTmpRootPos.IsCreated) sTmpRootPos.Dispose();
        if (sTmpRootScale.IsCreated) sTmpRootScale.Dispose();
        if (sTmpHeadPos.IsCreated) sTmpHeadPos.Dispose();
        if (sTmpHeadRot.IsCreated) sTmpHeadRot.Dispose();
        if (sTmpHipsPos.IsCreated) sTmpHipsPos.Dispose();
        if (sTmpHipsRot.IsCreated) sTmpHipsRot.Dispose();
        if (sTmpHipsWorldPos.IsCreated) sTmpHipsWorldPos.Dispose();
        if (sTmpHipsWorldRot.IsCreated) sTmpHipsWorldRot.Dispose();
        if (sTmpRootDerivedPos.IsCreated) sTmpRootDerivedPos.Dispose();
        if (sTmpRootDerivedRot.IsCreated) sTmpRootDerivedRot.Dispose();
        if (sTmpAvatarScales.IsCreated) sTmpAvatarScales.Dispose();
        if (sTmpScaleChanged.IsCreated) sTmpScaleChanged.Dispose();
    }

    /// <summary>
    /// Ensures all <see cref="TransformAccessArray"/> instances have enough capacity.
    /// </summary>
    /// <param name="needed">Required capacity.</param>
    static void EnsureTaaCapacity(int needed)
    {
        if (sRoots.capacity < needed)
        {
            int newCap = math.max(needed, math.max(4, sRoots.capacity * 2));
            sRoots.capacity = newCap;
            sHeads.capacity = newCap;
            sHips.capacity = newCap;

            sNamePlate.capacity = newCap;
            sAvatarScale.capacity = newCap;
            sMouth.capacity = newCap;
        }

        // sSkeletonBones was absent here while every other TAA was reserved: it takes
        // SyncBoneCount Adds per player with no reserve at all, so past the initial capacity
        // each avatar load walked it into repeated growth reallocations — 54 Adds against the
        // largest array in the system (SyncBoneCount x players). Reserved on the same doubling
        // policy as the rest.
        int skeletonNeeded = needed * BasisBoneRotationCompression.SyncBoneCount;
        if (sSkeletonBones.isCreated && sSkeletonBones.capacity < skeletonNeeded)
        {
            sSkeletonBones.capacity = math.max(skeletonNeeded, sSkeletonBones.capacity * 2);
        }
        if (sSkeletonTpose.IsCreated && sSkeletonTpose.Capacity < skeletonNeeded)
        {
            sSkeletonTpose.Capacity = math.max(skeletonNeeded, sSkeletonTpose.Capacity * 2);
        }
        if (sSkeletonValid.IsCreated && sSkeletonValid.Capacity < skeletonNeeded)
        {
            sSkeletonValid.Capacity = math.max(skeletonNeeded, sSkeletonValid.Capacity * 2);
        }
    }

    /// <summary>
    /// Grows the persistent reverse-key NativeArray, preserving existing entries.
    /// Doubling growth so a steadily increasing avatar count amortises to O(1) per add.
    /// </summary>
    static void EnsureKeyArrayCapacity(int needed)
    {
        if (sKeyArray.IsCreated && sKeyArray.Length >= needed) return;

        int newCap = math.max(needed, sKeyArray.IsCreated ? sKeyArray.Length * 2 : 16);
        var next = new NativeArray<int>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        if (sKeyArray.IsCreated)
        {
            int preserve = math.min(sKeyArray.Length, AuthoringLength);
            if (preserve > 0) NativeArray<int>.Copy(sKeyArray, 0, next, 0, preserve);
            sKeyArray.Dispose();
        }
        sKeyArray = next;
    }

    /// <summary>
    /// Schedules and kicks the root/head/hips gather jobs ahead of <see cref="Schedule"/>, which
    /// consumes the handles the same frame. The gathers read only last-frame transforms — never
    /// the interpolation output or the hips overrides — and use read-only transform scheduling
    /// (no hierarchy sort, no exclusive ownership, main-thread reads stay legal), so they can
    /// overlap the transmit / remote-apply / receiver work that runs between the two calls.
    /// </summary>
    public static void ScheduleGathers()
    {
        if (!sInitialized)
        {
            return;
        }

        // Complete the previous frame's jobs before mutating containers or rescheduling: the
        // safety system would otherwise see the old ApplyMouthJob (reader of sOut) conflict
        // with the new BasisRemoteBoneJob (writer of sOut).
        CompletePending();

        // Safe point for queued registrations — the sync above means no job is reading the
        // containers, so committing avatar-load adds here costs no extra stall.
        DrainPendingAdds();

        ClearPreScheduled();
        if (AuthoringLength == 0)
        {
            return;
        }

        EnsureTempBuffers(AuthoringLength);

        int workerCount = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int gatherBatch = math.max(1, (AuthoringLength + workerCount - 1) / workerCount);

        // Gather root/head/hips
        sGatherRoot = new GatherRootJob
        {
            rootPos = sTmpRootPos,
            rootScale = sTmpRootScale
        }.ScheduleReadOnly(sRoots, gatherBatch);

        sGatherHead = new GatherHeadJob
        {
            headPos = sTmpHeadPos,
            headRot = sTmpHeadRot
        }.ScheduleReadOnly(sHeads, gatherBatch);

        sGatherHips = new GatherHipsJob
        {
            hipsPos = sTmpHipsPos,
            hipsRot = sTmpHipsRot
        }.ScheduleReadOnly(sHips, gatherBatch);

        sPending = JobHandle.CombineDependencies(sGatherRoot, sGatherHead, sGatherHips);
        sGathersScheduled = true;

        JobHandle.ScheduleBatchedJobs();
    }

    /// <summary>
    /// Grow-only with slack: totalBones changes by SyncBoneCount on every join/leave, and an
    /// exact-length check meant disposing and reallocating ~800 KB each time. Both consumers
    /// take an explicit length (totalBones) or run over a TransformAccessArray, so the trailing
    /// slack is never read.
    /// </summary>
    static void EnsureSkeletonRotationBuffer(int totalBones)
    {
        if (!sSkeletonRotations.IsCreated
            || sSkeletonRotations.Length < totalBones
            || sSkeletonRotations.Length > totalBones * 4 + 64)
        {
            if (sSkeletonRotations.IsCreated) sSkeletonRotations.Dispose();
            if (sSkeletonLastWritten.IsCreated) sSkeletonLastWritten.Dispose();
            if (sSkeletonWriteMask.IsCreated) sSkeletonWriteMask.Dispose();

            int capacity = GrowCapacity(totalBones);
            sSkeletonRotations = new NativeArray<quaternion>(capacity, Allocator.Persistent);
            // Zero-filled, so every slot starts at the (0,0,0,0) "no known value" sentinel and the
            // first frame after a resize writes every bone. Losing the cache across a resize is
            // only ever a one-frame cost — a redundant write is always safe, a missed one is not.
            sSkeletonLastWritten = new NativeArray<quaternion>(capacity, Allocator.Persistent);
            sSkeletonWriteMask = new NativeArray<byte>(capacity, Allocator.Persistent);
        }
    }

    /// <summary>
    /// Marks a run of bone slots as having no known transform value, so the next compute pass
    /// writes them unconditionally. Required anywhere a slot is re-pointed at a different
    /// transform (join, avatar swap, swap-back on leave): the cached rotation describes the
    /// OUTGOING transform, and left in place it would suppress the first write to the incoming
    /// one — an avatar frozen in T-pose until it happened to move. Callers must already have
    /// fenced the bone jobs, which every mutation path does.
    /// </summary>
    static void InvalidateSkeletonCache(int start, int count)
    {
        if (!sSkeletonLastWritten.IsCreated) return;
        int end = math.min(start + count, sSkeletonLastWritten.Length);
        for (int i = math.max(0, start); i < end; i++)
        {
            sSkeletonLastWritten[i] = default;
        }
    }

    /// <summary>
    /// Kicks the skeleton delta compose (network bone deltas × cached T-pose locals) as soon as
    /// the interpolation output is readable — i.e. straight after
    /// <c>BasisRemoteNetworkDriver.BeginRead()</c>, AHEAD of the per-receiver loop. It reads
    /// <c>_outBoneRotations</c> and the T-pose cache and writes its own buffer; the receiver loop
    /// touches neither (it writes the skip flags, which only the NEXT frame's Compute reads, the
    /// filtered hips overrides, which only the hips derive reads, and the effector inputs). This
    /// is the largest independent job in the stretch — players × SyncBoneCount elements — so it
    /// is what actually fills the window instead of leaving workers parked.
    ///
    /// Requires the gathers to have been pre-scheduled: that call owns DrainPendingAdds, and
    /// without it Schedule()'s fallback could resize sSkeletonTpose under the in-flight job.
    /// </summary>
    public static void ScheduleSkeletonCompute(int maxBatchSize = 64)
    {
        sSkeletonComputeScheduled = false;
        if (!sInitialized || !sGathersScheduled)
        {
            return;
        }

        int totalBones = sSkeletonTpose.Length;
        if (totalBones == 0)
        {
            return;
        }

        EnsureSkeletonRotationBuffer(totalBones);

        int workerCount = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int boneBatch = math.max(1, math.min(maxBatchSize, (totalBones + workerCount - 1) / workerCount));

        sSkeletonCompute = BasisRemoteNetworkDriver.ScheduleComputeSkeletonRotations(
            sKeyArray, totalBones, BasisBoneRotationCompression.SyncBoneCount,
            sSkeletonTpose.AsDeferredJobArray(), sSkeletonValid.AsDeferredJobArray(),
            sSkeletonRotations, sSkeletonLastWritten, sSkeletonWriteMask,
            boneBatch);

        sSkeletonComputeBones = totalBones;
        sPending = JobHandle.CombineDependencies(sPending, sSkeletonCompute);
        sSkeletonComputeScheduled = true;

        JobHandle.ScheduleBatchedJobs();
    }

    /// <summary>
    /// Kicks the hips fan-out + root derive at the earliest point it is legal: straight after the
    /// per-receiver loop. That loop is a genuine ordering constraint — <c>SetFilteredHipsOverride</c>
    /// writes the very <c>_filteredPositions</c> slots this job reads (see the seat/vehicle path) —
    /// but nothing after it is, so waiting for Schedule() was pure main-thread latency.
    /// </summary>
    public static void ScheduleHipsDerive()
    {
        sHipsDeriveScheduled = false;
        if (!sInitialized || !sGathersScheduled || AuthoringLength == 0)
        {
            return;
        }

        sHipsDerive = BasisRemoteNetworkDriver.ScheduleBulkCopyHipsAndDerive(
            sKeyArray, AuthoringLength,
            sTPoseHipsLocalPos.AsDeferredJobArray(), sTPoseHipsLocalRot.AsDeferredJobArray(),
            sTmpHipsWorldPos, sTmpHipsWorldRot,
            sTmpAvatarScales, sTmpScaleChanged,
            sTmpRootDerivedPos, sTmpRootDerivedRot);

        sPending = JobHandle.CombineDependencies(sPending, sHipsDerive);
        sHipsDeriveScheduled = true;

        JobHandle.ScheduleBatchedJobs();
    }

    /// <summary>
    /// Schedules the entire simulation pipeline for the current set of avatars:
    /// gather → simulate → apply (nameplate/mouth/hips/skeleton).
    /// </summary>
    /// <param name="maxBatchSize">
    /// Upper cap on <c>innerloopBatchCount</c> for the per-avatar IJobParallelFor.
    /// The actual batch is <c>min(maxBatchSize, ceil(AuthoringLength / workerCount))</c>
    /// so the job spreads across all worker threads instead of running on one.
    /// </param>
    /// <returns>The final <see cref="JobHandle"/> for dependency chaining.</returns>
    public static JobHandle Schedule(int maxBatchSize = 64)
    {
        if (!sInitialized)
        {
            return default;
        }

        if (!sGathersScheduled)
        {
            ScheduleGathers();
        }
        if (!sGathersScheduled)
        {
            return default;
        }
        sGathersScheduled = false;

        // Taken as locals and cleared up front so no pre-scheduled handle can survive this call
        // and be consumed a second time against a later frame's data.
        bool havePreScheduledSkeleton = sSkeletonComputeScheduled;
        bool havePreScheduledHipsDerive = sHipsDeriveScheduled;
        sSkeletonComputeScheduled = false;
        sHipsDeriveScheduled = false;

        // sKeyArray is maintained directly by Add/RemoveRemotePlayer, so no per-frame snapshot
        // is needed. The previous code copied each entry out of a managed List<int> via
        // List<T>.get_Item, which was the dominant cost of Schedule().

        var hRoot = sGatherRoot;
        var hHead = sGatherHead;
        var hHips = sGatherHips;
        var gathers = JobHandle.CombineDependencies(hRoot, hHead, hHips);

        // Adaptive batch size — the whole point is to actually use multiple cores.
        // With a fixed batchSize of 64 and ~50 avatars, IJobParallelFor packs the
        // entire job into one chunk and one worker thread runs the lot.
        // Divide the work by JobWorkerCount so each core gets a slice; cap at the
        // caller-supplied maxBatchSize for very large avatar counts where per-batch
        // dispatch overhead would otherwise add up.
        int workerCount = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int simBatch = math.max(1, math.min(maxBatchSize,
            (AuthoringLength + workerCount - 1) / workerCount));

        // Run bone simulation directly off the gather outputs (SoA→AoS aggregation
        // is folded into BasisRemoteBoneJob to remove a job dispatch from the
        // critical path).
        var BoneSimulation = new BasisRemoteBoneJob
        {
            Authoring = sAuthoring.AsDeferredJobArray(),
            RootPos = sTmpRootPos,
            RootScale = sTmpRootScale,
            HeadPos = sTmpHeadPos,
            HeadRot = sTmpHeadRot,
            HipsPos = sTmpHipsPos,
            HipsRot = sTmpHipsRot,
            TposeHeadRot = sTPoseHeadRot.AsDeferredJobArray(),
            TposeHipsRot = sTPoseHipsRot.AsDeferredJobArray(),
            GeneratedScales = sScale.AsDeferredJobArray(),
            Out = sOut.AsDeferredJobArray(),
            MouthPositions = sMouthPositions.AsDeferredJobArray(),
            MouthForwards = sMouthForwards.AsDeferredJobArray()
        }.Schedule(AuthoringLength, simBatch, gathers);

        // ── One Burst dispatch fans out ALL per-player network state and
        //    derives the root pose in a single sequential pass:
        //      • copies filtered hips world pos/rot
        //      • copies scale + scale-change flag
        //      • reads hips local deltas inline (no temp round-trip)
        //      • reads cached TPose hips local pos/rot
        //      • computes the derived root pose via the conjugate inverse math
        //    Replaces what used to be three separate dispatches (bulk hips+scale,
        //    bulk hips deltas, derive-root). At thousand-player scale this
        //    saves dispatch overhead and keeps each player's data hot in cache.
        //    Normally already in flight — ScheduleHipsDerive kicked it right after the receiver
        //    loop, which is the earliest point its input (the filtered hips overrides) is final.
        //    The inline path covers direct callers that never pre-scheduled.
        JobHandle bulkAndDeriveJob;
        if (havePreScheduledHipsDerive)
        {
            bulkAndDeriveJob = sHipsDerive;
        }
        else
        {
            bulkAndDeriveJob = BasisRemoteNetworkDriver.ScheduleBulkCopyHipsAndDerive(
                sKeyArray, AuthoringLength,
                sTPoseHipsLocalPos.AsDeferredJobArray(), sTPoseHipsLocalRot.AsDeferredJobArray(),
                sTmpHipsWorldPos, sTmpHipsWorldRot,
                sTmpAvatarScales, sTmpScaleChanged,
                sTmpRootDerivedPos, sTmpRootDerivedRot);
        }

        JobHandle.ScheduleBatchedJobs();

        // Apply pass — parallel branches.
        //
        //   rootAndScaleJob ─┬─> nameplateJob
        //                    ├─> mouthJob
        //                    └─> hipsWorldJob
        //   skeletonJob (independent)
        //
        // Avatar scale and the root pos/rot apply write the SAME transform — see
        // BasisRemoteAvatarDriver's AddRemotePlayer call, which passes `animatorRoot` as both
        // `remotePlayerRoot` and `AvatarScale` — so they used to be two serialised dispatches over
        // one transform per player. Merged into ApplyRootAndScaleJob: one dispatch, one dependency
        // stage. Scale is written before the pose inside that job, which is what descendants
        // (mouth, nameplate) need — SetPositionAndRotation bakes the parent lossyScale into the
        // child's localPosition, so a later scale change would shift the child's world position.
        // Skeleton writes only localRotation and is unaffected.
        //
        // hRoot is combined for the TAA safety system (GatherRootJob read sRoots this frame).
        var rootApplyDeps = JobHandle.CombineDependencies(bulkAndDeriveJob, hRoot);
        var rootAndScaleJob = new ApplyRootAndScaleJob
        {
            Scales = sTmpAvatarScales,
            HasScaleChange = sTmpScaleChanged,
            Positions = sTmpRootDerivedPos,
            Rotations = sTmpRootDerivedRot,
        }.Schedule(sRoots, rootApplyDeps);

        // Skeleton: one compute pass (ComputeSkeletonRotationsFromNetworkJob — gathers each
        // player's filtered bone deltas from the network slots and multiplies by the cached
        // T-pose locals) feeding a transform-write pass (ApplySkeletonRotationsJob). The
        // gather+multiply used to be two dispatches round-tripping a packed delta buffer;
        // reading the network array in the compute job drops both. The compute pass has no job
        // dep — it runs concurrently with scale / sim / hips / mouth / nameplate.
        JobHandle skeletonJob = default;
        int totalBones = sSkeletonTpose.Length;
        if (totalBones > 0)
        {
            // Normally already in flight — ScheduleSkeletonCompute kicked it right after
            // BeginRead(), so it has been running across the whole receiver loop. The inline path
            // covers direct callers, and the bone-count check discards a pre-scheduled handle
            // whose layout no longer matches (a join/leave landed in between).
            JobHandle computeRotationsJob;
            if (havePreScheduledSkeleton && sSkeletonComputeBones == totalBones)
            {
                computeRotationsJob = sSkeletonCompute;
            }
            else
            {
                EnsureSkeletonRotationBuffer(totalBones);

                // Adaptive batch — same reasoning as BoneSimulation: a fixed batch leaves
                // small bone counts running on a single worker.
                int boneBatch = math.max(1, math.min(maxBatchSize,
                    (totalBones + workerCount - 1) / workerCount));

                computeRotationsJob = BasisRemoteNetworkDriver.ScheduleComputeSkeletonRotations(
                    sKeyArray, totalBones, BasisBoneRotationCompression.SyncBoneCount,
                    sSkeletonTpose.AsDeferredJobArray(), sSkeletonValid.AsDeferredJobArray(),
                    sSkeletonRotations, sSkeletonLastWritten, sSkeletonWriteMask,
                    boneBatch);
            }

            skeletonJob = new ApplySkeletonRotationsJob
            {
                Rotations = sSkeletonRotations,
                WriteMask = sSkeletonWriteMask,
            }.Schedule(sSkeletonBones, computeRotationsJob);
        }

        Vector3 CameraPosition = BasisLocalCameraDriver.Position;
        // Descendants of the root: they need the root's FINAL scale and pose in place before their
        // own SetPositionAndRotation, since Unity derives their localPosition from the parent's
        // current world matrix. Previously these hung off the scale-only job, which left them free
        // to race the root pose write on the same hierarchy.
        var simAndRoot = JobHandle.CombineDependencies(BoneSimulation, rootAndScaleJob);

        var nameplateJob = new MappedNameplateApplyJob
        {
            CameraPosition = CameraPosition,
            NamePlateIn = sOut.AsDeferredJobArray(),
        }.Schedule(sNamePlate, simAndRoot);

        var mouthJob = new ApplyMouthJob
        {
            MouthRotation = sOut.AsDeferredJobArray(),
        }.Schedule(sMouth, simAndRoot);

        // Hips world apply: writes hips.world.position/rotation directly via
        // SetPositionAndRotation. Hierarchy-agnostic — Unity walks the actual
        // parent chain so any Armature/intermediate node's transform is handled
        // automatically. Must run AFTER rootAndScaleJob: SetPositionAndRotation
        // computes hips.localPosition from the current parent (root) pose, so
        // root must be in its final per-frame state first. Also depends on
        // hHips (sHips TAA was read by GatherHipsJob) and on skeletonJob if
        // present (defensive; sSkeletonBones doesn't include hips).
        var hipsWorldDeps = JobHandle.CombineDependencies(hHips, rootAndScaleJob);
        if (totalBones > 0) hipsWorldDeps = JobHandle.CombineDependencies(hipsWorldDeps, skeletonJob);
        var hipsWorldJob = new ApplyHipsWorldJob
        {
            Positions = sTmpHipsWorldPos,
            Rotations = sTmpHipsWorldRot,
        }.Schedule(sHips, hipsWorldDeps);

        // End-effector IK: read the FK-posed bone worlds, two-bone-IK the anchored limbs onto their sent
        // targets, and write the resulting local rotations back — all on workers, chained off the skeleton
        // + hips apply and folded into `pending` so it completes at the tail with everything else.
        JobHandle effectorIkJob = default;
        if (BasisNetworkReceiver.EndEffectorIKEnabled && BasisRemoteNetworkDriver.AnyEffectorAnchored && totalBones > 0)
        {
            if (!sIkReadPos.IsCreated || sIkReadPos.Length != totalBones)
            {
                if (sIkReadPos.IsCreated) sIkReadPos.Dispose();
                if (sIkReadWorldRot.IsCreated) sIkReadWorldRot.Dispose();
                if (sIkReadLocalRot.IsCreated) sIkReadLocalRot.Dispose();
                if (sIkOverrideRot.IsCreated) sIkOverrideRot.Dispose();
                if (sIkOverrideMask.IsCreated) sIkOverrideMask.Dispose();
                sIkReadPos = new NativeArray<float3>(totalBones, Allocator.Persistent);
                sIkReadWorldRot = new NativeArray<quaternion>(totalBones, Allocator.Persistent);
                sIkReadLocalRot = new NativeArray<quaternion>(totalBones, Allocator.Persistent);
                sIkOverrideRot = new NativeArray<quaternion>(totalBones, Allocator.Persistent);
                sIkOverrideMask = new NativeArray<byte>(totalBones, Allocator.Persistent);
            }

            var readPoseJob = new ReadBonePoseJob
            {
                PlayerKeys = sKeyArray,
                EffMask = BasisRemoteNetworkDriver.EffectorMaskArray,
                ReadPos = sIkReadPos,
                ReadWorldRot = sIkReadWorldRot,
                ReadLocalRot = sIkReadLocalRot,
                OverrideMask = sIkOverrideMask,
                BoneCount = BasisBoneRotationCompression.SyncBoneCount,
                CapacityFixed = BasisRemoteNetworkDriver.FixedCapacity,
            }.ScheduleReadOnly(sSkeletonBones,
                math.max(1, math.min(maxBatchSize, (totalBones + workerCount - 1) / workerCount)),
                JobHandle.CombineDependencies(hipsWorldJob, skeletonJob));

            int ikBatch = math.max(1, math.min(maxBatchSize, (AuthoringLength + workerCount - 1) / workerCount));
            var ikComputeJob = BasisRemoteNetworkDriver.ScheduleComputeEffectorIK(
                sKeyArray, AuthoringLength, BasisBoneRotationCompression.SyncBoneCount,
                sTmpHipsWorldPos, sTmpHipsWorldRot,
                sIkReadPos, sIkReadWorldRot, sIkReadLocalRot, sSkeletonValid.AsDeferredJobArray(),
                sIkOverrideRot, sIkOverrideMask, ikBatch, readPoseJob);

            effectorIkJob = new WriteBoneRotationJob
            {
                OverrideRot = sIkOverrideRot,
                OverrideMask = sIkOverrideMask,
                LastWritten = sSkeletonLastWritten,
            }.Schedule(sSkeletonBones, ikComputeJob);
        }

        var pending = JobHandle.CombineDependencies(nameplateJob, mouthJob, rootAndScaleJob);
        pending = JobHandle.CombineDependencies(pending, hipsWorldJob);
        pending = JobHandle.CombineDependencies(pending, skeletonJob);
        pending = JobHandle.CombineDependencies(pending, effectorIkJob);
        sPending = pending;

        // Kick the queued batch so workers start now and overlap the main-thread simulate
        // work between here and CompleteRemoteBoneJobSystemJobs at the tail of the tick.
        JobHandle.ScheduleBatchedJobs();
        return pending;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Counts how many bone slots the last compute pass flagged for an actual transform write, out
    /// of every slot <see cref="ApplySkeletonRotationsJob"/> iterated. The gap between the two is
    /// what the write-skip saved. Editor/profiler only, and only valid once the bone jobs have
    /// completed — call it from the same place the other bone-job profiler data is sampled.
    /// </summary>
    public static void SampleSkeletonWriteStats(out int written, out int total)
    {
        written = 0;
        total = 0;
        if (!sInitialized || !sSkeletonWriteMask.IsCreated) return;

        total = math.min(sSkeletonTpose.Length, sSkeletonWriteMask.Length);
        for (int i = 0; i < total; i++)
        {
            if (sSkeletonWriteMask[i] != 0) written++;
        }
    }
#endif

    /// <summary>
    /// Completes a provided handle and any internally pending chain.
    /// </summary>
    /// <param name="handle">The job handle to complete.</param>
    public static void Complete(JobHandle handle)
    {
        handle.Complete();
        if (!sInitialized) return;

        CompletePending();
    }

    /// <summary>
    /// Retrieves the computed outgoing/world mouth position for an avatar by key.
    /// </summary>
    /// <param name="key">Avatar key used when adding the player.</param>
    /// <param name="outgoing">On success, the mouth world position; otherwise <see cref="Vector3.zero"/>.</param>
    /// <returns><c>true</c> if the key is found; otherwise <c>false</c>.</returns>
    public static unsafe bool GetOutGoingMouth(int key, out float3 outgoing)
    {
        if ((uint)key >= (uint)sKeyToIndex.Length)
        {
            outgoing = float3.zero;
            return false;
        }
        int idx = sKeyToIndex[key];
        if (idx < 0)
        {
            outgoing = float3.zero;
            return false;
        }
        outgoing = ((float3*)sMouthPositions.GetUnsafeReadOnlyPtr())[idx];
        return true;
    }
    /// <summary>
    /// Retrieves the computed mouth facing direction for an avatar by key. Paired
    /// with <see cref="GetOutGoingMouth"/> — voice directivity needs both.
    /// </summary>
    /// <param name="key">Avatar key used when adding the player.</param>
    /// <param name="forward">On success, the unit mouth forward; otherwise world forward.</param>
    /// <returns><c>true</c> if the key is found; otherwise <c>false</c>.</returns>
    public static unsafe bool GetOutGoingMouthForward(int key, out float3 forward)
    {
        if ((uint)key >= (uint)sKeyToIndex.Length)
        {
            forward = new float3(0f, 0f, 1f);
            return false;
        }
        int idx = sKeyToIndex[key];
        if (idx < 0)
        {
            forward = new float3(0f, 0f, 1f);
            return false;
        }
        forward = ((float3*)sMouthForwards.GetUnsafeReadOnlyPtr())[idx];
        return true;
    }
    /// <summary>
    /// Resolves an avatar key to its dense index in <c>sOut</c> without reading the frame.
    /// Used by callers that hand the index to a Burst job so the job can read frames directly.
    /// Returns <c>false</c> when the system isn't initialized yet or the key is unknown.
    /// </summary>
    public static bool TryGetSOutIndex(int key, out int idx)
    {
        if (!sInitialized || sKeyToIndex == null || (uint)key >= (uint)sKeyToIndex.Length) { idx = -1; return false; }
        idx = sKeyToIndex[key];
        return idx >= 0;
    }

    /// <summary>
    /// Exposes the playerId→dense <c>sOut</c> index map for bulk inline resolution in hot
    /// selection loops, avoiding a <see cref="TryGetSOutIndex"/> call per player. The array is
    /// indexed by ushort playerId; entries are -1 when absent. Read-only — do not mutate.
    /// Returns null when the system isn't initialized. Valid for the current frame only.
    /// </summary>
    public static int[] GetSOutIndexMap() => sInitialized ? sKeyToIndex : null;

    /// <summary>
    /// Returns <c>sOut</c> as a NativeArray for Burst-job consumption.
    /// Returns a default (uncreated) NativeArray when the system isn't initialized yet —
    /// callers must check <see cref="NativeArray{T}.IsCreated"/> before scheduling a
    /// reader job. Only valid for the current frame; NativeList memory may move on
    /// Add/RemoveAt so the array must be re-acquired each frame.
    /// </summary>
    public static NativeArray<RemoteFrameOutput> GetRemoteFrameArray()
    {
        if (!sInitialized || !sOut.IsCreated) return default;
        // sOut is written by BasisRemoteBoneJob, which is still in flight when the gaze selector
        // reads it mid-LateUpdate. Complete the pipeline before exposing sOut as a NativeArray —
        // remote players are now separate transform roots, so touching local transforms no longer
        // force-syncs these jobs the way a shared DeviceManagement root used to.
        CompletePending();
        return sOut.AsArray();
    }

    /// <summary>
    /// Returns the computed outgoing/world center-eye position and rotation for an avatar by key.
    /// </summary>
    /// <param name="key">Avatar key used when adding the player.</param>
    /// <param name="position">On success, the center-eye world position.</param>
    /// <param name="rotation">On success, the center-eye world rotation.</param>
    /// <returns><c>true</c> if the key is found; otherwise <c>false</c>.</returns>
    public static unsafe bool GetOutGoingCenterEye(int key, out float3 position, out quaternion rotation)
    {
        if ((uint)key >= (uint)sKeyToIndex.Length)
        {
            position = default;
            rotation = default;
            return false;
        }
        int idx = sKeyToIndex[key];
        if (idx < 0)
        {
            position = default;
            rotation = default;
            return false;
        }
        // Pointer access avoids NativeList<T>.get_Item — that indexer carries
        // bounds + safety-handle overhead that shows up under the gaze loop's
        // hot path when iterating many remote players.
        var frame = ((RemoteFrameOutput*)Unity.Collections.LowLevel.Unsafe.NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(sOut))[idx];
        position = frame.pos_CenterEye;
        rotation = frame.rot_CenterEye;
        return true;
    }
}
