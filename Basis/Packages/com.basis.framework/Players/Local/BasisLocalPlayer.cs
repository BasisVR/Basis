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
    public class BasisLocalPlayer : BasisPlayer
    {
        public static BasisLocalPlayer Instance;
        public static bool PlayerReady = false;
        public const float FallbackSize = 1.7f;

        public static float DefaultPlayerEyeHeight = FallbackSize;
        public static float DefaultAvatarEyeHeight = FallbackSize;

        public static float DefaultPlayerArmSpan = FallbackSize;
        public static float DefaultAvatarArmSpan = FallbackSize;

        public static string LoadFileNameAndExtension = "LastUsedAvatar.BAS";
        public static bool HasEvents = false;
        public static bool SpawnPlayerOnSceneLoad = true;
        public static bool HasCalibrationEvents = false;

        public static Action OnLocalPlayerCreatedAndReady;
        public static Action OnLocalPlayerCreated;
        public static Action OnLocalAvatarChanged;
        public static Action OnSpawnedEvent;
        public static Action OnPlayersHeightChangedNextFrame;
        public static BasisOrderedDelegate AfterFinalMove = new BasisOrderedDelegate();

        [Header("Camera Driver")]
        [SerializeField]
        public BasisLocalCameraDriver LocalCameraDriver;
        //bones that we use to map between avatar and trackers
        [Header("Bone Driver")]
        [SerializeField]
        public BasisLocalBoneDriver LocalBoneDriver = new BasisLocalBoneDriver();
        //calibration of the avatar happens here
        [Header("Calibration And Avatar Driver")]
        [SerializeField]
        public BasisLocalAvatarDriver LocalAvatarDriver = new BasisLocalAvatarDriver();
        [Header("Rig Driver")]
        [SerializeField]
        public BasisLocalRigDriver LocalRigDriver = new BasisLocalRigDriver();
        //how the player is able to move and have physics applied to them
        [Header("Character Driver")]
        [SerializeField]
        public BasisLocalCharacterDriver LocalCharacterDriver = new BasisLocalCharacterDriver();
        //Animations
        [Header("Animator Driver")]
        [SerializeField]
        public BasisLocalAnimatorDriver LocalAnimatorDriver = new BasisLocalAnimatorDriver();
        //finger poses
        [Header("Hand Driver")]
        [SerializeField]
        public BasisLocalHandDriver LocalHandDriver = new BasisLocalHandDriver();
        [Header("Eye Driver")]
        [SerializeField]
        public BasisLocalEyeDriver LocalEyeDriver = new BasisLocalEyeDriver();
        [Header("Mouth & Visemes Driver")]
        [SerializeField]
        public BasisAudioAndVisemeDriver LocalVisemeDriver = new BasisAudioAndVisemeDriver();
        [Header("Height Information")]
        public BasisLocalHeightInformation CurrentHeight = new BasisLocalHeightInformation();
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
            bool LoadedState = BasisDataStore.LoadAvatar(LoadFileNameAndExtension, BasisBeeConstants.DefaultAvatar, LoadModeLocal, out BasisDataStore.BasisSavedAvatar LastUsedAvatar);
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
                            BasisBundleConnector = new BasisBundleConnector("1", new BasisBundleDescription("Loading Avatar", "Loading Avatar"), new BasisBundleGenerated[] { new BasisBundleGenerated()}, null),
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

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            BasisDebug.Log("Teleporting", BasisDebug.LogTag.Local);
            LocalCharacterDriver.IsEnabled = false;
            this.transform.SetPositionAndRotation(position, rotation);
            LocalCharacterDriver.IsEnabled = true;
            LocalAnimatorDriver.HandleTeleport();
            OnSpawnedEvent?.Invoke();
        }

        public void OnSceneLoadedCallback(Scene scene, LoadSceneMode mode)
        {
            if (SpawnPlayerOnSceneLoad)
            {
                //swap over to on scene load
                BasisSceneFactory.SpawnPlayer(this);
            }
        }

        public async Task CreateAvatar(byte LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            await BasisAvatarFactory.LoadAvatarLocal(this, LoadMode, BasisLoadableBundle,this.transform.position,Quaternion.identity);
            BasisDataStore.SaveAvatar(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, LoadMode, LoadFileNameAndExtension);
            OnLocalAvatarChanged?.Invoke();
        }

        public async Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            byte LoadByte = (byte)LoadMode;
            await CreateAvatar(LoadByte, BasisLoadableBundle);
        }

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

        public void DriveAudioToViseme()
        {
            LocalVisemeDriver.ProcessAudioSamples(BasisLocalMicrophoneDriver.processBufferArray, 1, BasisLocalMicrophoneDriver.processBufferArray.Length);
        }

        public void SimulateOnLateUpdate()
        {
            FacialBlinkDriver.Simulate();
        }

        public void SimulateOnRender(float DeltaTime)
        {

            //now lets move the local player position.
            LocalCharacterDriver.SimulateMovement(DeltaTime);


            //moves all bones to where they belong
            LocalBoneDriver.SimulateAndApply(this, DeltaTime);

            //moves Avatar Hip Transform to where it belongs in tpose.
            if (BasisLocalAvatarDriver.CurrentlyTposing)
            {
                DriveTpose();
            }

            //Simulate Final Destination of IK
            //then
            //process Animator and IK processes.
            LocalRigDriver.SimulateIKDestinations(DeltaTime);

            //now that everything has been processed lets update WorldPosition in BoneDriver.
            //this is so AfterFinalMove can use world position coords. (stops Laggy pickups)
            LocalBoneDriver.SimulateWorldDestinations(transform.localToWorldMatrix);

            //Apply Animator Weights using most current data and outside movement effectors.
            LocalAnimatorDriver.SimulateAnimator(DeltaTime);

            //handles fingers
            LocalHandDriver.UpdateFingers(DeltaTime);

            //now other things can move like UI and NON-CHILDREN OF BASISLOCALPLAYER.
            AfterFinalMove?.Invoke();
        }
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
        // Define the delegate type
        public delegate void NextFrameAction();

        /// <summary>
        /// Executes the delegate in the next frame.
        /// </summary>
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
