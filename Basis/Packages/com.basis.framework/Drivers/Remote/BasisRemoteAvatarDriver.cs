using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using GatorDragonGames.JigglePhysics;
using System;
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
            References.RecordPoses(Player.BasisAvatar.Animator);

            // Initialize any jiggle rigs
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
            if (BasisRemoteFaceDriver.MeetsRequirements(RemotePlayer.BasisAvatar))
            {
                RemotePlayer.RemoteFaceDriver.Initialize(Player, RemotePlayer.BasisAvatar);
            }
            // Renderer perf flags
            RemoteRenderMeshSettings(BasisLayerMapper.RemoteAvatarLayer, SkinnedMeshRendererLength, SkinnedMeshRenderer);

            RemotePlayer.BasisAvatar.Animator.logWarnings = false;

            // Ensure stale data is removed
            if (InBoneDriver)
            {
                RemoteBoneJobSystem.RemoveRemotePlayer(RemotePlayer.NetworkReceiver.playerId);
                InBoneDriver = false;
            }

            // Register with the RemoteBoneJobSystem
            RemoteBoneJobSystem.AddRemotePlayer(
                key: RemotePlayer.NetworkReceiver.playerId,
                remotePlayerRoot: RemotePlayer.BasisAvatar.Animator.transform,
                head: RemotePlayer.RemoteAvatarDriver.References.head,
                hips: RemotePlayer.RemoteAvatarDriver.References.Hips,
                tposeHead: RemotePlayer.RemoteAvatarDriver.References.Tpose[HumanBodyBones.Head],
                tposeHips: RemotePlayer.RemoteAvatarDriver.References.Tpose[HumanBodyBones.Hips],
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
                TposedScale: RemotePlayer.RemoteAvatarDriver.AvatarInitalScale
            );
            InBoneDriver = true;

            // player.RemoteBoneDriver.InitializeFromAvatar(player);
            RemotePlayer.BasisAvatar.Animator.enabled = false;

            SetupAvatarJiggleColliders();
            ResetAvatarAnimator();

            // Fire optional callback
            CalibrationComplete?.Invoke();
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
