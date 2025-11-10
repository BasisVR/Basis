using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
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
            if (BasisLocalPlayer.PlayerReady)
                SetupInputs();
            else
                BasisLocalPlayer.OnLocalPlayerCreatedAndReady += SetupInputs;
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
            if (!input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole r))
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
            if (input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole role))
                if (!Inputs.RemoveByRole(role))
                    BasisDebug.LogError("Something went wrong while removing input");
        }

        /// <summary>
        /// Determines whether the interactable is within range of a source point.
        /// Uses collider if available, otherwise falls back to transform position.
        /// </summary>
        /// <param name="source">The source position (e.g., controller).</param>
        /// <param name="InteractRange">The maximum allowed range.</param>
        /// <returns>True if within range, false otherwise.</returns>
        public virtual bool IsWithinRange(Vector3 source, float InteractRange)
        {
            Collider collider = GetCollider();
            if (collider != null)
            {
                return Vector3.Distance(collider.ClosestPoint(source), source) <= InteractRange;
            }
            return Vector3.Distance(transform.position, source) <= InteractRange;
        }

        /// <summary>
        /// Gets the collider attached to this object if one exists.
        /// Override with cached reference when possible.
        /// </summary>
        public virtual Collider GetCollider()
        {
            if (TryGetComponent(out Collider col))
            {
                return col;
            }
            return null;
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

        protected bool _checkUsabilityWithState(BasisInput input, BasisInteractInputState requiredState)
        {
            return InteractableEnabled &&
                !input.BasisUIRaycast.HadRaycastUITarget &&                 // didn't hit UI target this frame
                Inputs.IsInputAdded(input) &&                               // input exists
                input.TryGetRole(out TransformBinders.BoneControl.BasisBoneTrackedRole role) && // has role
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&   // input exists within PlayerInteract system
                found.GetState() == requiredState &&                        // only this state can interact
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange); // within range
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
        public abstract void InputUpdate();

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
    }
}
