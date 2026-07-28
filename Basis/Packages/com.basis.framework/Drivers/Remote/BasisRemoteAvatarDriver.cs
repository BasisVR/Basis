using Basis.Network.Core.Compression;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Player;
using GatorDragonGames.JigglePhysics;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Drives setup and runtime behavior for a remote player's avatar:
    /// calibration, TPose swap-in/out, nameplate/mouth job registration,
    /// jiggle physics setup, and renderer configuration.
    /// </summary>
    [System.Serializable]
    public class BasisRemoteAvatarDriver : BasisAvatarDriver
    {
        /// <summary>
        /// Invoked after calibration completes successfully.
        /// </summary>
        public Action CalibrationComplete;

        /// <summary>
        /// Cached transform references (head, hips, etc.) auto-detected at calibration.
        /// </summary>
        [SerializeField]
        public BasisTransformMapping References = new BasisTransformMapping();

        /// <summary>
        /// All skinned renderers under the avatar's animator (filled during calibration).
        /// </summary>
        public SkinnedMeshRenderer[] SkinnedMeshRenderer;

        /// <summary>
        /// The associated high-level player wrapper for this avatar.
        /// </summary>
        public IBasisPlayer Player;

        /// <summary>
        /// Whether event hookups (like visibility checks) were made.
        /// </summary>
        public bool HasEvents = false;

        /// <summary>
        /// Cached length of <see cref="SkinnedMeshRenderer"/> to avoid repeated property lookups.
        /// </summary>
        public int SkinnedMeshRendererLength;

        /// <summary>
        /// Initial avatar local scale captured during calibration.
        /// </summary>
        public Vector3 AvatarInitialScale = Vector3.one;

        /// <summary>
        /// Tracks whether this avatar has been registered with the remote bone job system.
        /// </summary>
        public bool InBoneDriver = false;

        /// <summary>
        /// Jiggle rigs on the current avatar (filled during calibration).
        /// </summary>
        /// <summary>Filtered out of the content-harvest snapshot by BasisAvatarFactory at load;
        /// include-inactive, entries can be destroyed later — null-and-activity gate on use.</summary>
        public JiggleRig[] JiggleRigs = Array.Empty<JiggleRig>();
        private static Vector3[] sJiggleRootsBeforeSnap = Array.Empty<Vector3>();

        /// <summary>
        /// The wearer's networked body fit (see Basis.IK.BasisBodyFitCore). They stretch/collapse their
        /// own avatar's arm, leg and spine segments to match their real proportions; without replaying
        /// that here every remote would render them at the avatar's authored proportions instead.
        /// Identity (all 1) is a no-op end to end.
        /// </summary>
        public Basis.IK.BasisBodyFitResult AppliedBodyFit = Basis.IK.BasisBodyFitResult.Identity;

        readonly Transform[] _fitBones = new Transform[Basis.IK.BasisBodyFitApply.BoneCount];
        readonly Vector3[] _fitRestLocal = new Vector3[Basis.IK.BasisBodyFitApply.BoneCount];
        readonly float[] _fitScales = new float[Basis.IK.BasisBodyFitApply.BoneCount];
        bool _fitRestCaptured;

        /// <summary>
        /// Performs remote-avatar calibration and registers it with the job system.
        /// Initializes TPose, references, face visibility, eye/blink drivers, and physics colliders.
        /// </summary>
        /// <param name="RemotePlayer">The remote player whose avatar is being configured.</param>
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
            PutAvatarIntoTPose();

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
            BasisTransformMapping.AutoDetectReferences(Player.BasisAvatar.Animator, Player.BasisAvatar.Animator.transform, ref References, detectArmTwist: true, humanoidBones: Player.BasisAvatar.TransformStorage?.HumanoidBones);
            BasisAvatarModelCache.RecordPosesCached(References, Player.BasisAvatar.Animator);

            // ── Capture T-pose bone rotations and bone transforms for the receiver ──
            // This enables direct bone transform writes (no SetHumanPose needed).
            CaptureReceiverBoneData(RemotePlayer);

            // Capture the fresh authored bind, then apply this player's body fit. Order matters: the
            // rest capture must see authored local positions, so it runs before any fit is written.
            // Seeding from CACM covers every path that supplies an avatar record — a live avatar change,
            // initial load, and the server's late-join replay all set it before calibration runs — while
            // a fit-only update that arrived since is already in AppliedBodyFit and survives the reseed.
            SeedBodyFitFromAvatarRecord(RemotePlayer);
            CaptureBodyFitRestLocal();
            ApplyRemoteBodyFit();

            // Register authored motion (drives non-humanoid transforms the bone job / IK don't touch); rest captured at the current TPose.
            var authoredMotions = RemotePlayer.BasisAvatar.AuthoredMotions;
            if (authoredMotions != null)
            {
                for (int i = 0; i < authoredMotions.Length; i++)
                {
                    BasisAuthoredMotionSystem.Register(authoredMotions[i]);
                }
            }

            // Face visibility setup
            Player.FaceIsVisible = false;
            if (RemotePlayer.BasisAvatar == null)
            {
                BasisDebug.LogError("Missing Avatar On Remote", BasisDebug.LogTag.Avatar);
            }
            if (RemotePlayer.BasisAvatar.FaceVisemeMesh == null)
            {
                BasisDebug.Log("Missing Face for " + Player.DisplayName, BasisDebug.LogTag.Avatar);
            }

            Player.UpdateFaceVisibility(RemotePlayer.BasisAvatar.FaceVisemeMesh.isVisible);
            if (Player.FaceRenderer != null)
            {
                GameObject.Destroy(Player.FaceRenderer);
            }
            Player.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(RemotePlayer.BasisAvatar.FaceVisemeMesh.gameObject);
            Player.FaceRenderer.Check += Player.UpdateFaceVisibility;

            // Blink + eyes
            // Initialize unconditionally — Initialize handles a missing blink mesh
            // gracefully (sets BlinkingEnabled = false) and eye calibration still runs
            // for avatars that only have eye bones.
            RemotePlayer.RemoteFaceDriver.Initialize(Player, RemotePlayer.BasisAvatar);
            // Renderer perf flags
            RemoteRenderMeshSettings(BasisLayerMapper.RemoteAvatarLayer, SkinnedMeshRendererLength, SkinnedMeshRenderer);
            // Seed the skin LOD for the distance this avatar loaded at — ChangeMeshLOD is only
            // edge-triggered on LOD boundary crossings, so a reload at a stable distance never
            // reaches these fresh renderers.
            BasisAvatarSkinLOD.Apply(SkinnedMeshRenderer, SkinnedMeshRendererLength, RemotePlayer.CurrentLodLevel);
            // Snapshot the authored shadow modes before anything reduces them, then seed the tier.
            BasisAvatarShadowLOD.Capture(RemotePlayer);
            BasisAvatarShadowLOD.Apply(RemotePlayer, RemotePlayer.CurrentLodLevel);

            RemotePlayer.BasisAvatar.Animator.logWarnings = false;

            // Ensure stale data is removed
            if (InBoneDriver)
            {
                RemoteBoneJobSystem.RemoveRemotePlayer(RemotePlayer.NetworkReceiver.playerId);
                InBoneDriver = false;
            }

            // Register with the RemoteBoneJobSystem (including skeleton bones for job-based apply).
            // Use the cached References.AnimatorRoot rather than walking through
            // RemotePlayer.BasisAvatar.Animator.transform on each line.
            var receiver = RemotePlayer.NetworkReceiver;
            Transform animatorRoot = References.AnimatorRoot;
            Vector3 animatorRootPos = animatorRoot.position;
            // Sampled before the network rescale below writes this same transform — jiggle collider
            // radii are authored in metres against this scale and rebased off it at build time.
            ColliderScaleReference = animatorRoot.localScale;
            // TPose hips localPosition + localRotation feed the per-frame
            // BulkCopyHipsAndDeriveJob (the inline inverse derivation that
            // turns the received hips world pose into a derived root world
            // pose). Hips world itself is then applied directly via
            // ApplyHipsWorldJob, which is hierarchy-agnostic — these TPose
            // values are only used for the (best-effort) root derivation,
            // not for writing the hips bone's local transform anymore.
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
            // Initialize this player's interpolation slot before registering it with the bone
            // job system. The bone Schedule reads _filtered*[playerId] earlier in LateUpdate than
            // BeginWrite's lazy init runs (LateUpdate tail), so a cached/fallback avatar that
            // calibrates within a frame of joining would otherwise be read from uninitialized
            // memory and pose as NaN.
            BasisRemoteNetworkDriver.EnsureSlotInitialized(receiver.playerId);
            RemoteBoneJobSystem.AddRemotePlayer(
                key: receiver.playerId,
                remotePlayerRoot: animatorRoot,
                head: References.head,
                hips: References.Hips,
                tposeHead: References.TposeFromRoot[HumanBodyBones.Head],
                tposeHips: References.TposeFromRoot[HumanBodyBones.Hips],
                tposeHipsLocalPos: tposeHipsLocalPos,
                tposeHipsLocalRot: tposeHipsLocalRot,
                authoredCenterEyeWorld: BasisHelpers.ConvertFromLocalSpace(
                    BasisHelpers.AvatarPositionConversion(RemotePlayer.BasisAvatar.AvatarEyePosition),
                    animatorRootPos
                ),
                authoredMouthWorld: BasisHelpers.ConvertFromLocalSpace(
                    BasisHelpers.AvatarPositionConversion(RemotePlayer.BasisAvatar.AvatarMouthPosition),
                    animatorRootPos
                ),
                NamePlate: RemotePlayer.NamePlateTransformProvider?.Invoke(),
                AvatarScale: animatorRoot,
                MouthTransform: RemotePlayer.MouthTransform,
                TposedScale: RemotePlayer.RemoteAvatarDriver.AvatarInitialScale,
                boneTPoseLocal: receiver.TposeLocalRotations,
                boneTransforms: receiver.BoneTransforms
            );
            InBoneDriver = true;

            // player.RemoteBoneDriver.InitializeFromAvatar(player);
            RemotePlayer.BasisAvatar.Animator.enabled = false;

            SetupAvatarJiggleColliders();
            ResetAvatarAnimator();

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
                if (snapRig == null || !snapRig.gameObject.activeInHierarchy)
                {
                    continue;
                }
                var jiggleRoot = snapRig.GetJiggleRigData().rootBone;
                if (jiggleRoot != null)
                {
                    jiggleRootsBeforeSnap[Index] = jiggleRoot.position;
                }
            }

            // Apply scale before snapping any pose so localScale is in place for
            // the first frame; UpdateAllAvatarsJob's HasScaleChange tick would
            // otherwise overwrite the network scale on the first iteration.
            // GetLatestNetworkPose returns HIPS world (high-precision channel,
            // also what the server reduction system reads).
            receiver.GetLatestNetworkPose(out var hipsWorldPos, out var hipsWorldRot, out var networkScale);
            animatorRoot.localScale = networkScale;
            BasisRemoteNetworkDriver.SeedScaleState(receiver.playerId, networkScale);

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

            // Initialize any jiggle rigs. Performance-limit enforcement lives in
            // BasisAvatarPerformanceLimits.TrimExcessComponents (called earlier by
            // BasisAvatarFactory.InitializePlayerAvatar), so by the time we get
            // here the tree has already been trimmed to the allowed count — this
            // loop just wires up whatever's left.
            for (int Index = 0; Index < jiggleRigCount; Index++)
            {
                JiggleRig Rig = JiggleRigs[Index];
                if (Rig == null || !Rig.gameObject.activeInHierarchy)
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

            CalibrationComplete?.Invoke();
        }

        /// <summary>
        /// Captures T-pose local rotations and bone Transform references for all 54 humanoid bones.
        /// Populates the receiver's TposeLocalRotations and BoneTransforms arrays so that
        /// Apply() can write bone transforms directly without SetHumanPose.
        /// Must be called while the avatar is in T-pose (before ResetAvatarAnimator).
        /// </summary>
        private unsafe void CaptureReceiverBoneData(BasisRemotePlayer remotePlayer)
        {
            var receiver = remotePlayer.NetworkReceiver;
            var animator = remotePlayer.BasisAvatar.Animator;
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;

            // Dispose old data if re-calibrating
            if (receiver.TposeLocalRotations.IsCreated)
            {
                receiver.TposeLocalRotations.Dispose();
            }

            receiver.TposeLocalRotations = new NativeArray<quaternion>(boneCount, Allocator.Persistent);
            receiver.BoneTransforms = new Transform[boneCount];
            quaternion* tposeOut = (quaternion*)receiver.TposeLocalRotations.GetUnsafePtr();
            Transform[] boneTransforms = receiver.BoneTransforms;
            int[] writeOrder = BasisBoneRotationCompression.BONE_WRITE_ORDER;

            // Check if T-pose local rotations are already cached for this avatar model.
            // The rotations are deterministic per Avatar asset — only bone transforms are per-instance.
            EntityId cacheKey = BasisAvatarModelCache.GetKey(animator);
            var cacheEntry = cacheKey != EntityId.None ? BasisAvatarModelCache.GetOrCreate(cacheKey) : null;
            bool hasCachedTpose = cacheEntry?.TposeLocal != null;

            if (hasCachedTpose)
            {
                // Fast path: copy cached rotations, only resolve per-instance bone transforms
                var cachedRotations = cacheEntry.TposeLocal.Rotations;
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int boneEnum = writeOrder[slot];
                    tposeOut[slot] = cachedRotations[boneEnum];
                    boneTransforms[slot] = References.GetTransform((HumanBodyBones)boneEnum, out var transform) ? transform : null;
                }
            }
            else
            {
                // Slow path: read from TposeLocal dictionary, then cache for next time
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int boneEnum = writeOrder[slot];
                    var humanbone = (HumanBodyBones)boneEnum;
                    if (References.GetTransform(humanbone, out var transform))
                    {
                        if (References.TposeLocal.TryGetValue(humanbone, out var value))
                        {
                            tposeOut[slot] = value.rotation;
                            boneTransforms[slot] = transform;
                        }
                        else
                        {
                            tposeOut[slot] = quaternion.identity;
                            boneTransforms[slot] = null;
                        }
                    }
                }

                // Store T-pose local rotations in cache for other instances of this avatar
                if (cacheEntry != null)
                {
                    int totalBones = (int)HumanBodyBones.LastBone;
                    var rotations = new quaternion[totalBones];
                    var positions = new Unity.Mathematics.float3[totalBones];
                    for (int i = 0; i < totalBones; i++)
                    {
                        var bone = (HumanBodyBones)i;
                        if (References.TposeLocal.TryGetValue(bone, out var coords))
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
                    cacheEntry.TposeLocal = new BasisAvatarModelCache.TposeLocalData
                    {
                        Rotations = rotations,
                        Positions = positions
                    };
                }
            }
        }

        /// <summary>
        /// Applies the wearer's networked body fit to this remote avatar. Safe to call before the avatar
        /// has loaded — the fit is stored and replayed by RemoteCalibration once the bind is captured.
        /// Main thread only (touches Transform.localPosition).
        /// </summary>
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

        /// <summary>
        /// Snapshots the authored local positions of every fitted bone. Keying off this copy rather than
        /// the live value is what makes re-applying idempotent — writing rest*scale over an already
        /// scaled transform would compound the fit every time a new one arrived.
        /// </summary>
        private void CaptureBodyFitRestLocal()
        {
            Basis.IK.BasisBodyFitApply.CollectBones(References, _fitBones);
            for (int i = 0; i < _fitBones.Length; i++)
            {
                Transform bone = _fitBones[i];
                _fitRestLocal[i] = bone != null ? bone.localPosition : Vector3.zero;
            }
            _fitRestCaptured = true;
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

        /// <summary>
        /// True while the avatar is temporarily swapped to a TPose animator.
        /// </summary>
        public bool CurrentlyTposing;

        /// <summary>
        /// Stores the original animator controller while TPose is active.
        /// </summary>
        public RuntimeAnimatorController SavedruntimeAnimatorController;

        /// <summary>
        /// Loads and applies a TPose controller to the avatar's animator,
        /// forcing an update so bone poses are consistent for reference capture.
        /// </summary>
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

        /// <summary>
        /// Forces the animator to advance by <see cref="Time.deltaTime"/> to apply state changes immediately.
        /// </summary>
        /// <param name="Anim">Animator to update.</param>
        public void ForceUpdateAnimator(Animator Anim)
        {
            // Specify the time you want the Animator to update to (in seconds)
            float desiredTime = Time.deltaTime;

            // Call the Update method to force the Animator to update to the desired time
            Anim.Update(desiredTime);
        }

        /// <summary>
        /// Restores the original animator controller after TPose operations and clears flags.
        /// </summary>
        public void ResetAvatarAnimator()
        {
            // BasisDebug.Log("ResetAvatarAnimator", BasisDebug.LogTag.Avatar);
            Player.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
            SavedruntimeAnimatorController = null;
            CurrentlyTposing = false;
        }

        /// <summary>
        /// Rebuilds jiggle rig colliders based on player settings (async).
        /// Removes existing colliders, fetches settings, then conditionally adds new ones.
        /// </summary>
        public async void SetupAvatarJiggleColliders()
        {
            RemoveJiggleRigColliders();
            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(Player.UUID);
            if (Player == null || Player.IsDestroyed)
            {
                return;
            }
            if (BasisPlayerSettingsData.AvatarInteraction && Player.IsConsideredFallBackAvatar == false)
            {
                AddJiggleRigColliders(References, allowColliderLOD: true);
            }
        }

        /// <summary>
        /// Validates that the provided remote player and its avatar/animator are present.
        /// </summary>
        /// <param name="remotePlayer">Remote player to test.</param>
        /// <returns>True if calibration may proceed; otherwise false.</returns>
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

        /// <summary>
        /// Logs and returns whether the provided Unity object reference is null.
        /// </summary>
        /// <param name="obj">Unity object to test.</param>
        /// <returns>True if null; otherwise false.</returns>
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
