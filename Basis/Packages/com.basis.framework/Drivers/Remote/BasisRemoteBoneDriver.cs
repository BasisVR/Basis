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
    /// Hips → nameplate-bottom distance for THIS avatar, in model units (the frame the per-frame
    /// solve multiplies by the live root scale). Measured once at registration from the avatar's own
    /// crown — see <see cref="Basis.Scripts.UI.NamePlate.BasisNamePlateAnchorMath"/>, which also
    /// documents the fixed 1.2 m constant this replaced and why that constant read as a different
    /// bug on every body plan.
    /// </summary>
    public float NamePlateHeightAboveHips;

    /// <summary>
    /// The avatar root's lossy scale divided by its own localScale — i.e. everything the parent
    /// chain contributes, which is invariant while the avatar lives. The network drives the root's
    /// localScale, so the root's world scale is <c>networkScale × this</c>. Baked instead of read
    /// back off the localToWorldMatrix each frame, which was GatherRootJob's only live output.
    /// </summary>
    public float3 RootParentScale;
}

/// <summary>
/// One step of the baked hips→head chain that lets the head world pose be composed from data the
/// pipeline already owns, instead of read back off the transform a frame late.
///
/// Each link covers exactly one SYNCED bone plus any static nodes above it (twist/roll bones,
/// Armature-style intermediates), folded into a single offset + pre-rotation. That keeps the walk
/// as short as the number of synced bones in the chain — 5 on a standard humanoid (Spine, Chest,
/// UpperChest, Neck, Head, which are BONE_WRITE_ORDER slots 0..4) — no matter how many extra nodes
/// a rig puts between them.
/// </summary>
public struct HeadChainLink
{
    /// <summary>Translation to this link's bone, expressed in the previous link's rotated frame and
    /// before the accumulated scale is applied.</summary>
    public float3 Offset;
    /// <summary>Product of the folded static nodes' local rotations. Applied before this link's
    /// network-driven rotation; identity when the link has no folded nodes.</summary>
    public quaternion PreRot;
    /// <summary>Product of the folded nodes' local scales and this bone's own, carried down the chain.</summary>
    public float3 ScaleMul;
    /// <summary>Skeleton slot whose decoded rotation drives this link, or -1 when the link is
    /// entirely static (nothing on the remote apply path writes it).</summary>
    public int Slot;
}

/// <summary>
/// Per-avatar header for the baked head chain.
/// </summary>
public struct HeadChainHeader
{
    /// <summary>The hips' world scale divided by the root's localScale — the invariant part of the
    /// hierarchy between the two, so the hips' runtime world scale is <c>networkScale × this</c>.
    /// Seeds the scale accumulator the chain walk carries.</summary>
    public float3 HipsScalePerRootLocal;
    /// <summary>Number of valid links in this avatar's block.</summary>
    public int Length;
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
    /// Distance from the hips to the bottom edge of the nameplate, in world metres at the avatar's
    /// live scale. Add it to <see cref="pos_Hips"/>.y (plus the plate's own half-height, which is
    /// viewer-scaled, not avatar-scaled) to get the plate's world Y —
    /// <see cref="Basis.Scripts.UI.NamePlate.BasisNamePlateAnchorMath.AnchorWorldY"/> is that sum.
    /// </summary>
    public float NamePlateHeightAboveHips;
}

/// <summary>
/// Core remote bone job: scales authoring offsets, composes head/hips transforms, computes derived
/// joint positions, and writes a <see cref="RemoteFrameOutput"/>. Reads no transforms — every input
/// is produced by BulkCopyHipsAndDeriveJob, which fans out the network's hips pose and scale and
/// forward-kinematics the head off them. Folds in the SoA→AoS aggregation step, so the whole
/// pre-apply chain is one dependency deep.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct BasisRemoteBoneJob : IJobParallelFor
{
    /// <summary>Authoring-time TPose and offset data (unscaled).</summary>
    [ReadOnly] public NativeArray<TposeAndOffsetDataJob> Authoring;

    // Per-player inputs, all dense (index i = this job's index) and all written by
    // BulkCopyHipsAndDeriveJob. Hips pose and scale are the very values the applies below push to
    // the transforms — the hips/root gathers were reading our own writes back a frame later — and
    // the head pose is the chain FK that replaced the head gather.
    [ReadOnly] public NativeArray<float3> HeadPos;
    [ReadOnly] public NativeArray<quaternion> HeadRot;
    [ReadOnly] public NativeArray<float3> HipsPos;
    [ReadOnly] public NativeArray<quaternion> HipsRot;
    [ReadOnly] public NativeArray<float3> AvatarScale;
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
        // Root world scale = the localScale the network drives × whatever the parent chain
        // contributes (baked at registration). Same number GatherRootJob derived from
        // localToWorldMatrix, without the transform read.
        float3 nowScale = AvatarScale[i] * a.RootParentScale;
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
        // world positions directly.
        float3 headP = HeadPos[i];
        float3 hipsP = HipsPos[i];

        // Forward chain from head using headR and scaled offsets — already in
        // world space because headP is world.
        BasisRemoteBoneMath.ComposeHeadChain(headP, headR,
            sc.offsets_scaled_Neck, sc.offsets_scaled_Chest, sc.offsets_scaled_Spine, sc.offsets_scaled_CenterEye, sc.offsets_scaled_Mouth,
            out float3 neckP, out float3 chestP, out float3 spineP, out float3 eyeP, out float3 mouthP);

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

            // Nameplate height. The stored value is model units — the avatar's own measured
            // hips→crown distance plus its clearance gap — so a live resize is the same single
            // multiply the eye/mouth anchors take, and an avatar nobody resized still gets its own
            // height rather than a constant.
            NamePlateHeightAboveHips = a.NamePlateHeightAboveHips * nowScale.y,
        };
        MouthPositions[i] = mouthP;
        MouthForwards[i] = math.mul(headR, new float3(0f, 0f, 1f));
    }
}

// GatherRootJob / GatherHeadJob / GatherHipsJob are gone. All three were transform read-backs of
// state this system had already produced, one frame stale by construction:
//   • hips world pose  — exactly what ApplyHipsWorldJob wrote from the network SoA
//   • root world scale — exactly what ApplyRootAndScaleJob wrote, times the (baked) parent chain
//   • root world pos   — read into a BasisRemoteBoneJob field nothing ever used
//   • head world pose  — now forward-kinematic'd from the hips pose and the decoded bone rotations
//                        inside BulkCopyHipsAndDeriveJob, via the chain baked at registration
// That removes three IJobParallelForTransform dispatches, the sHeads TAA, and the last
// transform-read dependency in front of the bone sim.

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
/// Height comes from the avatar's own measured crown (see
/// <see cref="Basis.Scripts.UI.NamePlate.BasisNamePlateAnchorMath"/>), not a shared constant.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct MappedNameplateApplyJob : IJobParallelForTransform
{
    /// <summary>Camera world position used to bill-board the plate (yaw-only).</summary>
    public float3 CameraPosition;

    /// <summary>
    /// Half the plate's rendered height, in world metres. The plate's origin is the centre of its
    /// panel, so without this the bottom half of every plate hangs below the point we aim it at.
    /// It is a per-frame uniform rather than part of the per-avatar measurement because plate size
    /// tracks the VIEWER's scale and nameplate-size setting.
    /// </summary>
    public float PanelHalfHeightWorld;

    /// <summary>Input pose data (per-avatar) for nameplate placement.</summary>
    [ReadOnly] public NativeArray<RemoteFrameOutput> NamePlateIn;

    /// <summary>SoA slot → player ID, used to look this slot's plate up in <see cref="PlateActive"/>.</summary>
    [ReadOnly] public NativeArray<int> PlayerKeys;

    /// <summary>Per-player-ID nameplate active mirror; 0 = the plate is not being displayed.</summary>
    [ReadOnly] public NativeArray<byte> PlateActive;

    /// <summary>Computes position above hips and rotates toward camera.</summary>
    public void Execute(int jobIndex, TransformAccess tx)
    {
        int key = PlayerKeys[jobIndex];
        if ((uint)key >= (uint)PlateActive.Length || PlateActive[key] == 0) return;

        var data = NamePlateIn[jobIndex];
        float3 hips = data.pos_Hips;

        float3 nameplatePos = new float3(
            hips.x,
            Basis.Scripts.UI.NamePlate.BasisNamePlateAnchorMath.AnchorWorldY(hips.y, data.NamePlateHeightAboveHips, PanelHalfHeightWorld),
            hips.z);

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
    /// <summary>
    /// Generic→rig decode operators for the HIPS rotation, per avatar:
    /// <c>hips.localRotation = sHipsDecodePre[i] * networkHipsRotation * sHipsDecodePost[i]</c>.
    /// Hips is excluded from the bone packet and rides in the packet tail, but it is carried in
    /// the same rig-neutral space as the bone block, so it needs the same pair. Built from the
    /// avatar's own TposeLocal[Hips] and TposeFromRoot[Hips] — see
    /// <see cref="Basis.Network.Core.Compression.BasisGenericBoneRotation"/>.
    /// </summary>
    static NativeList<quaternion> sHipsDecodePre;
    /// <summary>Right factor of the pair above; see <see cref="sHipsDecodePre"/>.</summary>
    static NativeList<quaternion> sHipsDecodePost;

    // Transform access arrays (roots / hips). Both write-only now — nothing on this path reads a
    // transform back.
    /// <summary>Root transforms per avatar.</summary>
    static TransformAccessArray sRoots;
    /// <summary>Hips transforms per avatar.</summary>
    static TransformAccessArray sHips;

    // ─── Baked hips→head chain (replaces the head transform read-back) ───
    /// <summary>Chain links, flat with a fixed <see cref="HeadChainStride"/> block per avatar.</summary>
    static NativeList<HeadChainLink> sHeadChain;
    /// <summary>Per-avatar chain length + base scale, parallel to the SoA.</summary>
    static NativeList<HeadChainHeader> sHeadChainHeader;
    /// <summary>Per-avatar stride in <see cref="sHeadChain"/>. A humanoid spine chain uses 5; the
    /// slack absorbs rigs that sync extra bones between hips and head.</summary>
    public const int HeadChainStride = 8;

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
    /// <summary>
    /// Left factor of the generic→rig decode, flat parallel to sSkeletonBones:
    /// <c>localRotation = sSkeletonDecodePre[i] * networkRotation * sSkeletonDecodePost[i]</c>.
    /// Built per avatar from that avatar's own rest pose — see
    /// <see cref="Basis.Network.Core.Compression.BasisGenericBoneRotation"/>. With an identity rest
    /// frame the pair reduces to (T-pose local, identity), i.e. the plain T-pose × delta compose
    /// this used to be.
    /// </summary>
    static NativeList<quaternion> sSkeletonDecodePre;
    /// <summary>Right factor of the pair above; see <see cref="sSkeletonDecodePre"/>.</summary>
    static NativeList<quaternion> sSkeletonDecodePost;
    /// <summary>Valid mask (1 = bone exists, 0 = null/skip), flat parallel to sSkeletonBones.</summary>
    static NativeList<byte> sSkeletonValid;
    /// <summary>Precomputed local rotations (decode operators × network rotation) consumed by <see cref="ApplySkeletonRotationsJob"/>.</summary>
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
    /// <summary>Head world pose, produced by the head-chain FK inside BulkCopyHipsAndDeriveJob and
    /// consumed by <see cref="BasisRemoteBoneJob"/>.</summary>
    static NativeArray<float3> sTmpHeadPos;
    static NativeArray<quaternion> sTmpHeadRot;

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
    /// <summary>
    /// Native mirror of each remote player's face visibility, indexed by ushort player ID — the
    /// same key space as <see cref="sKeyToIndex"/>, so entries never move when the SoA
    /// swap-compacts on removal. Written from the managed setter (visibility only flips when a
    /// face mesh enters or leaves view) so Burst selection passes can filter candidates
    /// themselves instead of the main thread walking the receiver list to marshal one bool per
    /// player. 0 = hidden, 1 = visible.
    /// </summary>
    static NativeArray<byte> sFaceVisible;
    /// <summary>
    /// Native mirror of whether each player's nameplate is currently being displayed, indexed by
    /// ushort player ID like <see cref="sFaceVisible"/>. Owned entirely by
    /// <c>BasisRemoteNamePlate</c> — the plate pushes its own active state — so a plate that is
    /// disabled, blocked, out of range, face-hidden or switched off in settings costs no
    /// transform write in <see cref="MappedNameplateApplyJob"/>. 0 = not displayed.
    /// </summary>
    static NativeArray<byte> sNamePlateActive;
    /// <summary>Key space of <see cref="sFaceVisible"/> and <see cref="sKeyToIndex"/>.</summary>
    const int KeySpace = 65536;

    /// <summary>
    /// The visibility mirror is allocated on demand and deliberately does NOT live under
    /// <see cref="sInitialized"/>: avatar setup writes visibility, and that can run before the
    /// bone system initializes. Keying by player ID rather than SoA slot means there is never
    /// anything to rebuild, so lazy allocation is the whole lifecycle.
    /// </summary>
    static NativeArray<byte> FaceVisibleMap()
    {
        if (!sFaceVisible.IsCreated)
        {
            sFaceVisible = new NativeArray<byte>(KeySpace, Allocator.Persistent);
        }
        return sFaceVisible;
    }

    /// <summary>
    /// Same lifecycle as <see cref="FaceVisibleMap"/>: allocated on demand and keyed by player ID,
    /// because the nameplate registers itself before the bone system may be initialized.
    /// </summary>
    static NativeArray<byte> NamePlateActiveMap()
    {
        if (!sNamePlateActive.IsCreated)
        {
            sNamePlateActive = new NativeArray<byte>(KeySpace, Allocator.Persistent);
        }
        return sNamePlateActive;
    }
    /// <summary>Pending job handle chain.</summary>
    static JobHandle sPending;
    /// <summary>Set by <see cref="BeginFrame"/>: the containers are drained, sized and fenced for
    /// this frame, so the pre-scheduled passes below are safe to kick.</summary>
    static bool sFramePrepared;

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
        sFramePrepared = false;
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
        public quaternion HipsDecodePre;
        public quaternion HipsDecodePost;
        public Transform Root;
        public Transform Head;
        public Transform Hips;
        public Transform NamePlate;
        public Transform AvatarScale;
        public Transform Mouth;
        public quaternion[] BoneDecodePre;
        public quaternion[] BoneDecodePost;
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

    /// <summary>Scratch for <see cref="BakeHeadChain"/>. Main-thread only, one avatar at a time.</summary>
    static readonly List<Transform> sBakeWalk = new List<Transform>(32);
    static readonly HeadChainLink[] sBakeLinks = new HeadChainLink[HeadChainStride];

    /// <summary>Component-wise divide that won't blow up on a zero/degenerate denominator.</summary>
    static float3 SafeRatio(float3 numerator, float3 denominator)
    {
        const float eps = 1e-6f;
        float3 signed = math.select(new float3(eps), new float3(-eps), denominator < 0f);
        return numerator / math.select(denominator, signed, math.abs(denominator) < eps);
    }

    /// <summary>
    /// <see cref="SafeRatio"/> for scale factors, where a zero or non-finite result is never
    /// meaningful — those collapse the avatar — so fall back to unity instead.
    /// </summary>
    static float3 SafeScaleRatio(float3 numerator, float3 denominator)
    {
        float3 r = SafeRatio(numerator, denominator);
        return math.select(r, new float3(1f, 1f, 1f), !math.isfinite(r) | (math.abs(r) < 1e-6f));
    }

    /// <summary>Resolves a transform to its skeleton slot, or -1 when it isn't a synced bone.</summary>
    static int SlotOf(Transform[] boneTransforms, Transform node)
    {
        if (boneTransforms == null) return -1;
        for (int s = 0; s < boneTransforms.Length; s++)
        {
            if (ReferenceEquals(boneTransforms[s], node)) return s;
        }
        return -1;
    }

    /// <summary>
    /// Bakes the hips→head chain into <see cref="sBakeLinks"/> and returns the link count.
    ///
    /// The head world pose used to cost a transform read-back, but everything it depends on is
    /// already ours: the hips world pose arrives on the wire, the chain's rotations are the same
    /// rig-neutral rotations the skeleton decode consumes, and the local positions/scales between
    /// them are rig constants. So capture the constants once here and compose the pose in the job.
    ///
    /// Walks head → hips through the ACTUAL hierarchy, so rig-specific layout (twist bones,
    /// Armature-style intermediates, a missing UpperChest) is handled by construction. Static nodes
    /// fold into the link below them; only synced bones become links.
    ///
    /// Must run while the avatar is held in TPose: the folded local rotations are captured here and
    /// assumed constant thereafter, which holds because nothing on the remote apply path writes an
    /// unsynced bone — the effector IK only touches the arm/leg slots.
    /// </summary>
    /// <param name="boneTransforms">
    /// The bone array ONLY if the skeleton is actually being registered for this avatar — callers
    /// must pass null under exactly the condition that fills the skeleton slots with dummies.
    /// A bone whose transform is never written is a static node to this walk, and treating it as
    /// driven would pose the head off rotations that never reach the rig.
    /// </param>
    static int BakeHeadChain(in PendingAdd p, Transform[] boneTransforms, out HeadChainHeader header)
    {
        header = new HeadChainHeader
        {
            // The invariant slice of hierarchy between root and hips: the hips' runtime world scale
            // is the network-driven root localScale × this.
            HipsScalePerRootLocal = SafeScaleRatio(p.Hips.lossyScale, p.Root.localScale),
            Length = 0,   // caller fills this in from the returned link count
        };

        sBakeWalk.Clear();
        Transform node = p.Head;
        while (node != null && node != p.Hips && sBakeWalk.Count <= 64)
        {
            sBakeWalk.Add(node);
            node = node.parent;
        }

        if (!ReferenceEquals(node, p.Hips) || sBakeWalk.Count == 0)
        {
            return BakeRigidHeadChain(p);
        }

        int n = 0;
        bool dirty = false;
        float3 offset = float3.zero;
        quaternion preRot = quaternion.identity;
        float3 accScale = new float3(1f, 1f, 1f);

        for (int i = sBakeWalk.Count - 1; i >= 0; i--)   // hips-side → head
        {
            Transform bone = sBakeWalk[i];
            bone.GetLocalPositionAndRotation(out Vector3 localPos, out Quaternion localRot);
            float3 localScale = bone.localScale;

            offset += accScale * math.mul(preRot, (float3)localPos);
            dirty = true;

            int slot = SlotOf(boneTransforms, bone);
            if (slot < 0)
            {
                // Static node — fold its rotation and scale into the link still being built.
                preRot = math.mul(preRot, (quaternion)localRot);
                accScale *= localScale;
                continue;
            }

            if (n >= HeadChainStride) return BakeRigidHeadChain(p);

            // localRot is deliberately dropped: at runtime this bone's local rotation is the
            // decoded network rotation, which the job composes from data it already reads.
            sBakeLinks[n++] = new HeadChainLink
            {
                Offset = offset,
                PreRot = preRot,
                ScaleMul = accScale * localScale,
                Slot = slot,
            };
            dirty = false;
            offset = float3.zero;
            preRot = quaternion.identity;
            accScale = new float3(1f, 1f, 1f);
        }

        // The walk ended on static nodes below the last synced bone — a head that isn't itself
        // synced (an avatar registered without bone data, say). Close the chain with them.
        if (dirty)
        {
            if (n >= HeadChainStride) return BakeRigidHeadChain(p);
            sBakeLinks[n++] = new HeadChainLink
            {
                Offset = offset,
                PreRot = preRot,
                ScaleMul = accScale,
                Slot = -1,
            };
        }

        return n;
    }

    /// <summary>
    /// Fallback chain for a rig where head isn't a descendant of hips: one static link pinning the
    /// head to the hips at its TPose offset. The head rides the hips without spine articulation —
    /// degraded, but finite and roughly placed for the nameplate/mouth/eye consumers, and it only
    /// triggers on a rig the rest of this path (hips-world apply → skeleton decode) can't drive
    /// either.
    /// </summary>
    static int BakeRigidHeadChain(in PendingAdd p)
    {
        p.Hips.GetPositionAndRotation(out Vector3 hipsPos, out Quaternion hipsRot);
        p.Head.GetPositionAndRotation(out Vector3 headPos, out Quaternion headRot);

        quaternion invHips = math.conjugate((quaternion)hipsRot);
        sBakeLinks[0] = new HeadChainLink
        {
            // Divided by the bake-time hips world scale so the job's scale accumulator restores it.
            Offset = SafeRatio(math.mul(invHips, (float3)(headPos - hipsPos)), p.Hips.lossyScale),
            PreRot = math.mul(invHips, (quaternion)headRot),
            ScaleMul = new float3(1f, 1f, 1f),
            Slot = -1,
        };
        return 1;
    }

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
        sHipsDecodePre = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);
        sHipsDecodePost = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);

        sRoots = new TransformAccessArray(initialCapacity);
        sHips = new TransformAccessArray(initialCapacity);

        sHeadChain = new NativeList<HeadChainLink>(initialCapacity * HeadChainStride, Allocator.Persistent);
        sHeadChainHeader = new NativeList<HeadChainHeader>(initialCapacity, Allocator.Persistent);

        sNamePlate = new TransformAccessArray(initialCapacity);
        sAvatarScale = new TransformAccessArray(initialCapacity);
        sMouth = new TransformAccessArray(initialCapacity);

        sSkeletonBones = new TransformAccessArray(initialCapacity * BasisBoneRotationCompression.SyncBoneCount);
        sSkeletonDecodePre = new NativeList<quaternion>(initialCapacity * BasisBoneRotationCompression.SyncBoneCount, Allocator.Persistent);
        sSkeletonDecodePost = new NativeList<quaternion>(initialCapacity * BasisBoneRotationCompression.SyncBoneCount, Allocator.Persistent);
        sSkeletonValid = new NativeList<byte>(initialCapacity * BasisBoneRotationCompression.SyncBoneCount, Allocator.Persistent);

        // Create a dummy transform for null bone slots (TAA can't hold null)
        var dummyGO = new GameObject("[BoneJobDummy]") { hideFlags = HideFlags.HideAndDontSave };
        dummyGO.SetActive(false);
        sDummyBone = dummyGO.transform;

        sKeyToIndex = new int[KeySpace];
        Array.Fill(sKeyToIndex, -1);
        // Force the visibility mirror into existence here, before any player can join, so the
        // lazy path in FaceVisibleMap is never the one that races two first-touches. Writers are
        // all main-thread today; this keeps that from being load-bearing.
        FaceVisibleMap();
        NamePlateActiveMap();
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
        if (sHipsDecodePre.IsCreated) sHipsDecodePre.Dispose();
        if (sHipsDecodePost.IsCreated) sHipsDecodePost.Dispose();

        if (sRoots.isCreated) sRoots.Dispose();
        if (sHips.isCreated) sHips.Dispose();

        if (sHeadChain.IsCreated) sHeadChain.Dispose();
        if (sHeadChainHeader.IsCreated) sHeadChainHeader.Dispose();

        if (sNamePlate.isCreated) sNamePlate.Dispose();
        if (sAvatarScale.isCreated) sAvatarScale.Dispose();
        if (sMouth.isCreated) sMouth.Dispose();

        if (sSkeletonBones.isCreated) sSkeletonBones.Dispose();
        if (sSkeletonDecodePre.IsCreated) sSkeletonDecodePre.Dispose();
        if (sSkeletonDecodePost.IsCreated) sSkeletonDecodePost.Dispose();
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
        // Freed here rather than left for process exit so the editor's leak detector stays quiet.
        // A write after this point simply reallocates it — see FaceVisibleMap.
        if (sFaceVisible.IsCreated) sFaceVisible.Dispose();
        if (sNamePlateActive.IsCreated) sNamePlateActive.Dispose();

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
    /// <param name="namePlateHeightAboveHipsModel">Hips → nameplate-bottom distance in model units,
    /// measured from this avatar's own crown by
    /// <see cref="Basis.Scripts.UI.NamePlate.BasisNamePlateAnchorMath"/>.</param>
    /// <returns>The provided <paramref name="key"/>.</returns>
    public static int AddRemotePlayer(int key, Transform remotePlayerRoot, Transform head, Transform hips,BasisCalibratedCoords tposeHead, BasisCalibratedCoords tposeHips, float3 tposeHipsLocalPos, quaternion hipsDecodePre, quaternion hipsDecodePost, float3 authoredCenterEyeLocal,float3 authoredMouthLocal, float3 tposeHeadWorld, float3 tposeRootScale, Transform NamePlate, Transform AvatarScale, Transform MouthTransform,float namePlateHeightAboveHipsModel,
        NativeArray<quaternion> boneDecodePre = default, NativeArray<quaternion> boneDecodePost = default,
        Transform[] boneTransforms = null,
        quaternion[] cachedDecodePre = null, quaternion[] cachedDecodePost = null)
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
            NamePlateHeightAboveHips = namePlateHeightAboveHipsModel,
            // Everything the parent chain contributes to the root's world scale. Invariant while
            // the avatar lives, so the lossyScale the root gather used to sample per frame is just
            // this × the network-driven localScale.
            RootParentScale = SafeScaleRatio(remotePlayerRoot.lossyScale, remotePlayerRoot.localScale),
        };

        bool hasBoneSource = boneTransforms != null && boneDecodePre.IsCreated && boneDecodePost.IsCreated;
        bool isUpdate = sInitialized && (uint)key < (uint)sKeyToIndex.Length && sKeyToIndex[key] >= 0;

        // The DEFERRED path takes the caller's MANAGED operator arrays by reference and never
        // copies. It cannot take the NativeArrays: the receiver owns those and disposes them on
        // teardown, so holding one across the defer is a use-after-free rather than merely stale.
        // A managed array is safe to hold for the same reason boneTransforms already is — worst
        // case a later calibration overwrites it and the pending add commits the newer state.
        //
        // Callers supply either BasisAvatarModelCache's per-model array (shared by every wearer of
        // the avatar, immutable) or the receiver's own reused mirror when the rig has no Avatar
        // asset. Either way this used to be two SyncBoneCount ToArray() copies per install.
        bool hasManagedOperators = cachedDecodePre != null && cachedDecodePost != null;
        bool snapshot = boneTransforms != null && hasManagedOperators && !isUpdate;
        quaternion[] decodePre = snapshot ? cachedDecodePre : null;
        quaternion[] decodePost = snapshot ? cachedDecodePost : null;
        if (!isUpdate && boneTransforms != null && !hasManagedOperators)
        {
            // Would register a skeleton with no decode operators, which poses every bone through
            // an identity round trip instead of this rig's rest frame — visible as a subtly wrong
            // pose everywhere rather than an outright failure. Say so instead of degrading.
            BasisDebug.LogError($"Bone registration for {key} has bone transforms but no managed decode operators; skeleton not registered.", BasisDebug.LogTag.Avatar);
        }

        PendingAdd pending = new PendingAdd
        {
            Key = key,
            Authoring = a,
            TposeHeadRot = (quaternion)tposeHead.rotation,
            TposeHipsRot = (quaternion)tposeHips.rotation,
            TposeHipsLocalPos = tposeHipsLocalPos,
            HipsDecodePre = hipsDecodePre,
            HipsDecodePost = hipsDecodePost,
            Root = remotePlayerRoot,
            Head = head,
            Hips = hips,
            NamePlate = NamePlate,
            AvatarScale = AvatarScale,
            Mouth = MouthTransform,
            BoneDecodePre = decodePre,
            BoneDecodePost = decodePost,
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
            if (!CommitUpdateInternal(pending, sKeyToIndex[key],
                    hasBoneSource ? boneDecodePre : default, hasBoneSource ? boneDecodePost : default))
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
    static bool CommitUpdateInternal(in PendingAdd p, int idx,
        NativeArray<quaternion> boneDecodePre, NativeArray<quaternion> boneDecodePost)
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
        sHipsDecodePre[idx] = p.HipsDecodePre;
        sHipsDecodePost[idx] = p.HipsDecodePost;

        sRoots[idx] = p.Root;
        sHips[idx] = p.Hips;
        sNamePlate[idx] = p.NamePlate;
        sAvatarScale[idx] = p.AvatarScale;
        sMouth[idx] = p.Mouth;

        int boneCount = BasisBoneRotationCompression.SyncBoneCount;
        int baseIdx = idx * boneCount;
        bool hasBones = p.BoneTransforms != null && boneDecodePre.IsCreated && boneDecodePost.IsCreated;

        // The row now points at a different rig, so the baked chain describes the outgoing one.
        // Rebake in place — an avatar swap is exactly when the hierarchy between hips and head can
        // change shape.
        int chainLinks = BakeHeadChain(p, hasBones ? p.BoneTransforms : null, out HeadChainHeader chainHeader);
        chainHeader.Length = chainLinks;
        sHeadChainHeader[idx] = chainHeader;
        for (int l = 0; l < HeadChainStride; l++)
        {
            sHeadChain[idx * HeadChainStride + l] = l < chainLinks ? sBakeLinks[l] : default;
        }
        for (int b = 0; b < boneCount; b++)
        {
            Transform bone = hasBones ? p.BoneTransforms[b] : null;
            int slot = baseIdx + b;
            if (bone != null)
            {
                sSkeletonBones[slot] = bone;
                sSkeletonDecodePre[slot] = boneDecodePre[b];
                sSkeletonDecodePost[slot] = boneDecodePost[b];
                sSkeletonValid[slot] = 1;
            }
            else
            {
                sSkeletonBones[slot] = sDummyBone;
                sSkeletonDecodePre[slot] = quaternion.identity;
                sSkeletonDecodePost[slot] = quaternion.identity;
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
        sHipsDecodePre.Add(p.HipsDecodePre);
        sHipsDecodePost.Add(p.HipsDecodePost);

        sRoots.Add(p.Root);

        sNamePlate.Add(p.NamePlate);
        sAvatarScale.Add(p.AvatarScale);
        sMouth.Add(p.Mouth);

        sHips.Add(p.Hips);

        // Register skeleton bones for the parallel apply job
        int boneCount = BasisBoneRotationCompression.SyncBoneCount;
        bool hasSkeleton = p.BoneTransforms != null && p.BoneDecodePre != null && p.BoneDecodePost != null;

        // Bake the hips→head chain while the avatar is still held in TPose, so every
        // localPosition/localRotation captured is the bind value.
        int linkCount = BakeHeadChain(p, hasSkeleton ? p.BoneTransforms : null, out HeadChainHeader chainHeader);
        chainHeader.Length = linkCount;
        sHeadChainHeader.Add(chainHeader);
        for (int l = 0; l < HeadChainStride; l++)
        {
            sHeadChain.Add(l < linkCount ? sBakeLinks[l] : default);
        }

        if (hasSkeleton)
        {
            for (int b = 0; b < boneCount; b++)
            {
                Transform bone = p.BoneTransforms[b];
                if (bone != null)
                {
                    sSkeletonBones.Add(bone);
                    sSkeletonDecodePre.Add(p.BoneDecodePre[b]);
                    sSkeletonDecodePost.Add(p.BoneDecodePost[b]);
                    sSkeletonValid.Add(1);
                }
                else
                {
                    sSkeletonBones.Add(sDummyBone);
                    sSkeletonDecodePre.Add(quaternion.identity);
                    sSkeletonDecodePost.Add(quaternion.identity);
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
                sSkeletonDecodePre.Add(quaternion.identity);
                sSkeletonDecodePost.Add(quaternion.identity);
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
            sHipsDecodePre[idx] = sHipsDecodePre[last];
            sHipsDecodePost[idx] = sHipsDecodePost[last];

            sHeadChainHeader[idx] = sHeadChainHeader[last];
            for (int l = 0; l < HeadChainStride; l++)
            {
                sHeadChain[idx * HeadChainStride + l] = sHeadChain[last * HeadChainStride + l];
            }

            sNamePlate.RemoveAtSwapBack(idx);
            sAvatarScale.RemoveAtSwapBack(idx);
            sMouth.RemoveAtSwapBack(idx);

            sRoots.RemoveAtSwapBack(idx);
            sHips.RemoveAtSwapBack(idx);

            // O(1) reverse lookup instead of iterating the dictionary
            int movedKey = sKeyArray[last];
            sKeyToIndex[movedKey] = idx;
            sKeyArray[idx] = movedKey;
        }
        else
        {
            sRoots.RemoveAtSwapBack(last);
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
                sSkeletonDecodePre[dst] = sSkeletonDecodePre[src];
                sSkeletonDecodePost[dst] = sSkeletonDecodePost[src];
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
            sSkeletonDecodePre.RemoveAt(sSkeletonDecodePre.Length - 1);
            sSkeletonDecodePost.RemoveAt(sSkeletonDecodePost.Length - 1);
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
        sHipsDecodePre.RemoveAt(last);
        sHipsDecodePost.RemoveAt(last);
        sHeadChainHeader.RemoveAt(last);
        for (int l = HeadChainStride - 1; l >= 0; l--)
        {
            sHeadChain.RemoveAt(sHeadChain.Length - 1);
        }
        sKeyToIndex[key] = -1;
        // Drop the departing player's visibility bit. Selection jobs gate on AuthoringLength so
        // they can't reach this key any more, but a rejoin reuses the ID and would otherwise
        // inherit a stale "visible" before its avatar sets up.
        if (sFaceVisible.IsCreated) sFaceVisible[key] = 0;
        if (sNamePlateActive.IsCreated) sNamePlateActive[key] = 0;
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
        // The registration marker on the calibration side only covers QUEUEING an add; the
        // SyncBoneCount TransformAccessArray Adds per avatar actually land here, one frame stage
        // later and under a completely different parent marker. Attributed so a load-in spike
        // isn't split between two places that look unrelated.
        using (BasisNetworkMarkers.CommitAvatarAdds.Auto())
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
        AllocOrResize(ref sTmpHeadPos, count);
        AllocOrResize(ref sTmpHeadRot, count);
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
        if (sTmpHeadPos.IsCreated) sTmpHeadPos.Dispose();
        if (sTmpHeadRot.IsCreated) sTmpHeadRot.Dispose();
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
        if (sSkeletonDecodePre.IsCreated && sSkeletonDecodePre.Capacity < skeletonNeeded)
        {
            sSkeletonDecodePre.Capacity = math.max(skeletonNeeded, sSkeletonDecodePre.Capacity * 2);
        }
        if (sSkeletonDecodePost.IsCreated && sSkeletonDecodePost.Capacity < skeletonNeeded)
        {
            sSkeletonDecodePost.Capacity = math.max(skeletonNeeded, sSkeletonDecodePost.Capacity * 2);
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
    /// Opens the frame for the bone system, ahead of <see cref="Schedule"/>: fences last frame's
    /// jobs, commits queued registrations, and sizes the per-frame buffers. Was ScheduleGathers,
    /// which also kicked the root/head/hips transform reads — those are gone (see the note where
    /// the gather jobs used to live), but the pre-scheduled passes below still need this call to
    /// have drained the adds and sized the buffers before they touch a container, which is what
    /// <see cref="sFramePrepared"/> gates.
    /// </summary>
    public static void BeginFrame()
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
        sFramePrepared = true;
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
    /// Kicks the skeleton decode (rig-neutral network rotations mapped onto this rig by its cached
    /// operator pair) as soon as the interpolation output is readable — i.e. straight after
    /// <c>BasisRemoteNetworkDriver.BeginRead()</c>, AHEAD of the per-receiver loop. It reads
    /// <c>_outBoneRotations</c> and the decode tables and writes its own buffer; the receiver loop
    /// touches neither (it writes the skip flags, which only the NEXT frame's Compute reads, the
    /// filtered hips overrides, which only the hips derive reads, and the effector inputs). This
    /// is the largest independent job in the stretch — players × SyncBoneCount elements — so it
    /// is what actually fills the window instead of leaving workers parked.
    ///
    /// Requires <see cref="BeginFrame"/> to have run: that call owns DrainPendingAdds, and without
    /// it Schedule()'s fallback could resize sSkeletonDecodePre under the in-flight job.
    /// </summary>
    public static void ScheduleSkeletonCompute(int maxBatchSize = 64)
    {
        sSkeletonComputeScheduled = false;
        if (!sInitialized || !sFramePrepared)
        {
            return;
        }

        int totalBones = sSkeletonDecodePre.Length;
        if (totalBones == 0)
        {
            return;
        }

        EnsureSkeletonRotationBuffer(totalBones);

        int workerCount = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int boneBatch = math.max(1, math.min(maxBatchSize, (totalBones + workerCount - 1) / workerCount));

        sSkeletonCompute = BasisRemoteNetworkDriver.ScheduleComputeSkeletonRotations(
            sKeyArray, totalBones, BasisBoneRotationCompression.SyncBoneCount,
            sSkeletonDecodePre.AsDeferredJobArray(), sSkeletonDecodePost.AsDeferredJobArray(), sSkeletonValid.AsDeferredJobArray(),
            sSkeletonRotations, sSkeletonLastWritten, sSkeletonWriteMask,
            boneBatch);

        sSkeletonComputeBones = totalBones;
        sPending = JobHandle.CombineDependencies(sPending, sSkeletonCompute);
        sSkeletonComputeScheduled = true;

        JobHandle.ScheduleBatchedJobs();
    }

    /// <summary>
    /// Kicks the hips fan-out + root derive + head FK at the earliest point it is legal: straight
    /// after the per-receiver loop. That loop is a genuine ordering constraint —
    /// <c>SetFilteredHipsOverride</c> writes the very <c>_filteredPositions</c> slots this job reads
    /// (see the seat/vehicle path) — but nothing after it is, so waiting for Schedule() was pure
    /// main-thread latency.
    ///
    /// This is also why the head FK lives here rather than in the skeleton decode: the head hangs
    /// off the hips pose, so it has to see the overrides, and the decode is deliberately kicked
    /// AHEAD of the receiver loop where they aren't final yet.
    /// </summary>
    public static void ScheduleHipsDerive()
    {
        sHipsDeriveScheduled = false;
        if (!sInitialized || !sFramePrepared || AuthoringLength == 0)
        {
            return;
        }

        sHipsDerive = BasisRemoteNetworkDriver.ScheduleBulkCopyHipsAndDerive(
            sKeyArray, AuthoringLength,
            sTPoseHipsLocalPos.AsDeferredJobArray(), sHipsDecodePre.AsDeferredJobArray(), sHipsDecodePost.AsDeferredJobArray(),
            sTmpHipsWorldPos, sTmpHipsWorldRot,
            sTmpAvatarScales, sTmpScaleChanged,
            sTmpRootDerivedPos, sTmpRootDerivedRot,
            sHeadChain.AsDeferredJobArray(), sHeadChainHeader.AsDeferredJobArray(), HeadChainStride,
            sSkeletonDecodePre.AsDeferredJobArray(), sSkeletonDecodePost.AsDeferredJobArray(),
            BasisBoneRotationCompression.SyncBoneCount,
            sTmpHeadPos, sTmpHeadRot);

        sPending = JobHandle.CombineDependencies(sPending, sHipsDerive);
        sHipsDeriveScheduled = true;

        JobHandle.ScheduleBatchedJobs();
    }

    /// <summary>
    /// Schedules the entire simulation pipeline for the current set of avatars:
    /// derive → simulate → apply (nameplate/mouth/hips/skeleton). There is no gather stage — every
    /// input is either network state or a rig constant baked at registration.
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

        if (!sFramePrepared)
        {
            BeginFrame();
        }
        if (!sFramePrepared)
        {
            return default;
        }
        sFramePrepared = false;

        // Taken as locals and cleared up front so no pre-scheduled handle can survive this call
        // and be consumed a second time against a later frame's data.
        bool havePreScheduledSkeleton = sSkeletonComputeScheduled;
        bool havePreScheduledHipsDerive = sHipsDeriveScheduled;
        sSkeletonComputeScheduled = false;
        sHipsDeriveScheduled = false;

        // sKeyArray is maintained directly by Add/RemoveRemotePlayer, so no per-frame snapshot
        // is needed. The previous code copied each entry out of a managed List<int> via
        // List<T>.get_Item, which was the dominant cost of Schedule().

        // Adaptive batch size — the whole point is to actually use multiple cores.
        // With a fixed batchSize of 64 and ~50 avatars, IJobParallelFor packs the
        // entire job into one chunk and one worker thread runs the lot.
        // Divide the work by JobWorkerCount so each core gets a slice; cap at the
        // caller-supplied maxBatchSize for very large avatar counts where per-batch
        // dispatch overhead would otherwise add up.
        int workerCount = math.max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
        int simBatch = math.max(1, math.min(maxBatchSize,
            (AuthoringLength + workerCount - 1) / workerCount));

        // ── One Burst dispatch fans out ALL per-player network state and
        //    derives the root pose in a single sequential pass:
        //      • copies filtered hips world pos/rot
        //      • copies scale + scale-change flag
        //      • reads hips local deltas inline (no temp round-trip)
        //      • reads cached TPose hips local pos/rot
        //      • computes the derived root pose via the conjugate inverse math
        //      • walks the baked hips→head chain for the head world pose
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
                sTPoseHipsLocalPos.AsDeferredJobArray(), sHipsDecodePre.AsDeferredJobArray(), sHipsDecodePost.AsDeferredJobArray(),
                sTmpHipsWorldPos, sTmpHipsWorldRot,
                sTmpAvatarScales, sTmpScaleChanged,
                sTmpRootDerivedPos, sTmpRootDerivedRot,
                sHeadChain.AsDeferredJobArray(), sHeadChainHeader.AsDeferredJobArray(), HeadChainStride,
                sSkeletonDecodePre.AsDeferredJobArray(), sSkeletonDecodePost.AsDeferredJobArray(),
                BasisBoneRotationCompression.SyncBoneCount,
                sTmpHeadPos, sTmpHeadRot);
        }

        // Bone simulation runs off that job's dense outputs — hips world pose, avatar scale and
        // the FK'd head pose — instead of off three transform gathers. Its one dependency is
        // normally already satisfied by the time we get here, since the derive was kicked right
        // after the receiver loop.
        var BoneSimulation = new BasisRemoteBoneJob
        {
            Authoring = sAuthoring.AsDeferredJobArray(),
            HeadPos = sTmpHeadPos,
            HeadRot = sTmpHeadRot,
            HipsPos = sTmpHipsWorldPos,
            HipsRot = sTmpHipsWorldRot,
            AvatarScale = sTmpAvatarScales,
            TposeHeadRot = sTPoseHeadRot.AsDeferredJobArray(),
            TposeHipsRot = sTPoseHipsRot.AsDeferredJobArray(),
            GeneratedScales = sScale.AsDeferredJobArray(),
            Out = sOut.AsDeferredJobArray(),
            MouthPositions = sMouthPositions.AsDeferredJobArray(),
            MouthForwards = sMouthForwards.AsDeferredJobArray()
        }.Schedule(AuthoringLength, simBatch, bulkAndDeriveJob);

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
        // No gather to fence against any more — sRoots is written here and read nowhere.
        var rootApplyDeps = bulkAndDeriveJob;
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
        int totalBones = sSkeletonDecodePre.Length;
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
                    sSkeletonDecodePre.AsDeferredJobArray(), sSkeletonDecodePost.AsDeferredJobArray(), sSkeletonValid.AsDeferredJobArray(),
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
            PanelHalfHeightWorld = Basis.Scripts.UI.NamePlate.BasisRemoteNamePlateDriver.PanelHalfHeightWorld(),
            NamePlateIn = sOut.AsDeferredJobArray(),
            PlayerKeys = sKeyArray,
            PlateActive = NamePlateActiveMap(),
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
        // skeletonJob if present (defensive; sSkeletonBones doesn't include hips).
        var hipsWorldDeps = rootAndScaleJob;
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
                CapacityFixed = BasisRemoteNetworkDriver.Capacity,
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

        total = math.min(sSkeletonDecodePre.Length, sSkeletonWriteMask.Length);
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
        // sInitialized, like TryGetSOutIndex below: sKeyToIndex is null until Initialize() runs, and
        // the pointer read past the map lookup has no bounds check of its own to fall back on.
        if (!sInitialized || sKeyToIndex == null || (uint)key >= (uint)sKeyToIndex.Length)
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
        if (!sInitialized || sKeyToIndex == null || (uint)key >= (uint)sKeyToIndex.Length)
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
    /// Records a player's face visibility in the native mirror consumed by Burst selection
    /// passes. Called from the managed <c>FaceIsVisible</c> setter — visibility only flips when
    /// a face mesh enters or leaves view, so this is a cold path despite being a blind write.
    /// </summary>
    public static void SetFaceVisible(int key, bool visible)
    {
        if ((uint)key >= (uint)KeySpace) return;
        // Via a local: NativeArray<T> is a struct, so indexing the returned temporary directly
        // is a CS1612. The copy still writes through to the same unmanaged buffer.
        NativeArray<byte> map = FaceVisibleMap();
        map[key] = visible ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Native face-visibility mirror indexed by ushort player ID; pair it with
    /// <see cref="GetPlayerKeyArray"/> to filter SoA slots from inside a job. Always created.
    /// Read-only for consumers — write through <see cref="SetFaceVisible"/>.
    /// </summary>
    public static NativeArray<byte> GetFaceVisibleMap() => FaceVisibleMap();

    /// <summary>
    /// Records whether a player's nameplate is currently displayed. Called by
    /// <c>BasisRemoteNamePlate</c> whenever it applies its active state; while this is false
    /// <see cref="MappedNameplateApplyJob"/> leaves that plate's transform alone. Defaults to
    /// false, so a player with no live nameplate is never posed.
    /// </summary>
    public static void SetNamePlateActive(int key, bool active)
    {
        if ((uint)key >= (uint)KeySpace) return;
        CompletePending();
        NativeArray<byte> map = NamePlateActiveMap();
        map[key] = active ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Native nameplate-pose gate indexed by ushort player ID. Always created. Read-only for
    /// consumers — write through <see cref="SetNamePlateActive"/>.
    /// </summary>
    public static NativeArray<byte> GetNamePlateActiveMap() => NamePlateActiveMap();

    /// <summary>
    /// Exposes the SoA index → player ID reverse map so a job can walk the dense slot range
    /// itself instead of the main thread resolving one index per receiver. Only the first
    /// <see cref="AuthoringLength"/> entries are live; the tail holds stale keys after a
    /// removal. Returns a default (uncreated) array when the system isn't initialized.
    ///
    /// Valid for the current frame only — the backing array is reallocated when the player
    /// count grows, so callers must re-acquire it rather than cache it, exactly as with
    /// <see cref="GetRemoteFrameArray"/>.
    /// </summary>
    public static NativeArray<int> GetPlayerKeyArray() => sInitialized ? sKeyArray : default;

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
        if (!sInitialized || sKeyToIndex == null || (uint)key >= (uint)sKeyToIndex.Length)
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
