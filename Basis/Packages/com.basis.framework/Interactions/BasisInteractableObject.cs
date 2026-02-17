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
        public BasisAutoHold AutoHold = BasisAutoHold.No;

        /// <summary>
        /// Enum for controlling automatic hold behavior after interaction.
        /// </summary>
        [Serializable]
        public enum BasisAutoHold
        {
            /// <summary>
            /// Object remains held after interaction until explicitly dropped.
            /// </summary>
            Yes,

            /// <summary>
            /// Object does not remain held after interaction ends.
            /// </summary>
            No
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
        [NonSerialized]
        internal bool RequiresUpdateLoop = false;

        #region Interaction Events

        /// <summary>
        /// Event triggered when interaction starts with an input.
        /// </summary>
        public Action<BasisInput> OnInteractStartEvent;

        /// <summary>
        /// Event triggered when interaction ends with an input.
        /// </summary>
        public Action<BasisInput> OnInteractEndEvent;

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

        /// <summary>
        /// Called during object initialization.
        /// Sets up inputs when the local player is ready.
        /// </summary>
        public virtual void Awake()
        {
            _colliderRefs = GetColliders();
            if (BasisLocalPlayer.PlayerReady)
            {
                SetupInputs();
            }
            else
            {
                BasisLocalPlayer.OnLocalPlayerInitalized += SetupInputs;
            }
        }

        /// <summary>
        /// Registers input devices and subscribes to add/remove events.
        /// </summary>
        private void SetupInputs()
        {
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
            // - disabled -dooly  if (!input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole r))
            //     return;

            if (Inputs.SetInputByRole(input, BasisInteractInputState.Ignored))
            {
            }
            else
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
            if (input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole role))
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
                // Adding half the player's height mimics a VR user's arm reach.
                extraReach = BasisHeightDriver.SelectedScaledPlayerHeight / 2;
            }
            return Vector3.Distance(GetClosestPoint(source), source) <= interactRange + extraReach;
        }

        public Vector3 GetClosestPoint(Vector3 source)
        {
            float closestDistanceSqr = float.MaxValue;
            Vector3 closestPoint = transform.position;
            for (int i = 0; i < _colliderRefs.Length; i++)
            {
                Collider childCollider = _colliderRefs[i];
                Vector3 point = childCollider.ClosestPoint(source);
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
        /// Override with cached reference when possible.
        /// </summary>
        public Collider[] GetColliders()
        {
            if (_colliderRefs != null && _colliderRefs.Length > 0)
            {
                return _colliderRefs;
            }
            if (TryGetComponent(out Collider col))
            {
                return new Collider[] { col };
            }
            return GetComponentsInChildren<Collider>();
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
