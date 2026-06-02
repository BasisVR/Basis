using Basis.Network.Core.Compression;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.UI.NamePlate;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
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
    /// <summary>Per-avatar visual nameplate scale multiplier derived from current avatar height.</summary>
    public float NamePlateScaleMultiplier;
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
    private const float NamePlateMinimumHeightOffset = 0.4f;
    private const float NamePlateShrinkStartHeight = 0.5f;
    private const float NamePlateMinimumScaleMultiplier = 0.5f;
    private const float NamePlateGrowStartHeight = 2f;
    // Cap tall-avatar growth so the nameplate stays readable without becoming unreasonably large.
    private const float MaxNamePlateScaleMultiplier = 3f;

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

        // Compose world rotations (TPose→current)
        quaternion headR = math.mul(HeadRot[i], TposeHeadRot[i]);
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
        float3 neckP = headP + math.mul(headR, sc.offsets_scaled_Neck);
        float3 chestP = neckP + math.mul(headR, sc.offsets_scaled_Chest);
        float3 spineP = chestP + math.mul(headR, sc.offsets_scaled_Spine);
        float3 eyeP = headP + math.mul(headR, sc.offsets_scaled_CenterEye);
        float3 mouthP = headP + math.mul(headR, sc.offsets_scaled_Mouth);


        float3 difference = SafeDivide(nowScale, a.TposeScale);

        float rawNamePlateHeight = difference.y * 1.2f;

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


            // Keep tiny avatars readable without changing developer-branch behavior for avatars at or above 2m.
            HeightAvatarHipCoord = rawNamePlateHeight < NamePlateGrowStartHeight ? math.max(rawNamePlateHeight, NamePlateMinimumHeightOffset) : rawNamePlateHeight,
            NamePlateScaleMultiplier = ComputeNamePlateScaleMultiplier(rawNamePlateHeight),
        };
        MouthPositions[i] = mouthP;
    }

    private static float ComputeNamePlateScaleMultiplier(float namePlateHeight)
    {
        if (namePlateHeight < NamePlateShrinkStartHeight)
        {
            float t = math.saturate(namePlateHeight / NamePlateShrinkStartHeight);
            return math.lerp(NamePlateMinimumScaleMultiplier, 1f, t);
        }

        if (namePlateHeight > NamePlateGrowStartHeight)
        {
            // Preserve linear growth above the tall-avatar threshold, but stop before giant avatars overwhelm the UI.
            return math.min(1f + (namePlateHeight - NamePlateGrowStartHeight), MaxNamePlateScaleMultiplier);
        }

        return 1f;
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
    /// <summary>User-configured base local scale before per-avatar height compensation.</summary>
    public float NamePlateBaseScale;

    /// <summary>Input pose data (per-avatar) for nameplate placement.</summary>
    [ReadOnly] public NativeArray<RemoteFrameOutput> NamePlateIn;

    /// <summary>Computes position above hips and rotates toward camera.</summary>
    public void Execute(int jobIndex, TransformAccess tx)
    {
        var data = NamePlateIn[jobIndex];
        float3 hips = data.pos_Hips;

        float3 nameplatePos = new float3(hips.x, hips.y + data.HeightAvatarHipCoord, hips.z);

        // Face the camera (yaw only) with zero-distance guard.
        float3 toCam = CameraPosition - nameplatePos;
        float2 xz = new float2(toCam.x, toCam.z);
        float yaw = math.lengthsq(xz) > 1e-12f ? math.atan2(xz.x, xz.y) : 0f;
        quaternion rot = quaternion.RotateY(yaw);

        tx.SetPositionAndRotation(nameplatePos, rot);
        float localScale = NamePlateBaseScale * data.NamePlateScaleMultiplier;
        tx.localScale = new Vector3(localScale, localScale, localScale);
    }
}

/// <summary>
/// Burst IJobParallelFor that composes T-pose local rotation with the network delta into
/// a final localRotation per bone. Splitting this off the transform-write path lets the
/// math fully spread across worker threads — IJobParallelForTransform serializes bones
/// inside each root hierarchy (51 bones per avatar on a single core), which made the
/// combined apply job the heaviest in the pipeline.
/// </summary>
[BurstCompile]
public struct ComputeSkeletonRotationsJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<quaternion> TposeLocal;
    [ReadOnly] public NativeArray<quaternion> FilteredDeltas;
    [WriteOnly] public NativeArray<quaternion> Rotations;

    public void Execute(int index)
    {
        Rotations[index] = math.mul(TposeLocal[index], FilteredDeltas[index]);
    }
}

/// <summary>
/// Burst job that writes precomputed bone localRotations to transforms. Now does only the
/// transform side of the work — the quaternion multiply lives in <see cref="ComputeSkeletonRotationsJob"/>.
/// Runs across ALL remote players' bones in a single flat TransformAccessArray.
/// </summary>
[BurstCompile]
public struct ApplySkeletonRotationsJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<quaternion> Rotations;
    [ReadOnly] public NativeArray<byte> ValidMask;

    public void Execute(int index, TransformAccess transform)
    {
        if (ValidMask[index] == 0) return;
        transform.localRotation = Rotations[index];
    }
}

/// <summary>
/// Burst job that writes the avatar root's world position and rotation for all remote
/// players. Uses the sRoots TransformAccessArray (1 entry per player). Inputs are the
/// derived root pose computed inline by <c>BulkCopyHipsAndDeriveJob</c> from the
/// network's hips world pose + the local hips deltas — root rides along so the
/// hips bone lands exactly at the high-precision world pose we received.
/// </summary>
[BurstCompile]
public struct ApplyRootJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<float3> Positions;
    [ReadOnly] public NativeArray<quaternion> Rotations;

    public void Execute(int index, TransformAccess transform)
    {
        transform.SetPositionAndRotation(Positions[index], Rotations[index]);
    }
}

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
/// Burst job that writes avatar scale for all remote players.
/// Uses the sAvatarScale TransformAccessArray (1 entry per player).
/// </summary>
[BurstCompile]
public struct ApplyAvatarScaleJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<float3> Scales;
    [ReadOnly] public NativeArray<byte> HasChange;

    public void Execute(int index, TransformAccess transform)
    {
        if (HasChange[index] != 0)
        {
            transform.localScale = Scales[index];
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
    /// <summary>Filtered deltas copied from BasisRemoteNetworkDriver each frame.</summary>
    static NativeArray<quaternion> sSkeletonDeltas;
    /// <summary>Precomputed local rotations (TposeLocal × FilteredDeltas) consumed by <see cref="ApplySkeletonRotationsJob"/>.</summary>
    static NativeArray<quaternion> sSkeletonRotations;
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
    // scale. Input to ApplyRootJob. Computed such that
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

        if (sAuthoring.IsCreated) sAuthoring.Dispose();
        if (sScale.IsCreated) sScale.Dispose();
        if (sOut.IsCreated) sOut.Dispose();
        if (sMouthPositions.IsCreated) sMouthPositions.Dispose();

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
        if (sSkeletonDeltas.IsCreated) sSkeletonDeltas.Dispose();
        if (sSkeletonRotations.IsCreated) sSkeletonRotations.Dispose();
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
    /// Registers a remote avatar into the job system and returns the same key for convenience.
    /// Computes authoring TPose data/offsets in avatar-local space and caches TPose quats.
    /// </summary>
    /// <param name="key">External key identifying the avatar.</param>
    /// <param name="remotePlayerRoot">Avatar root transform.</param>
    /// <param name="head">Head transform.</param>
    /// <param name="hips">Hips/root transform.</param>
    /// <param name="tposeHead">Head TPose calibrated coordinates.</param>
    /// <param name="tposeHips">Hips TPose calibrated coordinates.</param>
    /// <param name="authoredCenterEyeWorld">Center-eye world position from authoring.</param>
    /// <param name="authoredMouthWorld">Mouth world position from authoring.</param>
    /// <param name="NamePlate">Nameplate transform to be driven.</param>
    /// <param name="AvatarScale">Transform used for avatar scaling (if any).</param>
    /// <param name="MouthTransform">Mouth transform to be driven.</param>
    /// <returns>The provided <paramref name="key"/>.</returns>
    public static int AddRemotePlayer(int key, Transform remotePlayerRoot, Transform head, Transform hips,BasisCalibratedCoords tposeHead, BasisCalibratedCoords tposeHips, float3 tposeHipsLocalPos, quaternion tposeHipsLocalRot, float3 authoredCenterEyeWorld,float3 authoredMouthWorld, Transform NamePlate, Transform AvatarScale, Transform MouthTransform,float3 TposedScale,
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
        float3 tEye = ToAvatarLocal(authoredCenterEyeWorld);
        float3 tMouth = ToAvatarLocal(authoredMouthWorld);

        // Compute unscaled offsets
        float3 offNeck = tNeck - tHead;
        float3 offChest = tChest - tNeck;
        float3 offSpine = tSpine - tChest;
        float3 offEye = tEye - tHead;
        float3 offMouth = tMouth - tHead;

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

        // Snapshot the bone TPose rotations: the source NativeArray is owned by the
        // receiver and may be disposed/recreated on a recalibration before this op is
        // committed, so the reference can't be held across the defer.
        quaternion[] boneTpose = (boneTransforms != null && boneTPoseLocal.IsCreated)
            ? boneTPoseLocal.ToArray()
            : null;

        sPendingAdds.Add(new PendingAdd
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
        });
        return key;
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
    static void DrainPendingAdds()
    {
        int n = sPendingAdds.Count;
        if (n == 0) return;
        for (int i = 0; i < n; i++)
        {
            CommitAddInternal(sPendingAdds[i]);
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

        void AllocOrResize<T>(ref NativeArray<T> arr, int len) where T : struct
        {
            if (arr.IsCreated)
            {
                if (arr.Length != len)
                {
                    arr.Dispose();
                    arr = new NativeArray<T>(len, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
            }
            else
            {
                arr = new NativeArray<T>(len, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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

        // Complete the previous frame's jobs before mutating containers or rescheduling: the
        // safety system would otherwise see the old ApplyMouthJob (reader of sOut) conflict
        // with the new BasisRemoteBoneJob (writer of sOut).
        CompletePending();

        // Safe point for queued registrations — the sync above means no job is reading the
        // containers, so committing avatar-load adds here costs no extra stall.
        DrainPendingAdds();

        if (AuthoringLength == 0)
        {
            return default;
        }

        // sKeyArray is maintained directly by Add/RemoveRemotePlayer, so no per-frame snapshot
        // is needed. The previous code copied each entry out of a managed List<int> via
        // List<T>.get_Item, which was the dominant cost of Schedule().

        EnsureTempBuffers(AuthoringLength);

        // Gather root/head/hips
        var hRoot = new GatherRootJob
        {
            rootPos = sTmpRootPos,
            rootScale = sTmpRootScale
        }.Schedule(sRoots);

        var hHead = new GatherHeadJob
        {
            headPos = sTmpHeadPos,
            headRot = sTmpHeadRot
        }.Schedule(sHeads);

        var hHips = new GatherHipsJob
        {
            hipsPos = sTmpHipsPos,
            hipsRot = sTmpHipsRot
        }.Schedule(sHips);

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
            MouthPositions = sMouthPositions.AsDeferredJobArray()
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
        var bulkAndDeriveJob = BasisRemoteNetworkDriver.ScheduleBulkCopyHipsAndDerive(
            sKeyArray, AuthoringLength,
            sTPoseHipsLocalPos.AsDeferredJobArray(), sTPoseHipsLocalRot.AsDeferredJobArray(),
            sTmpHipsWorldPos, sTmpHipsWorldRot,
            sTmpAvatarScales, sTmpScaleChanged,
            sTmpRootDerivedPos, sTmpRootDerivedRot);

        // Apply pass — parallel branches.
        //
        //   scaleApplyJob ─┬─> nameplateJob
        //                  ├─> mouthJob
        //                  └─> rootApplyJob ─> hipsWorldJob
        //   skeletonJob (independent)
        //
        // Avatar scale and the root pos/rot apply both write the avatar root transform
        // (sAvatarScale and sRoots are the same Animator.transform), so they must be
        // ordered. Scale also has to run BEFORE any SetPositionAndRotation on a
        // descendant of the root (mouth, nameplate): SetPositionAndRotation bakes the
        // parent lossyScale into the child's localPosition. If scale changed afterward,
        // the child's world position would shift by the scale delta. Skeleton writes
        // only localRotation and is unaffected.
        var scaleApplyJob = new ApplyAvatarScaleJob
        {
            Scales = sTmpAvatarScales,
            HasChange = sTmpScaleChanged,
        }.Schedule(sAvatarScale, bulkAndDeriveJob);

        // Skeleton: split into a fully parallel compute pass + a transform-write pass.
        //
        // ComputeSkeletonRotationsJob (IJobParallelFor) does the math.mul across all
        // 51×AuthoringLength bones with an adaptive batch size, so the multiplies
        // saturate every worker. ApplySkeletonRotationsJob (IJobParallelForTransform)
        // then writes the precomputed quaternions to the bone transforms; that pass
        // is hierarchy-bound (51 bones per avatar are sequential within one worker),
        // but it now does only a memory write, not a quaternion multiply.
        //
        // Inputs (sSkeletonTpose / sSkeletonValid / sSkeletonDeltas) are all filled
        // on the main thread above. The compute pass has no job dep — it runs
        // concurrently with scale / sim / hips / mouth / nameplate.
        JobHandle skeletonJob = default;
        int totalBones = sSkeletonTpose.Length;
        if (totalBones > 0)
        {
            // Ensure deltas + rotations buffers match current size
            if (!sSkeletonDeltas.IsCreated || sSkeletonDeltas.Length != totalBones)
            {
                if (sSkeletonDeltas.IsCreated) sSkeletonDeltas.Dispose();
                sSkeletonDeltas = new NativeArray<quaternion>(totalBones, Allocator.Persistent);
            }
            if (!sSkeletonRotations.IsCreated || sSkeletonRotations.Length != totalBones)
            {
                if (sSkeletonRotations.IsCreated) sSkeletonRotations.Dispose();
                sSkeletonRotations = new NativeArray<quaternion>(totalBones, Allocator.Persistent);
            }

            // Schedule the bone-delta bulk copy on a worker so it doesn't block main
            // thread and adds an extra in-flight job for the scheduler to dispatch.
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;
            var bulkSkeletonJob = BasisRemoteNetworkDriver.ScheduleBulkCopySkeletonDeltas(
                sKeyArray, AuthoringLength,
                sSkeletonDeltas, boneCount);

            // Adaptive batch — same reasoning as BoneSimulation: a fixed batch leaves
            // small bone counts running on a single worker.
            int boneBatch = math.max(1, math.min(maxBatchSize,
                (totalBones + workerCount - 1) / workerCount));

            var computeRotationsJob = new ComputeSkeletonRotationsJob
            {
                TposeLocal = sSkeletonTpose.AsDeferredJobArray(),
                FilteredDeltas = sSkeletonDeltas,
                Rotations = sSkeletonRotations,
            }.Schedule(totalBones, boneBatch, bulkSkeletonJob);

            skeletonJob = new ApplySkeletonRotationsJob
            {
                Rotations = sSkeletonRotations,
                ValidMask = sSkeletonValid.AsDeferredJobArray(),
            }.Schedule(sSkeletonBones, computeRotationsJob);
        }

        Vector3 CameraPosition = BasisLocalCameraDriver.Position;
        var simAndScale = JobHandle.CombineDependencies(BoneSimulation, scaleApplyJob);

        var nameplateJob = new MappedNameplateApplyJob
        {
            CameraPosition = CameraPosition,
            NamePlateBaseScale = BasisRemoteNamePlateDriver.BaseNamePlateLocalScale * BasisRemoteNamePlateDriver.NamePlateSize,
            NamePlateIn = sOut.AsDeferredJobArray(),
        }.Schedule(sNamePlate, simAndScale);

        var mouthJob = new ApplyMouthJob
        {
            MouthRotation = sOut.AsDeferredJobArray(),
        }.Schedule(sMouth, simAndScale);

        // Root pose is already derived (inline inside bulkAndDeriveJob), so
        // we just write it to the root TAA. scaleApplyJob writes the same
        // transform (sAvatarScale and sRoots are the same Animator transform),
        // so it must finish first; hRoot is combined for the TAA safety system.
        var rootApplyDeps = JobHandle.CombineDependencies(scaleApplyJob, hRoot);
        var rootApplyJob = new ApplyRootJob
        {
            Positions = sTmpRootDerivedPos,
            Rotations = sTmpRootDerivedRot,
        }.Schedule(sRoots, rootApplyDeps);

        // Hips world apply: writes hips.world.position/rotation directly via
        // SetPositionAndRotation. Hierarchy-agnostic — Unity walks the actual
        // parent chain so any Armature/intermediate node's transform is handled
        // automatically. Must run AFTER rootApplyJob: SetPositionAndRotation
        // computes hips.localPosition from the current parent (root) pose, so
        // root must be in its final per-frame state first. Also depends on
        // hHips (sHips TAA was read by GatherHipsJob) and on skeletonJob if
        // present (defensive; sSkeletonBones doesn't include hips).
        var hipsWorldDeps = JobHandle.CombineDependencies(hHips, rootApplyJob);
        if (totalBones > 0) hipsWorldDeps = JobHandle.CombineDependencies(hipsWorldDeps, skeletonJob);
        var hipsWorldJob = new ApplyHipsWorldJob
        {
            Positions = sTmpHipsWorldPos,
            Rotations = sTmpHipsWorldRot,
        }.Schedule(sHips, hipsWorldDeps);

        var pending = JobHandle.CombineDependencies(nameplateJob, mouthJob, rootApplyJob);
        pending = JobHandle.CombineDependencies(pending, hipsWorldJob);
        pending = JobHandle.CombineDependencies(pending, skeletonJob);
        sPending = pending;
        return pending;
    }

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
    /// Returns <c>sOut</c> as a NativeArray for Burst-job consumption.
    /// Returns a default (uncreated) NativeArray when the system isn't initialized yet —
    /// callers must check <see cref="NativeArray{T}.IsCreated"/> before scheduling a
    /// reader job. Only valid for the current frame; NativeList memory may move on
    /// Add/RemoveAt so the array must be re-acquired each frame.
    /// </summary>
    public static NativeArray<RemoteFrameOutput> GetRemoteFrameArray()
        => sInitialized && sOut.IsCreated ? sOut.AsArray() : default;

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
