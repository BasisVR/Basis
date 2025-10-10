using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Threading;
using UnityEngine;

/// <summary>
/// Basis Avatar Pedestal for showing off and allowing loading of an avatar.
/// Provides interaction logic, avatar loading, collider creation, and UI handling.
/// </summary>
public class BasisAvatarPedestal : BasisInteractableObject
{
    /// <summary>
    /// Defines how the avatar is loaded (by reference, bundle, etc.).
    /// </summary>
    public BasisLoadMode LoadMode;

    /// <summary>
    /// Reference to the avatar displayed or instantiated on the pedestal.
    /// </summary>
    public BasisAvatar Avatar;

    /// <summary>
    /// Unique identifier for this pedestal instance.
    /// </summary>
    [HideInInspector]
    public string UniqueID;

    /// <summary>
    /// Determines if the avatar should be shown visibly on the pedestal.
    /// </summary>
    public bool ShowAvatarOnPedestal = true;

    /// <summary>
    /// Flag to prevent multiple press interactions being triggered in quick succession.
    /// </summary>
    [HideInInspector]
    public bool WasJustPressed = false;

    /// <summary>
    /// Maximum interaction range in world units for hovering or interacting.
    /// </summary>
    public float InteractRange = 1f;

    /// <summary>
    /// Bundle definition used for loading an avatar when not using direct references.
    /// </summary>
    public BasisLoadableBundle LoadableBundle;

    /// <summary>
    /// Progress report object for monitoring loading operations.
    /// </summary>
    public BasisProgressReport BasisProgressReport;

    /// <summary>
    /// Cancellation token for aborting async avatar loading operations.
    /// </summary>
    public CancellationToken cancellationToken;

    /// <summary>
    /// Animator controller applied to pedestal avatars.
    /// </summary>
    public RuntimeAnimatorController PedestalAnimatorController;

    /// <summary>
    /// Unity Start callback. Initializes the progress report and begins setup.
    /// </summary>
    public void Start()
    {
        BasisProgressReport = new BasisProgressReport();
        Initalize();
    }

    /// <summary>
    /// Initializes the pedestal by loading or showing the avatar, 
    /// and creating its collider for interaction.
    /// </summary>
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
                        GameObject CreatedCopy = await BasisLoadHandler.LoadGameObjectBundle(
                            LoadableBundle, true, BasisProgressReport, cancellationToken,
                            Position, Rotation, Vector3.one, false, BundledContentHolder.Selector.Prop, transform);

                        if (CreatedCopy.TryGetComponent(out Avatar))
                        {
                            Avatar.Animator.runtimeAnimatorController = PedestalAnimatorController;
                        }
                    }
                    break;
                }
        }
        CreateCollider(1.5f);
    }

    /// <summary>
    /// Creates or updates a capsule collider for interaction detection.
    /// </summary>
    /// <param name="Height">Height of the capsule collider.</param>
    public void CreateCollider(float Height = 1.6f)
    {
        if (!TryGetComponent<CapsuleCollider>(out CapsuleCollider capsule))
        {
            capsule = gameObject.AddComponent<CapsuleCollider>();
        }

        capsule.center = new Vector3(0, 1f, 0);
        capsule.height = Height;
        capsule.radius = 0.25f;
        capsule.direction = 1; // Y axis

        BasisDebug.Log($"CapsuleCollider added: Height={Height}, Center={capsule.center}");
        UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
    }

    /// <summary>
    /// Handles the press interaction to trigger avatar swapping logic.
    /// </summary>
    public void WasPressed()
    {
        if (Avatar != null && WasJustPressed == false &&
            UniqueID != BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation)
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
                                LoadableGameobject = new BasisLoadableGameobject()
                                { InSceneItem = GameObject.Instantiate(Avatar.gameObject) }
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

    /// <summary>
    /// Loads the local avatar asynchronously using the selected load mode and bundle.
    /// </summary>
    public async void LocalAvatarLoad()
    {
        await BasisLocalPlayer.Instance.CreateAvatarFromMode(LoadMode, LoadableBundle);
        WasJustPressed = false;
    }

    /// <inheritdoc/>
    public override bool CanHover(BasisInput input)
    {
        return InteractableEnabled &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == BasisInteractInputState.Ignored &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }

    /// <inheritdoc/>
    public override bool CanInteract(BasisInput input)
    {
        return InteractableEnabled &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == BasisInteractInputState.Hovering &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override void OnInteractStart(BasisInput input)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
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

    /// <inheritdoc/>
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

    /// <summary>
    /// Highlights or unhighlights the pedestal object.
    /// Override to implement visuals (currently empty).
    /// </summary>
    /// <param name="IsHighlighted">Whether the object should be highlighted.</param>
    public void HighlightObject(bool IsHighlighted)
    {
    }

    /// <inheritdoc/>
    public override bool IsInteractingWith(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
    }

    /// <inheritdoc/>
    public override bool IsHoveredBy(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
    }

    /// <inheritdoc/>
    public override void InputUpdate()
    {
    }

    /// <inheritdoc/>
    public override bool IsInteractTriggered(BasisInput input)
    {
        return input.CurrentInputState.Trigger >= 0.9;
    }
}
