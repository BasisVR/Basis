using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using System.Collections;
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
        /// If <see langword="true"/>, builds a simple mesh at <see cref="Start"/> to visualize/highlight the collider.
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
        internal BasisParentConstraint InputConstraint;

        #endregion

        #region Runtime/Internal State

        /// <summary>
        /// Highlight mesh instance cloned from <see cref="ColliderRef"/> (if enabled).
        /// </summary>
        internal GameObject HighlightClone;

        /// <summary>
        /// Handle for the highlight material addressable operation.
        /// </summary>
        internal AsyncOperationHandle<Material> asyncOperationHighlightMat;

        /// <summary>
        /// Loaded highlight material applied to <see cref="HighlightClone"/>.
        /// </summary>
        internal Material ColliderHighlightMat;

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
        public Action<BasisPickUpUseMode> OnPickupUse;

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

        private float _previousDistance = 0;


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

            headPauseRequestName = $"{nameof(BasisPickupInteractable)}-{gameObject.GetInstanceID()}";

            AsyncOperationHandle<Material> op = Addressables.LoadAssetAsync<Material>(k_LoadMaterialAddress);
            ColliderHighlightMat = op.WaitForCompletion();
            asyncOperationHighlightMat = op;

            if (GenerateColliderMesh)
            {
                // NOTE: Collider mesh highlight position and size is only updated on Start().
                //       If runtime updates are required, handle them elsewhere or create a specialized interactable.
                Collider[] colliders = GetColliders();
                if (colliders != null && colliders.Length > 0 && colliders[0] != null)
                {
                    HighlightClone = BasisColliderClone.CloneColliderMesh(colliders[0], gameObject.transform, k_CloneName);
                }

                if (HighlightClone != null)
                {
                    if (HighlightClone.TryGetComponent(out MeshRenderer meshRenderer))
                    {
                        meshRenderer.material = ColliderHighlightMat;
                    }
                    else
                    {
                        BasisDebug.LogWarning("Pickup Interactable could not find MeshRenderer component on mesh clone. Highlights will be broken");
                    }
                }
            }
            OnInteractStartEvent += OnInteractionEventFired;
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
            Collider[] colliders = GetColliders();
            if (colliders != null && colliders.Length > 0 && HighlightClone)
            {
                HighlightClone.SetActive(highlight);
            }
        }

        /// <inheritdoc />
        public override bool CanHover(BasisInput input)
        {
            // NOTE: see CanInteract note
            return InteractableEnabled &&
                (!Inputs.AnyInteracting() || CanSelfSteal) &&               // self-steal
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
            // NOTE: Injected checks must be called at the end so that we can safely assume that at the time this was invoked, everything was valid.
            //       Important for net sync: pending steal requests shouldn't re-invoke with stale data.
            return InteractableEnabled &&
                (!Inputs.AnyInteracting() || CanSelfSteal) &&               // self-steal
                !input.BasisUIRaycast.HadRaycastUITarget &&                 // didn't hit UI target this frame
                Inputs.IsInputAdded(input) &&                               // input exists
                input.TryGetRole(out BasisBoneTrackedRole role) &&          // has role
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&   // input exists within PlayerInteract system
                found.GetState() == BasisInteractInputState.Hovering &&     // only current hover can interact
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange) && // within range
                CanInteractInjected.AllTrue(input);                         // injected
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
            HighlightObject(true);
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
                HighlightObject(false);
            }
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
            if (CanSelfSteal)
                Inputs.ForEachWithState(OnInteractEnd, BasisInteractInputState.Interacting);

            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                BasisDebug.Log("InteractStart: " + wrapper.GetState(), BasisDebug.LogTag.Pickups);
                if (wrapper.GetState() == BasisInteractInputState.Hovering)
                {
                    Vector3 inPos = wrapper.BoneControl.OutgoingWorldData.position;
                    Quaternion inRot = wrapper.BoneControl.OutgoingWorldData.rotation;
                    input.PlaySoundEffect("hover", SMModuleAudio.ActiveMenusVolume);
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

                    transform.GetPositionAndRotation(out Vector3 restPos, out Quaternion restRot);
                    InputConstraint.SetRestPositionAndRotation(restPos, restRot);

                    transform.GetPositionAndRotation(out Vector3 ActivePosition, out Quaternion ActiveRotation);

                    var offsetPos = Quaternion.Inverse(inRot) * (ActivePosition - inPos);
                    var offsetRot = Quaternion.Inverse(inRot) * ActiveRotation;

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
            if (!CanSelfSteal)
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
                    Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Ignored);

                    RequiresUpdateLoop = false;
                    // cleanup Desktop Manipulation since InputUpdate isnt run again till next pickup
                    targetOffset = Vector3.zero;
                    if (pauseHead)
                    {
                        HeadLock.Remove(headPauseRequestName);
                        currentZoopVelocity = Vector3.zero;
                        pauseHead = false;
                    }

                    InputConstraint.Enabled = false;
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

            Vector3 inPos = interactingInput.BoneControl.OutgoingWorldData.position;
            Quaternion inRot = interactingInput.BoneControl.OutgoingWorldData.rotation;


            if (BasisDeviceManagement.IsUserInDesktop())
            {
                PollDesktopControl(Inputs.desktopCenterEye.Source);
            }
            else
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

            // Trigger state machine for OnPickupUse
            bool State = HasState(interactingInput.Source.CurrentInputState, InputKey);
            bool LastState = HasState(interactingInput.Source.LastInputState, InputKey);
            if (State && LastState == false)
            {
                OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpUseDown);
            }
            else
            {
                if (State == false && LastState)
                {
                    OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpUseUp);
                }
                else
                {
                    if (State)
                    {
                        OnPickupUse?.Invoke(BasisPickUpUseMode.OnPickUpStillDown);
                    }
                }
            }

            InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);

            if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
            {
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
                    if (Inputs.leftHand.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = Inputs.leftHand;
                        return true;
                    }
                    else if (Inputs.rightHand.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = Inputs.rightHand;
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
                    if (Inputs.leftHand.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = Inputs.rightHand;
                        return true;
                    }
                    else if (Inputs.rightHand.GetState() == BasisInteractInputState.Interacting)
                    {
                        BasisInputWrapper = Inputs.leftHand;
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
            OnInteractStartEvent -= OnInteractionEventFired;

            Destroy(HighlightClone);
            if (asyncOperationHighlightMat.IsValid())
            {
                asyncOperationHighlightMat.Release();
            }
            base.OnDestroy();
        }

        /// <summary>
        /// Desktop-only: determines whether a held object should be dropped using the secondary trigger (e.g., right-click).
        /// </summary>
        /// <param name="input">Input to test.</param>
        /// <returns>True when desktop center-eye secondary trigger is pressed.</returns>
        public override bool IsHoldDropTriggered(BasisInput input)
        {
            return
                // special case for desktop (right-click)
                input.TryGetRole(out var role) &&
                role == BasisBoneTrackedRole.CenterEye &&
                input.CurrentInputState.SecondaryTrigger == 1; ;
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
