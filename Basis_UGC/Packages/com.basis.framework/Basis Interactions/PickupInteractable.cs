using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;

public class PickupInteractable : InteractableObject
{
    [Header("Pickup Settings")]
    public bool KinematicWhileInteracting = true;
    [Tooltip("Enables the ability to self-steal")]
    public bool CanSelfSteal = true;

    public float DesktopRotateSpeed = 0.1f;

    [Tooltip("Unity units per scroll step")]
    public float DesktopZoopSpeed = 0.2f;

    public float DesktopZoopMinDistance = 0.2f;

    [Tooltip("Generate a mesh on start to approximate the referenced collider")]
    public bool GenerateColliderMesh = true;
    [Space(10)]
    public float minLinearVelocity = 0.1f;
    public float interactEndLinearVelocityMultiplier = 1;
    [Space(5)]
    public float minAngularVelocity = 0.1f;
    public float interactEndAngularVelocityMultiplier = 1;

    [Header("References")]
    public Collider ColliderRef;
    public Rigidbody RigidRef;

    [SerializeReference]
    private BasisParentConstraint InputConstraint;

    // internal values
    private GameObject HighlightClone;
    private AsyncOperationHandle<Material> asyncOperationHighlightMat;
    private Material ColliderHighlightMat;
    private bool _previousKinematicValue = true;

    // constants
    const string k_LoadMaterialAddress = "Interactable/InteractHighlightMat.mat";
    const string k_CloneName = "HighlightClone";
    const float k_DesktopZoopSmoothing = 0.2f;
    const float k_DesktopZoopMaxVelocity = 10f;

    private readonly BasisLocks.LockContext HeadLock = BasisLocks.GetContext(BasisLocks.LookRotation);

    private static string headPauseRequestName;
    public float InteractRange = 1f;

    private bool pauseHead = false;
    private Vector3 targetOffset = Vector3.zero;
    private Vector3 currentZoopVelocity = Vector3.zero;

    public Action<PickUpUseMode> OnPickupUse;

    public enum PickUpUseMode
    {
        OnPickUpUseUp,
        OnPickUpUseDown,
        OnPickUpStillDown
    }
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
        InputConstraint.sources = new BasisParentConstraint.SourceData[] { new() { weight = 1f } };
        InputConstraint.Enabled = false;

        headPauseRequestName = $"{nameof(PickupInteractable)}: {gameObject.GetInstanceID()}";

        AsyncOperationHandle<Material> op = Addressables.LoadAssetAsync<Material>(k_LoadMaterialAddress);
        ColliderHighlightMat = op.WaitForCompletion();
        asyncOperationHighlightMat = op;

        if (GenerateColliderMesh)
        {
            // NOTE: Collider mesh highlight position and size is only updated on Start().
            //      If you wish to have the highlight update at runtime do that elsewhere or make a different InteractableObject Script
            HighlightClone = ColliderClone.CloneColliderMesh(ColliderRef, gameObject.transform, k_CloneName);

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
        // BasisDebug.Log($"CanHover {string.Join(", ", Inputs.ToArray().Select(x => x.GetState()))}");
        // BasisDebug.Log($"CanHover {!DisableInteract}, {!Inputs.AnyInteracting()}, {input.TryGetRole(out BasisBoneTrackedRole r)}, {Inputs.TryGetByRole(r, out BasisInputWrapper f)}, {r}, {f.GetState()}");
        return !pickupable &&
            !IsPuppeted &&
            (!Inputs.AnyInteracting() || CanSelfSteal) &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == InteractInputState.Ignored &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }
    public override bool CanInteract(BasisInput input)
    {
        // BasisDebug.Log($"CanInteract {!DisableInteract}, {!Inputs.AnyInteracting()}, {input.TryGetRole(out BasisBoneTrackedRole r)}, {Inputs.TryGetByRole(r, out BasisInputWrapper f)}, {r}, {f.GetState()}");
        // currently hovering can interact only, only one interacting at a time
        return !pickupable &&
            !IsPuppeted &&
            (!Inputs.AnyInteracting() || CanSelfSteal) &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == InteractInputState.Hovering &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }
    public override void OnHoverStart(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        if (found != null && found.Value.GetState() != InteractInputState.Ignored)
            BasisDebug.LogWarning(nameof(PickupInteractable) + " input state is not ignored OnHoverStart, this shouldn't happen");
        var added = Inputs.ChangeStateByRole(found.Value.Role, InteractInputState.Hovering);
        if (!added)
            BasisDebug.LogWarning(nameof(PickupInteractable) + " did not find role for input on hover");

        OnHoverStartEvent?.Invoke(input);
        HighlightObject(true);
    }
    public override void OnHoverEnd(BasisInput input, bool willInteract)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out _))
        {
            if (!willInteract)
            {
                if (!Inputs.ChangeStateByRole(role, InteractInputState.Ignored))
                {
                    BasisDebug.LogWarning(nameof(PickupInteractable) + " found input by role but could not remove by it, this is a bug.");
                }
            }
            OnHoverEndEvent?.Invoke(input, willInteract);
            HighlightObject(false);
        }
    }
    public override void OnInteractStart(BasisInput input)
    {
        // clean up interacting ourselves (system wont do this for us)
        if (CanSelfSteal)
            Inputs.ForEachWithState(OnInteractEnd, InteractInputState.Interacting);

        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            // same input that was highlighting previously
            if (wrapper.GetState() == InteractInputState.Hovering)
            {
                Vector3 inPos = wrapper.BoneControl.OutgoingWorldData.position;
                Quaternion inRot = wrapper.BoneControl.OutgoingWorldData.rotation;

                if (RigidRef != null && KinematicWhileInteracting)
                {
                    _previousKinematicValue = RigidRef.isKinematic;
                    RigidRef.isKinematic = true;
                }

                // Set ownership to the local player
                // syncNetworking.IsOwner = true;
                Inputs.ChangeStateByRole(wrapper.Role, InteractInputState.Interacting);
                RequiresUpdateLoop = true;

                transform.GetPositionAndRotation(out Vector3 restPos, out Quaternion restRot);
                InputConstraint.SetRestPositionAndRotation(restPos, restRot);

                var offsetPos = Quaternion.Inverse(inRot) * (transform.position - inPos);
                var offsetRot = Quaternion.Inverse(inRot) * transform.rotation;
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
            BasisDebug.LogWarning(nameof(PickupInteractable) + " did not find role for input on Interact start");
        }
        // cleaup hovers if we arent supposed to be able to self-steal
        if (!CanSelfSteal)
            Inputs.ForEachWithState(i => OnHoverEnd(i, false), InteractInputState.Hovering);
    }
    public override void OnInteractEnd(BasisInput input)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            if (wrapper.GetState() == InteractInputState.Interacting)
            {
                Inputs.ChangeStateByRole(wrapper.Role, InteractInputState.Ignored);

                if (KinematicWhileInteracting && RigidRef != null)
                {
                    RigidRef.isKinematic = _previousKinematicValue;
                }

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
                InputConstraint.sources = new BasisParentConstraint.SourceData[] { new() { weight = 1f } };

                if (!RigidRef.isKinematic)
                {
                    OnDropVelocity();
                }


                OnInteractEndEvent?.Invoke(input);
            }
        }
    }
    /// <summary>
    /// set linear/angular velocity to multiplier or 0 if below min velocity
    /// </summary>
    private void OnDropVelocity()
    {
        var linear = RigidRef.linearVelocity;
        var angular = RigidRef.angularVelocity;
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

        RigidRef.linearVelocity = linear;
        RigidRef.angularVelocity = angular;
    }

    public override void InputUpdate()
    {
        if (GetActiveInteracting(out BasisInputWrapper interactingInput))
        {
            Vector3 inPos = interactingInput.BoneControl.OutgoingWorldData.position;
            Quaternion inRot = interactingInput.BoneControl.OutgoingWorldData.rotation;
            // Optionally, match the rotation.
            //  transform.rotation = target.rotation;
            if (Basis.Scripts.Device_Management.BasisDeviceManagement.IsUserInDesktop())
            {
                // override with current camera position in desktop mode
                // TODO: this is weird??!? fixes jitter but only on forward rendered shaders
                BasisLocalCameraDriver.GetPositionAndRotation(out inPos, out inRot);
                PollDesktopManipulation(Inputs.desktopCenterEye.Source);
            }
            bool State = interactingInput.Source.CurrentInputState.PrimaryButtonGetState;
            bool LastState = interactingInput.Source.LastInputState.PrimaryButtonGetState;
            if (State && LastState == false)
            {
                OnPickupUse.Invoke(PickUpUseMode.OnPickUpUseDown);
            }
            else
            {
                if (State == false && LastState)
                {
                    OnPickupUse.Invoke(PickUpUseMode.OnPickUpUseUp);
                }
                else
                {
                    if (State)
                    {
                        OnPickupUse.Invoke(PickUpUseMode.OnPickUpStillDown);
                    }
                }
            }
            // Debug.Log($"[InputUpdate] Frame: {Time.frameCount}, Source inRot: {inRot.eulerAngles}, Stored Offset: {InputConstraint.sources[0].rotationOffset.eulerAngles}");
            InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);
            if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
            {
                // Debug.Log($"[InputUpdate] Frame: {Time.frameCount}, Evaluated Target Rot: {rot.eulerAngles}");
                //pretty sure rigidbody is the real issue with the jitter here.
                //as rigidbody occurs on physics timestamp? -LD
                RigidRef.Move(pos, rot);
                // TODO: fix jitter while still using rigidbody movement
                // transform.SetPositionAndRotation(pos, rot);
            }
        }
    }
    public override bool IsInteractingWith(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == InteractInputState.Interacting;
    }
    public override bool IsHoveredBy(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == InteractInputState.Hovering;
    }
    // this is cached, use it
    public override Collider GetCollider()
    {
        return ColliderRef;
    }
    private void PollDesktopManipulation(BasisInput DesktopEye)
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

            // moving towards camera, ignore moving closer if less than min distance
            // NOTE: this is cheating a bit since its assuming desktop camera is the constraint source, but its a lot faster than doing a bunch of world/local space transforms.
            //      This also does not set offset to min distance to avoid calculating min offset position, meaning this is effectively (distance > minDistance + ZoopSpeed).
            if (mouseScroll < 0 && newTargetOffset.z > DesktopZoopMinDistance)
            {
                targetOffset = newTargetOffset;
            }
            // moving away from camera
            else if (mouseScroll > 0)
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
                BasisDebug.LogWarning(nameof(PickupInteractable) + " was unable to un-pause head movement, this is a bug!");
            }
        }
    }
    private bool GetActiveInteracting(out BasisInputWrapper BasisInputWrapper)
    {

        switch (Inputs.desktopCenterEye.GetState())
        {
            case InteractInputState.Interacting:
                BasisInputWrapper = Inputs.desktopCenterEye;
                return true;
            default:
                if (Inputs.leftHand.GetState() == InteractInputState.Interacting)
                {
                    BasisInputWrapper = Inputs.leftHand;
                    return true;
                }
                else if (Inputs.rightHand.GetState() == InteractInputState.Interacting)
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
    public override void StartRemoteControl()
    {
        IsPuppeted = true;
        // _previousKinematicValue = RigidRef.isKinematic;
        // RigidRef.isKinematic = true;
        // TODO: _previousKinematic state should be synced so late joiners have pickups behave properly
    }
    public override void StopRemoteControl()
    {
        IsPuppeted = false;
        // RigidRef.isKinematic = _previousKinematicValue;
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
#if UNITY_EDITOR
    public void OnValidate()
    {
        string errPrefix = "ReparentInteractable needs component defined on self or given a reference for ";
        if (RigidRef == null && !TryGetComponent(out Rigidbody _))
        {
            Debug.LogWarning(errPrefix + "Rigidbody", gameObject);
        }
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

}
