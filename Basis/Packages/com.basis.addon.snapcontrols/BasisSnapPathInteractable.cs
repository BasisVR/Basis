using System.Collections;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Abstract interactable that constrains motion to a finite list of discrete snap points
    /// along an arbitrary path (arc, line, spline, etc.). While being grabbed by a hand or
    /// pointed at by a laser-trigger, the object tracks the active input's world position,
    /// finds the nearest snap point each frame, and tweens to that pose when the index changes.
    ///
    /// Concrete subclasses implement <see cref="FindNearestIndex"/> (path-specific projection)
    /// and optionally override <see cref="EvaluateAtIndex"/> (default: the snap point's own pose).
    /// </summary>
    public abstract class BasisSnapPathInteractable : BasisInteractableObject
    {
        [Header("Snap Path")]
        [Tooltip("Ordered list of snap-point transforms. Order defines the index sequence.")]
        public Transform[] snapPoints;

        [Tooltip("Index this interactable initially rests at. Clamped to [0, snapPoints.Length - 1] at Awake.")]
        public int initialIndex = 0;

        [Header("Snap Visuals")]
        [Tooltip("Duration of the ease-out tween between snap points. Set to 0 for instant teleport.")]
        public float tweenDuration = 0.08f;

        [Header("Snap Events")]
        public UnityEvent<int, int> OnSnapIndexChanged;

        public int CurrentIndex { get; protected set; }

        private Coroutine _tweenCoroutine;
        private BasisActivationTarget[] _activationTargets;

        public override void Awake()
        {
            base.Awake();
            RequiresUpdateLoop = false;
            if (snapPoints == null || snapPoints.Length == 0)
            {
                BasisDebug.LogError($"{nameof(BasisSnapPathInteractable)} on {name} has no snap points configured.");
                return;
            }
            CurrentIndex = Mathf.Clamp(initialIndex, 0, snapPoints.Length - 1);

            int snapCount = snapPoints.Length;
            _activationTargets = new BasisActivationTarget[snapCount];
            for (int i = 0; i < snapCount; i++)
            {
                if (snapPoints[i] == null) continue;
                if (snapPoints[i].TryGetComponent(out BasisActivationTarget target))
                {
                    _activationTargets[i] = target;
                    target.ApplyActiveState(i == CurrentIndex, fireEvents: false);
                }
            }
        }

        /// <summary>
        /// Project the active interacting input onto the path representation and return the
        /// nearest snap-point index. The wrapper exposes both the bone position
        /// (<c>BoneControl.OutgoingWorldData.position</c>) and the input source's
        /// <c>RaycastCoord</c>; subclasses choose which is appropriate for their geometry.
        /// Implementations should clamp the result to [0, snapPoints.Length - 1].
        /// </summary>
        protected abstract int FindNearestIndex(BasisInputWrapper interacting);

        /// <summary>
        /// Returns the world-space pose the interactable should adopt when settled at the given
        /// snap index. Default behavior is the snap point's own world pose; override to add a
        /// resting offset (e.g. "in front of" the marker).
        /// </summary>
        protected virtual void EvaluateAtIndex(int index, out Vector3 position, out Quaternion rotation)
        {
            Transform t = snapPoints[index];
            position = t.position;
            rotation = t.rotation;
        }

        /// <summary>
        /// Accept either the grip button or a near-fully-pressed trigger as the activation
        /// gate. Lets the same interactable be grabbed at close range with the grip and
        /// pulled at distance with the laser-pointer trigger.
        /// </summary>
        public override bool IsInteractTriggered(BasisInput input)
        {
            BasisInputState state = input.CurrentInputState;
            if (state.GripButton) return true;
            if (state.Trigger >= 0.9f) return true;
            return false;
        }

        public override bool CanHover(BasisInput input)
        {
            return InteractableEnabled
                && !Inputs.AnyInteracting()
                && !input.BasisUIRaycast.HadRaycastUITarget
                && Inputs.IsInputAdded(input)
                && input.TryGetRole(out BasisBoneTrackedRole role)
                && Inputs.TryGetByRole(role, out BasisInputWrapper found)
                && found.GetState() == BasisInteractInputState.Ignored
                && IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
        }

        public override bool CanInteract(BasisInput input)
        {
            return InteractableEnabled
                && !Inputs.AnyInteracting()
                && !input.BasisUIRaycast.HadRaycastUITarget
                && Inputs.IsInputAdded(input)
                && input.TryGetRole(out BasisBoneTrackedRole role)
                && Inputs.TryGetByRole(role, out BasisInputWrapper found)
                && found.GetState() == BasisInteractInputState.Hovering
                && IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
        }

        public override bool IsHoveredBy(BasisInput input)
        {
            BasisInputWrapper? found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
        }

        public override bool IsInteractingWith(BasisInput input)
        {
            BasisInputWrapper? found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
        }

        public override void OnHoverStart(BasisInput input)
        {
            if (!input.TryGetRole(out BasisBoneTrackedRole role)) return;
            Inputs.ChangeStateByRole(role, BasisInteractInputState.Hovering);
            OnHoverStartEvent?.Invoke(input);
        }

        public override void OnHoverEnd(BasisInput input, bool willInteract)
        {
            if (!input.TryGetRole(out BasisBoneTrackedRole role)) return;
            if (!willInteract)
            {
                Inputs.ChangeStateByRole(role, BasisInteractInputState.Ignored);
            }
            OnHoverEndEvent?.Invoke(input, willInteract);
        }

        public override void OnInteractStart(BasisInput input)
        {
            if (!InteractionTimerValidation()) return;
            if (!input.TryGetRole(out BasisBoneTrackedRole role)) return;
            if (!Inputs.TryGetByRole(role, out BasisInputWrapper wrapper)) return;
            if (wrapper.GetState() != BasisInteractInputState.Hovering) return;

            Inputs.ChangeStateByRole(role, BasisInteractInputState.Interacting);
            RequiresUpdateLoop = true;
            OnInteractStartEvent?.Invoke(input);
        }

        public override void OnInteractEnd(BasisInput input)
        {
            if (!input.TryGetRole(out BasisBoneTrackedRole role)) return;
            if (!Inputs.TryGetByRole(role, out BasisInputWrapper wrapper)) return;
            if (wrapper.GetState() != BasisInteractInputState.Interacting) return;

            Inputs.ChangeStateByRole(role, BasisInteractInputState.Ignored);
            RequiresUpdateLoop = false;
            OnInteractEndEvent?.Invoke(input);
        }

        public override void InputUpdate()
        {
            if (snapPoints == null || snapPoints.Length == 0) return;
            if (!TryGetActiveInteracting(out BasisInputWrapper interacting)) return;

            int target = Mathf.Clamp(FindNearestIndex(interacting), 0, snapPoints.Length - 1);

            if (target != CurrentIndex)
            {
                int previous = CurrentIndex;
                CurrentIndex = target;
                if (_activationTargets != null)
                {
                    _activationTargets[previous]?.Deactivate();
                    _activationTargets[target]?.Activate();
                }
                StartTweenToCurrent();
                OnSnapIndexChanged?.Invoke(previous, target);
            }
        }

        private bool TryGetActiveInteracting(out BasisInputWrapper wrapper)
        {
            if (Inputs.desktopCenterEye.GetState() == BasisInteractInputState.Interacting)
            {
                wrapper = Inputs.desktopCenterEye;
                return true;
            }
            if (Inputs.rightHand.GetState() == BasisInteractInputState.Interacting)
            {
                wrapper = Inputs.rightHand;
                return true;
            }
            if (Inputs.leftHand.GetState() == BasisInteractInputState.Interacting)
            {
                wrapper = Inputs.leftHand;
                return true;
            }
            wrapper = default;
            return false;
        }

        private void StartTweenToCurrent()
        {
            EvaluateAtIndex(CurrentIndex, out Vector3 targetPos, out Quaternion targetRot);
            if (tweenDuration <= 0f)
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
                return;
            }
            if (_tweenCoroutine != null) StopCoroutine(_tweenCoroutine);
            _tweenCoroutine = StartCoroutine(TweenTo(targetPos, targetRot, tweenDuration));
        }

        private IEnumerator TweenTo(Vector3 targetPos, Quaternion targetRot, float duration)
        {
            transform.GetPositionAndRotation(out Vector3 startPos, out Quaternion startRot);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - (1f - t) * (1f - t); // ease-out quad
                transform.SetPositionAndRotation(
                    Vector3.LerpUnclamped(startPos, targetPos, eased),
                    Quaternion.SlerpUnclamped(startRot, targetRot, eased));
                yield return null;
            }
            transform.SetPositionAndRotation(targetPos, targetRot);
            _tweenCoroutine = null;
        }
    }
}
