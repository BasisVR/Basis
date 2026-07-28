using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using System;
using System.Collections;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Abstract base class for interactable objects in the Basis SDK.
    /// Provides hover, interact, and influence event management for input devices.
    /// Requires a <see cref="Rigidbody"/> if using trigger-based hover spheres.
    /// </summary>
    [Serializable]
    public abstract class BasisInteractableObject : MonoBehaviour
    {
        /// <summary>
        /// Collider references used for range checks and interaction.
        /// If set to a non-empty array, these colliders will be used as the interactable's colliders.
        /// If empty or null, and a collider exists on the same GameObject, that collider will be used.
        /// If empty or null, and no collider exists on the same GameObject, all child colliders will be used.
        /// </summary>
        [Tooltip("Optional, leave this empty to auto-detect colliders on self, or on children if none on self.")]
        [SerializeField] private Collider[] _colliderRefs;

        private Collider[] _resolvedColliders;
        private GameObject[] _resolvedColliderObjects;

        /// <summary>
        /// Collection of input sources bound to this interactable.
        /// </summary>
        public BasisInputSources Inputs = new(0);

        [Header("Interactable Settings")]
        [SerializeField]
        private bool interactableEnabled = true;

        /// <summary>
        /// Determines whether the interactable should automatically be held after interaction.
        /// </summary>
        [SerializeField]
        public BasisAutoHold AutoHold = BasisAutoHold.None;

        /// <summary>
        /// Enum for controlling automatic hold behavior after interaction.
        /// </summary>
        [Serializable]
        public enum BasisAutoHold
        {
            /// <summary>
            /// Auto-hold on desktop only; VR must keep holding the grab input.
            /// </summary>
            DesktopOnly = 0,

            /// <summary>
            /// Object never remains held; drops as soon as the grab input is released.
            /// </summary>
            None = 1,

            /// <summary>
            /// Auto-hold on desktop and in VR.
            /// </summary>
            Everywhere = 2,
        }

        /// <summary>
        /// Whether auto-hold (staying held after release) is active for this object on the current device.
        /// VR players can opt out via <see cref="BasisSettingsDefaults.DisableVRAutoHold"/>.
        /// Centralizes the check so callers don't compare against <see cref="AutoHold"/> directly.
        /// </summary>
        public bool IsAutoHoldActive()
        {
            switch (AutoHold)
            {
                case BasisAutoHold.Everywhere:
                    if (!Device_Management.BasisDeviceManagement.IsUserInDesktop() && BasisSettingsDefaults.DisableVRAutoHold.RawValue)
                        return false;
                    return true;
                case BasisAutoHold.DesktopOnly:
                    return Device_Management.BasisDeviceManagement.IsUserInDesktop();
                default:
                    return false;
            }
        }
        public BasisInputKey InputKey = BasisInputKey.Trigger;
        public enum BasisInputKey
        {
            Trigger =0,
            SecondaryTrigger = 1,
            Primary2DAxis = 2,
            Secondary2DAxis = 3,
            Primary2DAxisClick = 4,
            Secondary2DAxisClick = 5,
            SecondaryButtonGetState = 6,
            PrimaryButtonGetState = 7,
            SystemOrMenuButton = 8,
            GripButton = 9,
        }
        public bool HasState(BasisInputState state, BasisInputKey Key)
        {
            switch (Key)
            {
                case BasisInputKey.Trigger:
                    // Fire when main trigger is fully pressed
                    return state.Trigger >= 0.9f;

                case BasisInputKey.SecondaryTrigger:
                    // Fire when secondary trigger is fully pressed
                    return state.SecondaryTrigger >= 0.9f;

                case BasisInputKey.Primary2DAxis:
                    // Axis has state if it's non-zero (already deadzoned in BasisInputState)
                    return state.Primary2DAxisDeadZoned.sqrMagnitude > 0f;

                case BasisInputKey.Secondary2DAxis:
                    return state.Secondary2DAxisDeadZoned.sqrMagnitude > 0f;

                case BasisInputKey.Primary2DAxisClick:
                    return state.Primary2DAxisClick;

                case BasisInputKey.Secondary2DAxisClick:
                    return state.Secondary2DAxisClick;

                case BasisInputKey.SecondaryButtonGetState:
                    return state.SecondaryButtonGetState;

                case BasisInputKey.PrimaryButtonGetState:
                    return state.PrimaryButtonGetState;

                case BasisInputKey.SystemOrMenuButton:
                    return state.SystemOrMenuButton;

                case BasisInputKey.GripButton:
                    return state.GripButton;

                default:
                    BasisDebug.LogError($"Unsupported BasisInputKey: {InputKey}");
                    return false;
            }
        }
        /// <summary>
        /// Flag indicating whether this object requires an update loop
        /// while being influenced by inputs.
        /// </summary>
        public bool RequiresUpdateLoop { get; protected set; } = false;

        #region Interaction Events

        /// <summary>
        /// Event triggered when interaction starts with an input.
        /// </summary>
        public UnityEngine.Events.UnityEvent<BasisInput> OnInteractStartEvent = new();

        /// <summary>
        /// Event triggered when interaction ends with an input.
        /// </summary>
        public UnityEngine.Events.UnityEvent<BasisInput> OnInteractEndEvent = new();

        /// <summary>
        /// Event triggered when hover starts from an input.
        /// </summary>
        public Action<BasisInput> OnHoverStartEvent;

        /// <summary>
        /// Event triggered when hover ends from an input.
        /// Includes whether the input will immediately interact.
        /// </summary>
        public Action<BasisInput, bool> OnHoverEndEvent;

        /// <summary>
        /// Event triggered when influence (enabled state) is activated.
        /// </summary>
        public Action OnInfluenceEnable;

        /// <summary>
        /// Event triggered when influence (enabled state) is deactivated.
        /// </summary>
        public Action OnInfluenceDisable;

        #endregion

        /// <summary>
        /// Whether this object can currently be interacted with.
        /// Changing this property invokes cleanup and influence events as needed.
        /// </summary>
        public bool InteractableEnabled
        {
            get => interactableEnabled;
            set
            {
                if (!value)
                {
                    ClearAllInfluencing();
                    if (interactableEnabled)
                        OnInfluenceDisable?.Invoke();
                }
                else
                {
                    if (!interactableEnabled)
                        OnInfluenceEnable?.Invoke();
                }
                interactableEnabled = value;
            }
        }

        /// <summary>
        /// Interaction range in meters (distance from input source to collider/transform).
        /// </summary>
        public float InteractRange = 1f;

        [Header("Direct Grab")]
        [Tooltip("Whether this object can be directly grabbed by hand proximity")]
        public bool AllowDirectGrab = true;

        [Tooltip("Radius around the hand for direct grab detection (meters)")]
        public float GrabRadius = 0.15f;

        /// <summary>
        /// Called during object initialization.
        /// Sets up inputs when the local player is ready.
        /// </summary>
        public virtual void Awake()
        {
            RefreshColliders();
            if (BasisLocalPlayer.PlayerReady)
            {
                SetupInputs();
            }
            else
            {
                BasisLocalPlayer.OnLocalPlayerInitialized += SetupInputs;
            }
        }

        /// <summary>
        /// Registers input devices and subscribes to add/remove events.
        /// </summary>
        private void SetupInputs()
        {
            BasisLocalPlayer.OnLocalPlayerInitialized -= SetupInputs;
            var Devices = Basis.Scripts.Device_Management.BasisDeviceManagement.Instance.AllInputDevices;
            Devices.OnListAdded += OnInputAdded;
            Devices.OnListItemRemoved += OnInputRemoved;
            foreach (BasisInput device in Devices)
            {
                OnInputAdded(device);
            }
        }

        /// <summary>
        /// Cleans up device subscriptions when destroyed.
        /// </summary>
        public virtual void OnDestroy()
        {
            BasisLocalPlayer.OnLocalPlayerInitialized -= SetupInputs;
            var Devices = Basis.Scripts.Device_Management.BasisDeviceManagement.Instance.AllInputDevices;
            Devices.OnListAdded -= OnInputAdded;
            Devices.OnListItemRemoved -= OnInputRemoved;
        }

        /// <summary>
        /// Called when a new input device is added.
        /// Sets up role bindings for the input.
        /// </summary>
        private void OnInputAdded(BasisInput input)
        {
            if (input == null || !input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole role))
                return;

            if (!BasisInputSources.IsInteractionRole(role))
                return;

            if (!Inputs.SetInputByRole(input, BasisInteractInputState.Ignored))
            {
                BasisDebug.LogError("New input added not setup as expected, Input role was set to ignored!");
            }
        }

        /// <summary>
        /// Called when an input device is removed.
        /// Removes role binding if applicable.
        /// </summary>
        private void OnInputRemoved(BasisInput input)
        {
            if (input != null && input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole role)
                && BasisInputSources.IsInteractionRole(role))
            {
                if (Inputs.TryGetByRole(role, out var wrapper) && wrapper.Source != null)
                {
                    if (wrapper.Source.UniqueDeviceIdentifier == input.UniqueDeviceIdentifier)
                    {
                        if (!Inputs.RemoveByRole(role))
                        {
                            BasisDebug.LogError("Something went wrong while removing input");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Determines whether the interactable is within range of a source point.
        /// Uses collider if available, otherwise falls back to transform position.
        /// </summary>
        /// <param name="source">The position of the interacting source (such as the player's hand controller or desktop user's head).</param>
        /// <param name="interactRange">Base interaction range (will be extended for desktop players).</param>
        /// <returns>True if within range, false otherwise.</returns>
        public virtual bool IsWithinRange(Vector3 source, float interactRange)
        {
            float extraReach = 0;
            if (Device_Management.BasisDeviceManagement.IsUserInDesktop())
            {
                // Adding half the AVATAR's height mimics a VR user's arm reach. It must be the avatar's,
                // not the player's: this is a world-space distance test, and SelectedScaledPlayerHeight is
                // the real human's eye height, which does not move when the avatar is scaled — so on a
                // 0.5x avatar the reach bonus was roughly its entire body height.
                extraReach = BasisHeightDriver.SelectedScaledAvatarHeight / 2;
            }
            float limit = interactRange + extraReach;
            return limit >= 0f && (GetClosestPoint(source) - source).sqrMagnitude <= limit * limit;
        }

        public Vector3 GetClosestPoint(Vector3 source)
        {
            Vector3 closestPoint = transform.position;

            if (_resolvedColliders == null)
            {
                RefreshColliders();
            }

            Collider[] colliders = _resolvedColliders;
            GameObject[] owners = _resolvedColliderObjects;
            float closestDistanceSqr = float.MaxValue;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];

                if (col == null || !col.enabled || !owners[i].activeInHierarchy)
                {
                    continue;
                }

                Vector3 point;

                if (col is MeshCollider meshCol && !meshCol.convex)
                {
                    point = meshCol.bounds.ClosestPoint(source);
                }
                else
                {
                    point = col.ClosestPoint(source);
                }

                float distanceSqr = (point - source).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closestPoint = point;
                }
            }

            return closestPoint;
        }

        /// <summary>
        /// Gets the collider attached to this object if one exists.
        /// Resolved once at <see cref="Awake"/> and cached; edit-time callers re-scan every call
        /// so newly authored colliders are picked up without a domain reload.
        /// </summary>
        public Collider[] GetColliders()
        {
            return _resolvedColliders ?? ScanColliders();
        }

        private Collider[] ScanColliders()
        {
            if (_colliderRefs != null && _colliderRefs.Length > 0)
            {
                return _colliderRefs;
            }
            if (TryGetComponent(out Collider col))
            {
                return new Collider[] { col };
            }
            return GetComponentsInChildren<Collider>(true);
        }

        /// <summary>
        /// Resolves the collider set and caches each collider's owning GameObject alongside it, so the
        /// per-frame range and closest-point queries never allocate or walk back to the GameObject
        /// through native interop. Runs at <see cref="Awake"/>; call again after adding or removing
        /// colliders at runtime. Toggling a collider or its GameObject does not need a refresh —
        /// that is checked live per query.
        /// </summary>
        public void RefreshColliders()
        {
            Collider[] colliders = ScanColliders();
            GameObject[] owners = new GameObject[colliders.Length];
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                owners[i] = col == null ? gameObject : col.gameObject;
            }

            _resolvedColliderObjects = owners;
            _resolvedColliders = colliders;
        }

        /// <summary>
        /// Determines whether an input is currently triggering an interaction.
        /// Default checks Grip button, and for desktop CenterEye role with Trigger == 1.
        /// </summary>
        /// <param name="input">The input to check.</param>
        /// <returns>True if interaction should start, false otherwise.</returns>
        public virtual bool IsInteractTriggered(BasisInput input)
        {
            return input.CurrentInputState.GripButton ||
                input.TryGetRole(out var role) &&
                role == Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.CenterEye &&
                input.CurrentInputState.Trigger == 1;
        }

        /// <summary>
        /// Determines whether hold drop has been triggered.
        /// Base implementation always returns true.
        /// Override for objects that have specific hold behavior.
        /// </summary>
        /// <param name="input">The input to check.</param>
        /// <returns>True if hold drop is triggered, otherwise false.</returns>
        public virtual bool IsHoldDropTriggered(BasisInput input)
        {
            return true;
        }

        /// <summary>
        /// Whether a currently auto-held object should be released for this input this frame:
        /// either auto-hold is not enabled, or the drop input has fired.
        /// Lets the interaction poller keep the release decision in one place.
        /// </summary>
        /// <param name="input">The input holding the object.</param>
        /// <returns>True if the held object should be released.</returns>
        public bool ShouldReleaseAutoHold(BasisInput input)
        {
            return !IsAutoHoldActive() || IsHoldDropTriggered(input);
        }
        protected bool CheckUsabilityWithState(BasisInput input, BasisInteractInputState requiredState)
        {
            if (InteractableEnabled == false)
            {
            //    BasisDebug.Log("Interactable was false", BasisDebug.LogTag.System);
                return false;
            }

            // Did we hit UI?
            if (input.BasisUIRaycast.HadRaycastUITarget)
            {
            //    BasisDebug.Log("UI Raycast target was hit", BasisDebug.LogTag.System);
                return false;
            }

            // Input exists?
            if (!Inputs.IsInputAdded(input))
            {
             //   BasisDebug.Log("Input was not added to Inputs", BasisDebug.LogTag.System);
                return false;
            }

            // Has a valid role?
            if (!input.TryGetRole(out TransformBinders.BoneControl.BasisBoneTrackedRole role))
            {
               // BasisDebug.Log("Input did not have a valid bone role", BasisDebug.LogTag.System);
                return false;
            }

            // PlayerInteract knows about this role/input?
            if (!Inputs.TryGetByRole(role, out BasisInputWrapper found))
            {
              //  BasisDebug.Log($"No BasisInputWrapper found for role {role}", BasisDebug.LogTag.System);
                return false;
            }

            // State must match
            if (found.GetState() != requiredState)
            {
               // BasisDebug.Log($"Input state mismatch: Expected {requiredState}, got {found.GetState()}", BasisDebug.LogTag.System);
                return false;
            }

            // Range check
            if (!IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange))
            {
             //   BasisDebug.Log("Input was out of interact range", BasisDebug.LogTag.System);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines if the input is capable of hovering this object.
        /// </summary>
        public abstract bool CanHover(BasisInput input);

        /// <summary>
        /// Checks if this object is currently hovered by the given input.
        /// </summary>
        public abstract bool IsHoveredBy(BasisInput input);

        /// <summary>
        /// Determines if the input is capable of interacting with this object.
        /// </summary>
        public abstract bool CanInteract(BasisInput input);

        /// <summary>
        /// Checks if this object is currently being interacted with by the given input.
        /// </summary>
        public abstract bool IsInteractingWith(BasisInput input);

        /// <summary>
        /// Called when interaction starts. Invokes <see cref="OnInteractStartEvent"/>.
        /// </summary>
        public virtual void OnInteractStart(BasisInput input)
        {
            OnInteractStartEvent?.Invoke(input);
        }

        /// <summary>
        /// Called when interaction ends. Invokes <see cref="OnInteractEndEvent"/>.
        /// </summary>
        public virtual void OnInteractEnd(BasisInput input)
        {
            OnInteractEndEvent?.Invoke(input);
        }

        /// <summary>
        /// Called when hover starts. Invokes <see cref="OnHoverStartEvent"/>.
        /// </summary>
        public virtual void OnHoverStart(BasisInput input)
        {
            OnHoverStartEvent?.Invoke(input);
        }

        /// <summary>
        /// Called when hover ends. Invokes <see cref="OnHoverEndEvent"/>.
        /// </summary>
        /// <param name="input">The input ending hover.</param>
        /// <param name="willInteract">Whether this hover will transition into interaction.</param>
        public virtual void OnHoverEnd(BasisInput input, bool willInteract)
        {
            OnHoverEndEvent?.Invoke(input, willInteract);
        }

        /// <summary>
        /// Per-frame update loop for inputs targeting this interactable.
        /// Only runs when <see cref="RequiresUpdateLoop"/> is true.
        /// </summary>
        public virtual void InputUpdate()
        {

        }

        /// <summary>
        /// Clears state of all influencing inputs.
        /// Ensures proper hover and interaction end events are called.
        /// </summary>
        public virtual void ClearAllInfluencing()
        {
            BasisInputWrapper[] InputArray = Inputs.ToArray();
            int count = InputArray.Length;
            for (int InputIndex = 0; InputIndex < count; InputIndex++)
            {
                BasisInputWrapper input = InputArray[InputIndex];
                if (input.Source != null)
                {
                    if (IsHoveredBy(input.Source))
                    {
                        OnHoverEnd(input.Source, false);
                    }
                    if (IsInteractingWith(input.Source))
                    {
                        OnInteractEnd(input.Source);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether this object can be influenced (hovered or interacted with) by the given input.
        /// </summary>
        /// <param name="input">The input to check.</param>
        /// <returns>True if this object can be influenced, false otherwise.</returns>
        public virtual bool IsInfluencable(BasisInput input)
        {
            return InteractableEnabled && (CanHover(input) || CanInteract(input));
        }

        /// <summary>
        /// Determines if the input can directly grab this object via hand proximity.
        /// Only applicable to hand inputs (LeftHand/RightHand).
        /// </summary>
        public virtual bool CanDirectGrab(BasisInput input)
        {
            if (!AllowDirectGrab || !InteractableEnabled) return false;
            if (input.BasisUIRaycast != null && input.BasisUIRaycast.HadRaycastUITarget) return false;
            if (!Inputs.IsInputAdded(input)) return false;
            if (!input.TryGetRole(out TransformBinders.BoneControl.BasisBoneTrackedRole role)) return false;
            if (role != TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand &&
                role != TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand) return false;
            if (!Inputs.TryGetByRole(role, out BasisInputWrapper found)) return false;
            var state = found.GetState();
            return state == BasisInteractInputState.Ignored || state == BasisInteractInputState.Hovering;
        }

        private bool _interactGateOpen = true;

        private IEnumerator InteractCooldown()
        {
            _interactGateOpen = false;
            yield return new WaitForSeconds(0.1f);
            _interactGateOpen = true;
        }
        public bool InteractionTimerValidation()
        {
            if (!_interactGateOpen)
            {
                return false;
            }

            // start cooldown immediately
            StartCoroutine(InteractCooldown());
            return true;
        }
    }
}
