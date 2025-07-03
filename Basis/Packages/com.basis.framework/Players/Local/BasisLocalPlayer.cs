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
        public const string DefaultAvatar = "LoadingAvatar";
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
        public BasisLocalHeightInformation LastHeight = new BasisLocalHeightInformation();
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
            bool LoadedState = BasisDataStore.LoadAvatar(LoadFileNameAndExtension, DefaultAvatar, LoadModeLocal, out BasisDataStore.BasisSavedAvatar LastUsedAvatar);
            if (LoadedState)
            {
                await LoadInitialAvatar(LastUsedAvatar);
            }
            else
            {
                await CreateAvatar(LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
            BasisLocalMicrophoneDriver.TryInitialize();
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
            if (BasisLoadHandler.IsMetaDataOnDisc(LastUsedAvatar.UniqueID, out BasisOnDiscInformation info))
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
                            BasisBundleConnector = new BasisBundleConnector("1", new BasisBundleDescription("Loading Avatar", "Loading Avatar"), new BasisBundleGenerated[] { new BasisBundleGenerated() }),
                            BasisLocalEncryptedBundle = info.StoredLocal,
                            UnlockPassword = Key.Pass
                        };
                        BasisDebug.Log("loading previously loaded avatar");
                        await CreateAvatar(LastUsedAvatar.loadmode, bundle);
                        return;
                    }
                }
                BasisDebug.Log("failed to load last used : no key found to load but was found on disc");
                await CreateAvatar(LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
            else
            {
                BasisDebug.Log("failed to load last used : url was not found on disc");
                await CreateAvatar(LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            BasisAvatarStrainJiggleDriver.PrepareTeleport();
            BasisDebug.Log("Teleporting");
            LocalCharacterDriver.IsEnabled = false;
            this.transform.SetPositionAndRotation(position, rotation);
            LocalCharacterDriver.IsEnabled = true;
            if (LocalAnimatorDriver != null)
            {
                LocalAnimatorDriver.HandleTeleport();
            }
            BasisAvatarStrainJiggleDriver.FinishTeleport();
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
            await BasisAvatarFactory.LoadAvatarLocal(this, LoadMode, BasisLoadableBundle);
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

        public void SimulateOnRender()
        {
            float DeltaTime = Time.deltaTime;
            if (float.IsNaN(DeltaTime))
            {
                return;
            }

            //moves all bones to where they belong
            LocalBoneDriver.SimulateAndApply(this, DeltaTime);

            //moves Avatar Hip Transform to where it belongs
            Quaternion Rotation = LocalAvatarDriver.MoveAvatar(BasisAvatar);

            //Simulate Final Destination of IK
            //then
            //process Animator and IK processes.
            LocalRigDriver.SimulateIKDestinations(Rotation, DeltaTime);

            //we move the player at the very end after everything has been processed.
            LocalCharacterDriver.SimulateMovement(DeltaTime, this.transform);

            //Apply Animator Weights
            LocalAnimatorDriver.SimulateAnimator(DeltaTime);

            //now that everything has been processed jiggles can move.
            if (HasJiggles)
            {
                //we use distance = 0 as the local avatar jiggles should always be processed.
                BasisAvatarStrainJiggleDriver.Simulate(0);
            }
            //now that everything has been processed lets update WorldPosition in BoneDriver.
            //this is so AfterFinalMove can use world position coords. (stops Laggy pickups)
            LocalBoneDriver.SimulateWorldDestinations(transform.localToWorldMatrix);

            //handles fingers
            LocalHandDriver.UpdateFingers(BasisLocalAvatarDriver.References);

            //now other things can move like UI and NON-CHILDREN OF BASISLOCALPLAYER.
            AfterFinalMove?.Invoke();
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
