using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Sync;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

public class BasisPickupSyncNetworking : BasisSyncedTransform, IBasisStaticLockable
{
    public BasisPickupInteractable BasisPickupInteractable;
    public bool CanNetworkSteal = true;
    /// <summary>
    /// Server-authoritative "static" flag. When true the prop can't be hovered or grabbed by anyone
    /// and is frozen (kinematic) in place. Set via the library Static toggle; applied through SetStatic.
    /// </summary>
    public bool IsStatic = false;
    public BasisInput pendingStealRequest = null;

    /// <summary>
    /// Opt-in: also stream the Rigidbody's linear/angular velocity, and let a free-flying remote copy simulate
    /// from it (prediction) with the synced pose pulling it back (correction), instead of being kinematic and
    /// pose-driven. Default off keeps the existing pose-only behaviour.
    /// </summary>
    public bool RemoteDeadReckon = false;

    /// <summary>
    /// While a player holds this prop, stop streaming its world transform and instead stream which
    /// hand holds it plus the hand-relative grab offset (the difference between the holding hand bone and the
    /// prop). Every remote re-parents the prop to its own copy of the owner's hand bone each frame, so the
    /// prop stays glued to the hand with no position-interpolation lag and near-zero traffic while held.
    /// On release it snaps back to normal transform sync. On by default; set false for plain world-position streaming while held.
    /// </summary>
    public bool AttachToHandOnGrab = true;

    /// <summary>
    /// While a player holds this prop, ignore distance-based send-rate reduction so the item being actively
    /// manipulated and watched stays full-rate (otherwise its send rate — and therefore the remote jitter
    /// buffer that rides on it — is throttled by distance to the nearest viewer, which is the main cause of
    /// laggy held props at range). Turn off to keep legacy distance throttling while held.
    /// </summary>
    public bool FullRateWhileHeld = true;

    private BasisSyncHandle _velX = BasisSyncHandle.Invalid;
    private BasisSyncHandle _velY = BasisSyncHandle.Invalid;
    private BasisSyncHandle _velZ = BasisSyncHandle.Invalid;
    private BasisSyncHandle _angX = BasisSyncHandle.Invalid;
    private BasisSyncHandle _angY = BasisSyncHandle.Invalid;
    private BasisSyncHandle _angZ = BasisSyncHandle.Invalid;

    // 0 = not attached (stream world transform), 1 = held in left hand, 2 = held in right hand.
    // Discrete, so any change forces a keyframe and the attach/detach edge is delivered reliably.
    private BasisSyncHandle _attachId = BasisSyncHandle.Invalid;
    private const byte HandNone = 0;
    private const byte HandLeft = 1;
    private const byte HandRight = 2;

    // Remote-side state for driving the prop off the owner's hand bone.
    private int _lastAppliedHandId;
    private Vector3 _attachWorldPos;
    private Quaternion _attachWorldRot = Quaternion.identity;
    private bool _haveAttachPose;
    private Animator _cachedHandAnimator;
    private int _cachedHandId;
    private Transform _cachedHand;

    private bool _authoredKinematic;
    private bool _authoredKinematicLatched;

    /// <summary>
    /// The resting <see cref="Rigidbody.isKinematic"/> this prop returns to when it isn't held, remotely
    /// driven, or <see cref="IsStatic"/>. Latched from the Rigidbody on load; set it to re-baseline a prop
    /// whose kinematic state is decided at runtime.
    /// </summary>
    public bool AuthoredKinematic
    {
        get
        {
            LatchAuthoredKinematic();
            return _authoredKinematic;
        }
        set
        {
            _authoredKinematic = value;
            _authoredKinematicLatched = true;
            ControlState();
        }
    }

    private void Reset()
    {
        Target = transform;
        SyncScale = true;
    }

    protected override void Awake()
    {
        Extrapolate = true;
        JitterBufferDepth = 1f;
        base.Awake();
        if (BasisPickupInteractable == null)
        {
            BasisPickupInteractable = this.transform.GetComponentInChildren<BasisPickupInteractable>();
        }
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.CanHoverInjected.Add(CanHover);
            BasisPickupInteractable.CanInteractInjected.Add(CanInteract);
            BasisPickupInteractable.OnInteractStartEvent.AddListener(OnInteractStartEvent);
            LatchAuthoredKinematic();
        }

        if (RemoteDeadReckon && BasisPickupInteractable != null && BasisPickupInteractable.RigidRef != null)
        {
            _velX = RegisterFloat(BasisQuantSpec.Half);
            _velY = RegisterFloat(BasisQuantSpec.Half);
            _velZ = RegisterFloat(BasisQuantSpec.Half);
            _angX = RegisterFloat(BasisQuantSpec.Half);
            _angY = RegisterFloat(BasisQuantSpec.Half);
            _angZ = RegisterFloat(BasisQuantSpec.Half);
        }

        if (AttachToHandOnGrab)
        {
            _attachId = RegisterByte();
        }
    }

    public void OnDisable()
    {
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.CanHoverInjected.Remove(CanHover);
            BasisPickupInteractable.CanInteractInjected.Remove(CanInteract);
            BasisPickupInteractable.OnInteractStartEvent.RemoveListener(OnInteractStartEvent);
        }
    }

    public override void OnNetworkReady()
    {
        base.OnNetworkReady();
        ControlState();
    }

    protected override void OnBeforeTransmit()
    {
        if (TryGetHeldHandPose(out byte handId, out Vector3 handPos, out Quaternion handRot) && Target != null)
        {
            // Held in attach mode: stream the hand-relative offset through the transform channels instead of
            // the world pose. Remotes reconstruct world = hand * offset against their copy of our hand bone,
            // so the prop tracks the hand with no interpolation lag and (offset being near-static) ~no traffic.
            Target.GetPositionAndRotation(out Vector3 wp, out Quaternion wr);
            Quaternion inv = Quaternion.Inverse(handRot);
            EncodePose(inv * (wp - handPos), inv * wr, Target.localScale);
            LocalSet(_attachId, handId);
            return;
        }

        base.OnBeforeTransmit();
        if (_attachId.IsValid) LocalSet(_attachId, HandNone);

        if (!_velX.IsValid || BasisPickupInteractable == null) return;
        Rigidbody rb = BasisPickupInteractable.RigidRef;
        if (rb == null) return;
        Vector3 v = rb.linearVelocity;
        Vector3 w = rb.angularVelocity;
        LocalSet(_velX, v.x); LocalSet(_velY, v.y); LocalSet(_velZ, v.z);
        LocalSet(_angX, w.x); LocalSet(_angY, w.y); LocalSet(_angZ, w.z);
    }

    protected override void ApplyInterpolated()
    {
        if (AttachToHandOnGrab && _attachId.IsValid)
        {
            int handId = GetByte(_attachId);

            if (handId != _lastAppliedHandId)
            {
                // The attach state flipped. The transform channels change meaning across this edge
                // (world pose <-> hand-relative offset), so collapse the interpolation buffer to avoid
                // sliding between the two — e.g. flying from the grab spot to the drop spot on release.
                SnapReceiver();
                bool released = handId == HandNone;
                _lastAppliedHandId = handId;
                if (released && _haveAttachPose && Target != null)
                {
                    // The snap lands on the next tick; hold the last in-hand pose for this one frame so the
                    // prop doesn't flash at the stale baseline in between.
                    Target.SetPositionAndRotation(_attachWorldPos, _attachWorldRot);
                    return;
                }
            }

            if (handId != HandNone)
            {
                if (Target != null && TryResolveOwnerHandTransform(handId, out Transform hand))
                {
                    ComposeSyncedPose(out Vector3 offPos, out Quaternion offRot, out Vector3 s);
                    hand.GetPositionAndRotation(out Vector3 hp, out Quaternion hr);
                    _attachWorldPos = hp + hr * offPos;
                    _attachWorldRot = hr * offRot;
                    _haveAttachPose = true;
                    Target.SetPositionAndRotation(_attachWorldPos, _attachWorldRot);
                    if (HasSyncedScale) Target.localScale = s;
                }
                // else: the owner's avatar/hand isn't resolvable (out of range, still loading) — leave the
                // prop at its last pose rather than snapping it to the offset interpreted as a world pose.
                return;
            }
        }

        if (!RemoteDeadReckon || !_velX.IsValid || BasisPickupInteractable == null)
        {
            base.ApplyInterpolated();
            return;
        }
        Rigidbody rb = BasisPickupInteractable.RigidRef;
        if (rb == null || rb.isKinematic)
        {
            base.ApplyInterpolated();
            return;
        }

        rb.linearVelocity = new Vector3(GetFloat(_velX), GetFloat(_velY), GetFloat(_velZ));
        rb.angularVelocity = new Vector3(GetFloat(_angX), GetFloat(_angY), GetFloat(_angZ));

        if (ComposeSyncedPose(out Vector3 lp, out Quaternion lr, out Vector3 ls))
        {
            Vector3 worldPos;
            Quaternion worldRot;
            if (WorldSpace)
            {
                worldPos = lp;
                worldRot = lr;
            }
            else
            {
                Transform parent = Target != null ? Target.parent : null;
                worldPos = parent != null ? parent.TransformPoint(lp) : lp;
                worldRot = parent != null ? parent.rotation * lr : lr;
            }

            const float correction = 0.15f;
            rb.position = Vector3.Lerp(rb.position, worldPos, correction);
            rb.rotation = Quaternion.Slerp(rb.rotation, worldRot, correction);
            if (Target != null && HasSyncedScale) Target.localScale = ls;
        }
    }

    /// <summary>Full-rate while the owner is holding it (see <see cref="FullRateWhileHeld"/>).</summary>
    protected override bool ShouldSuppressDistanceReduction() => FullRateWhileHeld && IsHeldByOwner();

    /// <summary>True if the local owner currently has this prop grabbed in either hand (or desktop).</summary>
    private bool IsHeldByOwner()
    {
        if (BasisPickupInteractable == null) return false;
        BasisInputSources inputs = BasisPickupInteractable.Inputs;
        return inputs.leftHand.GetState() == BasisInteractInputState.Interacting
            || inputs.rightHand.GetState() == BasisInteractInputState.Interacting
            || inputs.desktopCenterEye.GetState() == BasisInteractInputState.Interacting;
    }

    /// <summary>
    /// Owner side: if attach mode is on and we currently hold this prop, returns which hand bone holds it
    /// (left/right; desktop maps to the dominant hand) and that bone's live world pose.
    /// </summary>
    private bool TryGetHeldHandPose(out byte handId, out Vector3 handPos, out Quaternion handRot)
    {
        handId = HandNone;
        handPos = default;
        handRot = Quaternion.identity;
        if (!AttachToHandOnGrab || !IsOwnedLocallyOnClient || BasisPickupInteractable == null) return false;

        BasisInputSources inputs = BasisPickupInteractable.Inputs;
        BasisBoneTrackedRole role;
        if (inputs.leftHand.GetState() == BasisInteractInputState.Interacting)
            role = BasisBoneTrackedRole.LeftHand;
        else if (inputs.rightHand.GetState() == BasisInteractInputState.Interacting)
            role = BasisBoneTrackedRole.RightHand;
        else if (inputs.desktopCenterEye.GetState() == BasisInteractInputState.Interacting)
            role = BasisDominantHand.IsLeftHanded ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.RightHand;
        else
            return false;

        handId = role == BasisBoneTrackedRole.LeftHand ? HandLeft : HandRight;
        if (!TryResolveOwnerHandTransform(handId, out Transform hand)) return false;
        hand.GetPositionAndRotation(out handPos, out handRot);
        return true;
    }

    /// <summary>
    /// Resolves the owner's avatar hand bone (works on both ends — <see cref="currentOwnedPlayer"/> is the
    /// local player on the owner and the holding remote player on observers). Cached until the avatar swaps.
    /// </summary>
    private bool TryResolveOwnerHandTransform(int handId, out Transform hand)
    {
        hand = null;
        if (handId == HandNone) return false;

        BasisNetworkPlayer owner = currentOwnedPlayer;
        if (owner == null || !owner.TryGetPlayer(out IBasisPlayer player)) return false;
        var avatar = player.BasisAvatar;
        if (avatar == null) return false;
        Animator animator = avatar.Animator;
        if (animator == null) return false;

        if (animator == _cachedHandAnimator && handId == _cachedHandId && _cachedHand != null)
        {
            hand = _cachedHand;
            return true;
        }

        Transform resolved = animator.GetBoneTransform(handId == HandLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        if (resolved == null) return false;

        _cachedHandAnimator = animator;
        _cachedHandId = handId;
        _cachedHand = resolved;
        hand = resolved;
        return true;
    }

    private bool CanHover(BasisInput input)
    {
        if (IsStatic)
        {
            return false;
        }
        if (!BasisNetworkConnection.LocalPlayerIsConnected)
        {
            return true;
        }
        return IsOwnedLocallyOnClient || CanNetworkSteal;
    }

    private bool CanInteract(BasisInput input)
    {
        if (IsStatic)
        {
            return false;
        }
        if (IsOwnedLocallyOnClient)
        {
            return true;
        }
        return CanNetworkSteal && (pendingStealRequest == null || pendingStealRequest == input);
    }

    private void OnInteractStartEvent(BasisInput input)
    {
        if (!IsOwnedLocallyOnClient)
        {
            pendingStealRequest = input;
        }
        CanInteractAsync();
    }

    private async void CanInteractAsync()
    {
        var result = await TakeOwnershipAsync(5000);
        if (result.Success == false)
        {
            pendingStealRequest = null;
        }
    }

    public void SetIsKinematicOnPickup(bool state)
    {
        if (BasisPickupInteractable != null && BasisPickupInteractable.RigidRef != null)
        {
            BasisPickupInteractable.RigidRef.isKinematic = state;
        }
    }

    private void LatchAuthoredKinematic()
    {
        if (_authoredKinematicLatched)
        {
            return;
        }
        if (BasisPickupInteractable == null || BasisPickupInteractable.RigidRef == null)
        {
            return;
        }
        if (BasisPickupInteractable.RequiresUpdateLoop)
        {
            return;
        }
        _authoredKinematic = BasisPickupInteractable.RigidRef.isKinematic;
        _authoredKinematicLatched = true;
    }

    /// <summary>
    /// Apply or release the server-authoritative static / locked state (<see cref="IBasisStaticLockable"/>).
    /// </summary>
    public void SetStatic(bool isStatic)
    {
        if (IsStatic == isStatic)
        {
            return;
        }
        IsStatic = isStatic;
        ControlState();
    }

    public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
    {
        base.OnOwnershipTransfer(newOwner);
        ControlState();
    }

    public override void OnServerOwnershipDestroyed()
    {
        base.OnServerOwnershipDestroyed();
        ControlState();
    }

    public void ControlState()
    {
        if (Target == null) Target = transform;
        LatchAuthoredKinematic();

        if (IsStatic)
        {
            if (BasisPickupInteractable != null)
            {
                BasisPickupInteractable.Drop();
            }
            SetIsKinematicOnPickup(true);
            return;
        }

        if (IsOwnedLocallyOnClient)
        {
            if (pendingStealRequest != null)
            {
                SetIsKinematicOnPickup(_authoredKinematic);
                if (BasisPickupInteractable != null)
                {
                    if (BasisPickupInteractable.KinematicWhileInteracting)
                    {
                        BasisPickupInteractable._previousKinematicValue = _authoredKinematic;
                    }
                    if (!BasisPickupInteractable.IsInteractingWith(pendingStealRequest))
                    {
                        BasisPlayerInteract.Instance.ForceSetInteracting(BasisPickupInteractable, pendingStealRequest);
                    }
                }
                pendingStealRequest = null;
            }
            else if (BasisPickupInteractable != null
                && BasisPickupInteractable.KinematicWhileInteracting
                && BasisPickupInteractable.RequiresUpdateLoop)
            {
                // Held with KinematicWhileInteracting - preserve kinematic state
            }
            else
            {
                SetIsKinematicOnPickup(_authoredKinematic);
            }
        }
        else
        {
            if (BasisPickupInteractable != null)
            {
                BasisPickupInteractable.Drop();
            }
            SetIsKinematicOnPickup(_authoredKinematic || !RemoteDeadReckon);
        }
    }

    protected override void MigrateSerialized(int fromVersion)
    {
        base.MigrateSerialized(fromVersion);
        if (fromVersion < 1)
        {
            // Legacy pickups synced full position + rotation + scale.
            SyncPosition = true;
            PositionX = true; PositionY = true; PositionZ = true;
            SyncRotation = true;
            RotationX = true; RotationY = true; RotationZ = true;
            SyncScale = true;
            ScaleX = true; ScaleY = true; ScaleZ = true;
        }
    }
}
