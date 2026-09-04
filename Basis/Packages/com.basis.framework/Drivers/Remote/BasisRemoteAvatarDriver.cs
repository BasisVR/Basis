using Basis.Network.Core.Compression;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Player;
using Basis.Scripts.UI.NamePlate;
using GatorDragonGames.JigglePhysics;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisRemoteAvatarDriver : BasisAvatarDriver
    {
        // Remote calibration is the main-thread half of every avatar load, reload, far LOD swap
        // and range re-entry, and it reported one number for ~20 different stages. These split it
        // so a load-in spike attributes to the stage that owns it instead of "calibration".
        // BoneJobRegister measured as ~92% of calibration, so it gets split again. Three
        // candidates live under it and they have completely different fixes: a main-thread job
        // fence, TransformAccessArray mutation of the SyncBoneCount x players skeleton array, and
        // the per-player interpolation slot seed (which fences the interpolation job separately).

        public Action CalibrationComplete;

        [SerializeField]
        public BasisTransformMapping References = new BasisTransformMapping();

        public readonly BasisHandPoseGrid HandGrid = new BasisHandPoseGrid();
        public int HandGridGeneration = 1;

        public SkinnedMeshRenderer[] SkinnedMeshRenderer;

        public IBasisPlayer Player;

        public bool HasEvents = false;

        public int SkinnedMeshRendererLength;

        public Vector3 AvatarInitialScale = Vector3.one;

        public float NamePlateHeightAboveHipsModel;

        public Quaternion DerivedRootToCharacterBasis = Quaternion.identity;
        public Quaternion HipsToCharacterBasis = Quaternion.identity;

        public bool InBoneDriver = false;

        public JiggleRig[] JiggleRigs = Array.Empty<JiggleRig>();
        private static Vector3[] sJiggleRootsBeforeSnap = Array.Empty<Vector3>();

        /// <summary>Whether this remote's jiggle rigs are currently registered with the simulation. See BasisJiggleSimulationLOD.</summary>
        public bool JiggleSimulating = true;

        /// <summary>
        /// Enables/disables every jiggle rig on this remote. Disabling runs each JiggleRig's own
        /// OnDisable -&gt; JigglePhysics.RemoveJiggleTreeSegment; re-enabling re-adds and reseeds from
        /// the current bone pose. A rig authored disabled (JiggleRigs entries include inactive ones,
        /// same convention as the calibration loops below) is left alone either way.
        /// </summary>
        public void SetJiggleSimulating(bool simulating)
        {
            if (JiggleSimulating == simulating)
            {
                return;
            }
            JiggleSimulating = simulating;
            int count = JiggleRigs.Length;
            for (int i = 0; i < count; i++)
            {
                JiggleRig rig = JiggleRigs[i];
                if (rig != null && rig.gameObject.activeInHierarchy)
                {
                    rig.enabled = simulating;
                }
            }
        }

        [NonSerialized] public Basis.IK.BasisBodyFitResult AppliedBodyFit = Basis.IK.BasisBodyFitResult.Identity;

        readonly Transform[] _fitBones = new Transform[Basis.IK.BasisBodyFitApply.BoneCount];
        readonly Vector3[] _fitRestLocal = new Vector3[Basis.IK.BasisBodyFitApply.BoneCount];
        readonly float[] _fitScales = new float[Basis.IK.BasisBodyFitApply.BoneCount];
        bool _fitRestCaptured;

        public void RemoteCalibration(BasisRemotePlayer RemotePlayer)
        {
            if (!IsAble(RemotePlayer))
            {
                return;
            }
            else
            {
                // BasisDebug.Log("RemoteCalibration Underway", BasisDebug.LogTag.Avatar);
            }

            Player = RemotePlayer;

            // Cache renderers and prep avatar layer/tpose
            SkinnedMeshRenderer = Player.BasisAvatar.SkinnedMeshRenderers;
            if (SkinnedMeshRenderer == null)
            {
                var renders = Player.BasisAvatar.Renders;
                var skinnedCount = 0;
                for (var i = 0; i < renders.Length; i++)
                {
                    if (renders[i] is SkinnedMeshRenderer)
                    {
                        skinnedCount++;
                    }
                }
                SkinnedMeshRenderer = new SkinnedMeshRenderer[skinnedCount];
                var skinnedWriteIndex = 0;
                for (var i = 0; i < renders.Length; i++)
                {
                    if (renders[i] is SkinnedMeshRenderer skinnedMeshRenderer)
                    {
                        SkinnedMeshRenderer[skinnedWriteIndex++] = skinnedMeshRenderer;
                    }
                }
            }
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
            // Far avatars are skipped: BasisFarLodGenerator captures the payload skeleton under the
            // same "Animated TPose" controller this would apply, and BuildAvatar writes those baked
            // locals straight onto the bones — the hierarchy is already in the pose before we get
            // here. The pair costs two runtimeAnimatorController assignments (an animator rebind
            // each) plus a full humanoid Animator.Update, per install, on the transmit tick.
            // The same pair is skipped a second way below: once any instance of this model has been
            // posed, the locals it landed on are cached against the Avatar asset and replayed
            // straight onto the bones. The loading dummy is one prefab shared by every remote, so
            // the range-exit install that runs inline on the transmit tick never rebinds at all
            // after the first one in the session.
            NeedsTposeReset = !Player.BasisAvatar.IsFarLodAvatar;
            if (NeedsTposeReset)
            {
                using (BasisAvatarMarkers.CalibrateTpose.Auto())
                {
                    if (BasisAvatarModelCache.TryReplayTposeHierarchy(Player.BasisAvatar.Animator))
                    {
                        NeedsTposeReset = false;
                    }
                    else
                    {
                        PutAvatarIntoTPose();
                        BasisAvatarModelCache.StoreTposeHierarchy(Player.BasisAvatar.Animator);
                    }
                }
            }

            RemotePlayer.BasisAvatar.HumanScale = RemotePlayer.BasisAvatar.Animator.humanScale;
            RemotePlayer.BasisAvatar.Animator.applyRootMotion = false;
            RemotePlayer.BasisAvatar.Animator.updateMode = AnimatorUpdateMode.Normal;
            RemotePlayer.BasisAvatar.Animator.speed = 0;
            RemotePlayer.BasisAvatar.Animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            AvatarInitialScale = Player.BasisAvatar.transform.localScale;

            // Auto-detect bone refs and record TPose. Pass Animator.transform so
            // References.AnimatorRoot caches the actual animator root — downstream
            // calibration steps then read References.AnimatorRoot instead of going
            // through the Animator.transform property each time.
            // Twist bones are detected on remotes too: a networked body fit scales the forearm/upper-arm
            // segments, and the twist helpers sit partway along those segments. Scaling the arm without
            // moving the twists leaves them at the wrong fraction of a now-longer bone, which shows up as
            // mesh distortion around the elbow. Cost is a one-time child-name search per arm at load.
            using (BasisAvatarMarkers.CalibrateDetectReferences.Auto())
            {
                BasisTransformMapping.AutoDetectReferences(Player.BasisAvatar.Animator, Player.BasisAvatar.Animator.transform, ref References, detectArmTwist: true, humanoidBones: Player.BasisAvatar.TransformStorage?.HumanoidBones);
                BasisAvatarModelCache.RecordPosesCached(References, Player.BasisAvatar.Animator);
            }

            // ── Capture T-pose bone rotations and bone transforms for the receiver ──
            // This enables direct bone transform writes (no SetHumanPose needed).
            using (BasisAvatarMarkers.CalibrateBoneData.Auto())
            {
                CaptureReceiverBoneData(RemotePlayer);
            }

            // Capture the fresh authored bind, then apply this player's body fit. Order matters: the
            // rest capture must see authored local positions, so it runs before any fit is written.
            // Seeding from CACM covers every path that supplies an avatar record — a live avatar change,
            // initial load, and the server's late-join replay all set it before calibration runs — while
            // a fit-only update that arrived since is already in AppliedBodyFit and survives the reseed.
            using (BasisAvatarMarkers.CalibrateBodyFit.Auto())
            {
                SeedBodyFitFromAvatarRecord(RemotePlayer);
                CaptureBodyFitRestLocal();
                ApplyRemoteBodyFit();
            }

            // Register authored motion (drives non-humanoid transforms the bone job / IK don't touch); rest captured at the current TPose.
            var authoredMotions = RemotePlayer.BasisAvatar.AuthoredMotions;
            if (authoredMotions != null)
            {
                for (int i = 0; i < authoredMotions.Length; i++)
                {
                    BasisAuthoredMotionSystem.Register(authoredMotions[i]);
                }
            }

            // Face visibility setup. Not every avatar has a face mesh (far avatars, generic
            // imports without face wiring) — those skip visibility tracking entirely.
            Player.FaceIsVisible = false;
            if (RemotePlayer.BasisAvatar == null)
            {
                BasisDebug.LogError("Missing Avatar On Remote", BasisDebug.LogTag.Avatar);
            }
            SkinnedMeshRenderer faceVisemeMesh = RemotePlayer.BasisAvatar.FaceVisemeMesh;
            using (BasisAvatarMarkers.CalibrateFace.Auto())
            {
                if (Player.FaceRenderer != null)
                {
                    // Mute before the deferred destroy: the outgoing avatar's renderer fires a
                    // final OnBecameInvisible during its end-of-frame teardown, and that late
                    // notification would stomp the visibility state (and face driver) just set
                    // up for the incoming avatar.
                    Player.FaceRenderer.Check = null;
                    GameObject.Destroy(Player.FaceRenderer);
                    Player.FaceRenderer = null;
                }
                if (faceVisemeMesh != null)
                {
                    Player.UpdateFaceVisibility(faceVisemeMesh.isVisible);
                    Player.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(faceVisemeMesh.gameObject);
                    Player.FaceRenderer.Check += Player.UpdateFaceVisibility;
                }
                else
                {
                    BasisDebug.Log("Missing Face for " + Player.DisplayName, BasisDebug.LogTag.Avatar);
                    Player.UpdateFaceVisibility(false);
                }

                // Blink + eyes
                // Initialize unconditionally — Initialize handles a missing blink mesh
                // gracefully (sets BlinkingEnabled = false) and eye calibration still runs
                // for avatars that only have eye bones.
                RemotePlayer.RemoteFaceDriver.Initialize(Player, RemotePlayer.BasisAvatar);
            }
            using (BasisAvatarMarkers.CalibrateRenderers.Auto())
            {
                // Renderer perf flags
                RemoteRenderMeshSettings(BasisLayerMapper.RemoteAvatarLayer, SkinnedMeshRendererLength, SkinnedMeshRenderer);
                // Seed the skin LOD for the distance this avatar loaded at — ChangeMeshLOD is only
                // edge-triggered on LOD boundary crossings, so a reload at a stable distance never
                // reaches these fresh renderers.
                BasisAvatarSkinLOD.Apply(SkinnedMeshRenderer, SkinnedMeshRendererLength, RemotePlayer.CurrentLodLevel);
                // Snapshot the authored shadow modes before anything reduces them, then seed the tier.
                BasisAvatarShadowLOD.Capture(RemotePlayer);
                BasisAvatarShadowLOD.Apply(RemotePlayer, RemotePlayer.CurrentLodLevel);
                Basis.Scripts.Rendering.BasisAvatarVisibility.Register(RemotePlayer);
            }

            RemotePlayer.BasisAvatar.Animator.logWarnings = false;

            // Register with the RemoteBoneJobSystem (including skeleton bones for job-based apply).
            // Use the cached References.AnimatorRoot rather than walking through
            // RemotePlayer.BasisAvatar.Animator.transform on each line.
            var receiver = RemotePlayer.NetworkReceiver;
            Transform animatorRoot = References.AnimatorRoot;
            // Sampled before the network rescale below writes this same transform — jiggle collider
            // radii are authored in metres against this scale and rebased off it at build time.
            ColliderScaleReference = animatorRoot.localScale;
            using (BasisAvatarMarkers.CalibrateBoneJobRegister.Auto())
            {
                RegisterAvatarWithBoneJobSystem(RemotePlayer, snapToNetworkPose: false);
            }

            // player.RemoteBoneDriver.InitializeFromAvatar(player);
            RemotePlayer.BasisAvatar.Animator.enabled = false;

            using (BasisAvatarMarkers.CalibrateJiggle.Auto())
            {
                SetupAvatarJiggleColliders();
            }
            if (NeedsTposeReset)
            {
                using (BasisAvatarMarkers.CalibrateTpose.Auto())
                {
                    ResetAvatarAnimator();
                }
            }

            // JiggleRigs is filtered out of the content-harvest snapshot by BasisAvatarFactory at
            // load — no walk here, and recalibrations reuse the same stored set. The set is
            // include-inactive, so the loops gate on activity the way the old active-only scan did.
            int jiggleRigCount = JiggleRigs.Length;
            if (sJiggleRootsBeforeSnap.Length < jiggleRigCount)
            {
                sJiggleRootsBeforeSnap = new Vector3[jiggleRigCount];
            }
            Vector3[] jiggleRootsBeforeSnap = sJiggleRootsBeforeSnap;
            for (int Index = 0; Index < jiggleRigCount; Index++)
            {
                JiggleRig snapRig = JiggleRigs[Index];
                if (snapRig == null || !snapRig.gameObject.activeInHierarchy || !snapRig.enabled)
                {
                    continue;
                }
                var jiggleRoot = snapRig.GetJiggleRigData().rootBone;
                if (jiggleRoot != null)
                {
                    jiggleRootsBeforeSnap[Index] = jiggleRoot.position;
                }
            }

            // TPose hips locals for the root derivation below (also fed to the job system
            // inside RegisterAvatarWithBoneJobSystem).
            float3 tposeHipsLocalPos;
            quaternion tposeHipsLocalRot;
            if (References.TposeLocal.TryGetValue(HumanBodyBones.Hips, out var hipsTposeLocal))
            {
                tposeHipsLocalPos = hipsTposeLocal.position;
                tposeHipsLocalRot = hipsTposeLocal.rotation;
            }
            else
            {
                tposeHipsLocalPos = float3.zero;
                tposeHipsLocalRot = quaternion.identity;
            }

            // Apply scale before snapping any pose so localScale is in place for
            // the first frame; UpdateAllAvatarsJob's HasScaleChange tick would
            // otherwise overwrite the network scale on the first iteration.
            // GetLatestNetworkPose returns HIPS world (high-precision channel,
            // also what the server reduction system reads).
            receiver.GetLatestNetworkPose(out var hipsWorldPos, out var hipsWorldRot, out var networkScale);
            animatorRoot.localScale = networkScale;
            BasisRemoteNetworkDriver.SeedScaleState(receiver.playerId, networkScale);
            BasisRemoteNetworkDriver.SeedPoseState(receiver.playerId, hipsWorldPos, hipsWorldRot);

            // Derive an approximate root pose using the same inverse math as
            // BulkCopyHipsAndDeriveJob (assumes hips is effectively a child of
            // root — typical Unity humanoid). Only used to put the hierarchy
            // somewhere sensible; the hips world apply right after this is
            // hierarchy-agnostic and overrides any approximation error.
            // conjugate, not inverse — unit quaternions only.
            quaternion rootRot = math.mul(hipsWorldRot, math.conjugate(tposeHipsLocalRot));
            float3 scaledLocal = (float3)networkScale * tposeHipsLocalPos;
            float3 rootPos = (float3)hipsWorldPos - math.mul(rootRot, scaledLocal);
            animatorRoot.SetPositionAndRotation(rootPos, rootRot);

            // Snap hips world directly. SetPositionAndRotation walks the actual
            // parent chain, so an intermediate Armature (or any other node)
            // doesn't disturb the result — hips lands exactly at the received
            // high-precision world pose.
            References.Hips.SetPositionAndRotation(hipsWorldPos, hipsWorldRot);
            SnapNamePlateAndMouth(RemotePlayer, hipsWorldPos, networkScale.y);

            // Initialize any jiggle rigs. Performance-limit enforcement lives in
            // BasisAvatarPerformanceLimits.TrimExcessComponents (called earlier by
            // BasisAvatarFactory.InitializePlayerAvatar), so by the time we get
            // here the tree has already been trimmed to the allowed count — this
            // loop just wires up whatever's left.
            using (BasisAvatarMarkers.CalibrateJiggle.Auto())
            {
                for (int Index = 0; Index < jiggleRigCount; Index++)
                {
                    JiggleRig Rig = JiggleRigs[Index];
                    // !Rig.enabled also catches BasisJiggleSimulationLOD holding this rig off for
                    // distance — a recalibration snap must not force it back into the simulation.
                    if (Rig == null || !Rig.gameObject.activeInHierarchy || !Rig.enabled)
                    {
                        continue;
                    }
                    Rig.HasAnimatedParameters = false;
                    Rig.OnInitialize();
                    var jiggleRoot = Rig.GetJiggleRigData().rootBone;
                    if (jiggleRoot != null)
                    {
                        Rig.Teleport(jiggleRoot.position - jiggleRootsBeforeSnap[Index]);
                    }
                }
            }

            CalibrationComplete?.Invoke();

            // Seed the far LOD for the distance this avatar loaded at — the transmit tick's
            // swap check is edge-triggered, so a far-away load would otherwise start at full
            // detail. Also drops any far LOD belonging to the previous avatar.
            BasisAvatarFarLOD.SeedAfterCalibration(RemotePlayer);

            BasisFiniteWatchdog.CheckpointRemote("RemoteCalibration/Complete", RemotePlayer);
        }

        public void RegisterAvatarWithBoneJobSystem(BasisRemotePlayer RemotePlayer, bool snapToNetworkPose)
        {
            var receiver = RemotePlayer.NetworkReceiver;
            if (receiver == null || RemotePlayer.BasisAvatar == null || References?.AnimatorRoot == null ||
                receiver.BoneTransforms == null || !receiver.BoneDecodePre.IsCreated || !receiver.BoneDecodePost.IsCreated)
            {
                // The old avatar is already gone by the time re-registration runs (swap order
                // destroys first). Aborting while still registered would leave every bone
                // TransformAccessArray pointing at the dying hierarchy — same recovery as the
                // factory's catch path.
                RemotePlayer.RemoveFromBoneDriver();
                return;
            }
            Transform animatorRoot = References.AnimatorRoot;

            // No remove here any more. A re-registration keeps the same row and the same
            // SyncBoneCount skeleton slots, so AddRemotePlayer re-points them in place; tearing
            // the entry down first cost SyncBoneCount RemoveAtSwapBack calls against the biggest
            // TransformAccessArray in the system and measured 11.28ms on a crowded instance.
            // AddRemotePlayer still falls back to a real remove if the incoming transforms are
            // unusable, so a dying hierarchy can never stay registered.

            // TPose hips localPosition + the hips decode pair feed the per-frame
            // BulkCopyHipsAndDeriveJob (the inline inverse derivation that
            // turns the received hips world pose into a derived root world
            // pose). Hips world itself is then applied directly via
            // ApplyHipsWorldJob, which is hierarchy-agnostic — these TPose
            // values are only used for the (best-effort) root derivation,
            // not for writing the hips bone's local transform anymore.
            float3 tposeHipsLocalPos = References.TposeLocal.TryGetValue(HumanBodyBones.Hips, out var hipsTposeLocal)
                ? (float3)hipsTposeLocal.position
                : float3.zero;

            // Hips rides in the packet tail rather than the bone block, but it is carried in the
            // same rig-neutral space, so it needs the same decode pair. Built here rather than
            // inside AddRemotePlayer because the rest frame has to be normalised by this avatar's
            // character basis first — the raw TposeFromRoot rotation the registration already
            // carries is in the animator root's frame, which is not shared between rigs. Getting
            // this one wrong tilts hipsLocalRot, which the job divides out of the hips world pose
            // to derive the root, so the whole avatar shifts rather than just its pelvis.
            BasisGenericBoneRotationUtils.BuildDecodeOperators(
                BasisGenericBoneRotationUtils.GetRestFrame(References, HumanBodyBones.Hips),
                BasisGenericBoneRotationUtils.GetRestLocal(References, HumanBodyBones.Hips),
                out quaternion hipsDecodePre, out quaternion hipsDecodePost);

            // Measured off the same two rest rotations, here rather than per frame: the difference
            // between them is exactly what the derived root pose below carries on top of this
            // avatar's facing, and anything aiming at the player (the follow camera) has to divide
            // it back out.
            DerivedRootToCharacterBasis = BasisGenericBoneRotationUtils.GetDerivedRootToCharacterBasis(References);
            HipsToCharacterBasis = math.conjugate(BasisGenericBoneRotationUtils.GetRestFrame(References, HumanBodyBones.Hips));

            // Initialize this player's interpolation slot before registering it with the bone
            // job system. The bone Schedule reads _filtered*[playerId] earlier in LateUpdate than
            // BeginWrite's lazy init runs (LateUpdate tail), so a cached/fallback avatar that
            // calibrates within a frame of joining would otherwise be read from uninitialized
            // memory and pose as NaN.
            using (BasisAvatarMarkers.CalibrateBoneJobRegisterSlotSeed.Auto())
            {
                BasisRemoteNetworkDriver.EnsureSlotInitialized(receiver.playerId);
            }
            // T-pose only. The calibration path is the one that arrives here with the rig posed and
            // References freshly recorded; a far-LOD wake-up or any other re-registration reuses
            // what that measured rather than re-reading a live pose.
            if (!snapToNetworkPose || NamePlateHeightAboveHipsModel <= 0f)
            {
                CaptureNamePlateAnchor(RemotePlayer);
            }

            using (BasisAvatarMarkers.CalibrateBoneJobRegisterAdd.Auto())
            {
                RemoteBoneJobSystem.AddRemotePlayer(
                    key: receiver.playerId,
                    remotePlayerRoot: animatorRoot,
                    head: References.head,
                    hips: References.Hips,
                    tposeHead: References.TposeFromRoot[HumanBodyBones.Head],
                    tposeHips: References.TposeFromRoot[HumanBodyBones.Hips],
                    tposeHipsLocalPos: tposeHipsLocalPos,
                    hipsDecodePre: hipsDecodePre,
                    hipsDecodePost: hipsDecodePost,
                    // Handed over in the frame the authored Vector2 is already in: (height, forward)
                    // above the animator root, root-relative RENDERED metres. These used to be pushed
                    // through the translation-only ConvertFromLocalSpace overload into "world" and
                    // subtracted back out inside AddRemotePlayer, which cancelled the root translation
                    // but never applied the root ROTATION — so the authored forward offset pointed
                    // along world +Z instead of out of the avatar's face. The head operand has to come
                    // from TposeWorld, not TposeFromRoot: only TposeWorld keeps the root's scale in,
                    // which is what makes the two sides of the subtraction the same kind of metre.
                    authoredCenterEyeLocal: BasisHelpers.AvatarPositionConversion(RemotePlayer.BasisAvatar.AvatarEyePosition),
                    authoredMouthLocal: BasisHelpers.AvatarPositionConversion(RemotePlayer.BasisAvatar.AvatarMouthPosition),
                    tposeHeadWorld: References.TposeWorld[HumanBodyBones.Head].position,
                    tposeRootScale: References.RootScale,
                    NamePlate: RemotePlayer.NamePlateTransformProvider?.Invoke(),
                    AvatarScale: animatorRoot,
                    MouthTransform: RemotePlayer.MouthTransform,
                    namePlateHeightAboveHipsModel: NamePlateHeightAboveHipsModel,
                    boneDecodePre: receiver.BoneDecodePre,
                    boneDecodePost: receiver.BoneDecodePost,
                    boneTransforms: receiver.BoneTransforms,
                    cachedDecodePre: receiver.CachedDecodePre,
                    cachedDecodePost: receiver.CachedDecodePost
                );
            }
            InBoneDriver = true;
            // The plate pushes its own pose gate into the bone system, but its Initialize runs
            // before NetworkReceiver is linked, so that first push has no key to resolve. This is
            // the earliest point where both the receiver and the plate's TAA slot exist.
            RemotePlayer.OnNamePlateActiveStateShouldRefresh?.Invoke();

            if (!snapToNetworkPose)
            {
                BasisFiniteWatchdog.CheckpointRemote("RemoteRegister/PostBoneJobRegistration", RemotePlayer);
                return;
            }

            Vector3 hipsBeforeSnap = References.Hips.position;
            receiver.GetLatestNetworkPose(out var hipsWorldPos, out var hipsWorldRot, out var networkScale);
            animatorRoot.localScale = networkScale;
            BasisRemoteNetworkDriver.SeedScaleState(receiver.playerId, networkScale);
            BasisRemoteNetworkDriver.SeedPoseState(receiver.playerId, hipsWorldPos, hipsWorldRot);
            // conjugate, not inverse — unit quaternions only. The rest hips LOCAL rotation, not a
            // generic-space value: this snap has no network rotation to decode, it just backs the
            // root out of the hips world pose with the avatar at rest.
            quaternion rootRot = math.mul(hipsWorldRot,
                math.conjugate(BasisGenericBoneRotationUtils.GetRestLocal(References, HumanBodyBones.Hips)));
            float3 scaledLocal = (float3)networkScale * tposeHipsLocalPos;
            float3 rootPos = (float3)hipsWorldPos - math.mul(rootRot, scaledLocal);
            animatorRoot.SetPositionAndRotation(rootPos, rootRot);
            References.Hips.SetPositionAndRotation(hipsWorldPos, hipsWorldRot);
            SnapNamePlateAndMouth(RemotePlayer, hipsWorldPos, networkScale.y);

            // Teleport jiggle rigs by the travel delta so they don't whip across the distance
            // the player covered while the avatar was asleep.
            Vector3 jiggleDelta = (Vector3)hipsWorldPos - hipsBeforeSnap;
            int jiggleRigCount = JiggleRigs.Length;
            for (int Index = 0; Index < jiggleRigCount; Index++)
            {
                JiggleRig rig = JiggleRigs[Index];
                if (rig == null || !rig.gameObject.activeInHierarchy)
                {
                    continue;
                }
                rig.Teleport(jiggleDelta);
            }

            BasisFiniteWatchdog.CheckpointRemote("RemoteRegister/PostNetworkPoseSnap (far-LOD wake)", RemotePlayer);
        }

        private void SnapNamePlateAndMouth(BasisRemotePlayer RemotePlayer, float3 hipsWorldPos, float rootScaleY)
        {
            Transform namePlate = RemotePlayer.NamePlateTransformProvider?.Invoke();
            if (namePlate != null)
            {
                float3 platePosition = new float3(
                    hipsWorldPos.x,
                    BasisNamePlateAnchorMath.AnchorWorldY(
                        hipsWorldPos.y,
                        NamePlateHeightAboveHipsModel * rootScaleY,
                        BasisRemoteNamePlateDriver.PanelHalfHeightWorld()),
                    hipsWorldPos.z);

                float3 toCamera = (float3)BasisLocalCameraDriver.Position - platePosition;
                float2 flat = new float2(toCamera.x, toCamera.z);
                float yaw = math.lengthsq(flat) > 1e-12f ? math.atan2(flat.x, flat.y) : 0f;
                namePlate.SetPositionAndRotation(platePosition, quaternion.RotateY(yaw));
            }

            Transform mouth = RemotePlayer.MouthTransform;
            if (mouth != null && References.head != null)
            {
                References.head.GetPositionAndRotation(out Vector3 headPosition, out Quaternion headRotation);
                mouth.SetPositionAndRotation(headPosition, headRotation);
            }
        }

        private void CaptureNamePlateAnchor(BasisRemotePlayer RemotePlayer)
        {
            float rootScaleY = References.RootScale.y;
            if (float.IsNaN(rootScaleY) || float.IsInfinity(rootScaleY) || rootScaleY <= 1e-6f)
            {
                rootScaleY = 1f;
            }

            if (References.TposeWorld == null ||
                !References.TposeWorld.TryGetValue(HumanBodyBones.Hips, out var tposeHips) ||
                !References.TposeWorld.TryGetValue(HumanBodyBones.Head, out var tposeHead))
            {
                NamePlateHeightAboveHipsModel = BasisNamePlateAnchorMath.LegacyHeightAboveHips / rootScaleY;
                return;
            }

            float eyeAboveRoot = RemotePlayer.BasisAvatar != null ? RemotePlayer.BasisAvatar.AvatarEyePosition.x : 0f;
            bool hasBounds = TryGetTposeRenderedTop(RemotePlayer, out float boundsTopAboveRoot);

            NamePlateHeightAboveHipsModel = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                hipsAboveRoot: tposeHips.position.y,
                headAboveRoot: tposeHead.position.y,
                eyeAboveRoot: eyeAboveRoot,
                boundsTopAboveRoot: boundsTopAboveRoot,
                hasBounds: hasBounds,
                rootScaleY: rootScaleY);
        }

        private bool TryGetTposeRenderedTop(BasisRemotePlayer RemotePlayer, out float topAboveRoot)
        {
            topAboveRoot = 0f;
            Transform animatorRoot = References.AnimatorRoot;
            if (animatorRoot == null)
            {
                return false;
            }

            float rootY = animatorRoot.position.y;
            float highest = float.NegativeInfinity;

            // Renders is the authored full set (body, clothing, accessories); the skinned array is
            // the fallback for avatars that only ever populated that one.
            Renderer[] renderers = RemotePlayer.BasisAvatar != null ? RemotePlayer.BasisAvatar.Renders : null;
            int rendererCount = renderers != null ? renderers.Length : 0;
            for (int Index = 0; Index < rendererCount; Index++)
            {
                Renderer renderer = renderers[Index];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                float top = renderer.bounds.max.y;
                if (top > highest)
                {
                    highest = top;
                }
            }

            if (float.IsNegativeInfinity(highest))
            {
                for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
                {
                    SkinnedMeshRenderer renderer = SkinnedMeshRenderer[Index];
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    float top = renderer.bounds.max.y;
                    if (top > highest)
                    {
                        highest = top;
                    }
                }
            }

            if (float.IsNegativeInfinity(highest) || float.IsNaN(highest))
            {
                return false;
            }

            topAboveRoot = highest - rootY;
            return true;
        }

        private unsafe void CaptureReceiverBoneData(BasisRemotePlayer remotePlayer)
        {
            var receiver = remotePlayer.NetworkReceiver;
            var animator = remotePlayer.BasisAvatar.Animator;

            // Fingers arrive as ten curl/splay scalars and are expanded through the grid baked from
            // THIS avatar, which is what makes them correctly proportioned for whatever rig we are
            // actually drawing rather than the sender's. Interned per Avatar asset, so a crowd in
            // matching avatars pays for one bake.
            if (!BasisHandPoseGrid.TryAcquire(animator, BasisHandPoseGrid.DefaultIncrement, HandGrid))
            {
                BasisDebug.LogError("Failed to acquire hand pose grid; remote fingers will hold their bind pose.",
                    BasisDebug.LogTag.Avatar);
            }
            HandGridGeneration++;
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;

            // Reused across recalibrations rather than disposed and reallocated: boneCount is the
            // constant SyncBoneCount, so the old buffers are always the right size, and every slot
            // is overwritten below. Nothing holds these across the call — AddRemotePlayer snapshots
            // the operators with ToArray() and the transforms are only read during registration —
            // so a swap avoids a Persistent alloc plus a 54-element managed array per avatar load,
            // which at crowd load-in churn is the allocation, not the work.
            EnsureDecodeBuffer(ref receiver.BoneDecodePre, boneCount);
            EnsureDecodeBuffer(ref receiver.BoneDecodePost, boneCount);
            if (receiver.BoneTransforms == null || receiver.BoneTransforms.Length != boneCount)
            {
                receiver.BoneTransforms = new Transform[boneCount];
            }
            quaternion* preOut = (quaternion*)receiver.BoneDecodePre.GetUnsafePtr();
            quaternion* postOut = (quaternion*)receiver.BoneDecodePost.GetUnsafePtr();
            Transform[] boneTransforms = receiver.BoneTransforms;
            int[] writeOrder = BasisBoneRotationCompression.BONE_WRITE_ORDER;

            // Both rest tables are deterministic per Avatar asset — only bone transforms are
            // per-instance — so prefer the per-model cache and fall back to the live dictionaries.
            // TposeLocal and TposeFromRoot must come from the SAME capture: mixing a cached T with
            // a freshly measured F would build an operator pair describing two different rest
            // poses, which is a silent, small, everywhere-at-once pose error.
            EntityId cacheKey = BasisAvatarModelCache.GetKey(animator);
            var cacheEntry = cacheKey != EntityId.None ? BasisAvatarModelCache.GetOrCreate(cacheKey, animator.avatar) : null;
            bool hasCachedRest = cacheEntry?.TposeLocal != null && cacheEntry.TposeFromRoot != null;

            quaternion[] restLocalByBone = hasCachedRest ? cacheEntry.TposeLocal.Rotations : null;
            quaternion[] restFrameByBone = hasCachedRest ? cacheEntry.TposeFromRoot.Rotations : null;

            // The rest frames on both sides of this branch are RAW root-space rotations, which are
            // not a shared frame between two differently-authored rigs — see
            // BasisGenericBoneRotationUtils.GetCharacterBasis. The cached forward/up belongs to the
            // same capture as the cached rotations, so take the basis from whichever source the
            // rotations came from rather than always from the live mapping.
            quaternion characterBasis = hasCachedRest
                ? BasisGenericBoneRotationUtils.GetCharacterBasis(
                    cacheEntry.TposeFromRoot.AvatarForward, cacheEntry.TposeFromRoot.AvatarUp)
                : BasisGenericBoneRotationUtils.GetCharacterBasis(References);

            // Every input to the operator loop below is model-determined once the rest tables are
            // cached, so a second wearer of this avatar only needs the per-instance half: the bone
            // Transforms. The operators come back as one memcpy each, and the managed arrays are
            // handed to the bone-job registration as its deferred snapshot (see CachedDecodePre).
            var cachedOperators = hasCachedRest ? cacheEntry.BoneDecodeOperators : null;
            if (cachedOperators != null)
            {
                receiver.BoneDecodePre.CopyFrom(cachedOperators.Pre);
                receiver.BoneDecodePost.CopyFrom(cachedOperators.Post);
                receiver.CachedDecodePre = cachedOperators.Pre;
                receiver.CachedDecodePost = cachedOperators.Post;
                bool[] hasBone = cachedOperators.HasBone;
                for (int slot = 0; slot < boneCount; slot++)
                {
                    boneTransforms[slot] = hasBone[slot]
                        && References.GetTransform((HumanBodyBones)writeOrder[slot], out var cachedTransform)
                        ? cachedTransform
                        : null;
                }
                return;
            }

            // First wearer of this model: build the operators, and record which slots resolved a
            // bone so the copy path above reproduces this loop's null-transform decisions exactly.
            bool[] slotHasBone = cacheEntry != null ? new bool[boneCount] : null;

            for (int slot = 0; slot < boneCount; slot++)
            {
                int boneEnum = writeOrder[slot];
                var humanbone = (HumanBodyBones)boneEnum;

                if (!References.GetTransform(humanbone, out var transform))
                {
                    // The avatar has no transform for this humanoid bone. This branch used to
                    // be absent and the slot was left at the fresh allocation's zero-init — an
                    // invalid (0,0,0,0) rotation that only stayed harmless because the null
                    // transform drove sSkeletonValid to 0. The buffers are reused now, so an
                    // unwritten slot would register the PREVIOUS avatar's bone transform with
                    // valid = 1 and drive a dead hierarchy. Write the empty slot explicitly.
                    preOut[slot] = quaternion.identity;
                    postOut[slot] = quaternion.identity;
                    boneTransforms[slot] = null;
                    continue;
                }
                if (slotHasBone != null)
                {
                    slotHasBone[slot] = true;
                }

                quaternion restLocal, restFrame;
                if (hasCachedRest)
                {
                    restLocal = restLocalByBone[boneEnum];
                    restFrame = BasisGenericBoneRotationUtils.NormalizeRestFrame(restFrameByBone[boneEnum], characterBasis);
                }
                else
                {
                    restLocal = BasisGenericBoneRotationUtils.GetRestLocal(References, humanbone);
                    restFrame = BasisGenericBoneRotationUtils.GetRestFrame(References, humanbone, characterBasis);
                }

                if (slot >= BasisBoneRotationCompression.WireBoneSlotCount)
                {
                    // Finger slots carry no generic-space rotation since v47 — BasisHandPoseGrid
                    // writes this rig's LOCAL rotation straight into them. Identity operators make
                    // the compose job's DecodePre * value * DecodePost a pass-through instead of a
                    // generic round trip that would only undo itself.
                    preOut[slot] = quaternion.identity;
                    postOut[slot] = quaternion.identity;
                    boneTransforms[slot] = transform;
                    continue;
                }

                BasisGenericBoneRotationUtils.BuildDecodeOperators(restFrame, restLocal,
                    out preOut[slot], out postOut[slot]);
                boneTransforms[slot] = transform;
            }

            // Publish the operators for the next wearer, and use the same arrays as this player's
            // registration snapshot so even the first wearer stops paying two ToArray() copies.
            // Only when the rest tables they were built from are the cached ones — otherwise the
            // pair would describe a rest pose the cache does not hold.
            if (cacheEntry != null && hasCachedRest && cacheEntry.BoneDecodeOperators == null)
            {
                var operators = new BasisAvatarModelCache.BoneDecodeOperatorData
                {
                    Pre = receiver.BoneDecodePre.ToArray(),
                    Post = receiver.BoneDecodePost.ToArray(),
                    HasBone = slotHasBone,
                };
                cacheEntry.BoneDecodeOperators = operators;
                receiver.CachedDecodePre = operators.Pre;
                receiver.CachedDecodePost = operators.Post;
            }
            else
            {
                // No Avatar asset to key an entry on, so there is nothing to share. Mirror into
                // receiver-owned managed arrays rather than letting the registration hold the
                // NativeArrays: the add is DEFERRED and the receiver disposes those on teardown,
                // so a held reference would be a use-after-free rather than merely stale. Reused
                // across recalibrations, exactly like receiver.BoneTransforms.
                if (receiver.OwnedDecodePre == null || receiver.OwnedDecodePre.Length != boneCount)
                {
                    receiver.OwnedDecodePre = new quaternion[boneCount];
                    receiver.OwnedDecodePost = new quaternion[boneCount];
                }
                receiver.BoneDecodePre.CopyTo(receiver.OwnedDecodePre);
                receiver.BoneDecodePost.CopyTo(receiver.OwnedDecodePost);
                receiver.CachedDecodePre = receiver.OwnedDecodePre;
                receiver.CachedDecodePost = receiver.OwnedDecodePost;
            }

            // Store both rest tables for other instances of this avatar. StorePosesToCache fills
            // the same two slots from RecordPosesCached; whichever runs first wins and the other
            // is a no-op, so the pair can never come from two different captures.
            if (cacheEntry != null && !hasCachedRest)
            {
                int totalBones = (int)HumanBodyBones.LastBone;
                if (cacheEntry.TposeLocal == null)
                {
                    cacheEntry.TposeLocal = new BasisAvatarModelCache.TposeLocalData
                    {
                        Rotations = SnapshotRotations(References.TposeLocal, totalBones, out var localPositions),
                        Positions = localPositions
                    };
                }
                if (cacheEntry.TposeFromRoot == null)
                {
                    cacheEntry.TposeFromRoot = new BasisAvatarModelCache.TposeFromRootData
                    {
                        Rotations = SnapshotRotations(References.TposeFromRoot, totalBones, out var rootPositions),
                        Positions = rootPositions,
                        AvatarForward = References.AvatarForwards,
                        AvatarUp = References.AvatarUpwards,
                        AvatarRight = References.AvatarRightwards
                    };
                }
            }
        }

        private static void EnsureDecodeBuffer(ref NativeArray<quaternion> buffer, int boneCount)
        {
            if (buffer.IsCreated && buffer.Length == boneCount)
            {
                return;
            }
            if (buffer.IsCreated)
            {
                buffer.Dispose();
            }
            buffer = new NativeArray<quaternion>(boneCount, Allocator.Persistent);
        }

        private static quaternion[] SnapshotRotations(
            System.Collections.Generic.Dictionary<HumanBodyBones, Basis.Scripts.Common.BasisCalibratedCoords> source,
            int totalBones, out Unity.Mathematics.float3[] positions)
        {
            var rotations = new quaternion[totalBones];
            positions = new Unity.Mathematics.float3[totalBones];
            for (int i = 0; i < totalBones; i++)
            {
                if (source != null && source.TryGetValue((HumanBodyBones)i, out var coords))
                {
                    rotations[i] = coords.rotation;
                    positions[i] = coords.position;
                }
                else
                {
                    rotations[i] = quaternion.identity;
                    positions[i] = Unity.Mathematics.float3.zero;
                }
            }
            return rotations;
        }

        public void SetBodyFit(in Basis.IK.BasisBodyFitResult fit)
        {
            AppliedBodyFit = fit;
            ApplyRemoteBodyFit();
        }

        private void SeedBodyFitFromAvatarRecord(BasisRemotePlayer RemotePlayer)
        {
            if (RemotePlayer == null)
            {
                return;
            }
            AppliedBodyFit = Basis.IK.BasisBodyFitNetworking.ToFitResult(
                RemotePlayer.CACM.ArmScale, RemotePlayer.CACM.LegScale, RemotePlayer.CACM.TorsoScale);
        }

        private void CaptureBodyFitRestLocal()
        {
            Basis.IK.BasisBodyFitApply.CollectBones(References, _fitBones);

            // Authored bind positions are a property of the model, so the first wearer measures and
            // everyone after copies. This is not only cheaper — it removes a drift hazard. Reading
            // the LIVE localPosition means a recalibration of an avatar that already has a fit
            // applied records the SCALED positions as "rest", and the next apply compounds on top.
            Animator animator = Player?.BasisAvatar != null ? Player.BasisAvatar.Animator : null;
            EntityId cacheKey = BasisAvatarModelCache.GetKey(animator);
            BasisAvatarModelCache.Entry entry = null;
            if (cacheKey != EntityId.None && BasisAvatarModelCache.TryGet(cacheKey, out entry)
                && entry.BodyFitRest != null && entry.BodyFitRest.Local.Length == _fitRestLocal.Length)
            {
                System.Array.Copy(entry.BodyFitRest.Local, _fitRestLocal, _fitRestLocal.Length);
                _fitRestCaptured = true;
                return;
            }

            for (int i = 0; i < _fitBones.Length; i++)
            {
                Transform bone = _fitBones[i];
                _fitRestLocal[i] = bone != null ? bone.localPosition : Vector3.zero;
            }
            _fitRestCaptured = true;

            if (cacheKey != EntityId.None)
            {
                BasisAvatarModelCache.GetOrCreate(cacheKey, animator.avatar).BodyFitRest =
                    new BasisAvatarModelCache.BodyFitRestData { Local = (Vector3[])_fitRestLocal.Clone() };
            }
        }

        private void ApplyRemoteBodyFit()
        {
            if (!_fitRestCaptured)
            {
                return;
            }

            Basis.IK.BasisBodyFitApply.CollectScales(in AppliedBodyFit, _fitScales);
            for (int i = 0; i < _fitBones.Length; i++)
            {
                Transform bone = _fitBones[i];
                if (bone != null)
                {
                    bone.localPosition = _fitRestLocal[i] * _fitScales[i];
                }
            }
        }

        public bool CurrentlyTposing;

        private bool NeedsTposeReset;

        public RuntimeAnimatorController SavedruntimeAnimatorController;

        public void PutAvatarIntoTPose()
        {
            // BasisDebug.Log("PutAvatarIntoTPose", BasisDebug.LogTag.Avatar);
            CurrentlyTposing = true;
            if (SavedruntimeAnimatorController == null)
            {
                SavedruntimeAnimatorController = Player.BasisAvatar.Animator.runtimeAnimatorController;
            }

            Player.BasisAvatar.Animator.runtimeAnimatorController = BasisPlayerFactory.TposeController;
            ForceUpdateAnimator(Player.BasisAvatar.Animator);
        }

        public void ForceUpdateAnimator(Animator Anim)
        {
            // Specify the time you want the Animator to update to (in seconds)
            float desiredTime = Time.deltaTime;

            // Call the Update method to force the Animator to update to the desired time
            Anim.Update(desiredTime);
        }

        public void ResetAvatarAnimator()
        {
            // BasisDebug.Log("ResetAvatarAnimator", BasisDebug.LogTag.Avatar);
            Player.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
            SavedruntimeAnimatorController = null;
            CurrentlyTposing = false;
        }

        private int JiggleColliderSetupGeneration;

        public async void SetupAvatarJiggleColliders()
        {
            RemoveJiggleRigColliders();
            int generation = ++JiggleColliderSetupGeneration;
            // Synchronous on a cache hit, which is the normal case by the time calibration runs —
            // CreateAvatar asked for this same UUID moments ago. The await allocated a
            // Task<BasisPlayerSettingsData> and a state machine per calibration otherwise, and
            // every avatar install reaches here.
            if (!BasisPlayerSettingsManager.TryGetCached(Player.UUID, out BasisPlayerSettingsData BasisPlayerSettingsData))
            {
                BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(Player.UUID);
            }
            if (Player == null || Player.IsDestroyed)
            {
                return;
            }
            if (generation != JiggleColliderSetupGeneration)
            {
                return;
            }
            if (BasisPlayerSettingsData.AvatarInteraction && Player.IsConsideredFallBackAvatar == false)
            {
                AddJiggleRigColliders(References, allowColliderLOD: true);
            }
        }

        public bool IsAble(BasisRemotePlayer remotePlayer)
        {
            if (IsNull(remotePlayer.BasisAvatar))
            {
                return false;
            }
            if (IsNull(remotePlayer.BasisAvatar.Animator))
            {
                return false;
            }
            if (remotePlayer == null || remotePlayer.IsDestroyed)
            {
                BasisDebug.LogError("Missing Object during calibration");
                return false;
            }
            return true;
        }

        public bool IsNull(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                BasisDebug.LogError("Missing Object during calibration");
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
