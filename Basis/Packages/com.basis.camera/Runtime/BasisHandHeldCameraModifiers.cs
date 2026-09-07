using System.Collections.Generic;
using Basis.Cinematics;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Drives the camera from its modifier stack. One modifier owns the position channel, one owns the
/// rotation channel, and effects layer on top; the operator's fly controls steer over whatever the
/// stack produced, so flying a camera by hand while it keeps somebody framed is the ordinary case
/// rather than a special one.
/// </summary>
public abstract partial class BasisHandHeldCameraInteractable
{
    [Header("Modifiers")]
    public BasisCameraModifierStack modifiers = new BasisCameraModifierStack();

    public BasisCameraDollyTrack DollyTrack { get; private set; }

    /// <summary>Networked players framed by a group shot. The local player is carried by <see cref="targetGroupIncludesLocal"/>.</summary>
    public readonly List<ushort> TargetGroup = new List<ushort>();

    private const float SubjectVelocitySmoothing = 6f;

    private readonly BasisCameraModifierState modifierState = new BasisCameraModifierState();

    private Vector3 smoothedSubjectVelocity;
    private Vector3 lastSolveAnchor;
    private bool hasLastSolveAnchor;

    private int occlusionMask = ~0;
    private bool occlusionMaskBuilt;

    private readonly List<BasisCameraSubject> groupSubjects = new List<BasisCameraSubject>();
    private BasisCameraOcclusionProbe occlusionProbe;
    private BasisCameraSweepProbe sweepProbe;

    /// <summary>Layers that never block a shot: the players themselves, UI, and the camera's own props.</summary>
    private static readonly string[] NonBlockingLayers =
    {
        "OverlayUI", "UI", "Ignore Raycast", "IgnoredByInteractable",
        "Player", "LocalPlayerAvatar", "RemotePlayerAvatar", "Interactable",
    };

    public BasisCameraModifierStack Modifiers => modifiers ??= new BasisCameraModifierStack();

    /// <summary>
    /// Who the camera films and where on them it reads. A slot on the stack like the other two, so
    /// a saved mode carries it and the panel can show it beside them.
    /// </summary>
    public ref BasisCameraSubjectSettings subjectSettings => ref Modifiers.subject;

    internal BasisCameraModifierState ModifierState => modifierState;

    /// <summary>Whether the stack has taken the camera out of the operator's hands.</summary>
    public bool IsModifierDriven => Modifiers.DrivesAnything;

    internal void InitializeModifiers()
    {
        modifiers ??= new BasisCameraModifierStack();
        modifiers.Sanitize();
        DollyTrack ??= new BasisCameraDollyTrack();
        occlusionProbe ??= ProbeOcclusion;
        sweepProbe ??= ProbeSweep;
    }

    /// <summary>
    /// Rebuilds the dolly path from the live marker transforms and redraws it. Runs every frame
    /// whatever the stack is doing — a track is normally laid out with nothing fitted, and gating
    /// this left placed points unnumbered, undrawn, and stale in the solve.
    /// </summary>
    internal void TickDollyTrack()
    {
        if (DollyTrack == null)
        {
            return;
        }
        float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        Quaternion facing = GetLabelFacing();

        DollyTrack.SyncMode = Modifiers.dolly.syncMode;
        DollyTrack.Motion = Modifiers.dolly;
        DollyTrack.MotionActive = Modifiers.positionModifier == BasisCameraPositionModifier.DollyTrack;
        DollyTrack.Refresh(scale, facing);

        // Claimed every frame rather than on the share dropdown, so a track that turns networked
        // through a preset or a restored mode is shared on the same terms as one switched by hand.
        if (Modifiers.dolly.syncMode == BasisCameraDollySync.LocalOnly)
        {
            BasisCameraDollyManager.ReleaseLocalTrack(DollyTrack);
        }
        else
        {
            BasisCameraDollyManager.ClaimLocalTrack(DollyTrack);
        }
        BasisCameraDollyManager.Tick(Time.time);
    }

    internal void DisposeModifiers()
    {
        // Withdrawn before it is destroyed, or it stands on every other screen with nothing left
        // to update it.
        if (DollyTrack != null)
        {
            BasisCameraDollyManager.ReleaseLocalTrack(DollyTrack);
        }
        DollyTrack?.Dispose();
        DollyTrack = null;
    }

    // ---- Stack API used by the panel -------------------------------------------------------

    public void SetSubjectModifier(BasisCameraSubjectModifier modifier)
    {
        InitializeModifiers();
        if (modifiers.subject.modifier == modifier)
        {
            return;
        }
        modifiers.subject.modifier = modifier;

        // A group nobody has been added to films nothing, so picking it fills itself from whoever
        // is in the instance. Emptying it afterwards is a deliberate act, and is left alone.
        if (modifier == BasisCameraSubjectModifier.TargetGroup && TargetGroup.Count == 0)
        {
            RebuildTargetGroup();
        }

        OnModifiersChanged();
    }

    public void SetPositionModifier(BasisCameraPositionModifier modifier)
    {
        InitializeModifiers();
        if (modifiers.positionModifier == modifier)
        {
            return;
        }
        modifiers.positionModifier = modifier;
        OnModifiersChanged();
    }

    public void SetRotationModifier(BasisCameraRotationModifier modifier)
    {
        InitializeModifiers();
        if (modifiers.rotationModifier == modifier)
        {
            return;
        }
        modifiers.rotationModifier = modifier;
        OnModifiersChanged();
    }

    public bool AddModifierEffect(BasisCameraEffectModifier effect)
    {
        InitializeModifiers();
        if (!modifiers.AddEffect(effect))
        {
            return false;
        }
        OnModifiersChanged();
        return true;
    }

    public bool RemoveModifierEffect(BasisCameraEffectModifier effect)
    {
        InitializeModifiers();
        if (!modifiers.RemoveEffect(effect))
        {
            return false;
        }
        OnModifiersChanged();
        return true;
    }

    public bool HasModifierEffect(BasisCameraEffectModifier effect) => Modifiers.HasEffect(effect);

    // ---- Dolly transport --------------------------------------------------------------------

    /// <summary>Whether a dolly move is running right now.</summary>
    public bool IsDollyPlaying =>
        Modifiers.dolly.mode == BasisCameraDollyMode.Play && Modifiers.dolly.playing;

    /// <summary>
    /// Starts or stops the move. Starting from the far end of an open track rewinds first, so the
    /// play button is never a button that does nothing.
    /// </summary>
    public void SetDollyPlaying(bool playing)
    {
        InitializeModifiers();

        if (playing && modifierState.DollyCompleted)
        {
            RestartDolly();
        }

        modifiers.dolly.playing = playing;
        modifierState.DollyCompleted = false;
    }

    /// <summary>Sends the playhead back to the start of the track.</summary>
    public void RestartDolly()
    {
        InitializeModifiers();
        modifiers.dolly.position = 0f;
        modifierState.DollyPosition = 0f;
        modifierState.DollyCompleted = false;
    }

    /// <summary>
    /// Changes how the track is shared. The track reads the mode every refresh, so this only has
    /// to record the choice — the markers pick up whether they may be grabbed on the next frame.
    /// </summary>
    public void SetDollySync(BasisCameraDollySync mode)
    {
        InitializeModifiers();
        modifiers.dolly.syncMode = mode;
        if (DollyTrack != null)
        {
            DollyTrack.SyncMode = mode;
        }
    }

    /// <summary>Fits a whole stack at once, for a preset or a saved mode.</summary>
    public void ApplyModifierStack(BasisCameraModifierStack stack, bool seedFromCamera = true)
    {
        InitializeModifiers();
        modifiers.CopyFrom(stack);
        OnModifiersChanged(seedFromCamera);
    }

    /// <summary>
    /// Settles the camera around a change of stack. The state is seeded from where the camera
    /// actually is, so fitting a modifier eases from the live pose rather than cutting to wherever
    /// the solve last left off.
    /// </summary>
    private void OnModifiersChanged(bool seedFromCamera = true)
    {
        InitializeModifiers();
        modifiers.Sanitize();

        if (seedFromCamera && HHC != null && HHC.captureCamera != null)
        {
            HHC.captureCamera.transform.GetPositionAndRotation(out Vector3 livePosition, out Quaternion liveRotation);
            SeedPose(livePosition, liveRotation);
        }
        else
        {
            modifierState.Seed(smoothedPosition, smoothedRotation, GetCaptureFov());
        }

        if (modifiers.DrivesAnything)
        {
            DetachFromHand();
            hasLastSolveAnchor = false;
        }
        else if (PinSpace == CameraPinSpace.WorldSpace && !IsFlying)
        {
            PinSpace = CameraPinSpace.HandHeld;
        }
    }

    /// <summary>Drops every modifier, handing the camera back to the operator.</summary>
    public void ClearModifiers()
    {
        InitializeModifiers();
        modifiers.positionModifier = BasisCameraPositionModifier.FreeFly;
        modifiers.rotationModifier = BasisCameraRotationModifier.Hold;
        modifiers.ClearEffects();
        OnModifiersChanged();
    }

    // ---- Per-frame solve --------------------------------------------------------------------

    /// <summary>
    /// One modifier step. Writes the same <c>smoothedPosition</c>/<c>smoothedRotation</c> the pin
    /// constraint consumes, so nothing downstream needs to know what produced the pose.
    /// </summary>
    private void MoveCameraModifiers(float deltaTime, Vector3 operatorMove, float operatorYaw, float operatorPitch)
    {
        InitializeModifiers();

        // The strafe history belongs to whoever it was measured on. Carried across a change of
        // subject the gap between the two reads as a single-frame strafe of tens of metres per
        // second: the close-in saturates and the camera lurches sideways before easing back out.
        // That fires on every target switch, and again on every fallback to the local player while
        // a remote is between avatars.
        bool bound = TryGetFollowTargetPlayer(out ushort boundId);
        bool group = subjectSettings.modifier != BasisCameraSubjectModifier.FollowPlayer;
        if (bound != lastFollowAnchorWasRemote || boundId != lastFollowAnchorSubject ||
            group != lastFollowAnchorWasGroup)
        {
            lastFollowAnchorWasRemote = bound;
            lastFollowAnchorSubject = boundId;
            lastFollowAnchorWasGroup = group;
            modifierState.ResetSubjectHistory();
            modifierState.ResetSubjectSmoothing();
            smoothedSubjectVelocity = Vector3.zero;
            hasLastSolveAnchor = false;
        }

        BasisCameraSubject subject = ResolveSolveSubject(deltaTime);

        float scale = subject.Valid && subject.Scale > 1e-4f ? subject.Scale : 1f;
        float teleportDistance = modifiers.positionModifier == BasisCameraPositionModifier.FrameSubject
            ? modifiers.framing.teleportDistance
            : modifiers.follow.teleportDistance;

        if (subject.Valid)
        {
            if (hasLastSolveAnchor && Vector3.Distance(subject.AnchorPos, lastSolveAnchor) > teleportDistance * scale)
            {
                modifierState.Reseed();
                // The jump registers as a velocity in the hundreds of metres per second. Left in
                // the filter it keeps look-ahead throwing the aim for a second after the landing.
                smoothedSubjectVelocity = Vector3.zero;
                subject.Velocity = Vector3.zero;
            }
            lastSolveAnchor = subject.AnchorPos;
            hasLastSolveAnchor = true;
        }
        else
        {
            hasLastSolveAnchor = false;
        }

        BasisCameraSolveContext context = new BasisCameraSolveContext
        {
            Subject = subject,
            Fov = GetCaptureFov(),
            Aspect = GetCaptureAspect(),
            DeltaTime = deltaTime,
            Time = Time.time,
            DollyPoints = DollyTrack?.Points,
            DollyLooped = DollyTrack != null && DollyTrack.Looped,
            OperatorPosition = smoothedPosition,
            OperatorRotation = smoothedRotation,
            OperatorMove = operatorMove,
            OperatorYaw = operatorYaw,
            OperatorPitch = operatorPitch,
            OcclusionProbe = occlusionProbe,
            SweepProbe = sweepProbe,
        };

        BasisCameraPose pose = BasisCameraModifierSolver.Solve(modifiers, modifierState, context);

        // The solve only ever writes its own state, so it reports the end of the move rather than
        // reaching into the stack to stop it. Clearing the transport here is what makes the panel's
        // play button flip back on its own when the track runs out.
        if (modifierState.DollyCompleted && modifiers.dolly.playing)
        {
            modifiers.dolly.playing = false;
        }

        smoothedPosition = pose.Position;
        smoothedRotation = pose.Rotation;

        if (modifiers.DrivesLens && HHC != null && HHC.captureCamera != null)
        {
            HHC.captureCamera.fieldOfView = Mathf.Clamp(pose.Fov, 1f, 179f);
        }

        CaptureModifierGizmoSample(subject, pose);
    }

    private float GetCaptureFov()
    {
        if (HHC != null && HHC.captureCamera != null)
        {
            return HHC.captureCamera.fieldOfView;
        }
        return 40f;
    }

    private float GetCaptureAspect()
    {
        if (HHC != null && HHC.captureCamera != null && HHC.captureCamera.aspect > 0f)
        {
            return HHC.captureCamera.aspect;
        }
        return 16f / 9f;
    }

    /// <summary>Billboards the waypoint numbers at whichever camera the operator is actually looking through.</summary>
    private Quaternion GetLabelFacing()
    {
        if (BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.CameraInstance != null)
        {
            return BasisLocalCameraDriver.CameraInstance.transform.rotation;
        }
        return Quaternion.identity;
    }

    /// <summary>
    /// Resolves whoever is being filmed into the solver's own subject type, collapsing a group into
    /// a single bounded subject when group framing is on.
    /// </summary>
    private BasisCameraSubject ResolveSolveSubject(float deltaTime)
    {
        BasisCameraSubject subject;

        switch (subjectSettings.modifier)
        {
            case BasisCameraSubjectModifier.None:
                smoothedSubjectVelocity = Vector3.zero;
                return default;

            case BasisCameraSubjectModifier.FixedPoint:
                subject = FixedPointSubject();
                break;

            case BasisCameraSubjectModifier.TargetGroup:
            {
                groupSubjects.Clear();
                if (subjectSettings.groupIncludesLocal)
                {
                    BasisCameraSubject local = ToSolveSubject(ResolveLocalSubject());
                    if (local.Valid) groupSubjects.Add(local);
                }

                for (int Index = 0; Index < TargetGroup.Count; Index++)
                {
                    if (TryResolveSubjectForNetId(TargetGroup[Index], out BasisCameraSubject member))
                    {
                        groupSubjects.Add(member);
                    }
                }

                // An empty group is a group nobody is in yet, not a reason to stop filming — the
                // single target is what the roster falls back to while it is being populated.
                if (!BasisCameraTargetGroup.TryCombine(groupSubjects, null, out subject))
                {
                    subject = ToSolveSubject(ResolveFollowSubject());
                }
                break;
            }

            default:
                subject = ToSolveSubject(ResolveFollowSubject());
                break;
        }

        if (!subject.Valid)
        {
            smoothedSubjectVelocity = Vector3.zero;
            return subject;
        }

        if (hasLastSolveAnchor && deltaTime > 1e-5f)
        {
            Vector3 instantaneous = (subject.AnchorPos - lastSolveAnchor) / deltaTime;
            smoothedSubjectVelocity = Vector3.Lerp(smoothedSubjectVelocity, instantaneous,
                1f - Mathf.Exp(-SubjectVelocitySmoothing * deltaTime));
        }

        subject.Velocity = smoothedSubjectVelocity;
        return subject;
    }

    private bool TryResolveSubjectForNetId(ushort netId, out BasisCameraSubject subject)
    {
        if (Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers.TryGetValue(netId, out var remote) &&
            remote != null && !remote.IsDestroyed &&
            TryResolveRemoteSubject(netId, remote, out FollowSubject resolved))
        {
            subject = ToSolveSubject(resolved);
            return subject.Valid;
        }

        subject = default;
        return false;
    }

    /// <summary>
    /// The fixed world point as a subject. It has no facing of its own, so the binding frame falls
    /// back to world axes and an over-the-shoulder aim has nothing to match — which is the honest
    /// answer for a place rather than a person.
    /// </summary>
    private BasisCameraSubject FixedPointSubject()
    {
        float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        Vector3 point = subjectSettings.fixedPoint;

        return new BasisCameraSubject
        {
            Valid = true,
            AnchorPos = point,
            LookPoint = point + Vector3.up * (subjectSettings.aimHeightOffset * scale),
            GroundPos = point,
            Yaw = Quaternion.identity,
            Scale = scale > 1e-4f ? scale : 1f,
            Radius = Mathf.Max(0.05f, subjectSettings.framingRadius),
        };
    }

    /// <summary>Parks the fixed point where the camera is now, which is how one gets placed.</summary>
    public void SetFixedPointToCamera()
    {
        InitializeModifiers();
        if (HHC != null && HHC.captureCamera != null)
        {
            modifiers.subject.fixedPoint = HHC.captureCamera.transform.position;
        }
    }

    /// <summary>
    /// Puts the fixed point on a place that was picked rather than typed, and fits the slots that
    /// make the shot hold it.
    ///
    /// <para>Picking a thing to film is an answer to "what is this shot about", so it carries the
    /// subject slot with it — a point nothing reads is not a subject. The rotation slot is only
    /// taken when what is fitted does not aim at a subject at all: Hold and Aim Along Track both
    /// ignore one, and a dolly laid out with Aim Along Track is exactly the shot somebody is
    /// standing in when they point at something. Compose, Match Subject and Look At Subject are
    /// already aimed, and how they aim is a decision that was made on purpose.</para>
    /// </summary>
    public void SetFixedPointTo(Vector3 point)
    {
        InitializeModifiers();
        modifiers.subject.fixedPoint = point;
        SetSubjectModifier(BasisCameraSubjectModifier.FixedPoint);

        if (!BasisCameraModifiers.NeedsSubject(modifiers.rotationModifier))
        {
            SetRotationModifier(BasisCameraRotationModifier.LookAtSubject);
        }
    }

    /// <summary>Parks the fixed point on the player, for filming where you are standing.</summary>
    public void SetFixedPointToPlayer()
    {
        InitializeModifiers();
        if (BasisLocalPlayer.Instance != null)
        {
            float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            modifiers.subject.fixedPoint =
                BasisLocalPlayer.Instance.transform.position + Vector3.up * (1.2f * scale);
        }
    }

    /// <summary>Fills the target group with everyone currently in the instance.</summary>
    public void RebuildTargetGroup()
    {
        InitializeModifiers();
        TargetGroup.Clear();
        modifiers.subject.groupIncludesLocal = true;
        foreach (var pair in Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers)
        {
            if (pair.Value != null) TargetGroup.Add(pair.Key);
        }
    }

    private BasisCameraSubject ToSolveSubject(in FollowSubject source)
        => new BasisCameraSubject
        {
            Valid = source.Valid,
            AnchorPos = source.AnchorPos,
            LookPoint = source.LookPoint,
            GroundPos = source.GroundPos,
            Yaw = source.Yaw,
            Scale = source.Scale > 1e-4f ? source.Scale : 1f,
            Radius = BasisCameraSubjectAim.FramingRadius(subjectSettings.aimPoint, subjectSettings.framingRadius, source.Height, source.Scale),
        };

    /// <summary>
    /// Casts from the subject out toward the camera looking for anything solid in between. Player
    /// and UI layers are excluded: the cast starts inside the subject's own collider, and a sphere
    /// cast that begins overlapping reports a zero-distance hit, which would slam the camera into
    /// their face.
    /// </summary>
    private bool ProbeOcclusion(Vector3 target, Vector3 desiredCameraPos, out float freeDistanceFromTarget)
    {
        freeDistanceFromTarget = 0f;

        Vector3 delta = desiredCameraPos - target;
        float distance = delta.magnitude;
        if (distance <= 1e-4f)
        {
            return false;
        }

        if (!occlusionMaskBuilt)
        {
            BuildOcclusionMask();
        }

        float radius = Mathf.Max(0.01f, Modifiers.occlusion.probeRadius);
        if (Physics.SphereCast(target, radius, delta / distance, out RaycastHit hit, distance, occlusionMask, QueryTriggerInteraction.Ignore))
        {
            freeDistanceFromTarget = hit.distance;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sweeps the camera's own path for anything solid. Shares the occlusion mask, so the layers
    /// that never block a shot never stop the camera either — a camera that halted on the player it
    /// was filming would be unable to pass them.
    /// </summary>
    private bool ProbeSweep(Vector3 origin, Vector3 direction, float distance, float radius, out float freeDistance)
    {
        freeDistance = 0f;

        if (distance <= 1e-4f || direction.sqrMagnitude < 1e-8f)
        {
            return false;
        }

        if (!occlusionMaskBuilt)
        {
            BuildOcclusionMask();
        }

        if (Physics.SphereCast(origin, Mathf.Max(0.01f, radius), direction, out RaycastHit hit, distance,
                occlusionMask, QueryTriggerInteraction.Ignore))
        {
            freeDistance = hit.distance;
            return true;
        }
        return false;
    }

    private void BuildOcclusionMask()
    {
        int mask = ~0;
        for (int Index = 0; Index < NonBlockingLayers.Length; Index++)
        {
            int layer = LayerMask.NameToLayer(NonBlockingLayers[Index]);
            if (layer >= 0)
            {
                mask &= ~(1 << layer);
            }
        }
        occlusionMask = mask & ~BasisLayerMapper.HandHeldCameraUIMask;
        occlusionMaskBuilt = true;
    }

    // ---- Dolly queue API used by the panel ------------------------------------------------

    /// <summary>
    /// Drops a waypoint where the camera is now and appends it to the queue, which is how a track
    /// gets built: fly the camera to a spot, place a point, repeat.
    /// </summary>
    public BasisCameraDollyWaypoint SpawnWaypointAtCamera()
    {
        InitializeModifiers();

        Vector3 position;
        Quaternion rotation;
        if (HHC != null && HHC.captureCamera != null)
        {
            HHC.captureCamera.transform.GetPositionAndRotation(out position, out rotation);
        }
        else
        {
            transform.GetPositionAndRotation(out position, out rotation);
        }

        return DollyTrack.AddWaypoint(position, rotation, BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
    }

    /// <summary>How far in front of the eye a hand-placed waypoint lands, in metres at default scale.</summary>
    private const float WaypointReach = 0.5f;

    /// <summary>
    /// Drops a waypoint at eye height an arm's length in front of the player, for building a track
    /// on foot. Placed against the eye rather than the player root: the root is the playspace
    /// origin, so in VR it sits wherever the room was centred and turns only with the turn stick —
    /// placing against it dropped the point metres from where the player was standing, facing a
    /// direction they had never looked.
    /// </summary>
    public BasisCameraDollyWaypoint SpawnWaypointInFrontOfPlayer()
    {
        InitializeModifiers();

        float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        ReadPlayerEye(out Vector3 eye, out Quaternion yaw);
        Vector3 position = eye + yaw * (Vector3.forward * (WaypointReach * scale));

        return DollyTrack.AddWaypoint(position, yaw, scale);
    }

    /// <summary>
    /// Where the player is looking from and which way they are facing, flattened to a heading.
    ///
    /// <para>Head rather than camera, so a third-person view places at the player and not out where
    /// the orbiting camera sits; flattened, because a waypoint dropped while glancing at the floor
    /// belongs at the height it was placed at rather than in it.</para>
    /// </summary>
    private void ReadPlayerEye(out Vector3 eye, out Quaternion yaw)
    {
        if (BasisLocalCameraDriver.HasInstance)
        {
            eye = BasisLocalCameraDriver.HeadPosition;
            Vector3 forward = Vector3.ProjectOnPlane(BasisLocalCameraDriver.HeadForward(), Vector3.up);
            if (forward.sqrMagnitude > 1e-6f)
            {
                yaw = Quaternion.LookRotation(forward.normalized, Vector3.up);
                return;
            }

            // Looking straight up or down flattens to nothing. The root's heading is the wrong
            // frame for a position but is still the player's own facing on desktop, and is the only
            // heading left to fall back on.
            yaw = RootYaw();
            return;
        }

        if (BasisLocalPlayer.Instance != null)
        {
            eye = BasisLocalPlayer.Instance.transform.position;
            yaw = RootYaw();
            return;
        }

        transform.GetPositionAndRotation(out eye, out Quaternion cameraRotation);
        yaw = BasisCameraDamping.Yaw(cameraRotation.eulerAngles.y);
    }

    private static Quaternion RootYaw()
    {
        if (BasisLocalPlayer.Instance == null)
        {
            return Quaternion.identity;
        }
        return BasisCameraDamping.Yaw(BasisLocalPlayer.Instance.transform.eulerAngles.y);
    }

    public bool RemoveWaypoint(int index) => DollyTrack != null && DollyTrack.RemoveWaypointAt(index);

    /// <summary>
    /// Moves a waypoint to a different slot in the queue. The marker does not move — only where the
    /// path visits it, which is what the panel's position field edits.
    /// </summary>
    public bool SetWaypointQueuePosition(int currentIndex, int newIndex)
        => DollyTrack != null && DollyTrack.MoveWaypoint(currentIndex, newIndex);

    public void ClearDollyTrack() => DollyTrack?.Clear();

    public int DollyWaypointCount => DollyTrack?.Count ?? 0;

    // ---- Dolly presets --------------------------------------------------------------------

    /// <summary>
    /// Takes the laid-out track down as a preset, anchored on where the player is standing. The
    /// anchor is the player rather than the camera: a track is built by walking it, and it is the
    /// walk that decides which way round the shape is.
    /// </summary>
    public BasisCameraDollyPreset CaptureDollyPreset(string name)
    {
        InitializeModifiers();

        BasisCameraDollyPreset preset = new BasisCameraDollyPreset { name = name };
        preset.looped = DollyTrack.Looped;
        preset.gridSnap = DollyTrack.GridSnap;
        preset.gridSize = DollyTrack.GridSize;

        BasisCameraDollySettings motion = modifiers.dolly;
        motion.playing = false;
        motion.syncMode = BasisCameraDollySync.LocalOnly;
        preset.motion = motion;

        int count = DollyTrack.Count;
        var positions = new List<Vector3>(count);
        var rotations = new List<Quaternion>(count);
        for (int Index = 0; Index < count; Index++)
        {
            BasisCameraDollyWaypoint waypoint = DollyTrack.GetWaypoint(Index);
            if (waypoint == null) continue;

            positions.Add(waypoint.Position);
            rotations.Add(waypoint.Rotation);
        }

        ReadPlayerAnchor(out Vector3 anchor, out float yaw, out float scale);
        preset.Capture(positions, rotations, anchor, yaw, scale);
        return preset;
    }

    /// <summary>
    /// Lays a preset out, replacing whatever track is there.
    ///
    /// <para><paramref name="inPlace"/> puts it back at the world coordinates it was captured at,
    /// which is what you want for a track built for this spot. Without it the shape is rebuilt
    /// around where the player is standing now and scaled to the avatar they are wearing, which is
    /// what makes a preset something that can be reused at all — including one somebody else
    /// built, whose world coordinates mean nothing here.</para>
    ///
    /// <para>The share mode is deliberately not restored. Whether a track goes out to the instance
    /// is a decision about this session, not a property of the shape.</para>
    /// </summary>
    public bool ApplyDollyPreset(BasisCameraDollyPreset preset, bool inPlace)
    {
        if (preset == null || preset.Count == 0) return false;

        InitializeModifiers();

        Vector3 anchor;
        float yaw;
        float scale;
        if (inPlace)
        {
            anchor = preset.anchorPosition;
            yaw = preset.anchorYaw;
            scale = preset.anchorScale;
        }
        else
        {
            ReadPlayerAnchor(out anchor, out yaw, out scale);
        }

        DollyTrack.Clear();
        DollyTrack.Looped = preset.looped;
        DollyTrack.SetGridSnap(preset.gridSnap, preset.gridSize);

        float markerScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        for (int Index = 0; Index < preset.Count; Index++)
        {
            preset.Resolve(Index, anchor, yaw, scale, out Vector3 position, out Quaternion rotation);
            DollyTrack.AddWaypoint(position, rotation, markerScale);
        }

        BasisCameraDollySync sharing = modifiers.dolly.syncMode;
        modifiers.dolly = preset.motion;
        modifiers.dolly.syncMode = sharing;
        modifiers.dolly.playing = false;
        modifiers.Sanitize();

        RestartDolly();
        return true;
    }

    /// <summary>
    /// Where the player is, which way they are facing, and how big they are — the frame a preset is
    /// stored against and laid back out in. Falls back to the camera when there is no player, so a
    /// preset saved in a scene without one is still saved against something.
    /// </summary>
    private void ReadPlayerAnchor(out Vector3 anchor, out float yaw, out float scale)
    {
        scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        if (scale <= 0.001f) scale = 1f;

        ReadPlayerEye(out Vector3 eye, out Quaternion facing);
        yaw = facing.eulerAngles.y;

        // Under the player rather than at their eye: a preset is laid back out on the floor they
        // are standing on. The height comes off the root, the only thing that knows where that
        // floor is; the two horizontal axes come off the head, the only thing that knows where in
        // the playspace they have walked to.
        anchor = eye;
        if (BasisLocalPlayer.Instance != null)
        {
            anchor.y = BasisLocalPlayer.Instance.transform.position.y;
        }
    }
}
