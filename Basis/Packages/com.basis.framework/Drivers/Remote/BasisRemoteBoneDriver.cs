using Basis.Network.Core.Compression;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
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
/// Per-frame world-space inputs and precomputed rotations/scales consumed by the solver.
/// </summary>
public struct GeneratedTranslationalData
{
    /// <summary>World-space root position used as the avatar-local origin baseline.</summary>
    public float3 rootWorld;
    /// <summary>Head world position.</summary>
    public float3 headWPos;
    /// <summary>Hips world position.</summary>
    public float3 hipsWPos;
    /// <summary>Head world rotation.</summary>
    public quaternion headWRot;
    /// <summary>Hips world rotation.</summary>
    public quaternion hipsWRot;
    /// <summary>TPose-space reference rotation for the head.</summary>
    public quaternion tposeHeadRot;
    /// <summary>TPose-space reference rotation for the hips.</summary>
    public quaternion tposeHipsRot;
    /// <summary>Current world scale derived from the root transform.</summary>
    public float3 nowScale;
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
/// Core remote bone job: scales authoring offsets, composes head/hips transforms,
/// computes derived joint positions, and writes a <see cref="RemoteFrameOutput"/>.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct BasisRemoteBoneJob : IJobParallelFor
{
    /// <summary>Authoring-time TPose and offset data (unscaled).</summary>
    [ReadOnly] public NativeArray<TposeAndOffsetDataJob> Authoring;
    /// <summary>Per-frame inputs (root/head/hips/tpose quats/scales).</summary>
    [ReadOnly] public NativeArray<GeneratedTranslationalData> In;
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
        var f = In[i];
        var sc = GeneratedScales[i];

        // Scale TPose + offsets by current world scale
        sc.tposeLocal_scaled_Hips = a.tposeLocal_unscaled_Hips * f.nowScale;
        sc.tposeLocal_scaled_Mouth = a.tposeLocal_unscaled_Mouth * f.nowScale;
        sc.offsets_scaled_Neck = a.offsets_unscaled_Neck * f.nowScale;
        sc.offsets_scaled_Chest = a.offsets_unscaled_Chest * f.nowScale;
        sc.offsets_scaled_Spine = a.offsets_unscaled_Spine * f.nowScale;
        sc.offsets_scaled_CenterEye = a.offsets_unscaled_CenterEye * f.nowScale;
        sc.offsets_scaled_Mouth = a.offsets_unscaled_Mouth * f.nowScale;
        GeneratedScales[i] = sc;

        // Compose world rotations (TPose→current)
        quaternion headR = math.mul(f.headWRot, f.tposeHeadRot);
        quaternion hipsR = math.mul(f.tposeHipsRot, f.hipsWRot);

        // Convert to avatar-local positions relative to rootWorld
        float3 headP = f.headWPos - f.rootWorld;
        float3 hipsP = f.hipsWPos - f.rootWorld;

        // Forward chain from head using headR and scaled offsets
        float3 neckP = headP + math.mul(headR, sc.offsets_scaled_Neck);
        float3 chestP = neckP + math.mul(headR, sc.offsets_scaled_Chest);
        float3 spineP = chestP + math.mul(headR, sc.offsets_scaled_Spine);
        float3 eyeP = headP + math.mul(headR, sc.offsets_scaled_CenterEye);
        float3 mouthP = headP + math.mul(headR, sc.offsets_scaled_Mouth);


        float3 difference = SafeDivide(f.nowScale, a.TposeScale);

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
/// Aggregates all gathered transform samples and t-pose quaternions into
/// a single per-avatar struct used by the main bone simulation.
/// </summary>
[BurstCompile]
struct AgrigateTranslationalData : IJobParallelFor
{
    /// <summary>Root world positions.</summary>
    [ReadOnly] public NativeArray<float3> rootPos;
    /// <summary>Root lossy scales.</summary>
    [ReadOnly] public NativeArray<float3> rootScale;
    /// <summary>Head world positions.</summary>
    [ReadOnly] public NativeArray<float3> headPos;
    /// <summary>Head world rotations.</summary>
    [ReadOnly] public NativeArray<quaternion> headRot;
    /// <summary>Hips world positions.</summary>
    [ReadOnly] public NativeArray<float3> hipsPos;
    /// <summary>Hips world rotations.</summary>
    [ReadOnly] public NativeArray<quaternion> hipsRot;
    /// <summary>TPose head quaternions.</summary>
    [ReadOnly] public NativeArray<quaternion> tposeHeadRot;
    /// <summary>TPose hips quaternions.</summary>
    [ReadOnly] public NativeArray<quaternion> tposeHipsRot;

    /// <summary>Combined output to be consumed by <see cref="BasisRemoteBoneJob"/>.</summary>
    [WriteOnly]
    public NativeArray<GeneratedTranslationalData> InOut;

    /// <summary>Aggregates inputs into a single SoA element.</summary>
    public void Execute(int i)
    {
        InOut[i] = new GeneratedTranslationalData
        {
            rootWorld = rootPos[i],
            headWPos = headPos[i],
            hipsWPos = hipsPos[i],
            headWRot = headRot[i],
            hipsWRot = hipsRot[i],
            tposeHeadRot = tposeHeadRot[i],
            tposeHipsRot = tposeHipsRot[i],
            nowScale = rootScale[i]
        };
    }
}

/// <summary>
/// Burst job that composes T-pose local rotation with network delta and writes to bone transforms.
/// Runs across ALL remote players' bones in a single flat TransformAccessArray for maximum parallelism.
/// </summary>
[BurstCompile]
public struct ApplySkeletonRotationsJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<quaternion> TposeLocal;
    [ReadOnly] public NativeArray<quaternion> FilteredDeltas;
    [ReadOnly] public NativeArray<byte> ValidMask;

    public void Execute(int index, TransformAccess transform)
    {
        if (ValidMask[index] == 0) return;
        transform.localRotation = math.mul(TposeLocal[index], FilteredDeltas[index]);
    }
}

/// <summary>
/// Burst job that writes hips world position and rotation for all remote players.
/// Uses the existing sHips TransformAccessArray (1 entry per player).
/// </summary>
[BurstCompile]
public struct ApplyHipsJob : IJobParallelForTransform
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
    /// <summary>Per-frame inputs per avatar.</summary>
    static NativeList<GeneratedTranslationalData> sIn;
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
    /// <summary>Dummy transform for null bone slots in the TAA.</summary>
    static Transform sDummyBone;

    // Temp per-frame buffers (reused)
    /// <summary>Temp root positions.</summary>
    static NativeArray<float3> sTmpRootPos, sTmpHeadPos, sTmpHipsPos;
    /// <summary>Temp root scales.</summary>
    static NativeArray<float3> sTmpRootScale;
    /// <summary>Temp head rotations.</summary>
    static NativeArray<quaternion> sTmpHeadRot, sTmpHipsRot;

    // Hips + scale job data (populated from BasisRemoteNetworkDriver each frame)
    static NativeArray<float3> sTmpHipsWorldPos;
    static NativeArray<quaternion> sTmpHipsWorldRot;
    static NativeArray<float3> sTmpAvatarScales;
    static NativeArray<byte> sTmpScaleChanged;

    // Bookkeeping
    /// <summary>Map from external key → internal SoA index. Flat array indexed by ushort player ID; -1 = absent.</summary>
    static int[] sKeyToIndex;
    /// <summary>Reverse map: internal SoA index → external key. Used for O(1) swap-back removal.</summary>
    static readonly List<int> sIndexToKey = new List<int>();
    /// <summary>Cached array snapshot of sIndexToKey for bounds-check-free indexing in Schedule.</summary>
    static int[] sKeyArray = System.Array.Empty<int>();
    /// <summary>Pending job handle chain.</summary>
    static JobHandle sPending;
    /// <summary>Initialization flag.</summary>
    static bool sInitialized;
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
        sIn = new NativeList<GeneratedTranslationalData>(initialCapacity, Allocator.Persistent);
        sScale = new NativeList<RemoteScaleCache>(initialCapacity, Allocator.Persistent);
        sOut = new NativeList<RemoteFrameOutput>(initialCapacity, Allocator.Persistent);
        sMouthPositions = new NativeList<float3>(initialCapacity, Allocator.Persistent);

        sTPoseHeadRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);
        sTPoseHipsRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);

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
        sIndexToKey.Clear();

        sInitialized = true;
    }

    /// <summary>
    /// Disposes all persistent containers and temp buffers, and clears bookkeeping.
    /// </summary>
    public static void Dispose()
    {
        CompletePending();

        if (sAuthoring.IsCreated) sAuthoring.Dispose();
        if (sIn.IsCreated) sIn.Dispose();
        if (sScale.IsCreated) sScale.Dispose();
        if (sOut.IsCreated) sOut.Dispose();
        if (sMouthPositions.IsCreated) sMouthPositions.Dispose();

        if (sTPoseHeadRot.IsCreated) sTPoseHeadRot.Dispose();
        if (sTPoseHipsRot.IsCreated) sTPoseHipsRot.Dispose();

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
        if (sDummyBone != null) { UnityEngine.Object.Destroy(sDummyBone.gameObject); sDummyBone = null; }

        DisposeTempBuffers();

        if (sKeyToIndex != null) Array.Fill(sKeyToIndex, -1);
        sIndexToKey.Clear();
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
    public static int AddRemotePlayer(int key, Transform remotePlayerRoot, Transform head, Transform hips,BasisCalibratedCoords tposeHead, BasisCalibratedCoords tposeHips, float3 authoredCenterEyeWorld,float3 authoredMouthWorld, Transform NamePlate, Transform AvatarScale, Transform MouthTransform,float3 TposedScale,
        NativeArray<quaternion> boneTPoseLocal = default, Transform[] boneTransforms = null)
    {
        if (!sInitialized) Initialize();
        CompletePending();

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

        int idx = sAuthoring.Length;
        EnsureTaaCapacity(idx + 1);

        sAuthoring.Add(a);
        sIn.Add(default);
        sScale.Add(new RemoteScaleCache());
        sOut.Add(default);
        sMouthPositions.Add(default);

        sTPoseHeadRot.Add((quaternion)tposeHead.rotation);
        sTPoseHipsRot.Add((quaternion)tposeHips.rotation);

        sRoots.Add(remotePlayerRoot);

        sNamePlate.Add(NamePlate);
        sAvatarScale.Add(AvatarScale);
        sMouth.Add(MouthTransform);

        sHeads.Add(head);
        sHips.Add(hips);

        // Register skeleton bones for the parallel apply job
        int boneCount = BasisBoneRotationCompression.SyncBoneCount;
        if (boneTransforms != null && boneTPoseLocal.IsCreated)
        {
            for (int b = 0; b < boneCount; b++)
            {
                Transform bone = boneTransforms[b];
                if (bone != null)
                {
                    sSkeletonBones.Add(bone);
                    sSkeletonTpose.Add(boneTPoseLocal[b]);
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

        sKeyToIndex[key] = idx;
        sIndexToKey.Add(key);
        AuthoringLength = sAuthoring.Length;
        return key;
    }

    /// <summary>
    /// Unregisters a remote avatar by key, removing it from all SoA containers and TAA sets.
    /// Uses swap-back removal to keep arrays dense.
    /// </summary>
    /// <param name="key">The external key previously used to add the avatar.</param>
    /// <returns><c>true</c> if found and removed; otherwise <c>false</c>.</returns>
    public static bool RemoveRemotePlayer(int key)
    {
        if (!sInitialized) return false;
        CompletePending();

        if ((uint)key >= (uint)sKeyToIndex.Length) return false;
        int idx = sKeyToIndex[key];
        if (idx < 0) return false;

        int last = sAuthoring.Length - 1;
        if (idx != last)
        {
            // Swap-back SoA
            sAuthoring[idx] = sAuthoring[last];
            sIn[idx] = sIn[last];
            sScale[idx] = sScale[last];
            sOut[idx] = sOut[last];
            sMouthPositions[idx] = sMouthPositions[last];
            sTPoseHeadRot[idx] = sTPoseHeadRot[last];
            sTPoseHipsRot[idx] = sTPoseHipsRot[last];

            sNamePlate.RemoveAtSwapBack(idx);
            sAvatarScale.RemoveAtSwapBack(idx);
            sMouth.RemoveAtSwapBack(idx);

            sRoots.RemoveAtSwapBack(idx);
            sHeads.RemoveAtSwapBack(idx);
            sHips.RemoveAtSwapBack(idx);

            // O(1) reverse lookup instead of iterating the dictionary
            int movedKey = sIndexToKey[last];
            sKeyToIndex[movedKey] = idx;
            sIndexToKey[idx] = movedKey;
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
        sIn.RemoveAt(last);
        sScale.RemoveAt(last);
        sOut.RemoveAt(last);
        sMouthPositions.RemoveAt(last);
        sTPoseHeadRot.RemoveAt(last);
        sTPoseHipsRot.RemoveAt(last);
        sKeyToIndex[key] = -1;
        sIndexToKey.RemoveAt(last);
        AuthoringLength = sAuthoring.Length;
        return true;
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
    /// Schedules the entire simulation pipeline for the current set of avatars:
    /// gather → aggregate → simulate → apply (nameplate/mouth).
    /// </summary>
    /// <param name="batchSize">Job batch size for parallel loops.</param>
    /// <returns>The final <see cref="JobHandle"/> for dependency chaining.</returns>
    public static JobHandle Schedule(int batchSize = 64)
    {
        if (!sInitialized)
        {
            return default;
        }
        if (AuthoringLength == 0)
        {
            return default;
        }

        // Complete any still-pending jobs from the previous frame so the safety
        // system doesn't complain about the old ApplyMouthJob (reader of sOut)
        // conflicting with the new BasisRemoteBoneJob (writer of sOut).
        CompletePending();

        // Snapshot the key list into a plain array for bounds-check-free indexing.
        // List<T>.get_Item has per-access bounds checks that the JIT cannot elide;
        // array indexing in a counted loop is optimized away.
        if (sKeyArray.Length < AuthoringLength)
        {
            sKeyArray = new int[Unity.Mathematics.math.max(AuthoringLength, 16)];
        }

        sIndexToKey.CopyTo(sKeyArray);

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

        var deps = JobHandle.CombineDependencies(hRoot, hHead, hHips);

        // Aggregate into per-avatar input
        var combine = new AgrigateTranslationalData
        {
            rootPos = sTmpRootPos,
            rootScale = sTmpRootScale,
            headPos = sTmpHeadPos,
            headRot = sTmpHeadRot,
            hipsPos = sTmpHipsPos,
            hipsRot = sTmpHipsRot,
            tposeHeadRot = sTPoseHeadRot.AsDeferredJobArray(),
            tposeHipsRot = sTPoseHipsRot.AsDeferredJobArray(),
            InOut = sIn.AsDeferredJobArray()
        }.Schedule(AuthoringLength, batchSize, deps);

        // Run bone simulation
        var BoneSimulation = new BasisRemoteBoneJob
        {
            Authoring = sAuthoring.AsDeferredJobArray(),
            In = sIn.AsDeferredJobArray(),
            GeneratedScales = sScale.AsDeferredJobArray(),
            Out = sOut.AsDeferredJobArray(),
            MouthPositions = sMouthPositions.AsDeferredJobArray()
        }.Schedule(AuthoringLength, batchSize, combine);

        // Apply outputs
        Vector3 CameraPosition = BasisLocalCameraDriver.Position;

        var MappedNameplateApplyJob = new MappedNameplateApplyJob
        {
            CameraPosition = CameraPosition,
            NamePlateIn = sOut.AsDeferredJobArray(),
        }.Schedule(sNamePlate, BoneSimulation);

        var ApplyMouthJob = new ApplyMouthJob
        {
            MouthRotation = sOut.AsDeferredJobArray(),
        }.Schedule(sMouth, MappedNameplateApplyJob);

        // ── Bulk-copy hips position/rotation and scale from the network driver ──
        // One unsafe bulk copy replaces per-player main-thread SetPositionAndRotation.
        BasisRemoteNetworkDriver.BulkCopyHipsAndScale(
            sKeyArray, AuthoringLength,
            sTmpHipsWorldPos, sTmpHipsWorldRot,
            sTmpAvatarScales, sTmpScaleChanged);

        // Avatar scale must run BEFORE ApplyHipsJob: SetPositionAndRotation bakes
        // the parent lossyScale into the hips' localPosition. If we change the
        // avatar root scale after that, the hips world Y shifts by the scale delta
        // (remote avatar renders slightly low).
        var scaleApplyJob = new ApplyAvatarScaleJob
        {
            Scales = sTmpAvatarScales,
            HasChange = sTmpScaleChanged,
        }.Schedule(sAvatarScale, ApplyMouthJob);

        // Hips position + rotation (1 per player, parallel via TAA)
        var hipsApplyJob = new ApplyHipsJob
        {
            Positions = sTmpHipsWorldPos,
            Rotations = sTmpHipsWorldRot,
        }.Schedule(sHips, scaleApplyJob);

        // Schedule skeleton bone rotation apply job (all players' bones in parallel).
        JobHandle skeletonJob = hipsApplyJob;
        int totalBones = sSkeletonTpose.Length;
        if (totalBones > 0)
        {
            // Ensure deltas buffer matches current size
            if (!sSkeletonDeltas.IsCreated || sSkeletonDeltas.Length != totalBones)
            {
                if (sSkeletonDeltas.IsCreated) sSkeletonDeltas.Dispose();
                sSkeletonDeltas = new NativeArray<quaternion>(totalBones, Allocator.Persistent);
            }

            // Bulk-copy filtered bone deltas from the network driver for all players.
            // Uses UnsafeUtility.MemCpy per player instead of per-element indexing
            // to avoid NativeArray safety check overhead (was 13% of main thread).
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;
            int bytesPerPlayer = boneCount * UnsafeUtility.SizeOf<quaternion>();
            unsafe
            {
                quaternion* dstBase = (quaternion*)sSkeletonDeltas.GetUnsafePtr();
                for (int p = 0; p < AuthoringLength; p++)
                {
                    int playerKey = sKeyArray[p];
                    quaternion* src = BasisRemoteNetworkDriver.GetFilteredBoneRotationsPtr(playerKey);
                    if (src != null)
                    {
                        UnsafeUtility.MemCpy(
                            dstBase + p * boneCount,
                            src,
                            bytesPerPlayer);
                    }
                }
            }

            skeletonJob = new ApplySkeletonRotationsJob
            {
                TposeLocal = sSkeletonTpose.AsDeferredJobArray(),
                FilteredDeltas = sSkeletonDeltas,
                ValidMask = sSkeletonValid.AsDeferredJobArray(),
            }.Schedule(sSkeletonBones, hipsApplyJob);
        }

        sPending = skeletonJob;
        return skeletonJob;
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
    /// Returns the computed outgoing/world center-eye position and rotation for an avatar by key.
    /// </summary>
    /// <param name="key">Avatar key used when adding the player.</param>
    /// <param name="position">On success, the center-eye world position.</param>
    /// <param name="rotation">On success, the center-eye world rotation.</param>
    /// <returns><c>true</c> if the key is found; otherwise <c>false</c>.</returns>
    public static bool GetOutGoingCenterEye(int key, out float3 position, out quaternion rotation)
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
        var frame = sOut[idx];
        position = frame.pos_CenterEye;
        rotation = frame.rot_CenterEye;
        return true;
    }
}
