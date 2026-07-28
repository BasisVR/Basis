using Basis.BasisUI;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using Basis.Scripts.BasisSdk.Highlight;
using Basis.Scripts.BasisSdk.Players;
using Unity.Mathematics;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Interactable that supports being picked up, hovered, and manipulated by input sources
    /// (hands or desktop center-eye). Handles highlight mesh creation, input-state transitions,
    /// constraint-based following, desktop zoom/rotate ("zoop") behavior, and realistic drop velocities.
    /// </summary>
    public class BasisPickupInteractable : BasisInteractableObject
    {
        #region Inspector: Pickup Settings

        /// <summary>
        /// When <see langword="true"/>, sets the attached <see cref="Rigidbody"/> to <see cref="Rigidbody.isKinematic"/>
        /// while interacting to avoid physics jitter. If <see langword="false"/>, gravity is disabled during interaction instead.
        /// </summary>
        [Header("Pickup Settings")]
        public bool KinematicWhileInteracting = true;

        /// <summary>
        /// Allows the same player/input to steal interaction from itself, enabling quick re-grabs.
        /// </summary>
        [Tooltip("Enables the ability to self-steal")]
        public bool CanSelfSteal = true;

        /// <summary>
        /// If <see langword="true"/>, the object will smoothly interpolate to the hand position/rotation on pickup.
        /// </summary>
        [Tooltip("The object will move to the player's hand instead of keeping its offset on pickup")]
        public bool LerpToHandOnPickup = true;

        /// <summary>
        /// VR only: while a hand holds the object, follow the avatar's final IK-solved hand bone instead of the
        /// pre-IK hand target, so the object stays welded to the rendered hand and does not slide in the grip.
        /// </summary>
        [Tooltip("VR: weld the held object to the avatar's final IK-solved hand so it doesn't slide in the grip")]
        public bool WeldToHand = true;

        /// <summary>
        /// Show Highlight on haver. does not effect on hover exit.
        /// </summary>
        public bool ShowHighlightOnHover = true;
        /// <summary>
        /// Desktop-only rotation speed multiplier when dragging to rotate the held object.
        /// </summary>
        public float DesktopRotateSpeed = 0.1f;

        /// <summary>
        /// Desktop-only zoom step in Unity units per mouse wheel tick.
        /// </summary>
        [Tooltip("Unity units per scroll step")]
        public float DesktopZoopSpeed = 0.2f;

        /// <summary>
        /// Minimum distance from the source during desktop zoom.
        /// </summary>
        public float DesktopZoopMinDistance = 0.2f;

        /// <summary>
        /// Maximum distance from the source during desktop zoom (additional reach applied based on player height).
        /// </summary>
        public float DesktopZoopMaxDistance = 2.0f;

        /// <summary>
        /// If <see langword="true"/>, builds a simple mesh at <see cref="Start"/> to visualize/highlight the collider
        /// id there are no MeshRenderer children of this GameObject.
        /// </summary>
        [Tooltip("Generate a mesh on start to approximate the referenced collider")]
        public bool GenerateColliderMesh = true;

        /// <summary>
        /// Minimum linear velocity threshold used when applying release velocity on drop.
        /// </summary>
        [Space(10)]
        public float minLinearVelocity = 0.5f;

        /// <summary>
        /// Multiplier applied to linear velocity when interaction ends.
        /// </summary>
        public float interactEndLinearVelocityMultiplier = 1.0f;

        /// <summary>
        /// Minimum angular velocity threshold used when applying release velocity on drop.
        /// </summary>
        [Space(5)]
        public float minAngularVelocity = 0.5f;

        /// <summary>
        /// Multiplier applied to angular velocity when interaction ends.
        /// </summary>
        public float interactEndAngularVelocityMultiplier = 1.0f;
        #endregion

        #region Inspector: References

        [Header("References")]
        /// <summary>
        /// Optional rigidbody reference for physics-based motion and release velocities.
        /// </summary>
        public Rigidbody RigidRef;

        /// <summary>
        /// Parent constraint that drives the object to follow the active input source with offsets.
        /// </summary>
        [SerializeReference]
        protected internal BasisParentConstraint InputConstraint;

        #endregion

        #region Runtime/Internal State

        /// <summary>
        /// Highlight mesh instance cloned from <see cref="ColliderRef"/> (if enabled).
        /// </summary>
        protected internal GameObject HighlightClone;

        /// <summary>
        /// Renderers to highlight when this object is hovered (if enabled).
        /// It's populated from any MeshRenderers on the object, or <see cref="ColliderRef"/> no renderers are found.
        /// </summary>
        internal MeshRenderer[] HighlightRenderers;

        /// <summary>
        /// Source collider each generated <see cref="HighlightClone"/> renderer was built from,
        /// index-matched to <see cref="HighlightRenderers"/>. Null when the highlight uses the
        /// object's own MeshRenderers instead of generated collider meshes.
        /// </summary>
        private Collider[] _highlightCloneSources;

        /// <summary>
        /// Stores the previous kinematic state when toggling during interaction.
        /// </summary>
        public bool _previousKinematicValue = true;

        /// <summary>
        /// Stores the previous gravity state when toggling during interaction.
        /// </summary>
        internal bool _previousGravityValue = true;

        /// <summary>
        /// Addressable key for the highlight material.
        /// </summary>
        public const string k_LoadMaterialAddress = "Interactable/InteractHighlightMat.mat";

        /// <summary>
        /// Name assigned to the generated collider highlight clone.
        /// </summary>
        public const string k_CloneName = "HighlightClone";

        /// <summary>
        /// Smoothing time for desktop zoom interpolation.
        /// </summary>
        public const float k_DesktopZoopSmoothing = 0.2f;

        /// <summary>
        /// Maximum speed for desktop zoom interpolation.
        /// </summary>
        public const float k_DesktopZoopMaxVelocity = 10f;

        /// <summary>
        /// Lock context used to temporarily pause head/camera updates while rotating in desktop.
        /// </summary>
        private readonly BasisLocks.LockContext HeadLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        private static string headPauseRequestName;

        private bool pauseHead = false;
        private Vector3 targetOffset = Vector3.zero;
        private Vector3 currentZoopVelocity = Vector3.zero;

        /// <summary>
        /// Event-like callback invoked every frame a trigger state is detected while interacting.
        /// </summary>
        public UnityEngine.Events.UnityEvent<BasisPickUpUseMode> OnPickupUse;

        /// <summary>
        /// Optional hook points that must all return <see langword="true"/> for hover to be allowed.
        /// </summary>
        public List<Func<BasisInput, bool>> CanHoverInjected = new();

        /// <summary>
        /// Optional hook points that must all return <see langword="true"/> for interaction to be allowed.
        /// </summary>
        public List<Func<BasisInput, bool>> CanInteractInjected = new();

        private Vector3 linearVelocity;
        private Vector3 angularVelocity;
        private Vector3 _previousPosition;
        private Quaternion _previousRotation;

        #endregion

        #region Scale With Gesture
        [Header("Scale With Gesture")]
        /// <summary>
        /// When <see langword="true"/>, enables scaling the object by moving both hands apart/together while holding it.
        /// </summary>
        public bool enableScaleWithGesture = false;
        /// <summary>
        /// Minimum percentage the object can be ensmallened to.
        /// </summary>
        public float minScalePercent = 50f;
        /// <summary>
        /// Maximum percentage the object can be embiggened to.
        /// </summary>
        public float maxScalePercent = 200f;
        #endregion

        #region Lock to Axis

        [Header("Lock to Axis")]
        /// <summary>
        /// When set to an axis, constrains movement to that axis only. Ideal for sliders and buttons.
        /// </summary>
        public BasisAxisType constrainToAxis = BasisAxisType.None;
        /// <summary>
        /// Maximum positive travel limit from the starting position along the constrained axis, in meters.
        /// </summary>
        public float positiveTravelLimit = 0.2f;
        /// <summary>
        /// Maximum negative travel limit from the starting position along the constrained axis, in meters.
        /// </summary>
        public float negativeTravelLimit = 0.0f;

        # endregion

        #region Grid Snap & Rotation Lock

        [Header("Grid Snap & Rotation Lock")]
        [Tooltip("Snaps the held object's world position to a grid while held.")]
        public bool enableGridSnap = false;
        [Tooltip("Grid cell size in meters used when grid snapping.")]
        public float gridSnapSize = 0.25f;
        [Tooltip("Snaps the held object's rotation to fixed angular increments while held.")]
        public bool enableRotationSnap = false;
        [Tooltip("Rotation increment in degrees used when rotation snapping.")]
        public float rotationSnapDegrees = 15f;

        #endregion

        # region Auto Return
        [Header("Auto Return")]
        [Tooltip("Target world position to move to.")]
        /// <summary>
        /// When <see langword="true"/>, object will return to its starting position, scale and rotation after being released for a duration of time
        /// </summary>
        public bool enableAutoReturn = false;
        Vector3 _positionAtStart;
        Quaternion _rotationAtStart;
        Vector3 _scaleAtStart;

        /// <summary>
        /// Amount of time between when an object is released and when it begins to transform back to original state, in seconds
        /// </summary>
        [Tooltip("Delay in seconds before moving.")]
        public float delay = 3f;

        /// <summary>
        /// Amount of time an object will take to transition back to original state after it begins, in seconds
        /// </summary>
        [Tooltip("If > 0, the object will interpolate to the target over this duration; if 0, it will jump instantly.")]
        public float duration = 0f;

        /// <summary>
        /// Type of easing to apply to the interpolation when moving back to original state
        /// </summary>
        [Tooltip("Easing preset to apply to the interpolation.")]
        public BasisEasing.EasingType easing = BasisEasing.EasingType.Linear;

        /// <summary>
        /// Custom AnimationCurve to use for easing instead of the preset options
        /// </summary>
        [Tooltip("Use a custom AnimationCurve instead of the preset easing.")]
        public bool useCustomCurve = false;

        [Tooltip("Custom easing curve evaluated over 0..1 (time).")]
        public AnimationCurve customCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        private Coroutine _autoReturnCoroutine;
        # endregion

        public bool CanSelfStealResolved => CanSelfSteal && !BasisDeviceManagement.IsUserInDesktop();

        private const float lerpToHandDuration = 0.05f;
        private float _lerpElapsed;
        private bool _lerping;

        private Vector3 magicNumberHandOffsetRight = new(0.26f, -0.14f, 0.24f); // right, down, forward
        private Quaternion magicNumberHandRotationRight = Quaternion.Euler(00, 010, -100);
        private Vector3 magicNumberHandOffsetLeft = new(-0.26f, -0.14f, 0.24f); // left, down, forward
        private Quaternion magicNumberHandRotationLeft = Quaternion.Euler(0, -10, -80);
        private Vector3 magicNumberItemDeltaRight = new(-.05f, .025f, .05f); // slightly inward from right hand
        private Vector3 magicNumberItemDeltaLeft = new(.05f, .025f, .05f);   // slightly inward from left hand

        private BasisLocalBoneControl useHandBoneControl;
        private Vector3 useMagicNumberHandOffset;
        private Quaternion useMagicNumberHandRotation;
        private Vector3 useMagicNumberItemDelta;

        private float _previousDistance = 0;

        private bool _pickupUseLastEffectiveState;
        private bool _pickupUsePendingReleaseAfterUI;
        /// <summary>
        /// Unity start hook. Ensures references, allocates constraint, loads highlight material, and optionally builds the collider highlight mesh.
        /// </summary>
        public void Start()
        {
            transform.GetLocalPositionAndRotation(out _positionAtStart, out _rotationAtStart);
            _scaleAtStart = transform.localScale;

            if (RigidRef == null)
            {
                TryGetComponent(out RigidRef);
            }
            InputConstraint = new BasisParentConstraint();
            InputConstraint.sources = new BasisConstraintSourceData[] { new() { weight = 1f } };
            InputConstraint.Enabled = false;

            headPauseRequestName = $"{nameof(BasisPickupInteractable)}-{gameObject.GetEntityId()}";

            // NOTE: Collider mesh highlight position and size is only updated on Start().
            //       If runtime updates are required, handle them elsewhere or create a specialized interactable.
            CalculateHighlightRenderers();

            OnInteractStartEvent.AddListener(OnInteractionEventFired);
        }

        private void OnEnable()
        {
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        }

        private void OnDisable()
        {
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            Drop();
        }

        private void OnBootModeChanged(string mode)
        {
            if (BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.Instance.OnLatePollData -= OverrideDesktopHandTarget;
            }

            Drop();
        }

        internal void OnInteractionEventFired(BasisInput input)
        {
            if (enableAutoReturn && _autoReturnCoroutine != null)
            {
                StopCoroutine(_autoReturnCoroutine);
                _autoReturnCoroutine = null;
            }
        }

        /// <summary>
        /// Toggles the visibility of the highlight clone, if present.
        /// </summary>
        /// <param name="highlight">Whether to enable the highlight.</param>
        public void HighlightObject(bool highlight)
        {
            if (HighlightClone != null)
            {
                HighlightClone.SetActive(highlight);
            }

            if (HighlightRenderers == null || HighlightRenderers.Length == 0)
            {
                return;
            }

            if (!highlight)
            {
                foreach (MeshRenderer r in HighlightRenderers)
                {
                    BasisHighlightManager.Unhighlight(r);
                }
                return;
            }

            if (_highlightCloneSources != null)
            {
                SyncCloneActiveState();
            }

            foreach (MeshRenderer r in HighlightRenderers)
            {
                if (r != null && r.gameObject.activeInHierarchy)
                {
                    BasisHighlightManager.Highlight(r);
                }
            }
        }

        /// <inheritdoc />
        public override bool CanHover(BasisInput input)
        {
            // Prop pickup disabled in settings blocks grabbing a prop you aren't already holding (you can still drop one).
            if (BasisSettingsDefaults.DisablePropPickup.RawValue && !Inputs.AnyInteracting()) return false;
            // NOTE: see CanInteract note
            return InteractableEnabled &&
                (!Inputs.AnyInteracting() || CanSelfStealResolved) &&               // self-steal
                !input.BasisUIRaycast.HadRaycastUITarget &&                 // didn't hit UI target this frame
                Inputs.IsInputAdded(input) &&                               // input exists
                input.TryGetRole(out BasisBoneTrackedRole role) &&          // has role
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&   // input exists within PlayerInteract system
                found.GetState() == BasisInteractInputState.Ignored &&      // in the correct state for hover
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange) && // within range
                CanHoverInjected.AllTrue(input);                            // injected
        }

        /// <inheritdoc />
        public override bool CanInteract(BasisInput input)
        {
            // Prop pickup disabled in settings blocks grabbing a prop you aren't already holding (you can still drop one).
            if (BasisSettingsDefaults.DisablePropPickup.RawValue && !Inputs.AnyInteracting()) return false;
            // NOTE: Injected checks must be called at the end so that we can safely assume that at the time this was invoked, everything was valid.
            //       Important for net sync: pending steal requests shouldn't re-invoke with stale data.
            return InteractableEnabled &&
                (!Inputs.AnyInteracting() || CanSelfStealResolved) &&               // self-steal
                !input.BasisUIRaycast.HadRaycastUITarget &&                 // didn't hit UI target this frame
                Inputs.IsInputAdded(input) &&                               // input exists
                input.TryGetRole(out BasisBoneTrackedRole role) &&          // has role
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&   // input exists within PlayerInteract system
                found.GetState() == BasisInteractInputState.Hovering &&     // only current hover can interact
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange) && // within range
                CanInteractInjected.AllTrue(input);                         // injected
        }

        /// <inheritdoc />
        public override bool CanDirectGrab(BasisInput input)
        {
            if (!base.CanDirectGrab(input)) return false;
            // Prop pickup disabled in settings blocks grabbing a prop you aren't already holding (you can still drop one).
            if (BasisSettingsDefaults.DisablePropPickup.RawValue && !Inputs.AnyInteracting()) return false;
            if (Inputs.AnyInteracting() && !CanSelfStealResolved) return false;
            return CanInteractInjected.AllTrue(input);
        }

        /// <summary>
        /// Called when hovering begins for an input. Promotes the input to the <c>Hovering</c> state,
        /// shows highlight, and invokes <see cref="BasisInteractableObject.OnHoverStartEvent"/>.
        /// </summary>
        /// <param name="input">The input source beginning hover.</param>
        public override void OnHoverStart(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            if (found != null && found.Value.GetState() != BasisInteractInputState.Ignored)
                BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " input state is not ignored OnHoverStart, this shouldn't happen");
            var added = Inputs.ChangeStateByRole(found.Value.Role, BasisInteractInputState.Hovering);
            if (!added)
                BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " did not find role for input on hover");

            OnHoverStartEvent?.Invoke(input);
            if (ShowHighlightOnHover)
            {
                HighlightObject(true);
            }
        }

        /// <summary>
        /// Called when hover ends for an input. Optionally clears state if interaction won't begin,
        /// hides highlight, and invokes <see cref="BasisInteractableObject.OnHoverEndEvent"/>.
        /// </summary>
        /// <param name="input">The input source ending hover.</param>
        /// <param name="willInteract">Whether interaction is about to begin.</param>
        public override void OnHoverEnd(BasisInput input, bool willInteract)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out _))
            {
                if (!willInteract)
                {
                    if (!Inputs.ChangeStateByRole(role, BasisInteractInputState.Ignored))
                    {
                        BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " found input by role but could not remove by it, this is a bug.");
                    }
                }
                OnHoverEndEvent?.Invoke(input, willInteract);
                // Keep the highlight on while another input is still hovering this object.
                if (!AnyOtherInputHovering(role))
                {
                    HighlightObject(false);
                }
            }
        }

        /// <summary>
        /// Returns whether any input other than <paramref name="excludeRole"/> is currently hovering this object.
        /// Each role maps to a unique wrapper slot, so excluding by role safely ignores the calling input.
        /// </summary>
        private bool AnyOtherInputHovering(BasisBoneTrackedRole excludeRole)
        {
            return IsHoveringExcept(Inputs.desktopCenterEye)
                || IsHoveringExcept(Inputs.leftHand)
                || IsHoveringExcept(Inputs.rightHand);

            bool IsHoveringExcept(BasisInputWrapper wrapper)
                => wrapper.Role != excludeRole && wrapper.GetState() == BasisInteractInputState.Hovering;
        }

        /// <summary>
        /// Begins interaction: handles self-steal, toggles physics/gravity, configures the parent constraint
        /// offsets based on the current input pose, and enables evaluation.
        /// </summary>
        /// <param name="input">The input source starting interaction.</param>
        public override void OnInteractStart(BasisInput input)
        {
            if (InteractionTimerValidation() == false)
            {
                return;
            }

            // Clean up interacting ourselves (system won't do this for us) when self-steal is allowed.
            if (CanSelfStealResolved)
                Inputs.ForEachWithState(OnInteractEnd, BasisInteractInputState.Interacting);

            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                BasisDebug.Log("InteractStart: " + wrapper.GetState(), BasisDebug.LogTag.Pickups);
                if (wrapper.GetState() == BasisInteractInputState.Hovering)
                {
                    Vector3 inPos = wrapper.BoneControl.OutgoingWorldData.position;
                    Quaternion inRot = wrapper.BoneControl.OutgoingWorldData.rotation;
                    if (TryGetWeldHandPose(wrapper, out Vector3 weldHandPos, out Quaternion weldHandRot))
                    {
                        inPos = weldHandPos;
                        inRot = weldHandRot;
                    }
                    input.PlaySoundEffect("grab", SMModuleAudio.ActiveMenusVolume);
                    if (RigidRef != null)
                    {
                        if (KinematicWhileInteracting)
                        {
                            _previousKinematicValue = RigidRef.isKinematic;
                            RigidRef.isKinematic = true;
                        }
                        else
                        {
                            _previousGravityValue = RigidRef.useGravity;
                            RigidRef.useGravity = false;
                        }
                    }

                    Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Interacting);
                    RequiresUpdateLoop = true;
                    _pickupUseLastEffectiveState = false;
                    _pickupUsePendingReleaseAfterUI = false;

                    transform.GetPositionAndRotation(out Vector3 restPos, out Quaternion restRot);
                    InputConstraint.SetRestPositionAndRotation(restPos, restRot);

                    transform.GetPositionAndRotation(out Vector3 ActivePosition, out Quaternion ActiveRotation);
                    _previousPosition = ActivePosition;
                    _previousRotation = ActiveRotation;
                    linearVelocity = Vector3.zero;
                    angularVelocity = Vector3.zero;

                    Vector3 offsetPos;
                    bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
                    if (inDesktop)
                    {
                        EnableDesktopHandTracking();
                    }

                    if (LerpToHandOnPickup)
                    {
                        Vector3 lerpTarget = inPos;
                        if (inDesktop)
                        {
                            lerpTarget = inPos + inRot * (useMagicNumberHandOffset * BasisHeightDriver.ScaledToMatchValue);
                        }
                        offsetPos = ComputeClosestBoundsOffset(lerpTarget, inRot, ActivePosition);
                        InputConstraint.GlobalWeight = 0f;
                        _lerpElapsed = 0f;
                        _lerping = true;
                    }
                    else
                    {
                        offsetPos = Quaternion.Inverse(inRot) * (ActivePosition - inPos);
                        InputConstraint.GlobalWeight = 1f;
                    }

                    Quaternion offsetRot = Quaternion.Inverse(inRot) * ActiveRotation;

                    InputConstraint.SetOffsetPositionAndRotation(0, offsetPos, offsetRot);

                    InputConstraint.Enabled = true;

                    OnInteractStartEvent?.Invoke(input);
                }
                else
                {
                    Debug.LogWarning("Input source interacted with ReparentInteractable without highlighting first.");
                }
            }
            else
            {
                BasisDebug.LogWarning("Did not find role for input on Interact start", BasisDebug.LogTag.Pickups);
            }

            // Clean up hovers if self-steal is disabled.
            if (!CanSelfStealResolved)
                Inputs.ForEachWithState(i => OnHoverEnd(i, false), BasisInteractInputState.Hovering);
        }

        /// <summary>
        /// Ends interaction: restores physics/gravity, applies release velocities if appropriate,
        /// disables the parent constraint, clears desktop manipulation state, and fires end events.
        /// </summary>
        /// <param name="input">The input source ending interaction.</param>
        public override void OnInteractEnd(BasisInput input)
        {
            if (enableAutoReturn)
            {
                if (_autoReturnCoroutine != null)
                {
                    StopCoroutine(_autoReturnCoroutine);
                }
                _autoReturnCoroutine = StartCoroutine(MoveAfterDelayCoroutine());
            }
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                if (wrapper.GetState() == BasisInteractInputState.Interacting)
                {
                    UpdateHeldPoseFromInput(wrapper, false);
                    if (_pickupUseLastEffectiveState)
                    {
                        OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpUseUp);
                    }
                    _pickupUseLastEffectiveState = false;
                    _pickupUsePendingReleaseAfterUI = false;

                    Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Ignored);

                    RequiresUpdateLoop = false;
                    // cleanup Desktop Manipulation since InputUpdate isn't run again till next pickup
                    targetOffset = Vector3.zero;
                    if (pauseHead)
                    {
                        HeadLock.Remove(headPauseRequestName);
                        currentZoopVelocity = Vector3.zero;
                        pauseHead = false;
                    }

                    InputConstraint.Enabled = false;
                    _lerping = false;
                    InputConstraint.sources = new BasisConstraintSourceData[] { new() { weight = 1f } };

                    if (RigidRef != null)
                    {
                        if (KinematicWhileInteracting)
                        {
                            RigidRef.isKinematic = _previousKinematicValue;
                        }
                        else
                        {
                            RigidRef.useGravity = _previousGravityValue;
                        }

                        if (!RigidRef.isKinematic)
                        {
                            OnDropVelocity();
                        }
                    }
                    BasisDebug.Log($"OnInteractEnd", BasisDebug.LogTag.Pickups);

                    if (BasisDeviceManagement.IsUserInDesktop())
                    {
                        DisableDesktopHandTracking();
                    }

                    OnInteractEndEvent?.Invoke(input);
                }
            }
        }

        /// <summary>
        /// Applies cached linear and angular velocities to the rigidbody on drop,
        /// zeroing components that are below configured thresholds.
        /// </summary>
        private void OnDropVelocity()
        {
            Vector3 linear = linearVelocity;
            Vector3 angular = angularVelocity;

            if (linear.magnitude >= minLinearVelocity)
            {
                linear *= interactEndLinearVelocityMultiplier;
            }
            else
                linear = Vector3.zero;

            if (angular.magnitude >= minAngularVelocity)
            {
                angular *= interactEndAngularVelocityMultiplier;
            }
            else
                angular = Vector3.zero;

            BasisDebug.Log($"Setting OnDrop velocity. Linear: {linear}, Angular: {angular}", BasisDebug.LogTag.Pickups);

            RigidRef.linearVelocity = linear;
            RigidRef.angularVelocity = angular;
        }

        /// <summary>
        /// Computes instantaneous linear and angular velocity based on current and previous pose.
        /// </summary>
        /// <param name="pos">Current world position.</param>
        /// <param name="rot">Current world rotation.</param>
        private void CalculateVelocity(Vector3 pos, Quaternion rot)
        {
            // Instant linear velocity
            linearVelocity = (pos - _previousPosition) / Time.deltaTime;

            // Instant angular velocity
            Quaternion deltaRotation = rot * Quaternion.Inverse(_previousRotation);
            deltaRotation.ToAngleAxis(out float angle, out Vector3 axis);

            angle = NormalizeAngle360(angle);

            angularVelocity = axis * (angle * Mathf.Deg2Rad) / Time.deltaTime;

            _previousPosition = pos;
            _previousRotation = rot;
        }

        private static Vector3 SnapPositionToGrid(Vector3 position, float size)
        {
            if (size <= 0f)
                return position;
            return new Vector3(
                Mathf.Round(position.x / size) * size,
                Mathf.Round(position.y / size) * size,
                Mathf.Round(position.z / size) * size);
        }

        private static Quaternion SnapRotationToDegrees(Quaternion rotation, float degrees)
        {
            if (degrees <= 0f)
                return rotation;
            Vector3 euler = rotation.eulerAngles;
            euler.x = Mathf.Round(euler.x / degrees) * degrees;
            euler.y = Mathf.Round(euler.y / degrees) * degrees;
            euler.z = Mathf.Round(euler.z / degrees) * degrees;
            return Quaternion.Euler(euler);
        }

        /// <summary>
        /// Normalizes an angle into the [0, 360) range.
        /// </summary>
        /// <param name="angle">Angle in degrees.</param>
        /// <returns>Angle normalized to [0, 360).</returns>
        private float NormalizeAngle360(float angle)
        {
            angle %= 360f;
            if (angle < 0)
                angle += 360f;
            return angle;
        }

        /// <summary>
        /// Per-frame input update while interacting. Drives constraint evaluation, desktop controls,
        /// and invokes <see cref="OnPickupUse"/> depending on trigger states.
        /// </summary>
        public override void InputUpdate()
        {
            if (!GetActiveInteracting(out BasisInputWrapper interactingInput)) return;

            UpdateHeldPoseFromInput(interactingInput, true);
        }

        private void UpdateHeldPoseFromInput(BasisInputWrapper interactingInput, bool pollControls)
        {
            if (pollControls && _lerping)
            {
                _lerpElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_lerpElapsed / lerpToHandDuration);
                InputConstraint.GlobalWeight = t * t;
                if (t >= 1f)
                    _lerping = false;
            }

            Vector3 inPos;
            Quaternion inRot = interactingInput.BoneControl.OutgoingWorldData.rotation;
            bool inDesktop = BasisDeviceManagement.IsUserInDesktop();

            if (inDesktop && LerpToHandOnPickup)
            {
                inPos = useHandBoneControl.OutgoingWorldData.position
                        + interactingInput.BoneControl.OutgoingWorldData.rotation * useMagicNumberItemDelta * BasisHeightDriver.ScaledToMatchValue;
            }
            else
            {
                inPos = interactingInput.BoneControl.OutgoingWorldData.position;
            }

            if (TryGetWeldHandPose(interactingInput, out Vector3 weldHandPos, out Quaternion weldHandRot))
            {
                inPos = weldHandPos;
                inRot = weldHandRot;
            }

            if (inDesktop)
            {
                if (pollControls)
                {
                    PollDesktopControl(Inputs.desktopCenterEye.Source);
                }
            }
            else if (pollControls)
            {
                // If trigger pulled on opposing input, scale object based on hand distance
                if (enableScaleWithGesture && GetOppositeInteracting(out BasisInputWrapper opposingInput))
                {
                    if (HasState(opposingInput.Source.CurrentInputState, InputKey))
                    {
                        float distanceBetweenHands = BasisPickupHelpers.GetNormalizedDistanceBetweenHands(Inputs);
                        if (_previousDistance == -1)
                        {
                            _previousDistance = distanceBetweenHands;
                        }
                        else
                        {
                            float delta = math.abs(_previousDistance - distanceBetweenHands);
                            if (delta > 0.001f)
                            {
                                var scaleDirection = distanceBetweenHands > _previousDistance ? BasisTransform.Direction.Embiggen : BasisTransform.Direction.Ensmallen;
                                float minScale = (minScalePercent / 100) * _scaleAtStart.x;
                                float maxScale = (maxScalePercent / 100) * _scaleAtStart.x;
                                float stepSize = math.abs(minScale - maxScale) / 100f;
                                BasisTransform.ScaleObjectBetween(
                                    transform,
                                    scaleDirection,
                                    stepSize,
                                    minScale,
                                    maxScale
                                    );
                            }
                            _previousDistance = distanceBetweenHands;
                        }
                    }
                }
                else
                {
                    _previousDistance = -1;
                }
            }

            if (pollControls)
            {
                // Pickup use is normal input polling, so release-time pose sampling skips it.
                // UI interaction suppresses use until the input is released away from UI.
                BasisInput useSource = interactingInput.Source;
                bool uiActive =
                    (useSource.BasisUIRaycast != null && useSource.BasisUIRaycast.HadRaycastUITarget) ||
                    (BasisDirectTouch.Instance != null && BasisDirectTouch.Instance.IsDeviceTouching(useSource));
                bool rawState = HasState(useSource.CurrentInputState, InputKey);

                bool effectiveState;
                if (uiActive)
                {
                    // If the button is held while entering/on UI, require a release before the next use fire
                    // so dragging the hand off UI while still held doesn't phantom-fire UseDown.
                    if (rawState) _pickupUsePendingReleaseAfterUI = true;
                    effectiveState = false;
                }
                else if (_pickupUsePendingReleaseAfterUI)
                {
                    if (!rawState) _pickupUsePendingReleaseAfterUI = false;
                    effectiveState = false;
                }
                else
                {
                    effectiveState = rawState;
                }

                bool lastEffective = _pickupUseLastEffectiveState;
                if (effectiveState && !lastEffective)
                {
                    OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpUseDown);
                }
                else if (!effectiveState && lastEffective)
                {
                    OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpUseUp);
                }
                else if (effectiveState)
                {
                    OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpStillDown);
                }
                _pickupUseLastEffectiveState = effectiveState;
            }

            InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);

            if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
            {
                bool forceGridSnap = BasisSettingsDefaults.ForceGridSnap.RawValue;
                if (enableGridSnap || forceGridSnap)
                {
                    pos = SnapPositionToGrid(pos, forceGridSnap ? BasisSettingsDefaults.GridSnapSize.RawValue : gridSnapSize);
                }
                bool forceRotationSnap = BasisSettingsDefaults.ForceRotationSnap.RawValue;
                if (enableRotationSnap || forceRotationSnap)
                {
                    rot = SnapRotationToDegrees(rot, forceRotationSnap ? BasisSettingsDefaults.RotationSnapDegrees.RawValue : rotationSnapDegrees);
                }

                if (constrainToAxis != BasisAxisType.None)
                {
                    transform.GetLocalPositionAndRotation(out Vector3 currentPos, out Quaternion currentRot);

                    // Convert world space result to local space for constraint comparison
                    Vector3 localPos = transform.parent != null
                        ? transform.parent.InverseTransformPoint(pos)
                        : pos;

                    // Apply axis constraint in local space
                    switch (constrainToAxis)
                    {
                        case BasisAxisType.X:
                            localPos = IsWithinTravelLimit(localPos.x, _positionAtStart.x, negativeTravelLimit, positiveTravelLimit)
                                ? new Vector3(localPos.x, currentPos.y, currentPos.z)
                                : currentPos;
                            rot = currentRot; // Lock rotation when constrained
                            break;

                        case BasisAxisType.Y:
                            localPos = IsWithinTravelLimit(localPos.y, _positionAtStart.y, negativeTravelLimit, positiveTravelLimit)
                                ? new Vector3(currentPos.x, localPos.y, currentPos.z)
                                : currentPos;
                            rot = currentRot;
                            break;

                        case BasisAxisType.Z:
                            localPos = IsWithinTravelLimit(localPos.z, _positionAtStart.z, negativeTravelLimit, positiveTravelLimit)
                                ? new Vector3(currentPos.x, currentPos.y, localPos.z)
                                : currentPos;
                            rot = currentRot;
                            break;

                        case BasisAxisType.None:
                        default:
                            break;
                    }

                    // Convert back to world space for final application
                    pos = transform.parent != null
                        ? transform.parent.TransformPoint(localPos)
                        : localPos;

                    // Helper method to check travel limits
                    bool IsWithinTravelLimit(float current, float start, float negativeLimit, float positiveLimit)
                    {
                        float delta = math.abs(current - start);
                        return (current < start && delta <= negativeLimit) || (current > start && delta <= positiveLimit);
                    }
                }

                // Prefer Rigidbody movement when present to preserve physics consistency.
                if (RigidRef != null && !RigidRef.isKinematic)
                {
                    RigidRef.Move(pos, rot);
                }
                else
                {
                    transform.SetPositionAndRotation(pos, rot);
                }
                CalculateVelocity(pos, rot);
            }
        }

        /// <summary>
        /// VR weld source: when <see cref="WeldToHand"/> is enabled and a hand holds the object, returns that
        /// hand's post-IK world pose (<see cref="BasisLocalBoneControl.IKWorldData"/>, the rendered hand) to
        /// follow in place of the pre-IK hand target. False for desktop (center-eye) holds or before the first solve.
        /// </summary>
        private bool TryGetWeldHandPose(BasisInputWrapper wrapper, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            BasisBoneTrackedRole role = wrapper.Role;
            if (!WeldToHand || (role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand))
            {
                return false;
            }
            BasisLocalBoneControl bone = wrapper.BoneControl;
            if (bone == null)
            {
                return false;
            }
            var ik = bone.IKWorldData;
            if (ik.rotation.x == 0f && ik.rotation.y == 0f && ik.rotation.z == 0f && ik.rotation.w == 0f)
            {
                return false;
            }
            position = ik.position;
            rotation = ik.rotation;
            return true;
        }

        /// <summary>
        /// Returns whether the provided input is actively interacting with this object.
        /// </summary>
        /// <param name="input">Input to test.</param>
        public override bool IsInteractingWith(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
        }

        /// <summary>
        /// Returns whether the provided input is currently hovering this object.
        /// </summary>
        /// <param name="input">Input to test.</param>
        public override bool IsHoveredBy(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
        }

        /// <summary>
        /// Handles desktop-only controls: mouse wheel zoom ("zoop") and drag rotation.
        /// Temporarily pauses head/look rotation while rotating.
        /// </summary>
        /// <param name="DesktopEye">The desktop center-eye input wrapper.</param>
        private void PollDesktopControl(BasisInput DesktopEye)
        {
            // scroll zoop
            float mouseScroll = DesktopEye.CurrentInputState.Secondary2DAxisDeadZoned.y; // only ever 1, 0, -1

            Vector3 currentOffset = InputConstraint.sources[0].positionOffset;
            if (targetOffset == Vector3.zero)
            {
                // Initialize the target offset the first time we interact.
                targetOffset = currentOffset;
            }

            if (mouseScroll != 0)
            {
                Transform sourceTransform = BasisLocalCameraDriver.Instance.transform;

                Vector3 movement = DesktopZoopSpeed * mouseScroll * BasisLocalCameraDriver.Forward();
                Vector3 newTargetOffset = targetOffset + sourceTransform.InverseTransformVector(movement);

                // Enforce min/max distance along the source forward.
                float maxDistance = DesktopZoopMaxDistance + BasisHeightDriver.SelectedScaledPlayerHeight / 2;

                if (mouseScroll != 0 && newTargetOffset.z > DesktopZoopMinDistance && newTargetOffset.z < maxDistance)
                {
                    targetOffset = newTargetOffset;
                }
            }

            var dampendOffset = Vector3.SmoothDamp(currentOffset, targetOffset, ref currentZoopVelocity, k_DesktopZoopSmoothing, k_DesktopZoopMaxVelocity);
            InputConstraint.sources[0].positionOffset = dampendOffset;

            if (DesktopEye.CurrentInputState.Secondary2DAxisClick)
            {
                if (!pauseHead)
                {
                    HeadLock.Add(headPauseRequestName);
                    pauseHead = true;
                }

                // drag rotate
                var delta = Mouse.current.delta.ReadValue();
                Quaternion yRotation = Quaternion.AngleAxis(-delta.x * DesktopRotateSpeed, Vector3.up);
                Quaternion xRotation = Quaternion.AngleAxis(delta.y * DesktopRotateSpeed, Vector3.right);

                var rotation = yRotation * xRotation * InputConstraint.sources[0].rotationOffset;
                InputConstraint.sources[0].rotationOffset = rotation;
            }
            else if (pauseHead)
            {
                pauseHead = false;
                if (!HeadLock.Remove(headPauseRequestName))
                {
                    BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " was unable to un-pause head movement, this is a bug!");
                }
            }
        }

        /// <summary>
        /// Retrieves the active interacting input wrapper, if any.
        /// </summary>
        /// <param name="BasisInputWrapper">Outputs the active wrapper when interaction is in progress.</param>
        /// <returns><see langword="true"/> if an input is actively interacting; otherwise <see langword="false"/>.</returns>
        private bool GetActiveInteracting(out BasisInputWrapper BasisInputWrapper)
        {
            switch (Inputs.desktopCenterEye.GetState())
            {
                case BasisInteractInputState.Interacting:
                    BasisInputWrapper = Inputs.desktopCenterEye;
                    return true;
                default:
                    // Check dominant hand first so the preferred hand wins when both are interacting
                    BasisInputWrapper dominant = BasisDominantHand.IsLeftHanded ? Inputs.leftHand : Inputs.rightHand;
                    BasisInputWrapper nonDominant = BasisDominantHand.IsLeftHanded ? Inputs.rightHand : Inputs.leftHand;
                    if (dominant.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = dominant;
                        return true;
                    }
                    else if (nonDominant.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = nonDominant;
                        return true;
                    }
                    else
                    {
                        BasisInputWrapper = new BasisInputWrapper();
                        return false;
                    }
            }
        }

        /// <summary>
        /// Retrieves the opposing active interacting input wrapper, if any. Intended for non-desktop inputs, should return the "opposite" hand from that holding the object
        /// </summary>
        /// <param name="BasisInputWrapper">Outputs the active wrapper when interaction is in progress.</param>
        /// <returns><see langword="true"/> if an input is actively interacting; otherwise <see langword="false"/>.</returns>
        private bool GetOppositeInteracting(out BasisInputWrapper BasisInputWrapper)
        {
            switch (Inputs.desktopCenterEye.GetState())
            {
                case BasisInteractInputState.Interacting:
                    BasisInputWrapper = Inputs.desktopCenterEye;
                    return true;
                default:
                    // Check dominant hand first; return the opposite hand
                    BasisInputWrapper dominant = BasisDominantHand.IsLeftHanded ? Inputs.leftHand : Inputs.rightHand;
                    BasisInputWrapper nonDominant = BasisDominantHand.IsLeftHanded ? Inputs.rightHand : Inputs.leftHand;
                    if (dominant.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = nonDominant;
                        return true;
                    }
                    else if (nonDominant.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = dominant;
                        return true;
                    }
                    else
                    {
                        BasisInputWrapper = new BasisInputWrapper();
                        return false;
                    }
            }
        }

        /// <summary>
        /// Unity destroy hook. Cleans up highlight objects and releases the loaded addressable material.
        /// </summary>
        public override void OnDestroy()
        {
            OnInteractStartEvent.RemoveListener(OnInteractionEventFired);

            Destroy(HighlightClone);
            base.OnDestroy();
        }

        /// <summary>
        /// Desktop drop for an auto-held pickup: right-click (center-eye secondary trigger).
        /// In VR the pickup is dropped by pressing grab again (handled in the interaction poller).
        /// Override to add a device-specific drop button.
        /// </summary>
        /// <param name="input">Input to test.</param>
        /// <returns>True when the desktop drop input is active.</returns>
        public override bool IsHoldDropTriggered(BasisInput input)
        {
            return input.TryGetRole(out var role) &&
                role == BasisBoneTrackedRole.CenterEye &&
                input.CurrentInputState.SecondaryTrigger > 0.8f;
        }

        private IEnumerator MoveAfterDelayCoroutine() {
            yield return new WaitForSeconds(delay);

            if (duration <= 0f)
            {
                transform.SetLocalPositionAndRotation(_positionAtStart, _rotationAtStart);
                transform.localScale = _scaleAtStart;
                yield break;
            }

            float elapsed = 0f;
            transform.GetLocalPositionAndRotation(out Vector3 startPos, out Quaternion startRot);
            Vector3 startScale = transform.localScale;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float easedT = useCustomCurve
                    ? customCurve.Evaluate(Mathf.Clamp01(elapsed / duration))
                    : BasisEasing.ApplyEasing(Mathf.Clamp01(elapsed / duration), easing);

                transform.SetLocalPositionAndRotation(
                    Vector3.Lerp(startPos, _positionAtStart, easedT),
                    Quaternion.Lerp(startRot, _rotationAtStart, easedT)
                );
                transform.localScale = Vector3.Lerp(startScale, _scaleAtStart, easedT);

                yield return null;
            }

            // Ensure final position exactly
            transform.SetLocalPositionAndRotation(_positionAtStart, _rotationAtStart);
            transform.localScale = _scaleAtStart;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only validation to ensure required references are present and to initialize the constraint if missing.
        /// </summary>
        public void OnValidate()
        {
            string errPrefix = "Pickup Interactable needs component defined on self or given a reference for ";
            Collider[] colliders = GetColliders();
            if (colliders == null || colliders.Length == 0)
            {
                Debug.LogWarning(errPrefix + "Collider", gameObject);
            }
            if (InputConstraint == null)
            {
                InputConstraint = new BasisParentConstraint();
            }
        }
#endif

        private void EnableDesktopHandTracking()
        {
            UpdateDominantHandValues();
            BasisLocalPlayer.Instance.OnLatePollData -= OverrideDesktopHandTarget;
            BasisLocalPlayer.Instance.OnLatePollData += OverrideDesktopHandTarget;
            useHandBoneControl.HasTracked = BasisHasTracked.HasTracker;
            useHandBoneControl.HasRigLayer = BasisHasRigLayer.HasRigLayer;
        }

        private void DisableDesktopHandTracking()
        {
            useHandBoneControl.HasTracked = BasisHasTracked.HasNoTracker;
            useHandBoneControl.HasRigLayer = BasisHasRigLayer.HasNoRigLayer;
            BasisLocalPlayer.Instance.OnLatePollData -= OverrideDesktopHandTarget;
        }

        private void OverrideDesktopHandTarget()
        {
            BasisLocalBoneControl eye = BasisLocalBoneDriver.EyeControl;
            BasisLocalBoneControl hand = useHandBoneControl;

            Vector3 offset = useMagicNumberHandOffset * BasisHeightDriver.ScaledToMatchValue;

            hand.SetIncoming(
                eye.IncomingData.position + eye.IncomingData.rotation * offset,
                eye.IncomingData.rotation * useMagicNumberHandRotation);
        }

        private void UpdateDominantHandValues()
        {
            useHandBoneControl = BasisDominantHand.IsLeftHanded ? BasisLocalBoneDriver.LeftHandControl : BasisLocalBoneDriver.RightHandControl;
            useMagicNumberHandOffset = BasisDominantHand.IsLeftHanded ? magicNumberHandOffsetLeft : magicNumberHandOffsetRight;
            useMagicNumberHandRotation = BasisDominantHand.IsLeftHanded ? magicNumberHandRotationLeft : magicNumberHandRotationRight;
            useMagicNumberItemDelta = BasisDominantHand.IsLeftHanded ? magicNumberItemDeltaLeft : magicNumberItemDeltaRight;
        }

        /// <summary>
        /// Computes a constraint offset so that the closest point on the object's collider bounds
        /// aligns with the hand, rather than the object center.
        /// </summary>
        private Vector3 ComputeClosestBoundsOffset(Vector3 inPos, Quaternion handRot, Vector3 objectPos)
        {
            Collider[] colliders = GetColliders();
            if (colliders == null || colliders.Length == 0)
                return Vector3.zero;

            Vector3 bestPoint = objectPos;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
                Vector3 point = col.ClosestPoint(inPos);
                float distSq = (point - inPos).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestPoint = point;
                }
            }
            return Quaternion.Inverse(handRot) * (objectPos - bestPoint);
        }

        /// <summary>
        /// Mirrors each generated highlight clone's active state onto its source collider, so a
        /// child toggled off after <see cref="Start"/> stops contributing to the outline.
        /// </summary>
        private void SyncCloneActiveState()
        {
            int count = Mathf.Min(HighlightRenderers.Length, _highlightCloneSources.Length);
            for (int i = 0; i < count; i++)
            {
                MeshRenderer r = HighlightRenderers[i];
                if (r == null)
                {
                    continue;
                }

                Collider source = _highlightCloneSources[i];
                bool wanted = source != null && source.enabled && source.gameObject.activeInHierarchy;
                GameObject clone = r.gameObject;
                if (clone.activeSelf != wanted)
                {
                    clone.SetActive(wanted);
                }
            }
        }

        protected void CalculateHighlightRenderers()
        {
            HighlightObject(false);
            if (HighlightClone != null)
            {
                DestroyImmediate(HighlightClone);
            }

            _highlightCloneSources = null;
            HighlightRenderers = this.GetComponentsInChildren<MeshRenderer>(true);

            // If no MeshRenderer was found and GenerateColliderMesh is true
            if (GenerateColliderMesh && (HighlightRenderers == null || HighlightRenderers.Length == 0))
            {
                Collider[] colliders = GetColliders();
                if (colliders is { Length: > 0 })
                {
                    HighlightClone = new GameObject(k_CloneName);
                    Transform parent = HighlightClone.transform;
                    parent.SetParent(transform, false);

                    List<MeshRenderer> cloneRenderers = new(colliders.Length);
                    List<Collider> cloneSources = new(colliders.Length);
                    foreach (Collider col in colliders)
                    {
                        if (col == null)
                        {
                            continue;
                        }

                        GameObject newClone = BasisColliderClone.CloneColliderMesh(col, col.name);
                        if (newClone == null)
                        {
                            continue;
                        }

                        newClone.SetActive(true);
                        newClone.transform.SetParent(parent, true);

                        foreach (MeshRenderer r in newClone.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            r.enabled = false; // renderer does not be enabled for highlight feature
                            cloneRenderers.Add(r);
                            cloneSources.Add(col);
                        }
                    }

                    HighlightRenderers = cloneRenderers.ToArray();
                    _highlightCloneSources = cloneSources.ToArray();

                    HighlightClone.SetActive(false);
                }
            }

            if (HighlightRenderers == null || HighlightRenderers.Length == 0)
            {
                BasisDebug.LogWarning("Pickup Interactable could not find or generate any MeshRenderer components. Highlights will be broken");
            }
        }

        /// <summary>
        /// Convenience method to force a drop by clearing all influencers.
        /// </summary>
        public void Drop() => ClearAllInfluencing();
    }

    /// <summary>
    /// Helper extension for evaluating a list of boolean predicates against a single argument.
    /// </summary>
    internal static class PickupListExt
    {
        /// <summary>
        /// Returns <see langword="true"/> only if every predicate in <paramref name="list"/> returns
        /// <see langword="true"/> when invoked with <paramref name="arg"/>.
        /// </summary>
        /// <typeparam name="T">Argument type.</typeparam>
        /// <param name="list">List of predicates.</param>
        /// <param name="arg">Argument to pass to each predicate.</param>
        /// <returns>Whether all predicates returned true.</returns>
        internal static bool AllTrue<T>(this IList<Func<T, bool>> list, T arg)
        {
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                if (!list[i].Invoke(arg))
                    return false;
            }
            return true;
        }
    }
}
