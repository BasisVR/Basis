using Basis.Scripts.Animator_Driver;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;
using static BasisHeightDriver;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Local player controller that coordinates camera, character, rig, avatar, hands,
    /// visemes, input, calibration, and scene lifecycle for the current user.
    /// </summary>
    /// <remarks>
    /// Use <see cref="LocalInitialize"/> to wire up drivers, load the initial avatar,
    /// and signal readiness. Subscribe to events like <see cref="OnLocalPlayerInitalized"/>
    /// to know when the player has finished bootstrapping.
    /// </remarks>
    public class BasisLocalPlayer : BasisPlayer
    {
        /// <summary>
        /// Singleton-like reference to the active local player instance.
        /// </summary>
        public static BasisLocalPlayer Instance { get; private set; }

        /// <summary>
        /// True when the local player has completed initialization and is ready for interaction.
        /// </summary>
        public static bool PlayerReady = false;

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
        public static Action OnLocalPlayerInitalized;

        /// <summary>
        /// Fired whenever the local avatar asset changes (including initial creation).
        /// </summary>
        public static Action OnLocalAvatarChanged;

        /// <summary>
        /// Fired after the player has been spawned/teleported into the scene.
        /// </summary>
        public static Action OnTeleportEvent;

        /// <summary>
        /// Fired on the frame after a player height change is requested.
        /// </summary>
        public static Action<HeightModeChange> OnPlayersHeightChangedNextFrame;

        /// <summary>
        /// Fires Just Before the Apply of the remote player, good for chair movement
        /// </summary>
        public static BasisOrderedDelegate JustBeforeNetworkApply = new BasisOrderedDelegate();

        /// <summary>
        /// Ordered delegate queue invoked after all movement and simulation have completed for the frame.
        /// </summary>
        public static BasisOrderedDelegate AfterSimulateOnRender = new BasisOrderedDelegate();

        /// <summary>
        /// Ordered delegate queue invoked after all movement and simulation have completed for the frame.
        /// </summary>
        public static BasisOrderedDelegate AfterSimulateOnLate = new BasisOrderedDelegate();

        public static Matrix4x4 localToWorldMatrix = Matrix4x4.identity;
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
        /// Places a foot Down. not done yet, i give up - dooly
        /// </summary>
      //  [Header("Foot Driver")]
       // [SerializeField]
      //  public BasisLocalFootDriver BasisLocalFootDriver = new BasisLocalFootDriver();
        /// <summary>
        /// Character controller for movement, collisions, and physics.
        /// </summary>
        [Header("Character Driver")]
        [SerializeField]
        public BasisLocalCharacterDriver LocalCharacterDriver = new BasisLocalCharacterDriver();

        /// <summary>
        /// Local Seat Driver deals with sitting and using seats.
        /// </summary>
        [Header("Local Seat Driver")]
        [SerializeField]
        public BasisLocalSeatDriver LocalSeatDriver = new BasisLocalSeatDriver();

        /// <summary>
        /// Animator controller that blends animation states and applies weights each frame.
        /// </summary>
        [Header("Animator Driver")]
        [SerializeField]
        public BasisLocalAnimatorDriver LocalAnimatorDriver = new BasisLocalAnimatorDriver();

        /// <summary>
        ///
        /// </summary>
        [Header("Eye Driver")]
        [SerializeField]
        public BasisLocalEyeDriver LocalEyeDriver = new BasisLocalEyeDriver();

        /// <summary>
        /// Finger pose driver for hand tracking/controllers.
        /// </summary>
        [Header("Hand Driver")]
        [SerializeField]
        public BasisLocalHandDriver LocalHandDriver = new BasisLocalHandDriver();

        /// <summary>
        /// Audio capture and viseme (mouth shape) driver for lip sync.
        /// </summary>
        [Header("Mouth & Visemes Driver")]
        [SerializeField]
        public BasisAudioAndVisemeDriver LocalVisemeDriver = new BasisAudioAndVisemeDriver();

        /// <summary>
        /// Driver responsible for simulating/controlling facial blinking.
        /// </summary>
        [Header("Blink Driver")]
        [SerializeField]
        public BasisLocalFacialBlinkDriver FacialBlinkDriver = new BasisLocalFacialBlinkDriver();

        #endregion
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
            PlayerPlatform = Application.platform.ToString();

#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnPausedAction += LocalVisemeDriver.OnPausedEvent;
#endif
            IsLocal = true;

            LocalBoneDriver.CreateInitialArrays(true);
            LocalBoneDriver.Initialize();
            LocalHandDriver.Initialize();
            LocalSeatDriver.Initialize(this);

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

#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.Initialize();
#endif

            BasisScene BasisScene = FindFirstObjectByType<BasisScene>(FindObjectsInactive.Exclude);
            if (BasisScene != null)
            {
                BasisSceneFactory.Initalize(BasisScene);
                BasisSceneFactory.SpawnPlayer(this);
            }
            else
            {
                BasisDebug.LogError("Cant Find Basis Scene");
            }

            BasisUILoadingBar.Initalize();
            PlayerReady = true;
            OnLocalPlayerInitalized?.Invoke();
        }

        /// <summary>
        /// Loads the last-used avatar if present on disk and key is available; otherwise falls back to the loading avatar.
        /// </summary>
        /// <param name="LastUsedAvatar">Metadata pointing to the last persisted avatar selection.</param>
        public async Task LoadInitialAvatar(BasisDataStore.BasisSavedAvatar LastUsedAvatar)
        {
            if (BasisLoadHandler.IsMetaDataOnDisc(LastUsedAvatar.UniqueID, out BasisBEEExtensionMeta info))
            {
                await BasisDataStoreItemKeys.LoadKeys();
                ItemKey[] activeKeys = BasisDataStoreItemKeys.DisplayKeys();
                foreach (ItemKey Key in activeKeys)
                {
                    if (Key.Mode == BundledContentHolder.Mode.Avatar && Key.Url == LastUsedAvatar.UniqueID)
                    {
                        BasisLoadableBundle bundle = new BasisLoadableBundle
                        {
                            BasisRemoteBundleEncrypted = info.StoredRemote,
                            BasisBundleConnector = new BasisBundleConnector("1", new BasisBundleDescription("Loading Avatar", "Loading Avatar"), new BasisBundleGenerated[] { new BasisBundleGenerated() }, null, new BasisBounds(Vector3.zero, Vector3.one), new BasisBundleConnector.BasisMetaData()),
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
        /// Retrieves the current world position and rotation of the local player.
        /// </summary>
        /// <param name="position"></param>
        /// <param name="rotation"></param>
        public void GetPositionAndRotation(out Vector3 position, out Quaternion rotation)
        {
            this.transform.GetPositionAndRotation(out position, out rotation);
        }

        /// <summary>
        /// Teleports the local player to a world position and rotation, then re-enables character motion and notifies listeners.
        /// </summary>
        /// <param name="position">Target world position.</param>
        /// <param name="rotation">Target world rotation.</param>
        public void Teleport(Vector3 position, Quaternion rotation,bool BypassStand = false)
        {
            BasisDebug.Log("Teleporting", BasisDebug.LogTag.Local);
            if (BypassStand == false)
            {
                LocalSeatDriver.Stand();
            }
            bool wasCharacterEnabled = LocalCharacterDriver.IsEnabled;
            LocalCharacterDriver.IsEnabled = false;
            this.transform.SetPositionAndRotation(position, rotation);
            AvatarTransform.rotation = Quaternion.identity;
            LocalCharacterDriver.IsEnabled = wasCharacterEnabled;
            LocalAnimatorDriver.HandleTeleport();
            OnTeleportEvent?.Invoke();
        }
        public void Respawn()
        {
            BasisSceneFactory.SpawnPlayer(this);
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
            OnLocalAvatarChanged?.Invoke();
            BasisDataStore.SaveAvatar(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, LoadMode, LoadFileNameAndExtension);
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
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.OnHasAudio += DriveAudioToViseme;
                BasisLocalMicrophoneDriver.OnHasSilence += DriveAudioToViseme;
#endif
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
               LocalVisemeDriver?.OnDestroy();
                LocalCharacterDriver?.DeInitalize();
                OnLocalAvatarChanged -= OnCalibration;
                SceneManager.sceneLoaded -= OnSceneLoadedCallback;
                HasEvents = false;
            }
            if (HasCalibrationEvents)
            {
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.OnHasAudio -= DriveAudioToViseme;
                BasisLocalMicrophoneDriver.OnHasSilence -= DriveAudioToViseme;
#endif
                HasCalibrationEvents = false;
            }
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.DeInitialize();
#endif

            if (LocalHandDriver != null)
            {
                LocalHandDriver.Dispose();
            }
            BasisLocalEyeDriver.Dispose();
            if (FacialBlinkDriver != null)
            {
                FacialBlinkDriver.OnDestroy();
            }

#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnPausedAction -= LocalVisemeDriver.OnPausedEvent;
#endif
            LocalAnimatorDriver.OnDestroy();
            LocalBoneDriver.DeInitializeGizmos();
            BasisUILoadingBar.DeInitalize();
        }

        /// <summary>
        /// Pushes microphone audio samples into the viseme driver for lip-sync processing.
        /// </summary>
        public void DriveAudioToViseme()
        {
#if !BASIS_DISABLE_MICROPHONE
            LocalVisemeDriver.ProcessAudioSamples(BasisLocalMicrophoneDriver.processBufferArray,1,BasisLocalMicrophoneDriver.processBufferArray.Length);
#endif
        }
        public void Simulate(float DeltaTime)
        {
            // now lets move the local player position.
            LocalCharacterDriver.SimulateMovement(DeltaTime);

            // Apply virtual data (e.g. seat driver) before polling input devices so that
            // localToWorldMatrix reflects the seat-adjusted player position. This ensures
            // bone world positions and raycast origins are correct while seated (#514).
            ApplyVirtualData(this);
            if (LocalSeatDriver.IsSeated)
            {
                transform.GetPositionAndRotation(out Vector3 seatPos, out Quaternion seatRot);
                localToWorldMatrix = Matrix4x4.TRS(seatPos, seatRot, transform.lossyScale);
            }

            OnLateSimulateBones(this);

            // moves all bones to where they belong
            // This also drives head and camera movement.
            LocalBoneDriver.Simulate(DeltaTime, localToWorldMatrix);

            // moves Avatar Hip Transform to where it belongs in tpose.
            if (BasisLocalAvatarDriver.CurrentlyTposing)
            {
                LocalRigDriver.ResetSmoothingState();
                DriveTpose();
            }

            // Simulate Final Destination of IK then process Animator and IK processes.
            LocalRigDriver.SimulateIKDestinations(DeltaTime);

            // update WorldPosition in BoneDriver so AfterSimulateOnLate can use world coords
            LocalBoneDriver.SimulateWorldDestinations(localToWorldMatrix);

            // Apply Animator Weights using most current data and outside movement effectors.
            LocalAnimatorDriver.SimulateAnimator(DeltaTime);

            // handles fingers
            LocalHandDriver.UpdateFingers(DeltaTime);

            AfterSimulateOnLate?.Invoke();
        }
        public void OnDrawGizmosSelected()
        {
            LocalSeatDriver.DrawGizmosSelected();
        }
        public static void FireJustBeforeNetworkApply()
        {
            JustBeforeNetworkApply?.Invoke();
        }
        /// <summary>
        /// Main per-frame simulation entry point, executed on render/update.ddd
        /// Performs movement, bone simulation, T-pose driving, IK targets, animator evaluation, hands,
        /// and then invokes <see cref="AfterSimulateOnRender"/>.
        /// </summary>
        public void SimulateOnRender()
        {
            OnRenderSimulateBones(this);

            // now other things can move like UI and NON-CHILDREN OF BASISLOCALPLAYER.
            AfterSimulateOnRender?.Invoke();
        }
        public void OnLateSimulateBones(BasisPlayer Player)
        {
            Player.OnLatePollData?.Invoke();
        }
        public void ApplyVirtualData(BasisPlayer Player)
        {

            Player.OnVirtualData?.Invoke();
        }
        public void OnRenderSimulateBones(BasisPlayer Player)
        {
            Player.OnRenderPollData?.Invoke();
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
        public void Immobilize(bool immobilize)
        {
            var movementLock = BasisLocks.GetContext(BasisLocks.Movement);
            var crouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);
            var key = nameof(BasisLocalPlayer);

            if (immobilize)
            {
                if (!movementLock.Contains(key))
                {
                    movementLock.Add(key);
                }

                if (!crouchingLock.Contains(key))
                {
                    crouchingLock.Add(key);
                }
            }
            else
            {
                if (movementLock.Contains(key))
                {
                    movementLock.Remove(key);
                }

                if (crouchingLock.Contains(key))
                {
                    crouchingLock.Remove(key);
                }
            }
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
