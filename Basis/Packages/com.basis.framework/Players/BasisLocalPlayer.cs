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
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Basis.Scripts.Drivers.BasisBaseBoneDriver;
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
        [SerializeField]
        public LayerMask GroundMask;
        public static string LoadFileNameAndExtension = "LastUsedAvatar.BAS";
        public bool HasEvents = false;
        public bool SpawnPlayerOnSceneLoad = true;
        public const string DefaultAvatar = "LoadingAvatar";

        public bool HasCalibrationEvents = false;
        public bool ToggleAvatarSim = false;
        public float currentDistance;
        public float3 direction;
        public float overshoot;
        public float3 correction;
        public float3 output;

        public static Action OnLocalPlayerCreatedAndReady;
        public static Action OnLocalPlayerCreated;
        public static Action OnLocalAvatarChanged;
        public static Action OnSpawnedEvent;
        public static Action OnPlayersHeightChangedNextFrame;
        public static OrderedDelegate AfterFinalMove = new OrderedDelegate();

        public BasisLocalHeightInformation CurrentHeight = new BasisLocalHeightInformation();
        public BasisLocalHeightInformation LastHeight = new BasisLocalHeightInformation();
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
        public async Task LocalInitialize()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
            BasisMicrophoneRecorder.OnPausedAction += OnPausedEvent;
            OnLocalPlayerCreated?.Invoke();
            IsLocal = true;
            LocalBoneDriver.CreateInitialArrays(this.transform, true);
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
            BasisMicrophoneRecorder.TryInitialize();
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
                BasisMicrophoneRecorder.OnHasAudio += DriveAudioToViseme;
                BasisMicrophoneRecorder.OnHasSilence += DriveAudioToViseme;
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
                BasisMicrophoneRecorder.OnHasAudio -= DriveAudioToViseme;
                BasisMicrophoneRecorder.OnHasSilence -= DriveAudioToViseme;
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
            BasisMicrophoneRecorder.OnPausedAction -= OnPausedEvent;
            LocalAnimatorDriver.OnDestroy();
            LocalBoneDriver.DeInitializeGizmos();
            BasisUILoadingBar.DeInitalize();
        }
        public void DriveAudioToViseme()
        {
            LocalVisemeDriver.ProcessAudioSamples(BasisMicrophoneRecorder.processBufferArray, 1, BasisMicrophoneRecorder.processBufferArray.Length);
        }
        private void OnPausedEvent(bool IsPaused)
        {
            if (IsPaused)
            {
                if (LocalVisemeDriver.uLipSyncBlendShape != null)
                {
                    LocalVisemeDriver.uLipSyncBlendShape.maxVolume = 0;
                    LocalVisemeDriver.uLipSyncBlendShape.minVolume = 0;
                }
            }
            else
            {
                if (LocalVisemeDriver.uLipSyncBlendShape != null)
                {
                    LocalVisemeDriver.uLipSyncBlendShape.maxVolume = -1.5f;
                    LocalVisemeDriver.uLipSyncBlendShape.minVolume = -2.5f;
                }
            } 
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
            //moves Avatar Transform to where it belongs
            Quaternion Rotation = MoveAvatar();
            //Simulate Final Destination of IK
            LocalAvatarDriver.SimulateIKDestinations(Rotation);

            //process Animator and IK processes.
            LocalAvatarDriver.SimulateAnimatorAndIk(DeltaTime);

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
            LocalBoneDriver.SimulateWorldDestinations(transform.localToWorldMatrix, Rotation);

            //handles fingers
            LocalHandDriver.UpdateFingers(LocalAvatarDriver.References);

            //now other things can move like UI and NON-CHILDREN OF BASISLOCALPLAYER.
            AfterFinalMove?.Invoke();
        }
        public Quaternion MoveAvatar()
        {
            if (BasisAvatar == null)
            {
                return Quaternion.identity;
            }

            // World positions
            Vector3 headPosition = BasisLocalBoneDriver.Head.OutgoingWorldData.position;
            Vector3 hipsPosition = BasisLocalBoneDriver.Hips.OutgoingWorldData.position;
            Quaternion parentWorldRotation = BasisLocalBoneDriver.Hips.OutgoingWorldData.rotation;

            currentDistance = Vector3.Distance(headPosition, hipsPosition);

            // Use blended XZ center, but keep hips Y for grounded position
            Vector3 blendedXZ = Vector3.Lerp(hipsPosition, headPosition, 0.5f);
            blendedXZ.y = hipsPosition.y;
            if (currentDistance <= LocalAvatarDriver.MaxExtendedDistance)
            {
                output = -BasisLocalBoneDriver.Hips.TposeLocalScaled.position;
            }
            else
            {
                Vector3 direction = (hipsPosition - headPosition).normalized;
                float overshoot = currentDistance - LocalAvatarDriver.MaxExtendedDistance;
                Vector3 correction = direction * overshoot;
                Vector3 TposeHips = BasisLocalBoneDriver.Hips.TposeLocalScaled.position;
                float3 correctedHips = TposeHips + correction;
                output = -correctedHips;
            }

            Vector3 childWorldPosition = blendedXZ + parentWorldRotation * output;

            BasisAvatar.transform.SetPositionAndRotation(childWorldPosition, parentWorldRotation);
            return parentWorldRotation;
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
