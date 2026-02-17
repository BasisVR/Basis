using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.Scripts.Device_Management.Devices
{
    /// <summary>
    /// Abstract base class for all input devices (hands, HMD, simulated devices, etc.).
    /// Manages device identity, role assignment, calibration offsets, raycasting helpers,
    /// and lifecycle hooks for polling and applying data to the local rig.
    /// </summary>
    public abstract class BasisInput : MonoBehaviour
    {
        /// <summary>
        /// Whether event subscriptions have been registered for this input.
        /// </summary>
        public bool HasEvents = false;

        /// <summary>
        /// Identifier for the device subsystem/provider (e.g., OpenXR, SimulateXR).
        /// </summary>
        public string SubSystemIdentifier;

        [SerializeField]
        private BasisBoneTrackedRole trackedRole;

        /// <summary>
        /// True if a valid <see cref="BasisBoneTrackedRole"/> is assigned to this input.
        /// </summary>
        [SerializeField]
        public bool hasRoleAssigned;

        /// <summary>
        /// The bone control this input drives (e.g., left hand, right foot).
        /// </summary>
        public BasisLocalBoneControl Control = null;

        /// <summary>
        /// True if a valid <see cref="Control"/> reference exists.
        /// </summary>
        public bool HasControl = false;

        /// <summary>
        /// Unique, stable identifier for this concrete device (e.g., serial).
        /// </summary>
        public string UniqueDeviceIdentifier;

        /// <summary>
        /// Class/type name of the device (for logging or analytics).
        /// </summary>
        public string ClassName;

        [Header("Raw Position Of Device")]
        /// <summary>
        /// Device pose before player scaling is applied.
        /// </summary>
        public BasisCalibratedCoords UnscaledDeviceCoord = new BasisCalibratedCoords();

        [Header("Final Data normally just modified by EyeHeight/AvatarEyeHeight)")]
        /// <summary>
        /// Device pose after scaling/elevation adjustments.
        /// </summary>
        public BasisCalibratedCoords ScaledDeviceCoord = new BasisCalibratedCoords();
        /// <summary>
        /// Common/normalized device identifier (used for matching visual models, capabilities).
        /// </summary>
        public string CommonDeviceIdentifier;

        /// <summary>
        /// Optional visible device model attached to this input.
        /// </summary>
        public BasisVisualTracker BasisVisualTracker;

        /// <summary>
        /// Raycaster for pointing at interactables (e.g., UI).
        /// </summary>
        public BasisPointRaycaster BasisPointRaycaster; //used to raycast against things like UI

        /// <summary>
        /// UI-specific raycasting/interaction helper.
        /// </summary>
        public BasisUIRaycast BasisUIRaycast;

        /// <summary>
        /// Hover Supported Raycasting
        /// </summary>
        public BasisHoverSphere hoverSphere;

        /// <summary>
        /// line renderer associated with this input
        /// </summary>
        public LineRenderer InteractionLineRenderer;

        /// <summary>
        /// Capabilities and matching data for the concrete device.
        /// </summary>
        public DeviceSupportInformation DeviceMatchSettings;

        /// <summary>
        /// Current frame input state (buttons, axes).
        /// </summary>
        [SerializeField]
        public BasisInputState CurrentInputState = new BasisInputState();

        /// <summary>
        /// Last frame input state, used to detect edges/deltas.
        /// </summary>
        [SerializeField]
        public BasisInputState LastInputState = new BasisInputState();

        /// <summary>
        /// Roles that may be duplicated (e.g., both left and right hands).
        /// </summary>
        public static BasisBoneTrackedRole[] CanHaveMultipleRoles = new BasisBoneTrackedRole[] { BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand };

        /// <summary>
        /// Addressables key for the default fallback visual.
        /// </summary>
        public static string FallbackDeviceID = "FallbackSphere";

        /// <summary>
        /// GameObject hosting the <see cref="BasisPointRaycaster"/>.
        /// </summary>
        public GameObject BasisPointRaycasterRef;

        /// <summary>
        /// True once raycast helpers have been initialized.
        /// </summary>
        public bool HasRaycaster = false;

        /// <summary>
        /// Origin/rotation used for raycasts (computed per-frame).
        /// </summary>
        public BasisCalibratedCoords RaycastCoord;

        /// <summary>
        /// Data used to compute inverse offsets from bone after calibration.
        /// </summary>
        [SerializeField]
        public BasisInverseOffsetFromBoneData BasisInverseOffsetData = new BasisInverseOffsetFromBoneData();

        /// <summary>
        /// Additive bias applied when converting a splay parameter (hand-specific tuning).
        /// </summary>
        public float HandBiasSplay = 0;

        /// <summary>
        /// this is used for example when we have multi touch support and need a way to get a bunch of differnt fingers coming from the same "head role"
        /// </summary>
        public bool HasRayCastOverrideSupport;
        /// <summary>
        /// Initialize the tracking lifecycle for this input device, register events, and (optionally) create raycast helpers.
        /// </summary>
        /// <param name="uniqueID">Unique device identifier for this instance.</param>
        /// <param name="unUniqueDeviceID">Normalized device identifier for capability matching.</param>
        /// <param name="subSystems">Subsystem/provider ID (OpenXR, SimulateXR, etc.).</param>
        /// <param name="ForceAssignTrackedRole">If true, forces the provided role even if a matcher suggests otherwise.</param>
        /// <param name="basisBoneTrackedRole">Desired tracked role for this device.</param>
        public void InitalizeTracking(string uniqueID, string unUniqueDeviceID, string subSystems, bool ForceAssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, bool hasRayCastOverrideSupport = false)
        {
            //unassign the old tracker
            UnAssignTracker();
            BasisDebug.Log("Finding ID " + unUniqueDeviceID, BasisDebug.LogTag.Input);

            //configure device identifier
            SubSystemIdentifier = subSystems;
            CommonDeviceIdentifier = unUniqueDeviceID;
            UniqueDeviceIdentifier = uniqueID;
            HasRayCastOverrideSupport = hasRayCastOverrideSupport;
            // Resolve capabilities/overrides (role, visuals, raycast support...)
            DeviceMatchSettings = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier, basisBoneTrackedRole, ForceAssignTrackedRole);
            if (DeviceMatchSettings.HasTrackedRole)
            {
                BasisDebug.Log("Overriding Tracker " + DeviceMatchSettings.DeviceID, BasisDebug.LogTag.Input);
                AssignRoleAndTracker(DeviceMatchSettings.TrackedRole);
            }

            // Initialize raycasting helpers if supported
            if (HasRaycastSupport())
            {
                CreateRayCaster(this);
            }

            // Register simulation/apply loop hooks
            if (HasEvents == false)
            {
                BasisLocalPlayer.Instance.OnLatePollData += LatePollData;
                BasisLocalPlayer.Instance.OnRenderPollData += RenderPollData;
                BasisLocalPlayer.AfterSimulateOnRender.AddAction(98, ApplyFinalMovement);
                HasEvents = true;
            }
            else
            {
                BasisDebug.Log("has device events assigned already " + UniqueDeviceIdentifier, BasisDebug.LogTag.Input);
            }
        }
        public void ComputeUnscaledDeviceCoord(ref BasisCalibratedCoords coords,Vector3 position)
        {
            if (SMModuleSitStand.IsSteatedMode && BasisDeviceManagement.IsCurrentModeVR())
            {
                position.y += SMModuleSitStand.MissingHeightDelta;
                coords.position = position;
            }
            else
            {
                coords.position = position;
            }
        }
        /// <summary>
        /// Computes the raycast origin/direction using the hand’s final transform and active offset.
        /// </summary>
        public void ComputeRaycastDirection(Vector3 Position, Quaternion rotation, Quaternion ActiveRaycastOffset)
        {
            Matrix4x4 parentMatrix = BasisLocalPlayer.localToWorldMatrix;
            Quaternion OutGoingRotation = rotation * ActiveRaycastOffset;//HandFinal.rotation

            RaycastCoord.position = parentMatrix.MultiplyPoint3x4(Position);
            RaycastCoord.rotation = parentMatrix.rotation * OutGoingRotation;
        }
        /// <summary>
        /// Get the currently assigned tracked role (if any).
        /// </summary>
        /// <param name="BasisBoneTrackedRole">Out: role value when assigned.</param>
        /// <returns>True if a role is assigned; otherwise false.</returns>
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

        /// <summary>
        /// Assigns this device to drive a specific bone role and binds its <see cref="Control"/>.
        /// Also validates multiple-role constraints and sets tracker state on success.
        /// </summary>
        /// <param name="Role">The bone role to drive.</param>
        public void AssignRoleAndTracker(BasisBoneTrackedRole Role)
        {
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
                            BasisDebug.LogError($"Already Found tracker for  {Role}", BasisDebug.LogTag.Input);
                            hasRoleAssigned = false;
                            return;
                        }
                        else
                        {
                            BasisDebug.Log($"Has Multiple Roles assigned for {found} most likely ok.", BasisDebug.LogTag.Input);
                        }
                    }
                }
            }
            hasRoleAssigned = true;
            trackedRole = Role;
            HasControl = BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Control, trackedRole);
            if (HasControl)
            {
                if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(trackedRole))//we dont want to offset these ones
                {
                    CalculateOffset();
                }
                SetRealTrackers(BasisHasTracked.HasTracker, BasisHasRigLayer.HasRigLayer,UniqueDeviceIdentifier);
            }
            else
            {
                BasisDebug.LogError("Attempted to find " + Role + " but it did not exist", BasisDebug.LogTag.Input);
            }
        }

        /// <summary>
        /// Computes and applies the inverse offset from the driven bone so that the tracker maintains
        /// the spatial relationship determined during calibration.
        /// </summary>
        public void CalculateOffset()
        {
            BasisInverseOffsetData = new BasisInverseOffsetFromBoneData();

            //get the trackers position in space.
            transform.GetPositionAndRotation(out BasisInverseOffsetData.TrackerPosition, out BasisInverseOffsetData.TrackerRotation);
            BasisInverseOffsetData.InitialInverseTrackRotation = Quaternion.Inverse(BasisInverseOffsetData.TrackerRotation);
            BasisInverseOffsetData.InitialControlRotation = Control.OutgoingWorldData.rotation;

            Vector3 Offset = Control.OutgoingWorldData.position - BasisInverseOffsetData.TrackerPosition;
            Control.InverseOffsetFromBone.position = BasisInverseOffsetData.InitialInverseTrackRotation * (Offset);
            Control.InverseOffsetFromBone.rotation = BasisInverseOffsetData.InitialInverseTrackRotation * BasisInverseOffsetData.InitialControlRotation;
            Control.UseInverseOffset = true;
        }

        /// <summary>
        /// Clears role and control binding and resets tracker state, unless the role was forced by a device matcher.
        /// </summary>
        public void UnAssignRoleAndTracker()
        {
            if (Control != null)
            {
                Control.IncomingData.position = Vector3.zero;
                Control.IncomingData.rotation = Quaternion.identity;
                SetRealTrackers(BasisHasTracked.HasNoTracker, BasisHasRigLayer.HasNoRigLayer, UniqueDeviceIdentifier);
            }
            if (DeviceMatchSettings == null || DeviceMatchSettings.HasTrackedRole == false)
            {
                hasRoleAssigned = false;
                trackedRole = BasisBoneTrackedRole.CenterEye;
                Control = null;
                HasControl = false;
            }
        }

        /// <summary>
        /// Returns true if this device supports pointer/raycast interaction for the current role.
        /// </summary>
        public bool HasRaycastSupport()
        {
            if(HasRayCastOverrideSupport)
            {
                return true;
            }
            return hasRoleAssigned && DeviceMatchSettings.HasRayCastSupport;
        }

        /// <summary>
        /// Applies the final device pose to this transform after simulation each frame.
        /// </summary>
        public void ApplyFinalMovement()
        {
            this.transform.SetLocalPositionAndRotation(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation);
        }

        /// <summary>
        /// If this input controls a full-body (FB) tracker role, unassign it.
        /// </summary>
        public void UnAssignFullBodyTrackers()
        {
            if (hasRoleAssigned && HasControl)
            {
                if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(trackedRole))
                {
                    UnAssignTracker();
                }
            }
        }

        /// <summary>
        /// Unassigns the tracker if the current role is a full-body tracker role.
        /// </summary>
        public void UnAssignFBTracker()
        {
            if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(trackedRole))
            {
                UnAssignTracker();
            }
        }

        /// <summary>
        /// Clears current calibration/offset and unassigns role if present.
        /// Intended to be called when re-calibrating or removing a device.
        /// </summary>
        public void UnAssignTracker()
        {
            if (hasRoleAssigned)
            {
                if (HasControl)
                {
                    BasisDebug.Log($"UnAssigning Tracker {Control.name}", BasisDebug.LogTag.Input);
                    Control.InverseOffsetFromBone.position = Vector3.zero;
                    Control.InverseOffsetFromBone.rotation = Quaternion.identity;
                    Control.UseInverseOffset = false;
                }
                UnAssignRoleAndTracker();
            }
        }

        /// <summary>
        /// Applies tracker calibration and assigns the provided role, replacing any previous assignment.
        /// </summary>
        /// <param name="Role">Role to assign to this device post-calibration.</param>
        public void ApplyTrackerCalibration(BasisBoneTrackedRole Role)
        {
            UnAssignTracker();
            BasisDebug.Log($"ApplyTrackerCalibration {Role} to tracker {UniqueDeviceIdentifier}", BasisDebug.LogTag.Input);
            AssignRoleAndTracker(Role);
        }

        /// <summary>
        /// Stops this device from driving the rig and unregisters frame hooks.
        /// </summary>
        public void StopTracking()
        {
            if (BasisLocalPlayer.Instance.LocalBoneDriver == null)
            {
                BasisDebug.LogError($"Missing {nameof(BasisLocalBoneDriver)}!", BasisDebug.LogTag.Input);
                return;
            }
            UnAssignRoleAndTracker();
            if (HasEvents)
            {
                //deassign
                BasisLocalPlayer.Instance.OnLatePollData -= LatePollData;
                BasisLocalPlayer.Instance.OnRenderPollData -= RenderPollData;
                BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(98, ApplyFinalMovement);
                HasEvents = false;
            }
        }

        /// <summary>
        /// Sets the <see cref="BasisLocalBoneControl"/> tracker/rig-layer flags and toggles rig hints.
        /// </summary>
        /// <param name="hasTracked">Whether this control is actively tracked by hardware.</param>
        /// <param name="HasLayer">Whether a rig layer is available for this control.</param>
        public void SetRealTrackers(BasisHasTracked hasTracked, BasisHasRigLayer HasLayer,string DeviceID)
        {
            if (Control != null)
            {
                if (HasLayer == BasisHasRigLayer.HasNoRigLayer)
                {
                    Control.DevicesWithRoles.Remove(DeviceID);
                    if (Control.DevicesWithRoles.Count == 0)
                    {
                        hasRoleAssigned = false;
                        Control.HasTracked = hasTracked;
                        Control.HasRigLayer = HasLayer;
                    }
                    else
                    {
                        BasisDebug.Log($"Skipping {Control.name}! device had multiple devices associated waiting on removal of {string.Join("", Control.DevicesWithRoles)}", BasisDebug.LogTag.Input);
                    }
                }
                else
                {
                    if (Control.DevicesWithRoles.Contains(DeviceID) == false)
                    {
                        Control.DevicesWithRoles.Add(DeviceID);
                    }
                    hasRoleAssigned = true;
                    Control.HasTracked = hasTracked;
                    Control.HasRigLayer = HasLayer;
                }

                BasisDebug.Log($"Set Tracker State for tracker {UniqueDeviceIdentifier} with bone {Control.name} as {Control.HasTracked} | {Control.HasRigLayer}", BasisDebug.LogTag.Input);
            }
            else
            {
                BasisDebug.LogError("Missing Controller Or Bone", BasisDebug.LogTag.Input);
            }
        }

        /// <summary>
        /// Per-frame poll entry point: copies current state to last, then calls device-specific poll. Late Update
        /// </summary>
        public void LatePollData()
        {
            LastUpdatePlayerControl();//stays here as late update is good for controller inputs not controller movement.
            LateDoPollData();
        }
        /// <summary>
        /// Per-frame poll entry point: copies current state to last, then calls device-specific poll. On Render Pass
        /// </summary>
        public virtual void RenderPollData()
        {

        }
        /// <summary>
        /// Pushes current input state to the action driver and updates raycasting/UI systems.
        /// Invokes <see cref="AfterControlApply"/> afterwards.
        /// </summary>
        public void UpdateInputEvents(bool HasPlayerControlSupport = true,bool hasPlayerRaycastSupport = true)
        {
            if (HasPlayerControlSupport)
            {
                BasisActionDriver.UpdatePlayerControl(trackedRole, ref CurrentInputState, ref LastInputState);
            }
            if (hasPlayerRaycastSupport && HasRaycaster)
            {
                BasisPointRaycaster.UpdateRaycast();
                BasisUIRaycast.HandleUIRaycast();
            }
        }

        /// <summary>
        /// Copies current input state to last-frame state.
        /// </summary>
        public void LastUpdatePlayerControl()
        {
            CurrentInputState.CopyTo(LastInputState);
        }

        /// <summary>
        /// Plays a named UI sound using common Basis audio resources (default implementation).
        /// </summary>
        /// <param name="SoundEffectName">Name of the effect (e.g., "hover", "press").</param>
        /// <param name="Volume">Playback volume.</param>
        public void PlaySoundEffectDefaultImplementation(string SoundEffectName, float Volume)
        {
         //   BasisDebug.Log("Volume was " + Volume);
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

        /// <summary>
        /// Returns true if a fallback 3D model should be used for this device (e.g., for hands but not HMD).
        /// </summary>
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

        /// <summary>
        /// Destroys and hides any instantiated tracked visual.
        /// </summary>
        public void HideTrackedVisual()
        {
            BasisDebug.Log("HideTrackedVisual", BasisDebug.LogTag.Input);
            if (BasisVisualTracker != null)
            {
                BasisDebug.Log("Found and removing  HideTrackedVisual", BasisDebug.LogTag.Input);
                GameObject.Destroy(BasisVisualTracker.gameObject);
            }
        }
        /// <summary>
        /// Creates and initializes raycasting helpers for this device (pointer + UI raycast).
        /// </summary>
        /// <param name="input">The owning input device component.</param>
        public void CreateRayCaster(BasisInput input)
        {
            BasisDebug.Log("Adding RayCaster " + input.UniqueDeviceIdentifier);
            if (BasisPointRaycasterRef == null)
            {
                BasisPointRaycasterRef = new GameObject(nameof(BasisPointRaycaster));
                BasisPointRaycasterRef.transform.parent = BasisLocalPlayer.Instance.transform;
                BasisPointRaycasterRef.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            }
            if (BasisPointRaycaster == null)
            {
                BasisPointRaycaster = BasisHelpers.GetOrAddComponent<BasisPointRaycaster>(BasisPointRaycasterRef);
                BasisPointRaycaster.Initialize(input);
            }
            BasisUIRaycast = new BasisUIRaycast();
            BasisUIRaycast.Initialize(input, BasisPointRaycaster);

            if (InteractionLineRenderer == null)
            {
                GameObject LineRenderer = new GameObject($"{input.name} Line Renderer", new System.Type[] { typeof(LineRenderer) });
                LineRenderer.TryGetComponent<LineRenderer>(out InteractionLineRenderer);
                // deskies cant hover grab :)
                hoverSphere = new BasisHoverSphere(input.RaycastCoord.position, BasisPlayerInteract.hoverRadius, BasisPlayerInteract.k_MaxPhysicHitCount, BasisPlayerInteract.Mask, !BasisPlayerInteract.IsDesktopCenterEye(input), BasisPlayerInteract.OnlySortClosest);
                LineRenderer.transform.SetParent(BasisLocalPlayer.Instance.transform);
                LineRenderer.layer = BasisPlayerInteract.IgnoreRaycasting;
                LineRenderer.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                InteractionLineRenderer.enabled = false;
                InteractionLineRenderer.material = BasisPlayerInteract.LineMaterial;
                InteractionLineRenderer.useWorldSpace = true;
                InteractionLineRenderer.textureMode = LineTextureMode.Tile;
                InteractionLineRenderer.positionCount = 2;
                InteractionLineRenderer.numCapVertices = 20;
                InteractionLineRenderer.numCornerVertices = 20;
                InteractionLineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                InteractionLineRenderer.widthMultiplier = 1;
                InteractionLineRenderer.startWidth = 0.02f;
                InteractionLineRenderer.endWidth = 0.02f;
                InteractionLineRenderer.useWorldSpace = true;
                InteractionLineRenderer.textureMode = LineTextureMode.Tile;
                InteractionLineRenderer.applyActiveColorSpace = false;
            }
            HasRaycaster = true;
        }

        /// <summary>
        /// Remaps a [0,1] input to the range [-1,1] with a specific center shift.
        /// </summary>
        public float Remap01ToMinus1To1(float value)
        {
            return (0.75f - value) * 2f - 0.75f;
        }

        /// <summary>
        /// Converts a [0,1] splay value to [-1,1] and applies <see cref="HandBiasSplay"/>.
        /// </summary>
        public float SplayConversion(float value)
        {
            return value * 2f - 1f + HandBiasSplay;
        }

        /// <summary>
        /// Loads and instantiates a visual model for this device via Addressables.
        /// </summary>
        /// <param name="key">Addressables key for the model prefab.</param>
        public void LoadModelWithKey(string key)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(key);
            GameObject go = op.WaitForCompletion();
            GameObject gameObject = GameObject.Instantiate(go, this.transform);
            gameObject.name = CommonDeviceIdentifier;
            if (gameObject.TryGetComponent(out BasisVisualTracker))
            {
                BasisVisualTracker.Initialization(this);
            }
        }
        public static BasisCalibratedCoords OffsetCoords = new BasisCalibratedCoords(Vector3.zero,Quaternion.identity);
        // <summary>
        /// Applies player scale and OffsetCoords to UnscaledDeviceCoord to produce ScaledDeviceCoord.
        /// OffsetCoords is treated as a rigid transform (R, t).
        /// </summary>
        public void ConvertToScaledDeviceCoord(ref BasisCalibratedCoords unscaled, ref BasisCalibratedCoords scaled)
        {
            float s = BasisHeightDriver.DeviceScale;

            Vector3 p = unscaled.position * s;
            Quaternion r = unscaled.rotation;

            scaled.position = OffsetCoords.position + (OffsetCoords.rotation * p);
            scaled.rotation = OffsetCoords.rotation * r;
        }

        public void ConvertToScaledDeviceCoord()
        {
            float s = BasisHeightDriver.DeviceScale;

            Vector3 p = UnscaledDeviceCoord.position * s;
            Quaternion r = UnscaledDeviceCoord.rotation;

            ScaledDeviceCoord.position = OffsetCoords.position + (OffsetCoords.rotation * p);
            ScaledDeviceCoord.rotation = OffsetCoords.rotation * r;
        }

        /// <summary>
        /// Writes the device’s scaled pose directly into the bound bone control.
        /// </summary>
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

        /// <summary>
        /// Unity callback: final cleanup. Resets rig-layer tracker hints and destroys UI raycast artifacts.
        /// </summary>
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
            }
            if (BasisPointRaycaster != null)
            {
                GameObject.Destroy(BasisPointRaycaster.gameObject);
            }
            if (InteractionLineRenderer != null)
            {
                GameObject.Destroy(InteractionLineRenderer.gameObject);
            }
        }

        /// <summary>
        /// Device-specific poll implementation. Populate <see cref="UnscaledDeviceCoord"/> and/or
        /// <see cref="ScaledDeviceCoord"/> and call <see cref="UpdateInputEvents"/> at the end.
        /// </summary>
        public abstract void LateDoPollData();

        /// <summary>
        /// Implementor should show a tracked visual (controller model) if appropriate.
        /// </summary>
        public abstract void ShowTrackedVisual();

        /// <summary>
        /// Implementor-specific haptics (if supported).
        /// </summary>
        /// <param name="duration">Duration in seconds.</param>
        /// <param name="amplitude">Amplitude/intensity.</param>
        /// <param name="frequency">Frequency (Hz or device-specific units).</param>
        public abstract void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f);

        /// <summary>
        /// Implementor-specific sound playback.
        /// </summary>
        /// <param name="SoundEffectName">Named effect identifier.</param>
        /// <param name="Volume">Playback volume.</param>
        public abstract void PlaySoundEffect(string SoundEffectName, float Volume);

        /// <summary>
        /// Default helper to spawn a model using device matching or a fallback visual.
        /// </summary>
        public void ShowTrackedVisualDefaultImplementation()
        {
            if (BasisVisualTracker == null)
            {
                DeviceSupportInformation Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    LoadModelWithKey(Match.DeviceID);
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        LoadModelWithKey(FallbackDeviceID);
                    }
                }
            }
        }
    }
}
