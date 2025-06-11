using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AddressableAssets;

public abstract class BasisHandHeldCameraInteractable : InteractableObject
{
    [Tooltip("Generate a mesh on start to approximate the referenced collider")]
    public bool GenerateColliderMesh = true;

    [Header("References")]
    public Collider ColliderRef;

    [SerializeReference]
    private BasisParentConstraint InputConstraint;

    // internal values
    private GameObject HighlightClone;
    private AsyncOperationHandle<Material> asyncOperationHighlightMat;
    private Material ColliderHighlightMat;

    // constants
    const string k_LoadMaterialAddress = "Interactable/InteractHighlightMat.mat";
    const string k_CloneName = "HighlightClone";

    private readonly BasisLocks.LockContext HeadLock = BasisLocks.GetContext(BasisLocks.LookRotation);

    private static string headPauseRequestName;
    public float InteractRange = 1f;
    public void Start()
    {
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
        return !pickupable &&
            !IsPuppeted &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == InteractInputState.Ignored &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }
    public override bool CanInteract(BasisInput input)
    {
        return !pickupable &&
            !IsPuppeted &&
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
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            if (wrapper.GetState() == InteractInputState.Hovering)
            {
                Vector3 inPos = wrapper.BoneControl.OutgoingWorldData.position;
                Quaternion inRot = wrapper.BoneControl.OutgoingWorldData.rotation;

                Inputs.ChangeStateByRole(wrapper.Role, InteractInputState.Interacting);
                RequiresUpdateLoop = true;

                transform.GetPositionAndRotation(out Vector3 restPos, out Quaternion restRot);
                InputConstraint.SetRestPositionAndRotation(restPos, restRot);
                var offsetPos = Quaternion.Inverse(inRot) * (transform.position - inPos);
                var offsetRot = Quaternion.Inverse(inRot) * transform.rotation;
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
            BasisDebug.LogWarning(nameof(PickupInteractable) + " did not find role for input on Interact start");
        }
    }

    public override void OnInteractEnd(BasisInput input)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            if (wrapper.GetState() == InteractInputState.Interacting)
            {
                Inputs.ChangeStateByRole(wrapper.Role, InteractInputState.Ignored);
                RequiresUpdateLoop = false;
                // cleanup Desktop Manipulation since InputUpdate isnt run again till next pickup
                if (pauseHead)
                {
                    HeadLock.Remove(headPauseRequestName);
                    pauseHead = false;
                }

                InputConstraint.Enabled = false;
                OnInteractEndEvent?.Invoke(input);
            }
        }
    }

    public override void InputUpdate()
    {
        var interactingInput = GetActiveInteracting();
        if (interactingInput == null) return;

        var inputWrapper = interactingInput.Value;

        if (inputWrapper.BoneControl == null)
        {
            Debug.LogWarning("BoneControl is null in interactingInput. Skipping InputUpdate.");
            return;
        }

        Vector3 inPos;
        Quaternion inRot;

        if (Basis.Scripts.Device_Management.BasisDeviceManagement.IsUserInDesktop())
        {
            if (BasisLocalCameraDriver.Instance != null && BasisLocalCameraDriver.Instance.Camera != null)
            {
                BasisLocalCameraDriver.Instance.Camera.transform.GetPositionAndRotation(out inPos, out inRot);
                PollDesktopManipulation(Inputs.desktopCenterEye.Source);
            }
            else
            {
                Debug.LogWarning("BasisLocalCameraDriver or its Camera is null.");
                return;
            }
        }
        else
        {
            inPos = inputWrapper.BoneControl.OutgoingWorldData.position;
            inRot = inputWrapper.BoneControl.OutgoingWorldData.rotation;
        }

        if (InputConstraint == null)
        {
            Debug.LogWarning("InputConstraint is null in InputUpdate.");
            return;
        }

        InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);
        // NOTE: Removed transform.SetPositionAndRotation here to avoid UI lag
    }

    private void LateUpdate()
    {
        if (!RequiresUpdateLoop)
            return;

        var interactingInput = GetActiveInteracting();
        if (interactingInput == null || interactingInput.Value.BoneControl == null)
            return;

        Vector3 inPos;
        Quaternion inRot;

        if (Basis.Scripts.Device_Management.BasisDeviceManagement.IsUserInDesktop())
        {
            if (BasisLocalCameraDriver.Instance != null && BasisLocalCameraDriver.Instance.Camera != null)
            {
                BasisLocalCameraDriver.Instance.Camera.transform.GetPositionAndRotation(out inPos, out inRot);
                PollDesktopManipulation(Inputs.desktopCenterEye.Source);
            }
            else return;
        }
        else
        {
            inPos = interactingInput.Value.BoneControl.OutgoingWorldData.position;
            inRot = interactingInput.Value.BoneControl.OutgoingWorldData.rotation;
        }

        if (InputConstraint == null)
            return;

        InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);

        if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
        {
            transform.SetPositionAndRotation(pos, rot);
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

    private bool pauseHead = false;
    private void PollDesktopManipulation(BasisInput DesktopEye)
    {
        if (pauseHead)
        {
            pauseHead = false;
            if (!HeadLock.Remove(headPauseRequestName))
            {
                BasisDebug.LogWarning(nameof(PickupInteractable) + " was unable to un-pause head movement, this is a bug!");
            }
        }
    }
    private BasisInputWrapper? GetActiveInteracting()
    {

        if (Inputs.desktopCenterEye.GetState() == InteractInputState.Interacting)
            return Inputs.desktopCenterEye;
        else if (Inputs.leftHand.GetState() == InteractInputState.Interacting)
            return Inputs.leftHand;
        else if (Inputs.rightHand.GetState() == InteractInputState.Interacting)
            return Inputs.rightHand;
        else
            return null;
    }

    public override void StartRemoteControl()
    {
    }
    public override void StopRemoteControl()
    {
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
}
