using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Audio;
using Basis.Scripts.Avatar;
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
    [AutoStaticsCleanup]
    public abstract partial class BasisInput : MonoBehaviour
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
        /// True when this device is half of an active tracker pair, i.e. its data is being
        /// merged into a virtual midpoint device by the pairing service. Calibration skips
        /// linked devices so the merged virtual claims the body role for the pair on its
        /// own. Set/cleared by the pairing service when the partner virtual is created or
        /// torn down.
        /// </summary>
        [SerializeField]
        public bool IsLinked;

        /// <summary>
        /// True when this device's pose comes from camera/optical tracking (e.g. the MediaPipe
        /// webcam source) rather than a worn or handheld tracker. Camera-tracked devices don't
        /// count as body trackers for the desktop calibration entry — webcam tracking alone
        /// shouldn't surface the calibration panel.
        /// </summary>
        [SerializeField]
        public bool IsCameraTracked;

        /// <summary>
        /// What kind of tracking produces this device's pose, and therefore how noisy it is. Set by the
        /// backend that creates the device before it calls <see cref="InitializeTracking"/>, then refined
        /// there from the device identity strings. Read by the "Auto" smoothing preset to filter each body
        /// group for the hardware actually driving it.
        /// </summary>
        [SerializeField]
        public BasisTrackingHardware TrackingHardware = BasisTrackingHardware.Unknown;

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
        public BasisCalibratedCoords UnscaledDeviceCoord = BasisCalibratedCoords.Identity;

        /// <summary>
        /// Signed vertical offset (tracking space, metres) from this device's tracked origin to the
        /// runtime's center-eye. 0 unless a backend whose HMD origin differs from the eyes fills it
        /// (OpenVR); height calibration uses it to scale from the eyes rather than the device origin.
        /// </summary>
        public float CenterEyeVerticalOffset = 0f;

        /// <summary>
        /// Full tracking-space offset (metres) from this device's tracked origin to the runtime's center-eye
        /// (averaged left/right eye-to-head). Zero unless a backend whose HMD origin differs from the eyes
        /// fills it (OpenVR). <see cref="CenterEyeVerticalOffset"/> is its vertical component.
        /// </summary>
        public Vector3 CenterEyeOffset = Vector3.zero;

        [Header("Final Data normally just modified by EyeHeight/AvatarEyeHeight)")]
        /// <summary>
        /// Device pose after scaling/elevation adjustments.
        /// </summary>
        public BasisCalibratedCoords ScaledDeviceCoord = BasisCalibratedCoords.Identity;

        /// <summary>
        /// World-space position offset added to the bone Control only (not the camera/raycast/transform), so a
        /// device can place its avatar bone at the true eye while the rendered pose stays where the compositor
        /// expects. Zero except on the OpenVR HMD.
        /// </summary>
        public Vector3 ScaledControlPositionOffset = Vector3.zero;
        /// <summary>
        /// Common/normalized device identifier (used for matching visual models, capabilities).
        /// </summary>
        public string CommonDeviceIdentifier;

        /// <summary>
        /// The device's hardware serial as reported by its runtime (OpenVR Prop_SerialNumber_String,
        /// OpenXR input device description serial), empty when the backend doesn't expose one. Unlike
        /// <see cref="UniqueDeviceIdentifier"/> this carries no session-volatile device index, so
        /// integrations can recognize a specific physical or virtual device across reconnects
        /// (e.g. SlimeVR's virtual trackers serialize their body part as "human://WAIST").
        /// </summary>
        public string DeviceSerial = string.Empty;

        /// <summary>
        /// The runtime's controller-type/profile string for this device (OpenVR
        /// Prop_ControllerType_String), empty when the backend doesn't expose one. SteamVR encodes a
        /// tracker's user-assigned body role here ("vive_tracker_waist", "vive_tracker_left_foot",
        /// ...), for any tracker brand, so role integrations can honor it without geometry.
        /// </summary>
        public string DeviceControllerType = string.Empty;

        /// <summary>
        /// Optional visible device model attached to this input.
        /// </summary>
        public BasisVisualTracker BasisVisualTracker;
        private UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> _visualModelHandle;

        /// <summary>
        /// Raycaster for pointing at interactables (e.g., UI).
        /// </summary>
        public BasisPointRaycaster BasisPointRaycaster; //used to raycast against things like UI

        /// <summary>
        /// UI-specific raycasting/interaction helper.
        /// </summary>
        [System.NonSerialized] public BasisUIRaycast BasisUIRaycast;

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
        /// True if the given role may be held by multiple devices at once (the hands).
        /// </summary>
        public static bool RoleCanHaveMultiple(BasisBoneTrackedRole role)
        {
            for (int Index = 0; Index < CanHaveMultipleRoles.Length; Index++)
            {
                if (CanHaveMultipleRoles[Index] == role)
                {
                    return true;
                }
            }
            return false;
        }

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
        [System.NonSerialized]
        public BasisInverseOffsetFromBoneData BasisInverseOffsetData = new BasisInverseOffsetFromBoneData();

        /// <summary>
        /// Additive bias applied when converting a splay parameter (hand-specific tuning).
        /// </summary>
        public float HandBiasSplay = 0;

        /// <summary>
        /// this is used for example when we have multi touch support and need a way to get a bunch of different fingers coming from the same "head role"
        /// </summary>
        public bool HasRayCastOverrideSupport;
        [System.NonSerialized] public string OverrideKey;
        [System.NonSerialized] public BasisDeviceIgnore IgnoredParts;
        [System.NonSerialized] public bool HasNaturalRole;
        [System.NonSerialized] public BasisBoneTrackedRole NaturalRole;
        private bool roleFromOverride, pointerWasActive;
        public bool IgnoresDevice => (IgnoredParts & BasisDeviceIgnore.Device) != 0;
        public bool IgnoresPose => (IgnoredParts & (BasisDeviceIgnore.Pose | BasisDeviceIgnore.Device)) != 0;
        public bool IgnoresButtons => (IgnoredParts & (BasisDeviceIgnore.Buttons | BasisDeviceIgnore.Device)) != 0;
        public bool IgnoresSticks => (IgnoredParts & (BasisDeviceIgnore.Sticks | BasisDeviceIgnore.Device)) != 0;
        public bool IgnoresFingers => (IgnoredParts & (BasisDeviceIgnore.Fingers | BasisDeviceIgnore.Device)) != 0;
        public bool IgnoresPointer => (IgnoredParts & (BasisDeviceIgnore.Pointer | BasisDeviceIgnore.Device)) != 0;
        public bool IsHandDevice => (HasNaturalRole && BasisDeviceOverrides.IsHandRole(NaturalRole)) || (hasRoleAssigned && BasisDeviceOverrides.IsHandRole(trackedRole));
        public bool HasRoleOverride => IsHandDevice && BasisDeviceOverrides.TryGetHand(OverrideKey, out _);
        public bool HoldsForcedRole(BasisBoneTrackedRole role) => IsHandDevice && BasisDeviceOverrides.TryGetHand(OverrideKey, out BasisBoneTrackedRole forced) && forced == role;
        public bool IsPoseDriving => HasControl && Control != null && Control.DevicesWithRoles.Contains(UniqueDeviceIdentifier);
        public bool PointerActive => HasRaycaster && !IgnoresPointer && (hasRoleAssigned || HasRayCastOverrideSupport);
        /// <summary>
        /// Initialize the tracking lifecycle for this input device, register events, and (optionally) create raycast helpers.
        /// </summary>
        /// <param name="uniqueID">Unique device identifier for this instance.</param>
        /// <param name="unUniqueDeviceID">Normalized device identifier for capability matching.</param>
        /// <param name="subSystems">Subsystem/provider ID (OpenXR, SimulateXR, etc.).</param>
        /// <param name="ForceAssignTrackedRole">If true, forces the provided role even if a matcher suggests otherwise.</param>
        /// <param name="basisBoneTrackedRole">Desired tracked role for this device.</param>
        public void InitializeTracking(string uniqueID, string unUniqueDeviceID, string subSystems, bool ForceAssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, bool hasRayCastOverrideSupport = false)
        {
            //unassign the old tracker
            UnAssignTracker();
            BasisDebug.Log("Finding ID " + unUniqueDeviceID, BasisDebug.LogTag.Input);

            //configure device identifier
            SubSystemIdentifier = subSystems;
            CommonDeviceIdentifier = unUniqueDeviceID;
            UniqueDeviceIdentifier = uniqueID;
            HasRayCastOverrideSupport = hasRayCastOverrideSupport;
            // A backend's guess is only as good as its class: OpenVR alone carries lighthouse trackers,
            // SlimeVR's virtual ones and Standable's estimates. The identity strings are set by now.
            TrackingHardware = BasisTrackingHardwareClassifier.Refine(TrackingHardware, CommonDeviceIdentifier, DeviceSerial, IsCameraTracked);
            // Resolve capabilities/overrides (role, visuals, raycast support...)
            DeviceMatchSettings = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier, basisBoneTrackedRole, ForceAssignTrackedRole);
            HasNaturalRole = DeviceMatchSettings.HasTrackedRole;
            NaturalRole = DeviceMatchSettings.TrackedRole;
            OverrideKey = BasisDeviceOverrides.KeyFor(this);
            IgnoredParts = BasisDeviceOverrides.GetIgnore(OverrideKey);
            ResolveRole();

            // Initialize raycasting helpers if supported
            if (WantsRaycaster())
            {
                CreateRayCaster(this);
            }

            // Register simulation/apply loop hooks
            if (HasEvents == false)
            {
                BasisLocalPlayer.Instance.OnLatePollData += LatePollData;
                BasisLocalPlayer.Instance.OnRenderPollData += RenderPollData;
                HasEvents = true;
            }
            else
            {
                BasisDebug.Log("has device events assigned already " + UniqueDeviceIdentifier, BasisDebug.LogTag.Input);
            }
        }
        [System.NonSerialized]
        public BasisCalibratedCoords PhysicalDeviceCoord = BasisCalibratedCoords.Identity;
        [System.NonSerialized]
        public string DeviceOffsetKey;
        [System.NonSerialized]
        public bool HasDeviceOffset;
        [System.NonSerialized]
        public Vector3 DeviceOffsetPosition;
        [System.NonSerialized]
        public Quaternion DeviceOffsetRotation = Quaternion.identity;
        public virtual bool AppliesDeviceOffsetAtSource => false;
        public bool SupportsDeviceOffset => AppliesDeviceOffsetAtSource || !(this is BasisInputController);
        public void RefreshDeviceOffset()
        {
            if (!BasisDeviceOffsets.TryGetKey(this, out string key))
            {
                ClearDeviceOffset();
                return;
            }
            DeviceOffsetKey = key;
            BasisDeviceOffsets.TryGet(key, out Vector3 position, out Quaternion rotation);
            SetDeviceOffset(position, rotation);
        }
        public void SetDeviceOffset(Vector3 position, Quaternion rotation)
        {
            DeviceOffsetPosition = position;
            DeviceOffsetRotation = rotation;
            HasDeviceOffset = !BasisDeviceOffsetMath.IsIdentity(position, rotation);
        }
        public void ClearDeviceOffset()
        {
            DeviceOffsetKey = null;
            SetDeviceOffset(Vector3.zero, Quaternion.identity);
        }
        public void ResolveUnscaledFromPhysical(bool applyDeviceOffset)
        {
            if (applyDeviceOffset && HasDeviceOffset)
            {
                BasisDeviceOffsetMath.Compose(PhysicalDeviceCoord.position, PhysicalDeviceCoord.rotation, DeviceOffsetPosition, DeviceOffsetRotation, out UnscaledDeviceCoord.position, out UnscaledDeviceCoord.rotation);
                return;
            }
            UnscaledDeviceCoord = PhysicalDeviceCoord;
        }
        public void GetPhysicalUnscaledPose(out Vector3 position, out Quaternion rotation)
        {
            if (AppliesDeviceOffsetAtSource)
            {
                position = PhysicalDeviceCoord.position;
                rotation = PhysicalDeviceCoord.rotation;
                return;
            }
            position = UnscaledDeviceCoord.position;
            rotation = UnscaledDeviceCoord.rotation;
        }
        public void GetFinalScaledPose(out Vector3 position, out Quaternion rotation)
        {
            position = ScaledDeviceCoord.position;
            rotation = ScaledDeviceCoord.rotation;
            if (HasDeviceOffset && !AppliesDeviceOffsetAtSource)
            {
                BasisDeviceOffsetMath.ApplyScaled(ref position, ref rotation, DeviceOffsetPosition, DeviceOffsetRotation, BasisHeightDriver.DeviceScale);
            }
        }
        public void ComputeUnscaledDeviceCoord(ref BasisCalibratedCoords coords,Vector3 position)
        {
            // Vertical tracking-space offsets (VR only). Seated mode raises the eye to standing height;
            // the play-space mover adds its live OVRAS-style "Space Drag" vertical offset. Both shift the
            // whole tracking space here so the camera, hands, and avatar move together as one without
            // moving the character controller capsule.
            if (BasisDeviceManagement.IsCurrentModeVR())
            {
                float yOffset = BasisLocalPlayspaceMover.VerticalOffset;
                if (SMModuleSitStand.IsSteatedMode)
                {
                    yOffset += SMModuleSitStand.MissingHeightDelta;
                }
                yOffset += BasisHeightDriver.HeightModeGroundingOffset;
                position.y += yOffset;
            }
            coords.position = position;
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

        public void ResolveRole()
        {
            BasisBoneTrackedRole hand = BasisBoneTrackedRole.CenterEye;
            bool overridden = IsHandDevice && BasisDeviceOverrides.TryGetHand(OverrideKey, out hand);
            if (IgnoresDevice)
            {
                if (hasRoleAssigned || Control != null)
                {
                    BasisDebug.Log($"Device {UniqueDeviceIdentifier} is ignored, releasing its role", BasisDebug.LogTag.Input);
                    ReleaseRole();
                }
                return;
            }
            if (!overridden && !HasNaturalRole)
            {
                if (roleFromOverride)
                {
                    ReleaseRole();
                    return;
                }
                SyncPoseDriving();
                return;
            }
            BasisBoneTrackedRole role = overridden ? hand : NaturalRole;
            if (hasRoleAssigned && trackedRole == role && HasControl && Control != null)
            {
                SyncPoseDriving();
                roleFromOverride = overridden;
                return;
            }
            BasisDebug.Log($"Resolving {UniqueDeviceIdentifier} to {role}" + (overridden ? " (override)" : string.Empty), BasisDebug.LogTag.Input);
            AssignRoleAndTracker(role);
            roleFromOverride = overridden;
        }
        private void SyncPoseDriving()
        {
            if (!hasRoleAssigned || Control == null)
            {
                return;
            }
            bool driving = IsPoseDriving;
            if (driving && IgnoresPose)
            {
                ReleasePoseDriving();
            }
            else if (!driving && !IgnoresPose)
            {
                SetRealTrackers(BasisHasTracked.HasTracker, BasisHasRigLayer.HasRigLayer, UniqueDeviceIdentifier);
            }
        }
        public void RefreshOverrides()
        {
            IgnoredParts = BasisDeviceOverrides.GetIgnore(OverrideKey);
            ResolveRole();
            if (!IgnoresDevice && !hasRoleAssigned && BasisDeviceManagement.Instance != null)
            {
                BasisDeviceManagement.Instance.TryRestoreCachedRole(this);
            }
            if (HasRaycaster == false && HasEvents && WantsRaycaster())
            {
                CreateRayCaster(this);
            }
        }
        public bool WantsRaycaster()
        {
            if (HasRayCastOverrideSupport)
            {
                return true;
            }
            return (HasNaturalRole || HasRoleOverride) && DeviceMatchSettings != null && DeviceMatchSettings.HasRayCastSupport;
        }
        private void ReleaseRole()
        {
            UnAssignTracker();
            ForgetRole();
        }
        private void ReleasePoseDriving()
        {
            if (Control == null)
            {
                return;
            }
            bool wasAssigned = hasRoleAssigned;
            SetRealTrackers(BasisHasTracked.HasNoTracker, BasisHasRigLayer.HasNoRigLayer, UniqueDeviceIdentifier);
            if (Control.DevicesWithRoles.Count == 0)
            {
                Control.SetIncoming(Vector3.zero, Quaternion.identity);
            }
            hasRoleAssigned = wasAssigned;
        }
        private void ForgetRole()
        {
            hasRoleAssigned = false;
            roleFromOverride = false;
            trackedRole = BasisBoneTrackedRole.CenterEye;
            ClearDeviceOffset();
            Control = null;
            HasControl = false;
        }

        /// <summary>
        /// Assigns this device to drive a specific bone role and binds its <see cref="Control"/>.
        /// Also validates multiple-role constraints and sets tracker state on success.
        /// </summary>
        /// <param name="Role">The bone role to drive.</param>
        public void AssignRoleAndTracker(BasisBoneTrackedRole Role)
        {
            roleFromOverride = false;
            if (IgnoresDevice)
            {
                BasisDebug.Log($"Device {UniqueDeviceIdentifier} is ignored, refusing role {Role}", BasisDebug.LogTag.Input);
                return;
            }
            if (hasRoleAssigned && HasControl && Control != null && trackedRole != Role)
            {
                SetRealTrackers(BasisHasTracked.HasNoTracker, BasisHasRigLayer.HasNoRigLayer, UniqueDeviceIdentifier);
            }
            bool forced = HoldsForcedRole(Role);
            int InputsCount = BasisDeviceManagement.Instance.AllInputDevices.Count;
            for (int Index = 0; Index < InputsCount; Index++)
            {
                BasisInput Input = BasisDeviceManagement.Instance.AllInputDevices[Index];
                if (Input.TryGetRole(out BasisBoneTrackedRole found) && Input != this)
                {
                    if (found == Role)
                    {
                        if (RoleCanHaveMultiple(found) == false)
                        {
                            BasisDebug.LogError($"Already Found tracker for  {Role}", BasisDebug.LogTag.Input);
                            hasRoleAssigned = false;
                            return;
                        }
                        else
                        {
                            // A same-backend device already holds this multi-capable role (e.g. a
                            // stranded OpenVR controller left over from a SteamVR reconnect). Reclaim
                            // it so only one device per backend drives the role; cross-backend holders
                            // (e.g. hand-tracking coexisting with a controller) are intentionally kept.
                            if (Input.SubSystemIdentifier == SubSystemIdentifier)
                            {
                                if (!forced && Input.HoldsForcedRole(found))
                                {
                                    BasisDebug.Log($"{Role} is forced onto {Input.UniqueDeviceIdentifier}, {UniqueDeviceIdentifier} stays unassigned", BasisDebug.LogTag.Input);
                                    hasRoleAssigned = false;
                                    return;
                                }
                                BasisDebug.Log($"Reclaiming {Role} from same-backend holder {Input.UniqueDeviceIdentifier}", BasisDebug.LogTag.Input);
                                Input.UnAssignTracker();
                            }
                            else
                            {
                                BasisDebug.Log($"Has Multiple Roles assigned for {found} most likely ok.", BasisDebug.LogTag.Input);
                            }
                        }
                    }
                }
            }
            hasRoleAssigned = true;
            trackedRole = Role;
            RefreshDeviceOffset();
            HasControl = BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Control, trackedRole);
            if (HasControl)
            {
                if (IgnoresPose)
                {
                    BasisDebug.Log($"Device {UniqueDeviceIdentifier} holds {Role} for input only, its pose is ignored", BasisDebug.LogTag.Input);
                    return;
                }
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
        /// Calibration-time tracker pose snapshotted back into UNSCALED device space (DeviceScale and
        /// the rigid OffsetCoords undone), so the calibration geometry can be rebuilt at any future
        /// scale/avatar by BasisAvatarIKStageCalibration.ReprojectTrackerOffsetsForCurrentAvatar —
        /// the position analog of the rotation calibration surviving avatar swaps.
        /// </summary>
        public bool HasCalibratedOffsetSnapshot;
        public Vector3 CalibratedUnscaledPosition;
        public Quaternion CalibratedUnscaledRotation = Quaternion.identity;
        // Head anchor the snapshot above was captured against, in the same unscaled space. Each
        // tracker pairs with its OWN capture-time head so reprojection and continuous-calibration
        // adoption rebuild the geometry of THIS capture — a mid-session recapture (device reconnect,
        // matcher-pinned tracker) must not be rebuilt against the ritual-calibration head.
        public Vector3 CalibratedUnscaledHeadPosition;
        public Quaternion CalibratedUnscaledHeadRotation = Quaternion.identity;

        /// <summary>
        /// Computes and applies the inverse offset from the driven bone so that the tracker maintains
        /// the spatial relationship determined during calibration.
        /// </summary>
        public void CalculateOffset()
        {
            BasisInverseOffsetData = new BasisInverseOffsetFromBoneData();

            // Runtime drives the bone from GetFinalScaledPose, which includes a configured tracker
            // mount/device offset for backends that do not bake it at source. Calibration must use
            // that same virtual tracker pose or the mount offset appears a second time after capture.
            GetFinalScaledPose(out Vector3 trackerPosition, out Quaternion trackerRotation);
            BasisCalibratedCoords tracker = new BasisCalibratedCoords(trackerPosition, trackerRotation);
            BasisCalibratedCoords bone = Control.OutGoingData;

            BasisInverseOffsetData.TrackerPosition = tracker.position;
            BasisInverseOffsetData.TrackerRotation = tracker.rotation;
            BasisInverseOffsetData.InitialInverseTrackRotation = Quaternion.Inverse(tracker.rotation);
            BasisInverseOffsetData.InitialControlRotation = bone.rotation;

            // Land the bone on the avatar's own clean T-pose bone position (head-aligned by
            // DriveTpose), converted from world into the bone-sim/player-root frame. The bone sim's
            // degenerate yaw doesn't reliably track the head at large angles, so this uses the real
            // T-pose pose for the head/body direction actually calibrated in, then follows tracker
            // deltas. Prefer the fitted rest skeleton: body fit changes segment lengths before this
            // capture, and using the raw authored snapshot here left trackers attached to the old-sized
            // body. Reprojection uses this same fitted reference. Falls back to the raw snapshot, then
            // the live bone transform, then the bone-sim pose when the fitted reference is unavailable.
            Vector3 referencePosition = bone.position;
            BasisLocalAvatarDriver avatarDriver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalAvatarDriver : null;
            if (avatarDriver != null && TryGetRole(out BasisBoneTrackedRole role))
            {
                BasisLocalBoneControl headControl = BasisLocalBoneDriver.HeadControl;
                if (headControl != null
                    && BasisAvatarIKStageCalibration.TryGetFittedTposePositions(role, out Vector3 fittedHeadTpose, out Vector3 fittedBoneTpose))
                {
                    var headWorld = headControl.OutgoingWorldData;
                    BasisCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, fittedHeadTpose, out Vector3 anchorPos, out Quaternion anchorRot);
                    Vector3 world = anchorPos + anchorRot * fittedBoneTpose;
                    referencePosition = BasisLocalPlayer.localToWorldMatrix.inverse.MultiplyPoint3x4(world);
                }
                else if (BasisLocalAvatarDriver.HasTposeBoneSnapshot
                    && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(role, out var bind)
                    && headControl != null)
                {
                    // Anchor derived from the head (the same math DriveTpose uses to PLACE the avatar
                    // root), not read from the live root: reading the root is only valid in the instant
                    // after DriveTpose ran, and is wrong for captures outside a T-posed calibration
                    // (device-reconnect restores, T-pose-free calibration).
                    var headWorld = headControl.OutgoingWorldData;
                    BasisCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, headControl.TposeLocalScaled.position, out Vector3 anchorPos, out Quaternion anchorRot);
                    float avatarScale = avatarDriver.ScaleAvatarModification != null ? avatarDriver.ScaleAvatarModification.ApplyScale : 1f;
                    if (float.IsNaN(avatarScale) || float.IsInfinity(avatarScale) || avatarScale <= 1e-6f) avatarScale = 1f;
                    Vector3 world = anchorPos + anchorRot * (bind.position * avatarScale);
                    referencePosition = BasisLocalPlayer.localToWorldMatrix.inverse.MultiplyPoint3x4(world);
                }
                else if (avatarDriver.StoredRolesTransforms != null
                    && avatarDriver.StoredRolesTransforms.TryGetValue(role, out Transform avatarBone)
                    && avatarBone != null)
                {
                    referencePosition = BasisLocalPlayer.localToWorldMatrix.inverse.MultiplyPoint3x4(avatarBone.position);
                }
            }

            BasisCalibrationMath.ComputeInverseOffset(tracker.position, tracker.rotation, referencePosition, bone.rotation, out Vector3 InverseOffsetPosition, out Quaternion InverseOffsetRotation);
            Control.SetInverseOffset(InverseOffsetPosition, InverseOffsetRotation);
            Control.UseInverseOffset = true;

            // Scale-free snapshot of where this tracker sat at calibration, paired with the head
            // anchor it was captured against, so the position offset can be re-derived for a new
            // avatar/DeviceScale without redoing the T-pose.
            BasisLocalBoneControl anchorHeadControl = BasisLocalBoneDriver.HeadControl;
            if (anchorHeadControl != null)
            {
                BasisCalibrationMath.UnscaleDeviceCoord(tracker.position, tracker.rotation, BasisHeightDriver.DeviceScale, OffsetCoords.position, OffsetCoords.rotation, out CalibratedUnscaledPosition, out CalibratedUnscaledRotation);
                BasisCalibratedCoords anchorHeadOut = anchorHeadControl.OutGoingData;
                BasisCalibrationMath.UnscaleDeviceCoord(anchorHeadOut.position, anchorHeadOut.rotation, BasisHeightDriver.DeviceScale, OffsetCoords.position, OffsetCoords.rotation, out CalibratedUnscaledHeadPosition, out CalibratedUnscaledHeadRotation);
                HasCalibratedOffsetSnapshot = true;
            }
            else
            {
                HasCalibratedOffsetSnapshot = false;
            }

            BasisCalibrationDebugRecorder.OffsetCapture(this, Control);
        }

        /// <summary>
        /// Clears role and control binding and resets tracker state, unless the role was forced by a device matcher.
        /// </summary>
        public void UnAssignRoleAndTracker()
        {
            if (Control != null)
            {
                Control.SetIncoming(Vector3.zero, Quaternion.identity);
                SetRealTrackers(BasisHasTracked.HasNoTracker, BasisHasRigLayer.HasNoRigLayer, UniqueDeviceIdentifier);
            }
            if (DeviceMatchSettings == null || DeviceMatchSettings.HasTrackedRole == false)
            {
                ForgetRole();
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
            GetFinalScaledPose(out Vector3 localPosition, out Quaternion localRotation);
            // Tip the whole tracking rig (camera via the head device, controllers, trackers) to match the
            // avatar's play-space flip; no-op unless a flip is active. The character controller is untouched.
            BasisLocalPlayspaceMover.ApplyFlipToLocalPose(ref localPosition, ref localRotation);
            this.transform.SetLocalPositionAndRotation(localPosition, localRotation);
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
                    BasisDebug.Log($"UnAssigning Tracker {Control.Role}", BasisDebug.LogTag.Input);
                    Control.SetInverseOffset(Vector3.zero, Quaternion.identity);
                    Control.UseInverseOffset = false;
                }
                HasCalibratedOffsetSnapshot = false;
                UnAssignRoleAndTracker();
            }
        }

        /// <summary>
        /// Applies tracker calibration and assigns the provided role, replacing any previous assignment.
        /// </summary>
        /// <param name="Role">Role to assign to this device post-calibration.</param>
        public void ApplyTrackerCalibration(BasisBoneTrackedRole Role)
        {
            // Respect the master FBT toggle and the per-bone "Use For Calibration"
            // setting — if either is off for this role, drop the assignment so the
            // existing non-tracker fallback (head + hands + foot IK) handles the bone.
            if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(Role)
                && (!Basis.BasisUI.BasisSettingsDefaults.EnableFBT.RawValue
                    || !Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(Role)))
            {
                BasisDebug.Log($"ApplyTrackerCalibration skipped for {Role}: disabled in settings", BasisDebug.LogTag.Input);
                UnAssignTracker();
                return;
            }

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
                        BasisDebug.Log($"Skipping {Control.Role}! device had multiple devices associated waiting on removal of {string.Join("", Control.DevicesWithRoles)}", BasisDebug.LogTag.Input);
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

                BasisDebug.Log($"Set Tracker State for tracker {UniqueDeviceIdentifier} with bone {Control.Role} as {Control.HasTracked} | {Control.HasRigLayer}", BasisDebug.LogTag.Input);

                // Recompute whether ANY FBIK trackers remain — the animator checks this
                // flag to decide if it should drive legs. Without this, removing trackers
                // at runtime leaves the animator suppressed forever.
                BasisAvatarIKStageCalibration.HasFBIKTrackers = CheckAnyFBIKTrackersRemain();
            }
            else
            {
                BasisDebug.LogError("Missing Controller Or Bone", BasisDebug.LogTag.Input);
            }
        }

        /// <summary>
        /// Check if any full-body IK tracker bones still have an active tracker.
        /// Used to update HasFBIKTrackers after removal.
        /// </summary>
        private static bool CheckAnyFBIKTrackersRemain()
        {
            return IsTracked(BasisLocalBoneDriver.LeftFootControl)
                || IsTracked(BasisLocalBoneDriver.RightFootControl)
                || IsTracked(BasisLocalBoneDriver.LeftLowerLegControl)
                || IsTracked(BasisLocalBoneDriver.RightLowerLegControl)
                || IsTracked(BasisLocalBoneDriver.LeftUpperLegControl)
                || IsTracked(BasisLocalBoneDriver.RightUpperLegControl)
                || IsTracked(BasisLocalBoneDriver.HipsControl)
                || IsTracked(BasisLocalBoneDriver.ChestControl)
                || IsTracked(BasisLocalBoneDriver.LeftLowerArmControl)
                || IsTracked(BasisLocalBoneDriver.RightLowerArmControl)
                || IsTracked(BasisLocalBoneDriver.LeftShoulderControl)
                || IsTracked(BasisLocalBoneDriver.RightShoulderControl);
        }

        private static bool IsTracked(BasisLocalBoneControl control)
        {
            return control != null && control.HasTracked == BasisHasTracked.HasTracker;
        }

        /// <summary>
        /// Per-frame poll entry point: copies current state to last, then calls device-specific poll. Late Update
        /// </summary>
        public void LatePollData()
        {
            LastUpdatePlayerControl();//stays here as late update is good for controller inputs not controller movement.
            LateDoPollData();
            if (IgnoredParts != BasisDeviceIgnore.None)
            {
                ApplyIgnoreMask();
            }
        }
        public void ApplyIgnoreMask()
        {
            if (IgnoresButtons)
            {
                CurrentInputState.ClearButtons();
            }
            if (IgnoresSticks)
            {
                CurrentInputState.ClearSticks();
            }
        }
        private void HidePointer()
        {
            if (InteractionLineRenderer != null)
            {
                InteractionLineRenderer.enabled = false;
            }
            BasisUIRaycast?.ClearFrame();
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
            if (IgnoredParts != BasisDeviceIgnore.None)
            {
                ApplyIgnoreMask();
            }
            if (HasPlayerControlSupport && (hasRoleAssigned || HasRayCastOverrideSupport))
            {
                // Roles that may have multiple holders (the hands) dispatch once per frame on the
                // combined state of all holders, so a duplicate or coexisting device can't double-fire
                // edge actions (menu toggle, mic, jump). Every other role keeps the direct fast path.
                if (RoleCanHaveMultiple(trackedRole))
                {
                    BasisActionDriver.UpdatePlayerControlForRole(trackedRole);
                }
                else
                {
                    BasisActionDriver.UpdatePlayerControl(trackedRole, ref CurrentInputState, ref LastInputState);
                }
            }
            if (!hasPlayerRaycastSupport)
            {
                return;
            }
            bool pointerActive = PointerActive;
            if (pointerActive)
            {
                BasisPointRaycaster.UpdateRaycast();
                BasisUIRaycast.HandleUIRaycast();
            }
            else if (pointerWasActive)
            {
                HidePointer();
            }
            pointerWasActive = pointerActive;
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
                    BasisUISounds.PlayAt(BasisUISoundEvent.Hover, BasisDeviceManagement.Instance.HoverUI, transform.position, Volume);
                    break;
                case "grab":
                    BasisUISounds.PlayAt(BasisUISoundEvent.Grab, BasisDeviceManagement.Instance.HoverUI, transform.position, Volume);
                    break;
                case "press":
                    BasisUISounds.PlayAt(BasisUISoundEvent.Press, BasisDeviceManagement.Instance.pressUI, transform.position, Volume);
                    break;
                case "chat":
                    BasisUISounds.PlayAt(BasisUISoundEvent.Chat, BasisDeviceManagement.Instance.ChatNotificationUI, transform.position, Volume);
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
            BasisTrackerMarkerGizmos.Hide(this);
            if (BasisVisualTracker != null)
            {
                BasisDebug.Log("Found and removing  HideTrackedVisual", BasisDebug.LogTag.Input);
                GameObject.Destroy(BasisVisualTracker.gameObject);
            }
            if (_visualModelHandle.IsValid())
            {
                Addressables.Release(_visualModelHandle);
                _visualModelHandle = default;
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
                // deskies can't hover grab :)
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
                Basis.Scripts.UI.BasisRaycastLineCustomization.StyleInteractionLine(InteractionLineRenderer);
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
            // The generic marker ball is drawn by the batched gizmo backend rather than an
            // instantiated FallbackSphere — same material and sizing, no per-device GameObject.
            if (key == FallbackDeviceID)
            {
                BasisTrackerMarkerGizmos.Show(this);
                return;
            }
            if (_visualModelHandle.IsValid())
            {
                Addressables.Release(_visualModelHandle);
                _visualModelHandle = default;
            }
            _visualModelHandle = Addressables.LoadAssetAsync<GameObject>(key);
            GameObject go = _visualModelHandle.WaitForCompletion();
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
            BasisCalibrationMath.ScaleDeviceCoord(unscaled.position, unscaled.rotation, BasisHeightDriver.DeviceScale, OffsetCoords.position, OffsetCoords.rotation, out scaled.position, out scaled.rotation);
        }

        public void ConvertToScaledDeviceCoord()
        {
            BasisCalibrationMath.ScaleDeviceCoord(UnscaledDeviceCoord.position, UnscaledDeviceCoord.rotation, BasisHeightDriver.DeviceScale, OffsetCoords.position, OffsetCoords.rotation, out ScaledDeviceCoord.position, out ScaledDeviceCoord.rotation);
        }

        /// <summary>
        /// Writes the device’s scaled pose directly into the bound bone control.
        /// </summary>
        public void ControlOnlyAsDevice()
        {
            if (hasRoleAssigned && !IgnoresPose && Control.HasTracked != BasisHasTracked.HasNoTracker)
            {
                GetFinalScaledPose(out Vector3 position, out Quaternion rotation);
                Control.SetIncoming(position + ScaledControlPositionOffset, rotation);
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
        /// Default helper to spawn a device visual. Prefers the runtime-provided model (real
        /// controller/tracker geometry from the active XR runtime), then a matched pre-baked model,
        /// then the generic sphere fallback.
        /// </summary>
        public void ShowTrackedVisualDefaultImplementation()
        {
            if (BasisVisualTracker != null || BasisTrackerMarkerGizmos.IsShowing(this))
            {
                return;
            }
            string trackerVisuals = Basis.BasisUI.BasisSettingsDefaults.TrackerVisuals.RawValue;
            if (trackerVisuals == Basis.BasisUI.BasisSettingsDefaults.TrackerVisuals_Off)
            {
                return;
            }
            if (trackerVisuals == Basis.BasisUI.BasisSettingsDefaults.TrackerVisuals_DeviceModels && TryShowRuntimeDeviceModel())
            {
                return;
            }
            ShowBakedOrFallbackVisual();
        }

        /// <summary>
        /// Spawns the matched pre-baked model, or the generic sphere fallback, without attempting a
        /// runtime model. Runtime loaders call this to recover when an async runtime load fails.
        /// </summary>
        public void ShowBakedOrFallbackVisual()
        {
            if (BasisVisualTracker != null || BasisTrackerMarkerGizmos.IsShowing(this))
            {
                return;
            }
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

        /// <summary>
        /// Backend hook: load the real device model from the active XR runtime (SteamVR render
        /// models, OpenXR XR_EXT_render_model). Return true once a runtime model is found and its
        /// load has started (the model may appear asynchronously); false to fall through to the
        /// baked/sphere visual. Default: no runtime model available.
        /// </summary>
        public virtual bool TryShowRuntimeDeviceModel()
        {
            return false;
        }

        /// <summary>
        /// Transform a device visual should attach to so it sits at the device's true tracked pose.
        /// Defaults to this device node (correct for trackers/HMD, whose node is the tracked pose).
        /// Devices whose node is remapped elsewhere (e.g. a controller node placed at the avatar wrist
        /// with an IK rotation offset) override this to expose a node at the raw device pose.
        /// </summary>
        public virtual Transform GetVisualAnchor()
        {
            return transform;
        }
    }
}
