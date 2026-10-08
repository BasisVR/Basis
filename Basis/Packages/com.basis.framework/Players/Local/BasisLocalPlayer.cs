using Unity.Scripting.LifecycleManagement;
using Basis.IK;
using Basis.Scripts.Animator_Driver;
using Basis.Scripts.Audio;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Common;
using Basis.Scripts.Constraints;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI.UI_Panels;
using GatorDragonGames.JigglePhysics;
using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;
using static BasisHeightDriver;

namespace Basis.Scripts.BasisSdk.Players
{
    [AutoStaticsCleanup]
    public partial class BasisLocalPlayer : BasisPlayer, IBasisLocalPlayer
    {
        public static BasisLocalPlayer Instance { get; private set; }

        public static bool PlayerReady = false;

        public static string LoadFileNameAndExtension = "LastUsedAvatar.BAS";

        public static string CurrentAvatarUniqueID;

        public static bool HasEvents = false;

        public static bool SpawnPlayerOnSceneLoad = true;

        public static bool HasCalibrationEvents = false;

        public static Action OnLocalPlayerInitialized;

        public static Action OnLocalAvatarChanged;

        public static Action OnTeleportEvent;

        public static Action<HeightModeChange> OnPlayersHeightChangedNextFrame;

        public static BasisOrderedDelegate JustBeforeNetworkApply = new BasisOrderedDelegate();

        public static BasisOrderedDelegate AfterRemoteSyncInterpolated = new BasisOrderedDelegate();

        public static BasisOrderedDelegate AfterSimulateOnRender = new BasisOrderedDelegate();

        public static BasisOrderedDelegate AfterSimulateOnLate = new BasisOrderedDelegate();

        public static Matrix4x4 localToWorldMatrix = Matrix4x4.identity;
        #region Drivers

        [Header("Camera Driver")]
        [SerializeField]
        public BasisLocalCameraDriver LocalCameraDriver;

        [Header("Bone Driver")]
        [SerializeField]
        public BasisLocalBoneDriver LocalBoneDriver = new BasisLocalBoneDriver();

        [Header("Calibration And Avatar Driver")]
        [SerializeField]
        public BasisLocalAvatarDriver LocalAvatarDriver = new BasisLocalAvatarDriver();

        [Header("Rig Driver")]
        [SerializeField]
        public BasisLocalRigDriver LocalRigDriver = new BasisLocalRigDriver();

        [Header("Foot Driver")]
        [SerializeField]
        public BasisLocalFootDriver BasisLocalFootDriver = new BasisLocalFootDriver();

        [Header("Virtual Spine Driver")]
        [SerializeField]
        public BasisLocalVirtualSpineDriver LocalVirtualSpineDriver = new BasisLocalVirtualSpineDriver();
        [Header("Character Driver")]
        [SerializeField]
        public BasisLocalCharacterDriver LocalCharacterDriver = new BasisLocalCharacterDriver();

        [Header("Local Seat Driver")]
        [SerializeField]
        public BasisLocalSeatDriver LocalSeatDriver = new BasisLocalSeatDriver();

        [Header("Animator Driver")]
        [SerializeField]
        public BasisLocalAnimatorDriver LocalAnimatorDriver = new BasisLocalAnimatorDriver();

        [Header("Eye Driver")]
        [SerializeField]
        public BasisLocalEyeDriver LocalEyeDriver = new BasisLocalEyeDriver();

        [Header("Hand Driver")]
        [SerializeField]
        public BasisLocalHandDriver LocalHandDriver = new BasisLocalHandDriver();

        [Header("Mouth & Visemes Driver")]
        [SerializeField]
        public BasisAudioAndVisemeDriver LocalVisemeDriver = new BasisAudioAndVisemeDriver();

        [Header("Blink Driver")]
        [SerializeField]
        public BasisLocalFacialBlinkDriver FacialBlinkDriver = new BasisLocalFacialBlinkDriver();

        #endregion
        public async Task LocalInitialize()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
            BasisLocalPlayerData.Instance = this;
            PlayerPlatform = Application.platform.ToString();

#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnPausedAction += LocalVisemeDriver.OnPausedEvent;
#endif
            IsLocal = true;

            LocalBoneDriver.CreateInitialArrays(true);
            LocalBoneDriver.Initialize();
            LocalVirtualSpineDriver.Initialize();
            LocalHandDriver.Initialize();
            LocalSeatDriver.Initialize(this);

            BasisLocalInputActions.Initialize(this, BasisDeviceManagement.Instance.InputActions, BasisDeviceManagement.Instance.InputActionsRoot);
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
                await LoadFallbackAvatar();
            }

#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.Initialize();
#endif

            BasisScene BasisScene = FindAnyObjectByType<BasisScene>(FindObjectsInactive.Exclude);
            if (BasisScene != null)
            {
                BasisSceneFactory.Initialize(BasisScene);
                BasisSceneFactory.SpawnPlayer(this);
            }
            else
            {
                BasisDebug.LogError("Can't Find Basis Scene");
            }

            BasisUILoadingBar.Initialize();
            PlayerReady = true;
            OnLocalPlayerInitialized?.Invoke();
            BasisLocalPlayerData.RaiseLocalPlayerInitialized();
        }

        public async Task LoadInitialAvatar(BasisDataStore.BasisSavedAvatar LastUsedAvatar)
        {
            if (LastUsedAvatar.loadmode == (byte)BasisLoadMode.ByGameobjectReference)
            {
                BasisDebug.Log("failed to load last used : in-scene avatars cannot be restored", BasisDebug.LogTag.Avatar);
                await LoadFallbackAvatar();
                return;
            }

            await BasisDataStoreItemKeys.LoadKeys();
            ItemKey matchingKey = null;
            ItemKey[] activeKeys = BasisDataStoreItemKeys.DisplayKeys();
            foreach (ItemKey Key in activeKeys)
            {
                if (Key.Mode == BundledContentHolder.Mode.Avatar && Key.Url == LastUsedAvatar.UniqueID)
                {
                    matchingKey = Key;
                    break;
                }
            }

            string unlockPassword = !string.IsNullOrEmpty(LastUsedAvatar.Pass) ? LastUsedAvatar.Pass : matchingKey?.Pass;
            if (unlockPassword == null)
            {
                BasisDebug.Log("failed to load last used : no stored password and no key found", BasisDebug.LogTag.Avatar);
                await LoadFallbackAvatar();
                return;
            }

            var (onDisc, info) = await BasisLoadHandler.IsMetaDataOnDiscAsync(LastUsedAvatar.UniqueID);
            BasisLoadableBundle bundle = new BasisLoadableBundle
            {
                // Cloned, not aliased: this bundle is held for the lifetime of the worn avatar and
                // its version tag is part of the bundle registry key. Sharing the meta cache's
                // record lets the library UI re-key the avatar you are currently wearing, which
                // strands its DeIncrement and can unload it out from under you.
                BasisRemoteBundleEncrypted = onDisc ? info.StoredRemote.Clone() : new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = LastUsedAvatar.UniqueID },
                BasisBundleConnector = new BasisBundleConnector("1", new BasisBundleDescription("Loading Avatar", "Loading Avatar"), new BasisBundleGenerated[] { new BasisBundleGenerated() }, null, new BasisBounds(Vector3.zero, Vector3.one), new BasisBundleConnector.BasisMetaData()),
                BasisLocalEncryptedBundle = onDisc ? info.StoredLocal : new BasisStoredEncryptedBundle(),
                UnlockPassword = unlockPassword
            };
            BasisDebug.Log(onDisc ? "loading previously loaded avatar" : "last used avatar missing from disc cache, re-downloading", BasisDebug.LogTag.Avatar);
            await CreateAvatar(LastUsedAvatar.loadmode, bundle);
        }

        public async Task LoadFallbackAvatar()
        {
            CurrentAvatarUniqueID = BasisAvatarFactory.LoadingAvatar.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            await BasisAvatarFactory.LoadAvatarLocal(this, LoadModeLocal, BasisAvatarFactory.LoadingAvatar, this.transform.position, Quaternion.identity);
            OnLocalAvatarChanged?.Invoke();
            BasisConstraintSystem.SetPriorityRoot(BasisAvatar != null ? BasisAvatar.transform.root : null);
        }

        public void GetPositionAndRotation(out Vector3 position, out Quaternion rotation)
        {
            this.transform.GetPositionAndRotation(out position, out rotation);
        }

        public void Teleport(Vector3 position, Quaternion rotation, bool BypassStand = false, BasisTeleportMode mode = BasisTeleportMode.WorldRoot)
        {
            BasisDebug.Log("Teleporting", BasisDebug.LogTag.Local);
            if (BypassStand == false)
            {
                LocalSeatDriver.Stand();
            }
            if (mode == BasisTeleportMode.FacePoint)
            {
                rotation = GetFacingToward(position);
            }
            if (mode != BasisTeleportMode.WorldRoot)
            {
                position = GetFeetAlignedRoot(position, rotation);
            }
            bool wasCharacterEnabled = LocalCharacterDriver.IsEnabled;
            LocalCharacterDriver.IsEnabled = false;
            Vector3 deltaPosition = position - this.transform.position;
            this.transform.SetPositionAndRotation(position, rotation);
            AvatarTransform.rotation = Quaternion.identity;
            LocalCharacterDriver.IsEnabled = wasCharacterEnabled;
            LocalAnimatorDriver.HandleTeleport();
            var jiggleRigs = BasisLocalAvatarDriver.JiggleRigs;
            for (int i = 0; i < jiggleRigs.Length; i++)
            {
                JiggleRig rig = jiggleRigs[i];
                if (rig != null)
                {
                    rig.Teleport(deltaPosition);
                }
            }
            BasisLocalFootDriver?.Teleport(deltaPosition);
            OnTeleportEvent?.Invoke();
        }
        private Vector3 GetFeetAlignedRoot(Vector3 targetPosition, Quaternion targetRotation)
        {
            if (BasisLocalBoneDriver.HasEye == false)
            {
                return targetPosition;
            }
            this.transform.GetPositionAndRotation(out Vector3 rootPosition, out Quaternion rootRotation);
            Vector3 headOffset = BasisLocalBoneDriver.EyeControl.OutgoingWorldData.position - rootPosition;
            headOffset.y = 0f;
            Vector3 localOffset = Quaternion.Inverse(rootRotation) * headOffset;
            Vector3 aligned = targetPosition - (targetRotation * localOffset);
            aligned.y = targetPosition.y;
            return aligned;
        }
        private Quaternion GetFacingToward(Vector3 worldPoint)
        {
            Vector3 from = BasisLocalBoneDriver.HasEye
                ? BasisLocalBoneDriver.EyeControl.OutgoingWorldData.position
                : this.transform.position;
            Vector3 direction = worldPoint - from;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f)
            {
                return this.transform.rotation;
            }
            return Quaternion.LookRotation(direction.normalized, Vector3.up);
        }
        public void Respawn()
        {
            BasisSceneFactory.SpawnPlayer(this);
        }
        public void OnSceneLoadedCallback(Scene scene, LoadSceneMode mode)
        {
            if (SpawnPlayerOnSceneLoad)
            {
                // swap over to on scene load
                BasisSceneFactory.SpawnPlayer(this);
            }
        }
        public async Task CreateAvatar(byte LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            string previousAvatarUniqueID = CurrentAvatarUniqueID;
            CurrentAvatarUniqueID = BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            bool loaded = await BasisAvatarFactory.LoadAvatarLocal(this, LoadMode, BasisLoadableBundle, this.transform.position, Quaternion.identity);
            if (!loaded)
            {
                CurrentAvatarUniqueID = previousAvatarUniqueID;
            }
            OnLocalAvatarChanged?.Invoke();

            // Tell the constraint solver which hierarchy is ours. It bands how often it re-reads a
            // constraint's state by distance from here, and exempts this one entirely — our own
            // constraints have to keep up frame for frame, a remote across the room does not.
            // Told nothing, it treats every avatar as near and refreshes everything at full rate:
            // correct, just without the saving.
            BasisConstraintSystem.SetPriorityRoot(
                BasisAvatar != null ? BasisAvatar.transform.root : null);
            if (!loaded)
            {
                return;
            }
            if (LoadMode != (byte)BasisLoadMode.ByGameobjectReference)
            {
                BasisDataStore.SaveAvatar(CurrentAvatarUniqueID, LoadMode, LoadFileNameAndExtension, BasisLoadableBundle.UnlockPassword);
                if (LoadMode == (byte)BasisLoadMode.Download && !string.IsNullOrEmpty(CurrentAvatarUniqueID) && !BasisAvatarFactory.IsLoadingAvatar(BasisLoadableBundle))
                {
                    await BasisDataStoreItemKeys.AddNewKey(new ItemKey
                    {
                        Mode = BundledContentHolder.Mode.Avatar,
                        Url = CurrentAvatarUniqueID,
                        Pass = BasisLoadableBundle.UnlockPassword
                    });
                }
            }

            // Everyone else resolves this avatar from the address alone, which only works if the
            // address means something off this machine. Say so now rather than let the player find
            // out from someone telling them they are a grey dummy.
            BasisLocalAvatarNetworkNotice.NotifyIfLocalOnly();
        }

        public async Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            byte LoadByte = (byte)LoadMode;
            await CreateAvatar(LoadByte, BasisLoadableBundle);
        }

        public void OnCalibration()
        {
            LocalVisemeDriver.TryInitialize(this);
#if !BASIS_DISABLE_MICROPHONE
            LocalVisemeDriver.OnPausedEvent(BasisLocalMicrophoneDriver.isPaused);
#endif
            if (HasCalibrationEvents == false)
            {
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.OnHasAudio += DriveAudioToViseme;
                BasisLocalMicrophoneDriver.OnHasSilence += DriveAudioToViseme;
#endif
                HasCalibrationEvents = true;
            }
        }

        public void OnDestroy()
        {
            if (ReferenceEquals(BasisLocalPlayerData.Instance, this))
            {
                BasisLocalPlayerData.Instance = null;
                BasisLocalPlayerData.PlayerReady = false;
            }
            if (HasEvents)
            {
               LocalVisemeDriver?.OnDestroy();
                LocalCharacterDriver?.DeInitialize();
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
            LocalVirtualSpineDriver.DeInitialize();
            LocalBoneDriver.Dispose();
            BasisLocalFootDriver.Dispose();
            LocalRigDriver.CleanupBeforeContinue();
            BasisAvatarDriver.RemoveOldShadowClones();
            BasisUILoadingBar.DeInitialize();
        }

        public void DriveAudioToViseme()
        {
#if !BASIS_DISABLE_MICROPHONE
            LocalVisemeDriver.VoiceRms = BasisVoiceLevel.LocalVoiceRms;
            LocalVisemeDriver.ProcessAudioSamples(BasisLocalMicrophoneDriver.processBufferArray,1,BasisLocalMicrophoneDriver.processBufferArray.Length);
#endif
        }

        public void Simulate(float DeltaTime)
        {
            // Opens this frame's transform snapshot. Nothing cached can survive it, so a missed
            // invalidation is bounded to a single frame.
            BasisLocalPose.BeginFrame();

            // Kick the locomotion pose job first: when active it fills the IK stream on a worker
            // while everything below runs, and is joined inside SimulateIKDestinations.
            using (BasisLocalPlayerMarkers.LocoPoseSchedule.Auto())
            {
                LocalRigDriver.ScheduleLocomotionPose(this, DeltaTime);
            }

            // now lets move the local player position.
            using (BasisLocalPlayerMarkers.Movement.Auto())
            {
                LocalCharacterDriver.SimulateMovement(DeltaTime);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostCharacterMovement");

            // VR play space grab/drag override (no-op unless enabled and a controller input is held).
            using (BasisLocalPlayerMarkers.PlayspaceMover.Auto())
            {
                BasisLocalPlayspaceMover.Simulate(this, DeltaTime);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostPlayspaceMover");

            using (BasisLocalPlayerMarkers.VirtualData.Auto())
            {
                // Apply virtual data (e.g. seat driver) before polling input devices so that
                // localToWorldMatrix reflects the seat-adjusted player position. This ensures
                // bone world positions and raycast origins are correct while seated (#514).
                ApplyVirtualData(this);
                if (LocalSeatDriver.IsSeated)
                {
                    transform.GetPositionAndRotation(out Vector3 seatPos, out Quaternion seatRot);
                    localToWorldMatrix = Matrix4x4.TRS(seatPos, seatRot, transform.lossyScale);
                }

                // Apply the play-space flip (OVRAS-style) to the avatar's local->world matrix so the body
                // tips/inverts with the view. The view, controllers, and trackers get the same flip in
                // BasisInput.ApplyFinalMovement. No-op unless a flip is active; the capsule is never rotated.
                localToWorldMatrix = BasisLocalPlayspaceMover.ApplyFlipToMatrix(localToWorldMatrix);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostVirtualData (seat / flip)");

            using (BasisLocalPlayerMarkers.LateSimulateBones.Auto())
            {
                OnLateSimulateBones(this);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostLatePollData");

            // Virtual spine derives head/neck/chest/spine/hips from the freshly polled eye, and
            // runs ahead of the bone sim so the sim's follower chains (untracked legs and arms
            // hang off the hips via their targets) read this frame's hips rather than last frame's.
            using (BasisLocalPlayerMarkers.VirtualSpine.Auto())
            {
                LocalVirtualSpineDriver.Simulate();
            }
            BasisFiniteWatchdog.CheckpointBoneControls("LocalSim/PostVirtualSpine (virtual spine bone data)");

            // moves all bones to where they belong
            // This also drives head and camera movement.
            using (BasisLocalPlayerMarkers.BoneDriver.Auto())
            {
                LocalBoneDriver.Simulate(DeltaTime, localToWorldMatrix);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostBoneDriver");
            BasisFiniteWatchdog.CheckpointBoneControls("LocalSim/PostBoneDriver (bone control pose data)");

            // moves Avatar Hip Transform to where it belongs in tpose.
            if (BasisLocalAvatarDriver.CurrentlyTposing)
            {
                LocalRigDriver.ResetSmoothingState();
                DriveTpose();
                BasisFiniteWatchdog.Checkpoint("LocalSim/PostDriveTpose");
            }

            // Simulate Final Destination of IK then process Animator and IK processes.
            using (BasisLocalPlayerMarkers.IKDestinations.Auto())
            {
                LocalRigDriver.SimulateIKDestinations(DeltaTime);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostIKDestinations");

            // schedule finger slerp job (completed by Apply in BasisEventDriver)
            using (BasisLocalPlayerMarkers.HandDriver.Auto())
            {
                LocalHandDriver.Simulate(DeltaTime);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostHandSchedule");

            // Apply Animator Weights using most current data and outside movement effectors.
            using (BasisLocalPlayerMarkers.Animator.Auto())
            {
                LocalAnimatorDriver.SimulateAnimator(DeltaTime);
            }
            BasisFiniteWatchdog.Checkpoint("LocalSim/PostAnimatorWeights");
        }

        public void FinishSimulate()
        {
            LocalRigDriver.CompleteIKSolve();
            BasisFiniteWatchdog.Checkpoint("LocalFinish/PostIKSolveJoin");

            using (BasisLocalPlayerMarkers.AfterSimulateOnLate.Auto())
            {
                AfterSimulateOnLate?.Invoke();
            }
            BasisFiniteWatchdog.Checkpoint("LocalFinish/PostAfterSimulateOnLate");
        }
        public static void FireJustBeforeNetworkApply()
        {
            JustBeforeNetworkApply?.Invoke();
        }
        public static void FireAfterRemoteSyncInterpolated()
        {
            AfterRemoteSyncInterpolated?.Invoke();
        }
        public void SimulateOnRender()
        {
            OnRenderSimulateBones(this);
            BasisFiniteWatchdog.Checkpoint("LocalRender/PostRenderPollData");

            // now other things can move like UI and NON-CHILDREN OF BASISLOCALPLAYER.
            AfterSimulateOnRender?.Invoke();
            BasisFiniteWatchdog.Checkpoint("LocalRender/PostAfterSimulateOnRender");
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
        public void DriveTpose()
        {
            if (BasisLocalAvatarDriver.Mapping.HasHips == false)
            {
                return;
            }

            // World-space inputs
            var OutgoingWorldData = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
            Vector3 headPosWS = OutgoingWorldData.position;
            Quaternion headRotWS = OutgoingWorldData.rotation;

            // Flatten head forward onto the XZ plane to get yaw-only orientation
            Vector3 flatFwd = Vector3.ProjectOnPlane(headRotWS * Vector3.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-6f)
            {
                flatFwd = Vector3.forward; // fallback
            }
            Quaternion desiredRotWS = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);

            // Offset the avatar root by the head's T-pose offset so the head bone lands on headPosWS.
            Vector3 headTposeLocal = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position;
            Vector3 avatarWorldPos = headPosWS - desiredRotWS * headTposeLocal;

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
        public float GetMinimumMovementSpeed() => LocalCharacterDriver.MinimumMovementSpeed;
        public void SetMinimumMovementSpeed(float value)
        {
            LocalCharacterDriver.BaselineMinimumSpeed = value;
            LocalCharacterDriver.ApplyLocomotionOverrides(true);
        }
        public float GetDefaultMovementSpeed() => LocalCharacterDriver.DefaultMovementSpeed;
        public void SetDefaultMovementSpeed(float value)
        {
            LocalCharacterDriver.BaselineWalkSpeed = value;
            LocalCharacterDriver.ApplyLocomotionOverrides(true);
        }
        public float GetMaximumMovementSpeed() => LocalCharacterDriver.MaximumMovementSpeed;
        public void SetMaximumMovementSpeed(float value)
        {
            LocalCharacterDriver.BaselineRunSpeed = value;
            LocalCharacterDriver.ApplyLocomotionOverrides(true);
        }
        public float GetJumpHeight() => LocalCharacterDriver.jumpHeight;
        public void SetJumpHeight(float value)
        {
            LocalCharacterDriver.BaselineJumpHeight = value;
            LocalCharacterDriver.ApplyLocomotionOverrides(true);
        }
        public float GetGravityValue() => LocalCharacterDriver.gravityValue;
        public void SetGravityValue(float value)
        {
            LocalCharacterDriver.BaselineGravity = value;
            LocalCharacterDriver.ApplyLocomotionOverrides(true);
        }

        public int GetMovementMode() => (int)LocalCharacterDriver.CurrentModeKind;

        public void SetJumpHeightOverride(string key, float jumpHeight)
        {
            PushLocomotionOverride(key, new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.JumpHeight,
                JumpHeight = jumpHeight,
            });
        }

        public void SetWalkSpeedOverride(string key, float walkSpeed)
        {
            PushLocomotionOverride(key, new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.WalkSpeed,
                WalkSpeed = walkSpeed,
            });
        }

        public void SetRunSpeedOverride(string key, float runSpeed)
        {
            PushLocomotionOverride(key, new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.RunSpeed,
                RunSpeed = runSpeed,
            });
        }

        public void SetGravityOverride(string key, float gravity)
        {
            PushLocomotionOverride(key, new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.Gravity,
                Gravity = gravity,
            });
        }

        public void SetMovementModeOverride(string key, int mode)
        {
            if (mode < (int)BasisLocalCharacterDriver.Mode.Walk || mode > (int)BasisLocalCharacterDriver.Mode.NoClip)
            {
                BasisDebug.LogError($"Movement mode override rejected: {mode} is not a valid mode.");
                return;
            }

            PushLocomotionOverride(key, new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.Mode,
                Mode = (BasisLocalCharacterDriver.Mode)mode,
            });
        }

        public bool HasLocomotionOverride(string key) => BasisLocomotionOverrides.Contains(key);

        public bool ClearLocomotionOverride(string key)
        {
            if (BasisLocomotionOverrides.IsReservedKey(key))
            {
                BasisDebug.LogError($"Locomotion override key '{key}' is reserved and cannot be cleared here.");
                return false;
            }

            return BasisLocomotionOverrides.Remove(key);
        }

        public void ClearAllLocomotionOverrides() => BasisLocomotionOverrides.RemoveAll(false);

        private void PushLocomotionOverride(string key, BasisLocomotionValues values)
        {
            if (BasisLocomotionOverrides.IsReservedKey(key))
            {
                BasisDebug.LogError($"Locomotion override key '{key}' is reserved.");
                return;
            }

            BasisLocomotionOverrides.Set(key, BasisLocomotionOverrides.DefaultPriority, values);
        }
        public delegate void NextFrameAction();

        public void ExecuteNextFrame(NextFrameAction action)
        {
            StartCoroutine(RunNextFrame(action));
        }

        private IEnumerator RunNextFrame(NextFrameAction action)
        {
            yield return null; // Waits for the next frame
            action?.Invoke();
        }
    }
}
