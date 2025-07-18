using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using BattlePhaze.SettingsManager.Intergrations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
namespace Basis.Scripts.BasisSdk.Interactions
{

    public class BasisPickupInteractable : BasisInteractableObject
    {
        [Header("Pickup Settings")]
        public bool KinematicWhileInteracting = true;
        [Tooltip("Enables the ability to self-steal")]
        public bool CanSelfSteal = true;
        public float DesktopRotateSpeed = 0.1f;

        [Tooltip("Unity units per scroll step")]
        public float DesktopZoopSpeed = 0.2f;
        public float DesktopZoopMinDistance = 0.2f;
        public float DesktopZoopMaxDistance = 2.0f;

        [Tooltip("Generate a mesh on start to approximate the referenced collider")]
        public bool GenerateColliderMesh = true;
        [Space(10)]
        public float minLinearVelocity = 0.5f;
        public float interactEndLinearVelocityMultiplier = 1.0f;
        [Space(5)]
        public float minAngularVelocity = 0.5f;
        public float interactEndAngularVelocityMultiplier = 1.0f;

        [Header("References")]
        public Collider ColliderRef;
        public Rigidbody RigidRef;

        [SerializeReference]
        internal BasisParentConstraint InputConstraint;

        // internal values
        internal GameObject HighlightClone;
        internal AsyncOperationHandle<Material> asyncOperationHighlightMat;
        internal Material ColliderHighlightMat;
        public bool _previousKinematicValue = true;
        internal bool _previousGravityValue = true;

        // constants
        public const string k_LoadMaterialAddress = "Interactable/InteractHighlightMat.mat";
        public const string k_CloneName = "HighlightClone";
        public const float k_DesktopZoopSmoothing = 0.2f;
        public const float k_DesktopZoopMaxVelocity = 10f;

        private readonly BasisLocks.LockContext HeadLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        private static string headPauseRequestName;
        public float InteractRange = 1f;

        private bool pauseHead = false;
        private Vector3 targetOffset = Vector3.zero;
        private Vector3 currentZoopVelocity = Vector3.zero;

        public Action<BasisPickUpUseMode> OnPickupUse;

        // TODO: execution order may be desired here.
        public List<Func<BasisInput, bool>> CanHoverInjected = new();
        public List<Func<BasisInput, bool>> CanInteractInjected = new();



        private Vector3 linearVelocity;
        private Vector3 angularVelocity;

        private Vector3 _previousPosition;
        private Quaternion _previousRotation;
        public void Start()
        {
            if (RigidRef == null)
            {
                TryGetComponent(out RigidRef);
            }
            if (ColliderRef == null)
            {
                TryGetComponent(out ColliderRef);
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
                //      If you wish to have the highlight update at runtime do that elsewhere or make a different InteractableObject Script
                HighlightClone = BasisColliderClone.CloneColliderMesh(ColliderRef, gameObject.transform, k_CloneName);

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
            BasisDebug.Log($"Pickup {string.Join(", ", Inputs.ToArray().Select(x => x.GetState()))}");
        }
        public void HighlightObject(bool highlight)
        {
            if (ColliderRef && HighlightClone)
            {
                HighlightClone.SetActive(highlight);
            }
        }
        public override bool CanHover(BasisInput input)
        {
            // bool netPickup = (!IsPuppeted || ); 
            // BasisDebug.Log($"CanHover {string.Join(", ", Inputs.ToArray().Select(x => x.GetState()))}");
            // BasisDebug.Log($"CanHover {!DisableInteract}, {!Inputs.AnyInteracting()}, {input.TryGetRole(out BasisBoneTrackedRole r)}, {Inputs.TryGetByRole(r, out BasisInputWrapper f)}, {r}, {f.GetState()}");

            // NOTE: see CanInteract note
            return InteractableEnabled &&
                (!Inputs.AnyInteracting() || CanSelfSteal) &&               // self-steal
                !input.BasisUIRaycast.HadRaycastUITarget &&                 // didn't hit UI target this frame
                Inputs.IsInputAdded(input) &&                               // input exists
                input.TryGetRole(out BasisBoneTrackedRole role) &&          // has role
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&   // input exists within PlayerInteract system 
                found.GetState() == BasisInteractInputState.Ignored &&           // in the correct state for hover
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange) && // within range
                CanHoverInjected.AllTrue(input);                            // injected
        }
        public override bool CanInteract(BasisInput input)
        {
            // BasisDebug.Log($"CanInteract {!DisableInteract}, {!Inputs.AnyInteracting()}, {input.TryGetRole(out BasisBoneTrackedRole r)}, {Inputs.TryGetByRole(r, out BasisInputWrapper f)}, {r}, {f.GetState()}");
            // currently hovering can interact only, only one interacting at a time
            
            // NOTE: Injected checks must be called at the end so that we can safely assume that at the time this was invoked, everything was valid.
            //      This is especially important for network sync, since the pending steal request doesnt need to re-invoke this (with stale data) when ownership transfers.
            return InteractableEnabled &&
                (!Inputs.AnyInteracting() || CanSelfSteal) &&               // self-steal
                !input.BasisUIRaycast.HadRaycastUITarget &&                 // didn't hit UI target this frame
                Inputs.IsInputAdded(input) &&                               // input exists
                input.TryGetRole(out BasisBoneTrackedRole role) &&          // has role
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&   // input exists within PlayerInteract system 
                found.GetState() == BasisInteractInputState.Hovering &&          // in the correct state for hover
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange) && // within range
                CanInteractInjected.AllTrue(input);                         // injected
        }

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
        public override void OnInteractStart(BasisInput input)
        {
            input.PlaySoundEffect("hover", SMModuleAudio.ActiveMenusVolume / 80);
            // TODO: request net ownership

            // clean up interacting ourselves (system wont do this for us)
            if (CanSelfSteal)
                Inputs.ForEachWithState(OnInteractEnd, BasisInteractInputState.Interacting);

            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                BasisDebug.Log("InteractStart: " + wrapper.GetState(), BasisDebug.LogTag.Pickups);
                // same input that was highlighting previously
                if (wrapper.GetState() == BasisInteractInputState.Hovering)
                {
                    Vector3 inPos = wrapper.BoneControl.OutgoingWorldData.position;
                    Quaternion inRot = wrapper.BoneControl.OutgoingWorldData.rotation;

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

                    // Set ownership to the local player
                    // syncNetworking.IsOwner = true;
                    Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Interacting);
                    RequiresUpdateLoop = true;

                    transform.GetPositionAndRotation(out Vector3 restPos, out Quaternion restRot);
                    InputConstraint.SetRestPositionAndRotation(restPos, restRot);

                    transform.GetPositionAndRotation(out Vector3 ActivePosition, out Quaternion ActiveRotation);

                    var offsetPos = Quaternion.Inverse(inRot) * (ActivePosition - inPos);
                    var offsetRot = Quaternion.Inverse(inRot) * ActiveRotation;

                    InputConstraint.SetOffsetPositionAndRotation(0, offsetPos, offsetRot);

                    // Debug.Log($"[OnInteractStart] Frame: {Time.frameCount}, Input Source Rot: {inRot.eulerAngles}, Object Rot: {transform.rotation.eulerAngles}, Calculated Offset: {offsetRot.eulerAngles}");
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
            // cleaup hovers if we arent supposed to be able to self-steal
            if (!CanSelfSteal)
                Inputs.ForEachWithState(i => OnHoverEnd(i, false), BasisInteractInputState.Hovering);
        }
        public override void OnInteractEnd(BasisInput input)
        {
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
        /// set linear/angular velocity to multiplier or 0 if below min velocity
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

        private float NormalizeAngle360(float angle)
        {
            angle %= 360f;
            if (angle < 0)
                angle += 360f;
            return angle;
        }

        public override void InputUpdate()
        {
            if (!GetActiveInteracting(out BasisInputWrapper interactingInput)) return;

            Vector3 inPos = interactingInput.BoneControl.OutgoingWorldData.position;
            Quaternion inRot = interactingInput.BoneControl.OutgoingWorldData.rotation;


            if (BasisDeviceManagement.IsUserInDesktop())
            {
                PollDesktopControl(Inputs.desktopCenterEye.Source);
            }

            // TODO: for index primary button is the A button, trigger should be the right one?
            // this needs to be verified as expected behavior with more controllers...
            bool State = interactingInput.Source.CurrentInputState.Trigger == 1;
            bool LastState = interactingInput.Source.LastInputState.Trigger == 1;
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
                // TODO: fix jitter while still using rigidbody movement
                //  Update 6/10/25: this seems to be a unity bug! - mriise
                //  Update 7/14/25: the bug is in-editor only, builds do not have the bug - mriise

                //pretty sure rigidbody is the real issue with the jitter here.
                //as rigidbody occurs on physics timestamp? -LD
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
        public override bool IsInteractingWith(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
        }
        public override bool IsHoveredBy(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
        }
        // this is cached, use it
        public override Collider GetCollider()
        {
            return ColliderRef;
        }

        private void PollDesktopControl(BasisInput DesktopEye)
        {
            // scroll zoop
            float mouseScroll = DesktopEye.CurrentInputState.Secondary2DAxis.y; // only ever 1, 0, -1

            Vector3 currentOffset = InputConstraint.sources[0].positionOffset;
            if (targetOffset == Vector3.zero)
            {
                // BasisDebug.Log("Setting initial target to current offset:" + targetOffset + " : " + currentOffset);
                targetOffset = currentOffset;
            }

            if (mouseScroll != 0)
            {
                Transform sourceTransform = BasisLocalCameraDriver.Instance.Camera.transform;

                Vector3 movement = DesktopZoopSpeed * mouseScroll * BasisLocalCameraDriver.Forward();
                Vector3 newTargetOffset = targetOffset + sourceTransform.InverseTransformVector(movement);

                // moving towards camera, ignore moving closer if less than min/max distance
                // NOTE: this is cheating a bit since its assuming desktop camera is the constraint source, but its a lot faster than doing a bunch of world/local space transforms.
                //      This also does not set offset to min distance to avoid calculating min offset position, meaning this is effectively (distance > minDistance + ZoopSpeed).
                float maxDistance = DesktopZoopMaxDistance + BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight / 2;

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

                // BasisDebug.Log("Destop manipulate Pickup zoop: " + dampendOffset + " rotate: " + delta);
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
        public override void OnDestroy()
        {
            Destroy(HighlightClone);
            if (asyncOperationHighlightMat.IsValid())
            {
                asyncOperationHighlightMat.Release();
            }
            base.OnDestroy();
        }

        // override since we add extra reach on desktop
        public override bool IsWithinRange(Vector3 source, float _interactRange)
        {
            float extraReach = 0;
            if (BasisDeviceManagement.IsUserInDesktop())
            {

                extraReach = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight / 2;
            }
            Collider collider = GetCollider();
            if (collider != null)
            {
                return Vector3.Distance(collider.ClosestPoint(source), source) <= _interactRange + extraReach;
            }
            // Fall back to object transform distance
            return Vector3.Distance(transform.position, source) <= _interactRange + extraReach;
        }

#if UNITY_EDITOR
        public void OnValidate()
        {
            string errPrefix = "Pickup Interactable needs component defined on self or given a reference for ";
            if (ColliderRef == null && !TryGetComponent(out Collider _))
            {
                Debug.LogWarning(errPrefix + "Collider", gameObject);
            }
            if (InputConstraint == null)
            {
                InputConstraint = new BasisParentConstraint();
            }
        }
#endif
        public void Drop() => ClearAllInfluencing();
    }

    // NOTE: ew, i dont like - mriise
    internal static class PickupListExt
    {
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
