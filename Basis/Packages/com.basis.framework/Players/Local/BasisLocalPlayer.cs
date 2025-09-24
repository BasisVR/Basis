using Basis.Scripts.Animator_Driver;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Eye_Follow;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Local player controller that coordinates camera, character, rig, avatar, hands,
    /// visemes, input, calibration, and scene lifecycle for the current user.
    /// </summary>
    /// <remarks>
    /// Use <see cref="LocalInitialize"/> to wire up drivers, load the initial avatar,
    /// and signal readiness. Subscribe to events like <see cref="OnLocalPlayerCreatedAndReady"/>
    /// to know when the player has finished bootstrapping.
    /// </remarks>
    public class BasisLocalPlayer : BasisPlayer
    {
        /// <summary>
        /// Singleton-like reference to the active local player instance.
        /// </summary>
        public static BasisLocalPlayer Instance;

        /// <summary>
        /// True when the local player has completed initialization and is ready for interaction.
        /// </summary>
        public static bool PlayerReady = false;

        /// <summary>
        /// Fallback height (meters) used when no measurement is available.
        /// </summary>
        public const float FallbackSize = 1.7f;

        /// <summary>
        /// Default measured eye height for the player (meters).
        /// </summary>
        public static float DefaultPlayerEyeHeight = FallbackSize;

        /// <summary>
        /// Default measured eye height for the avatar (meters).
        /// </summary>
        public static float DefaultAvatarEyeHeight = FallbackSize;

        /// <summary>
        /// Default measured arm span for the player (meters).
        /// </summary>
        public static float DefaultPlayerArmSpan = FallbackSize;

        /// <summary>
        /// Default measured arm span for the avatar (meters).
        /// </summary>
        public static float DefaultAvatarArmSpan = FallbackSize;

        /// <summary>
        /// File name used to persist the last-used avatar reference.
        /// </summary>
        public static string LoadFileNameAndExtension = "LastUsedAvatar.BAS";

        /// <summary>
        /// Guards registration of global/local events to avoid duplicate subscriptions.
        /// </summary>
        public static bool HasEvents = false;

        /// <summary>
        /// If true, the player is spawned automatically when a new scene is loaded.
        /// </summary>
        public static bool SpawnPlayerOnSceneLoad = true;

        /// <summary>
        /// Guards calibration-related event hookups.
        /// </summary>
        public static bool HasCalibrationEvents = false;

        /// <summary>
        /// Fired once the local player has completed <see cref="LocalInitialize"/> and is ready.
        /// </summary>
        public static Action OnLocalPlayerCreatedAndReady;

        /// <summary>
        /// Fired as soon as the local player object is created and begins initialization.
        /// </summary>
        public static Action OnLocalPlayerCreated;

        /// <summary>
        /// Fired whenever the local avatar asset changes (including initial creation).
        /// </summary>
        public static Action OnLocalAvatarChanged;

        /// <summary>
        /// Fired after the player has been spawned/teleported into the scene.
        /// </summary>
        public static Action OnSpawnedEvent;

        /// <summary>
        /// Fired on the frame after a player height change is requested.
        /// </summary>
        public static Action OnPlayersHeightChangedNextFrame;

        /// <summary>
        /// Ordered delegate queue invoked after all movement and simulation have completed for the frame.
        /// </summary>
        public static BasisOrderedDelegate AfterFinalMove = new BasisOrderedDelegate();

        #region Drivers

        /// <summary>
        /// Controls activation and positioning of the local camera rig.
        /// </summary>
        [Header("Camera Driver")]
        [SerializeField]
        public BasisLocalCameraDriver LocalCameraDriver;

        /// <summary>
        /// Maps tracked devices to avatar bones and performs bone simulation.
        /// </summary>
        [Header("Bone Driver")]
        [SerializeField]
        public BasisLocalBoneDriver LocalBoneDriver = new BasisLocalBoneDriver();

        /// <summary>
        /// Handles avatar calibration and avatar-specific behaviors for the local player.
        /// </summary>
        [Header("Calibration And Avatar Driver")]
        [SerializeField]
        public BasisLocalAvatarDriver LocalAvatarDriver = new BasisLocalAvatarDriver();

        /// <summary>
        /// Manages IK targets and rig constraints for the local avatar.
        /// </summary>
        [Header("Rig Driver")]
        [SerializeField]
        public BasisLocalRigDriver LocalRigDriver = new BasisLocalRigDriver();

        /// <summary>
        /// Character controller for movement, collisions, and physics.
        /// </summary>
        [Header("Character Driver")]
        [SerializeField]
        public BasisLocalCharacterDriver LocalCharacterDriver = new BasisLocalCharacterDriver();

        /// <summary>
        /// Animator controller that blends animation states and applies weights each frame.
        /// </summary>
        [Header("Animator Driver")]
        [SerializeField]
        public BasisLocalAnimatorDriver LocalAnimatorDriver = new BasisLocalAnimatorDriver();

        /// <summary>
        /// Finger pose driver for hand tracking/controllers.
        /// </summary>
        [Header("Hand Driver")]
        [SerializeField]
        public BasisLocalHandDriver LocalHandDriver = new BasisLocalHandDriver();

        /// <summary>
        /// Eye gaze/pupil/eyelid driver for the local avatar.
        /// </summary>
        [Header("Eye Driver")]
        [SerializeField]
        public BasisLocalEyeDriver LocalEyeDriver = new BasisLocalEyeDriver();

        /// <summary>
        /// Audio capture and viseme (mouth shape) driver for lip sync.
        /// </summary>
        [Header("Mouth & Visemes Driver")]
        [SerializeField]
        public BasisAudioAndVisemeDriver LocalVisemeDriver = new BasisAudioAndVisemeDriver();

        #endregion

        /// <summary>
        /// Stores the current height measurements and derived values for the local player.
        /// </summary>
        [Header("Height Information")]
        public BasisLocalHeightInformation CurrentHeight = new BasisLocalHeightInformation();

        /// <summary>
        /// Bootstraps the local player by wiring up drivers, input, and events, and loading the initial avatar.
        /// </summary>
        /// <returns>A task that completes when initialization and avatar load are finished.</returns>
        public async Task LocalInitialize()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }

            BasisLocalMicrophoneDriver.OnPausedAction += LocalVisemeDriver.OnPausedEvent;

            OnLocalPlayerCreated?.Invoke();
            IsLocal = true;

            LocalBoneDriver.CreateInitialArrays(true);
            LocalBoneDriver.Initialize();
            LocalHandDriver.Initialize();

            BasisDeviceManagement.Instance.InputActions.Initialize(this);
            LocalCharacterDriver.Initialize(this);
            LocalCameraDriver.gameObject.SetActive(true);

            if (HasEvents == false)
            {
                OnLocalAvatarChanged += OnCalibration;
                SceneManager.sceneLoaded += OnSceneLoadedCallback;
                HasEvents = true;
            }

            bool LoadedState = BasisDataStore.LoadAvatar(
                LoadFileNameAndExtension,
                BasisBeeConstants.DefaultAvatar,
                LoadModeLocal,
                out BasisDataStore.BasisSavedAvatar LastUsedAvatar);

            if (LoadedState)
            {
                await LoadInitialAvatar(LastUsedAvatar);
            }
            else
            {
                await CreateAvatar(LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }

            BasisLocalMicrophoneDriver.Initialize();
            PlayerReady = true;
            OnLocalPlayerCreatedAndReady?.Invoke();

            BasisScene BasisScene = FindFirstObjectByType<BasisScene>(FindObjectsInactive.Exclude);
            if (BasisScene != null)
            {
                BasisSceneFactory.Initalize(BasisScene);
            }
            else
            {
                BasisDebug.LogError("Cant Find Basis Scene");
            }

            BasisUILoadingBar.Initalize();
        }

        /// <summary>
        /// Loads the last-used avatar if present on disk and key is available; otherwise falls back to the loading avatar.
        /// </summary>
        /// <param name="LastUsedAvatar">Metadata pointing to the last persisted avatar selection.</param>
        public async Task LoadInitialAvatar(BasisDataStore.BasisSavedAvatar LastUsedAvatar)
        {
            if (BasisLoadHandler.IsMetaDataOnDisc(LastUsedAvatar.UniqueID, out BasisBEEExtensionMeta info))
            {
                await BasisDataStoreAvatarKeys.LoadKeys();
                List<BasisDataStoreAvatarKeys.AvatarKey> activeKeys = BasisDataStoreAvatarKeys.DisplayKeys();
                foreach (BasisDataStoreAvatarKeys.AvatarKey Key in activeKeys)
                {
                    if (Key.Url == LastUsedAvatar.UniqueID)
                    {
                        BasisLoadableBundle bundle = new BasisLoadableBundle
                        {
                            BasisRemoteBundleEncrypted = info.StoredRemote,
                            BasisBundleConnector = new BasisBundleConnector(
                                "1",
                                new BasisBundleDescription("Loading Avatar", "Loading Avatar"),
                                new BasisBundleGenerated[] { new BasisBundleGenerated() },
                                null),
                            BasisLocalEncryptedBundle = info.StoredLocal,
                            UnlockPassword = Key.Pass
                        };
                        BasisDebug.Log("loading previously loaded avatar", BasisDebug.LogTag.Avatar);
                        await CreateAvatar(LastUsedAvatar.loadmode, bundle);
                        return;
                    }
                }
                BasisDebug.Log("failed to load last used : no key found to load but was found on disc", BasisDebug.LogTag.Avatar);
                await CreateAvatar(LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
            else
            {
                BasisDebug.Log("failed to load last used : url was not found on disc", BasisDebug.LogTag.Avatar);
                await CreateAvatar(LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
        }

        /// <summary>
        /// Teleports the local player to a world position and rotation, then re-enables character motion and notifies listeners.
        /// </summary>
        /// <param name="position">Target world position.</param>
        /// <param name="rotation">Target world rotation.</param>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            BasisDebug.Log("Teleporting", BasisDebug.LogTag.Local);
            LocalCharacterDriver.IsEnabled = false;
            this.transform.SetPositionAndRotation(position, rotation);
            LocalCharacterDriver.IsEnabled = true;
            LocalAnimatorDriver.HandleTeleport();
            OnSpawnedEvent?.Invoke();
        }

        /// <summary>
        /// Scene-load callback that optionally spawns the player when a new scene is activated.
        /// </summary>
        /// <param name="scene">The loaded scene.</param>
        /// <param name="mode">The loading mode used.</param>
        public void OnSceneLoadedCallback(Scene scene, LoadSceneMode mode)
        {
            if (SpawnPlayerOnSceneLoad)
            {
                // swap over to on scene load
                BasisSceneFactory.SpawnPlayer(this);
            }
        }

        /// <summary>
        /// Creates or replaces the local avatar using the specified load mode and bundle, then persists the selection.
        /// </summary>
        /// <param name="LoadMode">Avatar load mode (e.g., <see cref="LoadModeLocal"/> for local).</param>
        /// <param name="BasisLoadableBundle">Bundle describing the avatar to load.</param>
        public async Task CreateAvatar(byte LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            await BasisAvatarFactory.LoadAvatarLocal(this, LoadMode, BasisLoadableBundle, this.transform.position, Quaternion.identity);
            BasisDataStore.SaveAvatar(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, LoadMode, LoadFileNameAndExtension);
            OnLocalAvatarChanged?.Invoke();
        }

        /// <summary>
        /// Overload that accepts a strongly typed load mode enum and forwards to <see cref="CreateAvatar(byte, BasisLoadableBundle)"/>.
        /// </summary>
        /// <param name="LoadMode">Typed load mode.</param>
        /// <param name="BasisLoadableBundle">Bundle describing the avatar to load.</param>
        public async Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            byte LoadByte = (byte)LoadMode;
            await CreateAvatar(LoadByte, BasisLoadableBundle);
        }

        /// <summary>
        /// Runs calibration-dependent hookups (visemes, microphone events) when the local avatar changes.
        /// </summary>
        public void OnCalibration()
        {
            LocalVisemeDriver.TryInitialize(this);
            if (HasCalibrationEvents == false)
            {
                BasisLocalMicrophoneDriver.OnHasAudio += DriveAudioToViseme;
                BasisLocalMicrophoneDriver.OnHasSilence += DriveAudioToViseme;
                HasCalibrationEvents = true;
            }
        }

        /// <summary>
        /// Cleans up event subscriptions, disposes drivers, and deinitializes microphone and UI systems.
        /// </summary>
        public void OnDestroy()
        {
            if (HasEvents)
            {
                OnLocalAvatarChanged -= OnCalibration;
                SceneManager.sceneLoaded -= OnSceneLoadedCallback;
                HasEvents = false;
            }
            if (HasCalibrationEvents)
            {
                BasisLocalMicrophoneDriver.OnHasAudio -= DriveAudioToViseme;
                BasisLocalMicrophoneDriver.OnHasSilence -= DriveAudioToViseme;
                HasCalibrationEvents = false;
            }
            BasisLocalMicrophoneDriver.DeInitialize();

            if (LocalHandDriver != null)
            {
                LocalHandDriver.Dispose();
            }
            if (LocalEyeDriver != null)
            {
                LocalEyeDriver.OnDestroy(this);
            }
            if (FacialBlinkDriver != null)
            {
                FacialBlinkDriver.OnDestroy();
            }

            BasisLocalMicrophoneDriver.OnPausedAction -= LocalVisemeDriver.OnPausedEvent;
            LocalAnimatorDriver.OnDestroy();
            LocalBoneDriver.DeInitializeGizmos();
            BasisUILoadingBar.DeInitalize();
        }

        /// <summary>
        /// Pushes microphone audio samples into the viseme driver for lip-sync processing.
        /// </summary>
        public void DriveAudioToViseme()
        {
            LocalVisemeDriver.ProcessAudioSamples(
                BasisLocalMicrophoneDriver.processBufferArray,
                1,
                BasisLocalMicrophoneDriver.processBufferArray.Length);
        }

        /// <summary>
        /// Per-frame late-update hook for facial blink simulation.
        /// </summary>
        public void SimulateOnLateUpdate()
        {
            FacialBlinkDriver.Simulate();
        }

        /// <summary>
        /// Main per-frame simulation entry point, executed on render/update.
        /// Performs movement, bone simulation, T-pose driving, IK targets, animator evaluation, hands,
        /// and then invokes <see cref="AfterFinalMove"/>.
        /// </summary>
        /// <param name="DeltaTime">Frame delta time.</param>
        public void SimulateOnRender(float DeltaTime)
        {
            // now lets move the local player position.
            LocalCharacterDriver.SimulateMovement(DeltaTime);

            // moves all bones to where they belong
            LocalBoneDriver.SimulateAndApply(this, DeltaTime);

            // moves Avatar Hip Transform to where it belongs in tpose.
            if (BasisLocalAvatarDriver.CurrentlyTposing)
            {
                DriveTpose();
            }

            // Simulate Final Destination of IK then process Animator and IK processes.
            LocalRigDriver.SimulateIKDestinations(DeltaTime);

            // update WorldPosition in BoneDriver so AfterFinalMove can use world coords
            LocalBoneDriver.SimulateWorldDestinations(transform.localToWorldMatrix);

            // Apply Animator Weights using most current data and outside movement effectors.
            LocalAnimatorDriver.SimulateAnimator(DeltaTime);

            // handles fingers
            LocalHandDriver.UpdateFingers(DeltaTime);

            // now other things can move like UI and NON-CHILDREN OF BASISLOCALPLAYER.
            AfterFinalMove?.Invoke();
        }

        /// <summary>
        /// Positions the avatar in a T-pose such that the head aligns to tracked head position/orientation (yaw only).
        /// </summary>
        public void DriveTpose()
        {
            // World-space inputs
            var OutgoingWorldData = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
            Vector3 headPosWS = OutgoingWorldData.position;
            Quaternion headRotWS = OutgoingWorldData.rotation;

            // Flatten head forward onto the XZ plane to get yaw-only orientation
            Vector3 flatFwd = Vector3.ProjectOnPlane(headRotWS * Vector3.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward; // fallback
            Quaternion desiredRotWS = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);

            // Full T-pose local offset from hips/root to head (already scaled)
            Vector3 headTposeLocal = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position;

            // Place avatar so that (hips + desiredRot * headTposeLocal) == headPosWS
            Vector3 avatarWorldPos = headPosWS - (desiredRotWS * headTposeLocal);

            AvatarTransform.SetPositionAndRotation(avatarWorldPos, desiredRotWS);
        }

        /// <summary>
        /// Delegate type for scheduling a callback on the next frame.
        /// </summary>
        public delegate void NextFrameAction();

        /// <summary>
        /// Schedules an action to execute on the next frame.
        /// </summary>
        /// <param name="action">Callback to invoke next frame.</param>
        public void ExecuteNextFrame(NextFrameAction action)
        {
            StartCoroutine(RunNextFrame(action));
        }

        /// <summary>
        /// Coroutine that waits one frame and then invokes the provided action.
        /// </summary>
        /// <param name="action">Callback to invoke next frame.</param>
        private IEnumerator RunNextFrame(NextFrameAction action)
        {
            yield return null; // Waits for the next frame
            action?.Invoke();
        }
    }
}
