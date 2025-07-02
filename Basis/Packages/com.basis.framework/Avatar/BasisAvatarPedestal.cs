using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Threading;
using UnityEngine;
public class BasisAvatarPedestal : BasisInteractableObject
{
    public BasisLoadMode LoadMode;
    public BasisAvatar Avatar;
    [HideInInspector]
    public string UniqueID;
    public bool ShowAvatarOnPedestal = true;
    [HideInInspector]
    public bool WasJustPressed = false;
    public float InteractRange = 1f;
    public BasisLoadableBundle LoadableBundle;
    public BasisProgressReport BasisProgressReport;
    public CancellationToken cancellationToken;
    public RuntimeAnimatorController PedestalAnimatorController;
    public void Start()
    {
        BasisProgressReport = new BasisProgressReport();
        Initalize();
    }
    public async void Initalize()
    {
        switch (LoadMode)
        {
            case BasisLoadMode.ByGameobjectReference:
                Avatar.gameObject.SetActive(ShowAvatarOnPedestal);
                Avatar.Animator.runtimeAnimatorController = PedestalAnimatorController;
                break;
            default:
                {
                    if (ShowAvatarOnPedestal)
                    {
                        transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                        GameObject CreatedCopy = await BasisLoadHandler.LoadGameObjectBundle(LoadableBundle, true, BasisProgressReport, cancellationToken, Position, Rotation, Vector3.one, false, BundledContentHolder.Selector.Prop, transform);
                        if (CreatedCopy.TryGetComponent(out Avatar))
                        {
                            Avatar.Animator.runtimeAnimatorController = PedestalAnimatorController;
                        }
                    }
                    break;
                }
        }
        CreateCollider( 1.5f);
    }
    public void CreateCollider(float Height = 1.6f)//bounds.center
    {
        // Add or get a CapsuleCollider
        if (TryGetComponent<CapsuleCollider>(out CapsuleCollider capsule))
        {
        }
        else
        {
            capsule = gameObject.AddComponent<CapsuleCollider>();
        }

        capsule.center = new Vector3(0,1f,0);
        capsule.height = Height;
        capsule.radius = 0.25f;
        capsule.direction = 1; // Y axis

        BasisDebug.Log($"CapsuleCollider added: Height={Height}, Center={capsule.center}");
        UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
    }
    public void WasPressed()
    {
        if (Avatar != null && WasJustPressed == false && UniqueID != BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation)
        {
            WasJustPressed = true;



            BasisUIAcceptDenyPanel.OpenAcceptDenyPanel("Do You Want To Swap Into This Avatar?", (bool accepted) =>
            {
                if (accepted)
                {
                    switch (LoadMode)
                    {
                        case BasisLoadMode.ByGameobjectReference:
                            RuntimeAnimatorController copy = Avatar.Animator.runtimeAnimatorController;
                            Avatar.Animator.runtimeAnimatorController = null;
                            LoadableBundle = new BasisLoadableBundle
                            {
                                LoadableGameobject = new BasisLoadableGameobject() { InSceneItem = GameObject.Instantiate(Avatar.gameObject) }
                            };
                            LoadableBundle.LoadableGameobject.InSceneItem.transform.parent = null;
                            LoadableBundle.BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
                            {
                                RemoteBeeFileLocation = UniqueID
                            };
                            Avatar.Animator.runtimeAnimatorController = copy;
                            break;
                    }
                    LocalAvatarLoad();
                }
                else
                {
                    WasJustPressed = false;
                }
            });
        }
    }
    public async void LocalAvatarLoad()
    {
        await BasisLocalPlayer.Instance.CreateAvatarFromMode(LoadMode, LoadableBundle);
        WasJustPressed = false;
    }
    public override bool CanHover(BasisInput input)
    {
        return InteractableEnabled &&
            !IsPuppeted &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == BasisInteractInputState.Ignored &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }
    public override bool CanInteract(BasisInput input)
    {
        return InteractableEnabled &&
            !IsPuppeted &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == BasisInteractInputState.Hovering &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
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
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            // same input that was highlighting previously
            if (wrapper.GetState() == BasisInteractInputState.Hovering)
            {
                WasPressed();
                OnInteractStartEvent?.Invoke(input);
            }
            else
            {
                Debug.LogWarning("Input source interacted with ReparentInteractable without highlighting first.");
            }
        }
        else
        {
            BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " did not find role for input on Interact start");
        }
    }
    public override void OnInteractEnd(BasisInput input)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            if (wrapper.GetState() == BasisInteractInputState.Interacting)
            {
                Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Ignored);

                WasPressed();
                OnInteractEndEvent?.Invoke(input);
            }
        }
    }
    public void HighlightObject(bool IsHighlighted)
    {

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
    public override void InputUpdate()
    {
    }
    public override bool IsInteractTriggered(BasisInput input)
    {
        // click or mostly triggered
        return input.CurrentInputState.Trigger >= 0.9;
    }
}
