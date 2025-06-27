using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Factory;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using static Basis.Scripts.BasisSdk.Players.BasisPlayer;
namespace Basis.Scripts.Device_Management.Devices
{
    public abstract class BasisInput : MonoBehaviour
    {
        public bool HasEvents = false;
        public string SubSystemIdentifier;
        [SerializeField] private BasisBoneTrackedRole trackedRole;
        [SerializeField] public bool hasRoleAssigned;
        public BasisBoneControl Control = new BasisBoneControl();
        public bool HasControl = false;
        public string UniqueDeviceIdentifier;
        public string ClassName;

        [Header("Raw Position Of Device")]
        public BasisCalibratedCoords UnscaledDeviceCoord = new BasisCalibratedCoords();

        [Header("Final Data normally just modified by EyeHeight/AvatarEyeHeight)")]
        public BasisCalibratedCoords ScaledDeviceCoord = new BasisCalibratedCoords();

        public string CommonDeviceIdentifier;
        public BasisVisualTracker BasisVisualTracker;
        public BasisPointRaycaster BasisPointRaycaster;//used to raycast against things like UI
        public BasisUIRaycast BasisUIRaycast;
        public AddressableGenericResource LoadedDeviceRequest;
        public event SimulationHandler AfterControlApply;
        public DeviceSupportInformation DeviceMatchSettings;
        [SerializeField]
        public BasisInputState CurrentInputState = new BasisInputState();
        [SerializeField]
        public BasisInputState LastInputState = new BasisInputState();
        public static BasisBoneTrackedRole[] CanHaveMultipleRoles = new BasisBoneTrackedRole[] { BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand };
        public static string FallbackDeviceID = "FallbackSphere";
        public GameObject BasisPointRaycasterRef;
        public bool HasRaycaster = false;
        public Quaternion InitialRotation;
        public Quaternion InitialBoneRotation;

        public BasisCalibratedCoords RaycastCoord;
        public bool TryGetRole(out BasisBoneTrackedRole BasisBoneTrackedRole)
        {
            if (hasRoleAssigned)
            {
                BasisBoneTrackedRole = trackedRole;
                return true;
            }
            BasisBoneTrackedRole = BasisBoneTrackedRole.CenterEye;
            return false;
        }
        public void AssignRoleAndTracker(BasisBoneTrackedRole Role)
        {
            hasRoleAssigned = true;
            int InputsCount = BasisDeviceManagement.Instance.AllInputDevices.Count;
            for (int Index = 0; Index < InputsCount; Index++)
            {
                BasisInput Input = BasisDeviceManagement.Instance.AllInputDevices[Index];
                if (Input.TryGetRole(out BasisBoneTrackedRole found) && Input != this)
                {
                    if (found == Role)
                    {
                        if (CanHaveMultipleRoles.Contains(found) == false)
                        {
                            BasisDebug.LogError("Already Found tracker for  " + Role, BasisDebug.LogTag.Input);
                            return;
                        }
                        else
                        {
                            BasisDebug.Log("Has Multiple Roles assigned for " + found + " most likely ok.", BasisDebug.LogTag.Input);
                        }
                    }
                }
            }
            trackedRole = Role;
            if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Control, trackedRole))
            {
                HasControl = true;
            }
            if (HasControl)
            {
                if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(trackedRole))//we dont want to offset these ones
                {
                    InitialRotation = Quaternion.Inverse(transform.rotation);
                    InitialBoneRotation = Control.OutgoingWorldData.rotation;
                    Control.InverseOffsetFromBone.position = Quaternion.Inverse(transform.rotation) * ((Vector3)Control.OutgoingWorldData.position - transform.position);
                    Control.InverseOffsetFromBone.rotation = InitialRotation * InitialBoneRotation;
                    Control.InverseOffsetFromBone.Use = true;
                    Control.IsHintRoleIgnoreRotation = BasisBoneTrackedRoleCommonCheck.CheckIfHintRole(trackedRole);
                }
                SetRealTrackers(BasisHasTracked.HasTracker, BasisHasRigLayer.HasRigLayer);
            }
            else
            {
                BasisDebug.LogError("Attempted to find " + Role + " but it did not exist");
            }
        }
        public void UnAssignRoleAndTracker()
        {
            if (Control != null)
            {
                Control.IncomingData.position = Vector3.zero;
                Control.IncomingData.rotation = Quaternion.identity;
            }
            if (DeviceMatchSettings == null || DeviceMatchSettings.HasTrackedRole == false)
            {
                //unassign last
                if (hasRoleAssigned)
                {
                    SetRealTrackers(BasisHasTracked.HasNoTracker, BasisHasRigLayer.HasNoRigLayer);
                }
                hasRoleAssigned = false;
                trackedRole = BasisBoneTrackedRole.CenterEye;
                Control = null;
                HasControl = false;
            }
        }
        public void OnDisable()
        {
            StopTracking();
        }
        public void OnDestroy()
        {
            StopTracking();
            if (BasisUIRaycast != null)
            {
                BasisUIRaycast.OnDeInitialize();
                if (BasisUIRaycast.highlightQuadInstance != null)
                {
                    GameObject.Destroy(BasisUIRaycast.highlightQuadInstance.gameObject);
                }
                GameObject.Destroy(BasisPointRaycaster.gameObject);
            }
        }
        public bool HasRaycastSupport()
        {
            if (DeviceMatchSettings == null)
            {
                return false;
            }
            return hasRoleAssigned && DeviceMatchSettings.HasRayCastSupport;
        }
        /// <summary>
        /// initalize the tracking of this input
        /// </summary>
        /// <param name="uniqueID"></param>
        /// <param name="unUniqueDeviceID"></param>
        /// <param name="subSystems"></param>
        /// <param name="ForceAssignTrackedRole"></param>
        /// <param name="basisBoneTrackedRole"></param>
        public void InitalizeTracking(string uniqueID, string unUniqueDeviceID, string subSystems, bool ForceAssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole)
        {
            //unassign the old tracker
            UnAssignTracker();
            BasisDebug.Log("Finding ID " + unUniqueDeviceID, BasisDebug.LogTag.Input);
            //configure device identifier
            SubSystemIdentifier = subSystems;
            CommonDeviceIdentifier = unUniqueDeviceID;
            UniqueDeviceIdentifier = uniqueID;
            // lets check to see if there is a override from a devices matcher
            DeviceMatchSettings = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier, basisBoneTrackedRole, ForceAssignTrackedRole);
            if (DeviceMatchSettings.HasTrackedRole)
            {
                BasisDebug.Log("Overriding Tracker " + DeviceMatchSettings.DeviceID, BasisDebug.LogTag.Input);
                AssignRoleAndTracker(DeviceMatchSettings.TrackedRole);
            }
            //reset the offsets, its up to the higher level to set this now.
            if (HasRaycastSupport())
            {
                CreateRayCaster(this);
            }
            if (HasEvents == false)
            {
                BasisLocalPlayer.Instance.OnPreSimulateBones += PollData;
                BasisLocalPlayer.Instance.OnAvatarSwitched += UnAssignFullBodyTrackers;
                BasisLocalPlayer.AfterFinalMove.AddAction(98, ApplyFinalMovement);
                HasEvents = true;
            }
            else
            {
                BasisDebug.Log("has device events assigned already " + UniqueDeviceIdentifier, BasisDebug.LogTag.Input);
            }
        }
        public void ApplyFinalMovement()
        {
            this.transform.SetLocalPositionAndRotation(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation);
        }
        public void UnAssignFullBodyTrackers()
        {
            if (hasRoleAssigned && HasControl)
            {
                if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(trackedRole))
                {
                    Control.IsHintRoleIgnoreRotation = false;
                    UnAssignTracker();
                }
            }
        }
        public void UnAssignFBTracker()
        {
            if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(trackedRole))
            {
                Control.IsHintRoleIgnoreRotation = false;
                UnAssignTracker();
            }
        }
        /// <summary>
        /// this api makes it so after a calibration the inital offset is reset.
        /// will only do its logic if has role assigned
        /// </summary>
        public void UnAssignTracker()
        {
            if (hasRoleAssigned)
            {
                if (HasControl)
                {
                    BasisDebug.Log("UnAssigning Tracker " + Control.name, BasisDebug.LogTag.Input);
                    Control.InverseOffsetFromBone.position = Vector3.zero;
                    Control.InverseOffsetFromBone.rotation = Quaternion.identity;
                    Control.InverseOffsetFromBone.Use = false;
                }
                UnAssignRoleAndTracker();
            }
        }
        public void ApplyTrackerCalibration(BasisBoneTrackedRole Role)
        {
            UnAssignTracker();
            BasisDebug.Log("ApplyTrackerCalibration " + Role + " to tracker " + UniqueDeviceIdentifier, BasisDebug.LogTag.Input);
            AssignRoleAndTracker(Role);
        }
        public void StopTracking()
        {
            if (BasisLocalPlayer.Instance.LocalBoneDriver == null)
            {
                BasisDebug.LogError("Missing Driver!");
                return;
            }
            UnAssignRoleAndTracker();
            if (HasEvents)
            {
                BasisLocalPlayer.Instance.OnPreSimulateBones -= PollData;
                BasisLocalPlayer.Instance.OnAvatarSwitched -= UnAssignFullBodyTrackers;
                BasisLocalPlayer.AfterFinalMove.RemoveAction(98, ApplyFinalMovement);
                HasEvents = false;
            }
            else
            {
                BasisDebug.Log("has device events assigned already " + UniqueDeviceIdentifier, BasisDebug.LogTag.Input);
            }
        }
        public void SetRealTrackers(BasisHasTracked hasTracked, BasisHasRigLayer HasLayer)
        {
            if (Control != null && Control.HasBone)
            {
                Control.HasTracked = hasTracked;
                Control.HasRigLayer = HasLayer;
                if (Control.HasRigLayer == BasisHasRigLayer.HasNoRigLayer)
                {
                    hasRoleAssigned = false;
                    if (TryGetRole(out BasisBoneTrackedRole Role))
                    {
                        BasisLocalPlayer.Instance.LocalAvatarDriver.ApplyHint(Role, false);
                    }
                }
                else
                {
                    hasRoleAssigned = true;
                    if (TryGetRole(out BasisBoneTrackedRole Role))
                    {
                        BasisLocalPlayer.Instance.LocalAvatarDriver.ApplyHint(Role, true);
                    }
                }
                BasisDebug.Log("Set Tracker State for tracker " + UniqueDeviceIdentifier + " with bone " + Control.name + " as " + Control.HasTracked.ToString() + " | " + Control.HasRigLayer.ToString(), BasisDebug.LogTag.Input);
            }
            else
            {
                BasisDebug.LogError("Missing Controller Or Bone", BasisDebug.LogTag.Input);
            }
        }
        public void PollData()
        {
            LastUpdatePlayerControl();
            DoPollData();
        }
        public abstract void DoPollData();
        public void UpdatePlayerControl()
        {
            switch (trackedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    float largestValue = Mathf.Abs(CurrentInputState.Primary2DAxis.x) > Mathf.Abs(CurrentInputState.Primary2DAxis.y)
                        ? CurrentInputState.Primary2DAxis.x
                        : CurrentInputState.Primary2DAxis.y;
                    //0 to 1 largestValue

                    BasisLocalPlayer.Instance.LocalCharacterDriver.SetMovementSpeedMultiplier(largestValue);
                    BasisLocalPlayer.Instance.LocalCharacterDriver.SetMovementVector(CurrentInputState.Primary2DAxis);
                    // todo: consider hoisting variable to be toggled by another user input (eg: thumbstick click)
                    BasisLocalPlayer.Instance.LocalCharacterDriver.UpdateMovementSpeed(true);
                    //only open ui after we have stopped pressing down on the secondary button
                    if (CurrentInputState.SecondaryButtonGetState == false && LastInputState.SecondaryButtonGetState)
                    {
                        if (BasisHamburgerMenu.Instance == null)
                        {
                            BasisHamburgerMenu.OpenHamburgerMenuNow();
                        }
                        else
                        {
                            BasisHamburgerMenu.Instance.CloseThisMenu();
                        }
                    }
                    if (CurrentInputState.PrimaryButtonGetState == false && LastInputState.PrimaryButtonGetState)
                    {
                        if (BasisInputModuleHandler.Instance.HasHoverONInput == false)
                        {
                            BasisLocalMicrophoneDriver.ToggleIsPaused();
                        }
                    }
                    break;
                case BasisBoneTrackedRole.RightHand:
                    BasisLocalPlayer.Instance.LocalCharacterDriver.Rotation = CurrentInputState.Primary2DAxis;
                    if (CurrentInputState.PrimaryButtonGetState)
                    {
                        BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJump();
                    }
                    break;
                case BasisBoneTrackedRole.CenterEye:
                    if (CurrentInputState.PrimaryButtonGetState == false && LastInputState.PrimaryButtonGetState)
                    {
                        if (BasisInputModuleHandler.Instance.HasHoverONInput == false)
                        {
                            BasisLocalMicrophoneDriver.ToggleIsPaused();
                        }
                    }
                    break;
                case BasisBoneTrackedRole.Head:
                    break;
                case BasisBoneTrackedRole.Neck:
                    break;
                case BasisBoneTrackedRole.Chest:
                    break;
                case BasisBoneTrackedRole.Hips:
                    break;
                case BasisBoneTrackedRole.Spine:
                    break;
                case BasisBoneTrackedRole.LeftUpperLeg:
                    break;
                case BasisBoneTrackedRole.RightUpperLeg:
                    break;
                case BasisBoneTrackedRole.LeftLowerLeg:
                    break;
                case BasisBoneTrackedRole.RightLowerLeg:
                    break;
                case BasisBoneTrackedRole.LeftFoot:
                    break;
                case BasisBoneTrackedRole.RightFoot:
                    break;
                case BasisBoneTrackedRole.LeftShoulder:
                    break;
                case BasisBoneTrackedRole.RightShoulder:
                    break;
                case BasisBoneTrackedRole.LeftUpperArm:
                    break;
                case BasisBoneTrackedRole.RightUpperArm:
                    break;
                case BasisBoneTrackedRole.LeftLowerArm:
                    break;
                case BasisBoneTrackedRole.RightLowerArm:
                    break;
                case BasisBoneTrackedRole.LeftToes:
                    break;
                case BasisBoneTrackedRole.RightToes:
                    break;
                case BasisBoneTrackedRole.Mouth:
                    break;
            }
            if (HasRaycaster)
            {
                BasisPointRaycaster.UpdateRaycast();
                BasisUIRaycast.HandleUIRaycast();
            }
            AfterControlApply?.Invoke();
        }
        public void LastUpdatePlayerControl()
        {
            CurrentInputState.CopyTo(LastInputState);
        }
        public abstract void ShowTrackedVisual();
        public abstract void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f);
        public abstract void PlaySoundEffect(string SoundEffectName, float Volume);

        public void PlaySoundEffectDefaultImplementation(string SoundEffectName, float Volume)
        {
            switch (SoundEffectName)
            {
                case "hover":
                    AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.HoverUI, transform.position, Volume);
                    break;
                case "press":
                    AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.pressUI, transform.position, Volume);
                    break;
            }
        }
        public bool UseFallbackModel()
        {
            if (hasRoleAssigned == false)
            {
                return true;
            }
            else
            {
                if (TryGetRole(out BasisBoneTrackedRole Role))
                {
                    if (Role == BasisBoneTrackedRole.Head || Role == BasisBoneTrackedRole.CenterEye || Role == BasisBoneTrackedRole.Neck)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
        public void HideTrackedVisual()
        {
            BasisDebug.Log("HideTrackedVisual", BasisDebug.LogTag.Input);
            if (BasisVisualTracker != null)
            {
                BasisDebug.Log("Found and removing  HideTrackedVisual", BasisDebug.LogTag.Input);
                GameObject.Destroy(BasisVisualTracker.gameObject);
            }
            if (LoadedDeviceRequest != null)
            {
                BasisDebug.Log("Released Memory", BasisDebug.LogTag.Input);
                AddressableLoadFactory.ReleaseResource(LoadedDeviceRequest);
            }
        }
        public void CreateRayCaster(BasisInput BaseInput)
        {
            BasisDebug.Log("Adding RayCaster " + BaseInput.UniqueDeviceIdentifier);

            BasisPointRaycasterRef = new GameObject(nameof(BasisPointRaycaster));
            BasisPointRaycasterRef.transform.parent = BasisLocalPlayer.Instance.transform;
            BasisPointRaycasterRef.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            BasisPointRaycaster = BasisHelpers.GetOrAddComponent<BasisPointRaycaster>(BasisPointRaycasterRef);
            BasisPointRaycaster.Initialize(BaseInput);

            BasisUIRaycast = new BasisUIRaycast();
            BasisUIRaycast.Initialize(BaseInput, BasisPointRaycaster);
            HasRaycaster = true;
        }
        public float Remap01ToMinus1To1(float value)
        {
            return (0.75f - value) * 2f - 0.75f;
        }
        public void LoadModelWithKey(string key)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(key);
            GameObject go = op.WaitForCompletion();
            GameObject gameObject = GameObject.Instantiate(go);
            gameObject.name = CommonDeviceIdentifier;
            gameObject.transform.parent = this.transform;
            if (gameObject.TryGetComponent(out BasisVisualTracker))
            {
                BasisVisualTracker.Initialization(this);
            }
        }
        public void ConvertToScaledDeviceCoord()
        {
            ScaledDeviceCoord.position = UnscaledDeviceCoord.position * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
            ScaledDeviceCoord.rotation = UnscaledDeviceCoord.rotation;
        }
        public void ControlOnlyAsDevice()
        {
            if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
            {
                // Apply position offset using math.mul for quaternion-vector multiplication
                Control.IncomingData.position = ScaledDeviceCoord.position;

                // Apply rotation offset using math.mul for quaternion multiplication
                Control.IncomingData.rotation = ScaledDeviceCoord.rotation;
            }

        }
    }
}
