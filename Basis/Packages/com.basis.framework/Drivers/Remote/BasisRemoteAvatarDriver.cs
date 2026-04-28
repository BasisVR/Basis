using Basis.Network.Core.Compression;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using GatorDragonGames.JigglePhysics;
using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;

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
        public BasisPlayer Player;

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
        public Vector3 AvatarInitalScale = Vector3.one;

        /// <summary>
        /// Tracks whether this avatar has been registered with the remote bone job system.
        /// </summary>
        public bool InBoneDriver = false;

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
            SkinnedMeshRenderer = Player.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
            PutAvatarIntoTPose();

            RemotePlayer.BasisAvatar.HumanScale = RemotePlayer.BasisAvatar.Animator.humanScale;
            RemotePlayer.BasisAvatar.Animator.applyRootMotion = false;
            RemotePlayer.BasisAvatar.Animator.updateMode = AnimatorUpdateMode.Normal;
            RemotePlayer.BasisAvatar.Animator.speed = 0;
            AvatarInitalScale = Player.BasisAvatar.transform.localScale;

            // Auto-detect bone refs and record TPose
            BasisTransformMapping.AutoDetectReferences(Player.BasisAvatar.Animator, RemotePlayer.BasisAvatar.transform, ref References);
            BasisAvatarModelCache.RecordPosesCached(References, Player.BasisAvatar.Animator);

            // ── Capture T-pose bone rotations and bone transforms for the receiver ──
            // This enables direct bone transform writes (no SetHumanPose needed).
            CaptureReceiverBoneData(RemotePlayer);

            // Initialize any jiggle rigs. Performance-limit enforcement lives in
            // BasisAvatarPerformanceLimits.TrimExcessComponents (called earlier by
            // BasisAvatarFactory.InitializePlayerAvatar), so by the time we get
            // here the tree has already been trimmed to the allowed count — this
            // loop just wires up whatever's left.
            var JiggleRigs = RemotePlayer.BasisAvatar.GetComponentsInChildren<JiggleRig>();
            int length = JiggleRigs.Length;
            for (int Index = 0; Index < length; Index++)
            {
                JiggleRig Rig = JiggleRigs[Index];
                JiggleRigData Data = Rig.GetJiggleRigData();
                Rig.HasAnimatedParameters = false;
                Rig.OnInitialize();
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

            RemotePlayer.BasisAvatar.Animator.logWarnings = false;

            // Ensure stale data is removed
            if (InBoneDriver)
            {
                RemoteBoneJobSystem.RemoveRemotePlayer(RemotePlayer.NetworkReceiver.playerId);
                InBoneDriver = false;
            }

            // Register with the RemoteBoneJobSystem (including skeleton bones for job-based apply)
            var receiver = RemotePlayer.NetworkReceiver;
            RemoteBoneJobSystem.AddRemotePlayer(
                key: receiver.playerId,
                remotePlayerRoot: RemotePlayer.BasisAvatar.Animator.transform,
                head: RemotePlayer.RemoteAvatarDriver.References.head,
                hips: RemotePlayer.RemoteAvatarDriver.References.Hips,
                tposeHead: RemotePlayer.RemoteAvatarDriver.References.TposeFromRoot[HumanBodyBones.Head],
                tposeHips: RemotePlayer.RemoteAvatarDriver.References.TposeFromRoot[HumanBodyBones.Hips],
                authoredCenterEyeWorld: BasisHelpers.ConvertFromLocalSpace(
                    BasisHelpers.AvatarPositionConversion(RemotePlayer.BasisAvatar.AvatarEyePosition),
                    RemotePlayer.BasisAvatar.Animator.transform.position
                ),
                authoredMouthWorld: BasisHelpers.ConvertFromLocalSpace(
                    BasisHelpers.AvatarPositionConversion(RemotePlayer.BasisAvatar.AvatarMouthPosition),
                    RemotePlayer.BasisAvatar.Animator.transform.position
                ),
                NamePlate: RemotePlayer.RemoteNamePlate.Self,
                AvatarScale: RemotePlayer.BasisAvatar.Animator.transform,
                MouthTransform: RemotePlayer.MouthTransform,
                TposedScale: RemotePlayer.RemoteAvatarDriver.AvatarInitalScale,
                boneTPoseLocal: receiver.TposeLocalRotations,
                boneTransforms: receiver.BoneTransforms
            );
            InBoneDriver = true;

            // player.RemoteBoneDriver.InitializeFromAvatar(player);
            RemotePlayer.BasisAvatar.Animator.enabled = false;

            SetupAvatarJiggleColliders();
            ResetAvatarAnimator();

            // Apply scale BEFORE hips: SetPositionAndRotation bakes the parent
            // lossyScale into the hips localPosition, so the root must already be
            // at its network scale when we snap the hips. If we skipped this, the
            // avatar would spawn at prefab scale (1,1,1) and the hips would land
            // under the wrong parent scale until UpdateAllAvatarsJob produced a
            // HasScaleChange tick — visible as "scale wrong when a player joins".
            receiver.GetLatestNetworkPose(out var networkPos, out var networkRot, out var networkScale);
            RemotePlayer.BasisAvatar.Animator.transform.localScale = networkScale;
            // Seed the job system's scale-tracking slots to the same value.
            // Without this, the first UpdateAllAvatarsJob tick (before
            // SetFrameInputs seeds the real interp window) would compute outScale
            // from stale prev/target scales and ApplyAvatarScaleJob would clobber
            // the value we just wrote.
            BasisRemoteNetworkDriver.SeedScaleState(receiver.playerId, networkScale);
            References.Hips.SetPositionAndRotation(networkPos, networkRot);
            CalibrationComplete?.Invoke();
        }

        /// <summary>
        /// Captures T-pose local rotations and bone Transform references for all 54 humanoid bones.
        /// Populates the receiver's TposeLocalRotations and BoneTransforms arrays so that
        /// Apply() can write bone transforms directly without SetHumanPose.
        /// Must be called while the avatar is in T-pose (before ResetAvatarAnimator).
        /// </summary>
        private void CaptureReceiverBoneData(BasisRemotePlayer remotePlayer)
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

            // Check if T-pose local rotations are already cached for this avatar model.
            // The rotations are deterministic per Avatar asset — only bone transforms are per-instance.
            int cacheKey = BasisAvatarModelCache.GetKey(animator);
            var cacheEntry = cacheKey != 0 ? BasisAvatarModelCache.GetOrCreate(cacheKey) : null;
            bool hasCachedTpose = cacheEntry?.TposeLocal != null;

            if (hasCachedTpose)
            {
                // Fast path: copy cached rotations, only resolve per-instance bone transforms
                var cachedRotations = cacheEntry.TposeLocal.Rotations;
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int boneEnum = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];
                    var humanbone = (HumanBodyBones)boneEnum;

                    receiver.TposeLocalRotations[slot] = cachedRotations[boneEnum];

                    if (References.GetTransform(humanbone, out var transform))
                    {
                        receiver.BoneTransforms[slot] = transform;
                    }
                    else
                    {
                        receiver.BoneTransforms[slot] = null;
                    }
                }
            }
            else
            {
                // Slow path: read from TposeLocal dictionary, then cache for next time
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int boneEnum = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];
                    var humanbone = (HumanBodyBones)boneEnum;
                    if (References.GetTransform(humanbone, out var transform))
                    {
                        if (References.TposeLocal.TryGetValue(humanbone, out var value))
                        {
                            receiver.TposeLocalRotations[slot] = value.rotation;
                            receiver.BoneTransforms[slot] = transform;
                        }
                        else
                        {
                            receiver.TposeLocalRotations[slot] = quaternion.identity;
                            receiver.BoneTransforms[slot] = null;
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

            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<RuntimeAnimatorController> op =
                Addressables.LoadAssetAsync<RuntimeAnimatorController>(TPose);
            RuntimeAnimatorController RAC = op.WaitForCompletion();
            Player.BasisAvatar.Animator.runtimeAnimatorController = RAC;
            ForceUpdateAnimator(Player.BasisAvatar.Animator);
        }

        /// <summary>
        /// Addressable path for the TPose controller asset.
        /// </summary>
        public const string TPose = "Assets/Animator/Animated TPose.controller";

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
            if (BasisPlayerSettingsData.AvatarInteraction && Player.IsConsideredFallBackAvatar == false)
            {
                AddJiggleRigColliders(References);
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
            if (IsNull(remotePlayer))
            {
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
