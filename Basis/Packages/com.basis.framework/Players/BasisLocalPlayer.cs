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
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Basis.Scripts.Drivers.BaseBoneDriver;
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
        public event Action OnLocalAvatarChanged;
        public event Action OnSpawnedEvent;
        public Action OnPlayersHeightChanged;
        public OrderedDelegate AfterFinalMove = new OrderedDelegate();

        public LocalHeightInformation CurrentHeight = new LocalHeightInformation();
        public LocalHeightInformation LastHeight= new LocalHeightInformation();
        public MicrophoneRecorder MicrophoneRecorder;
        public BasisLocalCameraDriver CameraDriver;
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
        public LocalCharacterDriver LocalCharacterDriver = new LocalCharacterDriver();
        //Animations
        [Header("Animator Driver")]
        [SerializeField]
        public BasisLocalAnimatorDriver LocalAnimatorDriver = new BasisLocalAnimatorDriver();
        //finger poses
        [Header("Muscle Driver")]
        [SerializeField]
        public BasisMuscleDriver LocalMuscleDriver = new BasisMuscleDriver();
        [Header("Eye Follow")]
        [SerializeField]
        public BasisLocalEyeFollowBase LocalEyeFollow = new BasisLocalEyeFollowBase();
        [Header("Mouth Visemes")]
        [SerializeField]
        public BasisAudioAndVisemeDriver LocalVisemeDriver = new BasisAudioAndVisemeDriver();
        public async Task LocalInitialize()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
            MicrophoneRecorder.OnPausedAction += OnPausedEvent;
            OnLocalPlayerCreated?.Invoke();
            IsLocal = true;
            LocalBoneDriver.CreateInitialArrays(this.transform, true);
            LocalBoneDriver.InitalizeLocal();

            BasisDeviceManagement.Instance.InputActions.Initialize(this);
            CameraDriver.gameObject.SetActive(true);
            LocalCharacterDriver.Initialize(this);
            if (HasEvents == false)
            {
                OnLocalAvatarChanged += OnCalibration;
                SceneManager.sceneLoaded += OnSceneLoadedCallback;
                HasEvents = true;
            }
            bool LoadedState = BasisDataStore.LoadAvatar(LoadFileNameAndExtension, DefaultAvatar, BasisPlayer.LoadModeLocal, out BasisDataStore.BasisSavedAvatar LastUsedAvatar);
            if (LoadedState)
            {
                await LoadInitialAvatar(LastUsedAvatar);
            }
            else
            {
                await CreateAvatar(BasisPlayer.LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
            if (MicrophoneRecorder == null)
            {
                MicrophoneRecorder = BasisHelpers.GetOrAddComponent<MicrophoneRecorder>(BasisDeviceManagement.Instance.gameObject);
            }
            MicrophoneRecorder.TryInitialize();
            PlayerReady = true;
            OnLocalPlayerCreatedAndReady?.Invoke();
            BasisSceneFactory BasisSceneFactory = FindFirstObjectByType<BasisSceneFactory>(FindObjectsInactive.Exclude);
            if (BasisSceneFactory != null)
            {
                BasisScene BasisScene = FindFirstObjectByType<BasisScene>(FindObjectsInactive.Exclude);
                if (BasisScene != null)
                {
                    BasisSceneFactory.Initalize(BasisScene);
                }
                else
                {
                    BasisDebug.LogError("Cant Find Basis Scene");
                }
            }
            else
            {
                BasisDebug.LogError("Cant Find Scene Factory");
            }
            BasisUILoadingBar.Initalize();

        }
        public async Task LoadInitialAvatar(BasisDataStore.BasisSavedAvatar LastUsedAvatar)
        {
            if (BasisLoadHandler.IsMetaDataOnDisc(LastUsedAvatar.UniqueID, out OnDiscInformation info))
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
                await CreateAvatar(BasisPlayer.LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
            else
            {
                BasisDebug.Log("failed to load last used : url was not found on disc");
                await CreateAvatar(BasisPlayer.LoadModeLocal, BasisAvatarFactory.LoadingAvatar);
            }
        }
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            BasisAvatarStrainJiggleDriver.PrepareTeleport();
            BasisDebug.Log("Teleporting");
            LocalCharacterDriver.IsEnabled = false;
            transform.SetPositionAndRotation(position, rotation);
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
            if (BasisSceneFactory.Instance != null && SpawnPlayerOnSceneLoad)
            {
                //swap over to on scene load
                BasisSceneFactory.Instance.SpawnPlayer(this);
            }
        }
        public async Task CreateAvatar(byte mode, BasisLoadableBundle BasisLoadableBundle)
        {
            await BasisAvatarFactory.LoadAvatarLocal(this, mode, BasisLoadableBundle);
            BasisDataStore.SaveAvatar(BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL, mode, LoadFileNameAndExtension);
            OnLocalAvatarChanged?.Invoke();
        }
        public void OnCalibration()
        {
            LocalVisemeDriver.TryInitialize(this);
            if (HasCalibrationEvents == false)
            {
                MicrophoneRecorderBase.OnHasAudio += DriveAudioToViseme;
                MicrophoneRecorderBase.OnHasSilence += DriveAudioToViseme;
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
                MicrophoneRecorderBase.OnHasAudio -= DriveAudioToViseme;
                MicrophoneRecorderBase.OnHasSilence -= DriveAudioToViseme;
                HasCalibrationEvents = false;
            }
            if (LocalMuscleDriver != null)
            {
                LocalMuscleDriver.DisposeAllJobsData();
            }
            if (LocalEyeFollow != null)
            {
                LocalEyeFollow.OnDestroy(this);
            }
            if (FacialBlinkDriver != null)
            {
                FacialBlinkDriver.OnDestroy();
            }
            MicrophoneRecorder.OnPausedAction -= OnPausedEvent;
            LocalAnimatorDriver.OnDestroy(this);
            LocalBoneDriver.DeInitializeGizmos();
            BasisUILoadingBar.DeInitalize();
        }
        public void DriveAudioToViseme()
        {
            LocalVisemeDriver.ProcessAudioSamples(MicrophoneRecorder.processBufferArray, 1, MicrophoneRecorder.processBufferArray.Length);
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
            LocalAvatarDriver.SimulateAnimatorAndIk();

            //we move the player at the very end after everything has been processed.
            LocalCharacterDriver.SimulateMovement(DeltaTime, transform);

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
            LocalBoneDriver.PostSimulateBonePositions();

            //handles fingers
            LocalMuscleDriver.UpdateFingers(LocalAvatarDriver);

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
            Vector3 centerPosition = blendedXZ;
          
            float3 correctedHips = BasisLocalBoneDriver.Hips.TposeLocal.position;
            output = -correctedHips;

            Vector3 childWorldPosition = centerPosition + parentWorldRotation * output;

            BasisAvatar.transform.SetPositionAndRotation(childWorldPosition, parentWorldRotation);
            return parentWorldRotation;
        }
    }
}
