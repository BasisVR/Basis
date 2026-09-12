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

    // Which hand holds the prop and which frame the streamed grip is expressed in. Discrete, so any change
    // forces a keyframe and the attach/detach edge is delivered reliably.
    //   None                  stream the world transform
    //   Left / Right          legacy: offset in the holder's raw wrist bone frame, in metres
    //   LeftPalm / RightPalm  offset in the canonical BasisHandGrip frame, as multiples of hand length
    //   LeftGrip / RightGrip  as Palm, and the prop carries an authored GripPoint the receiver can solve
    //                         from its own prefab — the streamed offset is only the fallback for a receiver
    //                         whose copy has no grip authored.
    private BasisSyncHandle _attachId = BasisSyncHandle.Invalid;
    private const byte HandNone = 0;
    private const byte HandLeft = 1;
    private const byte HandRight = 2;
    private const byte HandLeftPalm = 3;
    private const byte HandRightPalm = 4;
    private const byte HandLeftGrip = 5;
    private const byte HandRightGrip = 6;

    private static bool IsLeftHand(int handId) => handId == HandLeft || handId == HandLeftPalm || handId == HandLeftGrip;
    private static bool IsHandFrame(int handId) => handId >= HandLeftPalm && handId <= HandRightGrip;
    private static bool IsAuthoredGrip(int handId) => handId == HandLeftGrip || handId == HandRightGrip;

    // Remote-side state for driving the prop off the owner's hand bone.
    private int _lastAppliedHandId;
    private Vector3 _attachWorldPos;
    private Quaternion _attachWorldRot = Quaternion.identity;
    private Vector3 _attachOffsetPos;
    private Quaternion _attachOffsetRot = Quaternion.identity;
    private bool _haveAttachPose;
    private bool _reweldQueued;
    private Animator _cachedHandAnimator;
    private int _cachedHandId;
    private Transform _cachedHand;

    // Owner-side: the hand id we last wrote into _attachId, so a transient failure to resolve our own
    // hand bone (avatar swap mid-hold) freezes the in-hand stream instead of flipping to world encoding.
    private byte _lastSentHandId;

    // Consecutive transmits held frozen by an unresolvable hand. Bounded so an avatar that never
    // resolves a hand bone falls back to world streaming instead of stranding the prop for the hold.
    private int _handLostTransmits;
    private const int MaxHandLostTransmits = 10;

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
        if (BasisPickupInteractable == null)
        {
            BasisPickupInteractable = this.transform.GetComponentInChildren<BasisPickupInteractable>();
        }
        if (BasisPickupInteractable != null && Target != BasisPickupInteractable.transform)
        {
            if (Target != null)
            {
                BasisDebug.LogWarning($"{name}: sync Target was {Target.name} but the pickup drives " +
                    $"{BasisPickupInteractable.name}; retargeting so remotes receive the pose that actually moves.",
                    BasisDebug.LogTag.Pickups);
            }
            Target = BasisPickupInteractable.transform;
        }
        base.Awake();
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
            WarnIfAttachChannelsAreLossy();
        }
    }

    /// <summary>
    /// While held, the transform channels carry the prop's offset from the holder's hand rather than a world
    /// pose, so every axis has to be synced: a disabled one drops that component of the grip and the prop
    /// sits wrong on every other client, permanently, while looking perfect to the holder — who never decodes
    /// what it sent. Free-flying props can legitimately sync a subset, which is why this warns rather than
    /// forcing the axes back on.
    /// </summary>
    private void WarnIfAttachChannelsAreLossy()
    {
        bool position = SyncPosition && PositionX && PositionY && PositionZ;
        bool rotation = SyncRotation && (RotationMode != BasisRotationSyncMode.Euler || (RotationX && RotationY && RotationZ));
        if (position && rotation)
        {
            return;
        }
        BasisDebug.LogWarningOnce($"pickup-lossy-attach-channels-{name}",
            $"{name}: AttachToHandOnGrab streams the in-hand grip through the transform channels, but " +
            $"{(position ? "a rotation" : "a position")} axis is not synced. The dropped component of the grip " +
            $"will be missing on every remote while this prop is held, and correct on the holder's screen. " +
            $"Enable every position and rotation axis, or turn AttachToHandOnGrab off.",
            BasisDebug.LogTag.Pickups);
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
        if (TryGetHeldHandPose(out byte handId, out BasisHandFrame frame) && Target != null)
        {
            // Held in attach mode: stream the hand-relative offset through the transform channels instead of
            // the world pose. Remotes reconstruct world = hand * offset against their own copy of the hand
            // frame, so the prop tracks the hand with no interpolation lag and (offset being near-static)
            // ~no traffic. Lengths go as multiples of hand length so an observer drawing a different avatar
            // holds it proportionally rather than at our absolute reach.
            Target.GetPositionAndRotation(out Vector3 wp, out Quaternion wr);
            Quaternion inv = Quaternion.Inverse(frame.Rotation);
            float scale = frame.HandLength > 0f ? 1f / frame.HandLength : 1f;
            EncodePose(inv * (wp - frame.Position) * scale, inv * wr, Target.localScale);
            LocalSet(_attachId, handId);
            _lastSentHandId = handId;
            _handLostTransmits = 0;
            if (BasisPickupWeldDiagnostics.Enabled) ReportOwnerWeld(handId, frame, wp, wr);
            return;
        }

        if (_attachId.IsValid && _lastSentHandId != HandNone && IsHeldByOwner()
            && ++_handLostTransmits <= MaxHandLostTransmits)
        {
            // Still held but our own hand bone can't be resolved right now (avatar swapping/loading).
            // Freeze the in-hand stream rather than flipping to world encoding - remotes keep the prop
            // glued to their copy of our hand, mirroring the receive-side hold for an unresolvable owner.
            return;
        }
        _handLostTransmits = 0;

        base.OnBeforeTransmit();
        if (_attachId.IsValid) LocalSet(_attachId, HandNone);
        _lastSentHandId = HandNone;

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
                if (Target != null && TryResolveHandFrame(handId, out BasisHandFrame frame))
                {
                    ComposeSyncedPose(out Vector3 offPos, out Quaternion offRot, out Vector3 s);
                    if (!TryGetAuthoredGripOffset(handId, out _attachOffsetPos, out _attachOffsetRot))
                    {
                        _attachOffsetPos = offPos * frame.HandLength;
                        _attachOffsetRot = offRot;
                    }
                    _attachWorldPos = frame.Position + frame.Rotation * _attachOffsetPos;
                    _attachWorldRot = frame.Rotation * _attachOffsetRot;
                    _haveAttachPose = true;
                    Target.SetPositionAndRotation(_attachWorldPos, _attachWorldRot);
                    if (HasSyncedScale) Target.localScale = s;
                    if (!_reweldQueued)
                    {
                        _reweldQueued = true;
                        BasisSyncDriver.QueuePickupReweld(this);
                    }
                    if (BasisPickupWeldDiagnostics.Enabled) ReportObserverWeld(handId, frame, resolved: true);
                }
                // else: the owner's avatar/hand isn't resolvable (out of range, still loading, or a hand
                // frame this client cannot build the way the sender did) — leave the prop at its last pose
                // rather than snapping it to the offset interpreted as a world pose.
                else if (BasisPickupWeldDiagnostics.Enabled)
                {
                    ReportObserverWeld(handId, default, resolved: false);
                }
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
    /// Owner side: if attach mode is on and we currently hold this prop, returns which hand holds it
    /// (left/right; desktop maps to the dominant hand), which frame the grip is expressed in, and that
    /// frame's live pose. Falls back to the raw wrist bone frame when this avatar has no finger bones to
    /// build the canonical basis from, so the id always says what the receiver should decode against.
    /// </summary>
    private bool TryGetHeldHandPose(out byte handId, out BasisHandFrame frame)
    {
        handId = HandNone;
        frame = default;
        if (!AttachToHandOnGrab || !_attachId.IsValid || !IsOwnedLocallyOnClient || BasisPickupInteractable == null) return false;

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

        bool left = role == BasisBoneTrackedRole.LeftHand;
        // Probed directly rather than through TryResolveHandFrame: a degraded frame is a normal answer here
        // (it just picks the legacy id below), where on the receive side it means the two ends disagree.
        if (TryResolveOwner(out IBasisPlayer self) && BasisHandGrip.TryGetPlayerFrame(self, left, out frame) && frame.Canonical)
        {
            // Only when the local hold is genuinely on the grip. The receiver treats this id as "ignore the
            // streamed offset and re-solve the grip yourself", so claiming it for a hold that is not aligned
            // to the GripPoint hands every observer a pose the owner never had.
            bool authored = BasisPickupInteractable.GripPoint != null && BasisPickupInteractable.HoldIsGripAligned;
            handId = left
                ? (authored ? HandLeftGrip : HandLeftPalm)
                : (authored ? HandRightGrip : HandRightPalm);
            return true;
        }

        // No finger bones to build the basis from. Fall back to the raw wrist bone in metres, which is the
        // one frame a receiver can always reproduce, and say so in the id.
        byte legacyId = left ? HandLeft : HandRight;
        if (!TryResolveHandFrame(legacyId, out frame)) return false;
        handId = legacyId;
        return true;
    }

    /// <summary>
    /// The frame a grip is expressed in for the given attach id, resolved against the owning player's own
    /// bone mapping. Works on both ends — <see cref="currentOwnedPlayer"/> is the local player on the owner
    /// and the holding remote on observers — and neither end sends the other any rig data to do it.
    ///
    /// The id is a contract about which space the streamed offset is in, so this resolves that space or
    /// nothing: a canonical id will not silently settle for the degraded wrist frame, because the two are
    /// different spaces and decoding one as the other is invisible to the holder.
    /// </summary>
    private bool TryResolveHandFrame(int handId, out BasisHandFrame frame)
    {
        frame = default;
        if (handId == HandNone) return false;

        bool left = IsLeftHand(handId);
        if (!IsHandFrame(handId))
        {
            if (!TryResolveOwnerHandTransform(handId, out Transform wrist)) return false;
            wrist.GetPositionAndRotation(out Vector3 wristPos, out Quaternion wristRot);
            frame = new BasisHandFrame
            {
                Position = wristPos,
                Rotation = wristRot,
                HandLength = 1f,
                Canonical = false,
            };
            return true;
        }

        if (!TryResolveOwner(out IBasisPlayer player)) return false;
        if (!BasisHandGrip.TryGetPlayerFrame(player, left, out frame)) return false;

        if (!frame.Canonical)
        {
            // The id says the offset is expressed in the canonical hand basis, in hand-length units, but
            // this end could not build that basis from its copy of the holder (a fallback or still-loading
            // avatar, a model with no finger bones). The degraded frame TryGetFrame hands back is the raw
            // wrist bone plus a stand-in hand length, which is a DIFFERENT space — decoding against it puts
            // the object off the palm and turned by the avatar's wrist bind, permanently, and the holder
            // cannot see it because the owner never decodes what it sent. Refuse instead: the caller holds
            // the object at its last pose, the same as for an owner whose hand cannot be resolved at all.
            BasisDebug.LogWarningOnce($"pickup-noncanonical-hand-{name}",
                $"{name}: holder streamed a canonical hand grip but this client cannot build the canonical " +
                $"hand frame for them (no finger bones on the avatar it is drawing); holding the last pose.",
                BasisDebug.LogTag.Pickups);
            frame = default;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Owner-side readout, including the one check the holder can make alone: rebuild the pose from the
    /// values just written into the sync channels and compare it with the prop. That round trip goes
    /// through the component's enabled-axis toggles, so a prefab with a position or rotation axis left
    /// unsynced — which silently drops that component of the grip offset for every observer while looking
    /// perfect locally — shows up here as a non-zero error instead of as a mystery on someone else's screen.
    /// </summary>
    private void ReportOwnerWeld(byte handId, in BasisHandFrame frame, Vector3 propPos, Quaternion propRot)
    {
        float error = 0f;
        float angle = 0f;
        if (ComposeSyncedPose(out Vector3 offPos, out Quaternion offRot, out _))
        {
            Vector3 rebuiltPos = frame.Position + frame.Rotation * (offPos * frame.HandLength);
            Quaternion rebuiltRot = frame.Rotation * offRot;
            error = Vector3.Distance(rebuiltPos, propPos);
            angle = Quaternion.Angle(rebuiltRot, propRot);
        }

        BasisPickupWeldDiagnostics.Report(new BasisPickupWeldReport
        {
            Name = name,
            Owner = true,
            HandId = handId,
            Left = IsLeftHand(handId),
            FrameResolved = true,
            Canonical = frame.Canonical,
            HandLength = frame.HandLength,
            PalmPosition = frame.Position,
            PropPosition = propPos,
            OffsetHandLengths = frame.HandLength > 0f ? Vector3.Distance(propPos, frame.Position) / frame.HandLength : 0f,
            SelfCheckError = error,
            SelfCheckAngle = angle,
        });
    }

    /// <summary>Observer-side readout: the frame this client rebuilt, so it can be compared with the holder's.</summary>
    private void ReportObserverWeld(int handId, in BasisHandFrame frame, bool resolved)
    {
        BasisPickupWeldDiagnostics.Report(new BasisPickupWeldReport
        {
            Name = name,
            Owner = false,
            HandId = (byte)handId,
            Left = IsLeftHand(handId),
            FrameResolved = resolved,
            Canonical = frame.Canonical,
            HandLength = frame.HandLength,
            PalmPosition = frame.Position,
            PropPosition = Target != null ? Target.position : Vector3.zero,
            OffsetHandLengths = resolved && frame.HandLength > 0f ? _attachOffsetPos.magnitude / frame.HandLength : 0f,
        });
    }

    /// <summary>Human-readable name for an attach id, for logs and debug views.</summary>
    public static string DescribeHandId(byte handId)
    {
        switch (handId)
        {
            case HandNone: return "none";
            case HandLeft: return "L wrist";
            case HandRight: return "R wrist";
            case HandLeftPalm: return "L palm";
            case HandRightPalm: return "R palm";
            case HandLeftGrip: return "L grip";
            case HandRightGrip: return "R grip";
            default: return "?";
        }
    }

    /// <summary>
    /// The player holding this prop: the local player on the owner, the holding remote on observers.
    /// </summary>
    private bool TryResolveOwner(out IBasisPlayer player)
    {
        player = null;
        BasisNetworkPlayer owner = currentOwnedPlayer;
        return owner != null && owner.TryGetPlayer(out player);
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

    /// <summary>Re-weld against the holder's freshly posed skeleton (see <see cref="BasisSyncDriver.ReweldAttachedPickups"/>).</summary>
    internal void ReweldAfterRemoteBones()
    {
        _reweldQueued = false;
        if (IsOwnedLocallyOnClient || Target == null) return;
        if (!TryResolveHandFrame(_lastAppliedHandId, out BasisHandFrame frame)) return;
        _attachWorldPos = frame.Position + frame.Rotation * _attachOffsetPos;
        _attachWorldRot = frame.Rotation * _attachOffsetRot;
        Target.SetPositionAndRotation(_attachWorldPos, _attachWorldRot);
    }

    /// <summary>
    /// The grip an authored <see cref="BasisPickupInteractable.GripPoint"/> defines, solved locally from this
    /// receiver's own copy of the prefab. Nothing about it travels: both ends run the same solve against the
    /// same authored transform, so the held pose carries no measurement noise, no interpolation lag and no
    /// trace of the holder's rig. False when the sender did not flag an authored grip, or when this copy of
    /// the prop has none — in which case the streamed offset is used instead.
    /// </summary>
    private bool TryGetAuthoredGripOffset(int handId, out Vector3 offsetPos, out Quaternion offsetRot)
    {
        offsetPos = default;
        offsetRot = default;
        if (!IsAuthoredGrip(handId) || BasisPickupInteractable == null || BasisPickupInteractable.GripPoint == null)
        {
            return false;
        }
        Target.GetPositionAndRotation(out Vector3 objectPos, out Quaternion objectRot);
        return BasisPickupInteractable.TryGetGripOffsets(objectPos, objectRot, out offsetPos, out offsetRot);
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
        if (IsOwnedLocallyOnClient)
        {
            // Our tenure invalidates the remote-side attach state. Without this, when ownership later
            // returns to a remote holder, the first applied packet sees the stale held-edge and flashes
            // the prop at the previous tenure's in-hand pose.
            _lastAppliedHandId = HandNone;
            _haveAttachPose = false;
        }
        else
        {
            _lastSentHandId = HandNone;
        }
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
            // Held with KinematicWhileInteracting - preserve kinematic state
            bool heldKinematic = BasisPickupInteractable != null
                && BasisPickupInteractable.KinematicWhileInteracting
                && BasisPickupInteractable.RequiresUpdateLoop;
            if (pendingStealRequest != null)
            {
                if (!heldKinematic)
                {
                    SetIsKinematicOnPickup(_authoredKinematic);
                }
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
            else if (!heldKinematic)
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
