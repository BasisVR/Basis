using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.InputSystem;
using Basis.Scripts.Device_Management;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.TransformBinders.BoneControl;

/// <summary>
/// Interactable handheld/fly camera controller:
/// - Pins the capture camera to handheld, playspace, or world space
/// - Provides a desktop “fly” mode with smoothed movement/rotation, momentum, and auto-leveling
/// - Locks/unlocks player controls while interacting
/// </summary>
public abstract class BasisHandHeldCameraInteractable : BasisPickupInteractable
{
    /// <summary>Owning handheld camera component and metadata.</summary>
    public BasisHandHeldCamera HHC;

    /// <summary>Reference to the camera UI for orientation updates.</summary>
    private BasisHandHeldCameraUI cameraUI;

    [Header("Camera Settings")]
    /// <summary>Space to which the capture camera is pinned.</summary>
    public CameraPinSpace PinSpace = CameraPinSpace.HandHeld;

    [Header("Flying Camera Settings")]
    /// <summary>Base fly speed (units/second).</summary>
    public float flySpeed = 2f;

    /// <summary>Multiplier applied when fast-move is held.</summary>
    public float flyFastMultiplier = 3f;

    /// <summary>Acceleration toward target velocity.</summary>
    public float flyAcceleration = 10f;

    /// <summary>Deceleration factor when no input (used with momentum).</summary>
    public float flyDeceleration = 8f;

    /// <summary>Position smoothing factor while flying.</summary>
    public float flyMovementSmoothing = 12f;

    [Header("Camera Rotation")]
    /// <summary>Mouse sensitivity for fly rotation.</summary>
    public float mouseSensitivity = 0.5f;

    /// <summary>Smoothing applied to fly rotation changes.</summary>
    [Range(5f, 25f)]
    public float rotationSmoothing = 15f;

    [Header("Cinematic Controls")]
    /// <summary>Whether to use momentum/inertia for movement.</summary>
    public bool useMomentum = true;

    /// <summary>How quickly momentum falls off.</summary>
    [Range(2f, 12f)]
    public float inertiaDamping = 5f;

    /// <summary>Automatically level pitch toward eye-height.</summary>
    public bool useAutoLeveling = false;

    /// <summary>Strength of the auto-leveling force.</summary>
    public float autoLevelStrength = 2f;

    /// <summary>Extra damping applied to cinematic motion.</summary>
    [Range(0.1f, 0.9f)]
    public float cinematicDamping = 0.8f;

    [Header("VR Handheld Stabilization")]
    public bool useVRHandheldSmoothing = false;
    public bool onlySmoothWhenStreamingToDesktop = true;

    [Range(1f, 30f)]
    public float vrHandheldPositionSmoothing = 12f;

    [Range(1f, 30f)]
    public float vrHandheldRotationSmoothing = 14f;

    private Vector3 smoothedHandheldWorldPos;
    private Quaternion smoothedHandheldWorldRot = Quaternion.identity;
    private bool handheldSmoothingInitialized = false;

    // --- internal values / locks ---
    private readonly BasisLocks.LockContext LookLock = BasisLocks.GetContext(BasisLocks.LookRotation);
    private readonly BasisLocks.LockContext MovementLock = BasisLocks.GetContext(BasisLocks.Movement);
    private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);

    /// <summary>Capture camera’s starting local position (handheld mode baseline).</summary>
    private Vector3 cameraStartingLocalPos;

    /// <summary>Capture camera’s starting local rotation (handheld mode baseline).</summary>
    private Quaternion cameraStartingLocalRot;

    // Modes / orientation
    private BasisCameraOrientation currentOrientation = BasisCameraOrientation.Landscape;
    private float orientationCheckCooldown = 0f;

    [SerializeReference] private BasisParentConstraint cameraPinConstraint;
    [SerializeReference] private BasisFlyCamera flyCamera;

    private const float cameraDefaultScale = 0.00015f;

    [Tooltip("Fraction of the desktop view the camera is allowed to cover before it is scaled down.")]
    [Range(0.1f, 1f)] public float desktopScreenFitFraction = 0.85f;

    [Tooltip("Fraction of the spawn offset the camera is pulled toward the desktop eye. Lower is closer.")]
    public float desktopEyeDistanceScale = 0.325f;

    private Vector3 desktopSpawnOffset = Vector3.forward;
    private Quaternion desktopOffsetRotation = Quaternion.identity;

    [Header("Auto Follow")]
    [Tooltip("Flies the camera along with the player instead of holding it. Works in desktop and VR.")]
    public bool autoFollowEnabled = false;

    [Tooltip("Offset from the player in yaw-relative space, in metres at default avatar scale: X right, Y up from calibrated eye level, Z forward.")]
    public Vector3 autoFollowPositionOffset = new Vector3(0.5f, 0f, 1.4f);

    [Tooltip("Extra rotation applied after aiming, in degrees.")]
    public Vector3 autoFollowRotationOffset = Vector3.zero;

    [Tooltip("Follow your body's centre of mass (hips) so room-scale movement keeps you in frame. Off anchors to the playspace origin, which is steadier but ignores physical walking.")]
    public bool autoFollowPlayspace = true;

    [Tooltip("Aim the camera at the player rather than facing the player's forward.")]
    public bool autoFollowLookAtPlayer = true;

    [Tooltip("Shifts the aim point up or down from head height, in metres at default avatar scale. Negative aims lower down the body.")]
    public float autoFollowLookAtHeightOffset = 0f;

    [Tooltip("How strongly sideways player movement pulls the camera in to keep the shot tight. 0 holds the fixed side offset; 1 closes the side gap fully while strafing.")]
    [Range(0f, 1f)] public float autoFollowLateralTracking = 0.5f;

    public float autoFollowPositionSmoothing = 4f;
    public float autoFollowRotationSmoothing = 6f;

    [Tooltip("Snap instead of easing when the target is further away than this, in metres at default avatar scale.")]
    public float autoFollowTeleportDistance = 10f;

    [Tooltip("Continuously focus depth of field on the follow subject, keeping them sharp as they move.")]
    public bool autoFocusFollowSubject = false;

    [Tooltip("Network id of the remote player to follow. 0 follows the local player.")]
    public ushort followTargetPlayerId = 0;

    /// <summary>Set the follow target to a remote player, or to the local player when id is 0.</summary>
    public void SetFollowTargetPlayer(ushort netId) => followTargetPlayerId = netId;

    public bool IsFollowingRemotePlayer => followTargetPlayerId != 0;

    public bool IsAutoFollowing => autoFollowEnabled;

    /// <summary>Resolved pose of whoever the camera follows — local player or a remote — in world space.</summary>
    private struct FollowSubject
    {
        public bool Valid;
        public Vector3 AnchorPos;   // where the camera is placed relative to (centre of mass or root)
        public Vector3 GroundPos;   // the subject's feet, for the feet-relative aim helper
        public Quaternion Yaw;      // yaw-only facing of the subject
        public Vector3 LookPoint;   // head-height point to aim at and focus on
        public float Scale;         // avatar-to-default scale for offset sizing
    }

    /// <summary>True while the desktop fly controls have the camera, so it is off in the world somewhere.</summary>
    public bool IsFlying => pauseMove;

    /// <summary>
    /// True whenever the camera is not pinned to the hand — flying, following, or world/playspace
    /// pinned. This is the "the camera has left your hand" condition the follow marker keys off.
    /// </summary>
    public bool IsDetachedFromHand => PinSpace != CameraPinSpace.HandHeld;

    public void SetAutoFollowEnabled(bool enabled)
    {
        if (autoFollowEnabled == enabled)
        {
            return;
        }

        autoFollowEnabled = enabled;

        if (enabled)
        {
            if (HHC != null && HHC.captureCamera != null)
            {
                HHC.captureCamera.transform.GetPositionAndRotation(out smoothedPosition, out smoothedRotation);
            }
            PinSpace = CameraPinSpace.WorldSpace;
        }
        else if (PinSpace == CameraPinSpace.WorldSpace)
        {
            PinSpace = CameraPinSpace.HandHeld;
        }
    }

    /// <summary>Previous frame's follow anchor, for measuring how fast the subject strafes.</summary>
    private Vector3 lastFollowAnchor;
    private bool hasLastFollowAnchor;

    /// <summary>Lateral speed above which the camera pulls in fully, in metres/second at default scale.</summary>
    private const float LateralTrackingReferenceSpeed = 1.5f;

    /// <summary>Low-pass rate for the measured lateral speed, so a single-frame spike can't slam the offset.</summary>
    private const float LateralTrackingSpeedSmoothing = 8f;

    /// <summary>Filtered lateral speed of the follow subject.</summary>
    private float smoothedLateralSpeed;

    /// <summary>
    /// Every intermediate the follow solve produces, so the debug gizmos can draw what the
    /// solve actually computed rather than re-deriving it (a re-derivation drifts silently the
    /// moment the solve changes). Only filled while <see cref="CaptureFollowGizmoSample"/> is on.
    /// </summary>
    internal struct FollowGizmoSample
    {
        public bool Valid;
        public bool Live;
        public Vector3 AnchorPos;
        public Vector3 GroundPos;
        public Vector3 LookPoint;
        public Quaternion Yaw;
        public float Scale;
        public Vector3 AuthoredOffset;
        public Vector3 AppliedOffset;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public float LateralSpeed;
        public float CloseIn;
        public bool Snapped;
    }

    /// <summary>Set by the gizmo visualiser while the follow layer is on; keeps the solve free otherwise.</summary>
    internal bool CaptureFollowGizmoSample;

    private FollowGizmoSample followGizmoSample;
    private int followGizmoSampleFrame = -1;

    private void MoveCameraAutoFollow(float deltaTime)
    {
        FollowSubject subject = ResolveFollowSubject();
        if (!subject.Valid)
        {
            hasLastFollowAnchor = false;
            return;
        }

        Vector3 sideOffset = autoFollowPositionOffset;
        float appliedCloseIn = 0f;

        // Sideways tracking: when the subject strafes, close the side gap so they stay framed
        // instead of sliding toward the edge of the shot. Measured as the subject's velocity
        // along the camera's side axis; standing still leaves the authored offset untouched, so
        // the smoothing below eases the camera back out when they stop.
        if (autoFollowLateralTracking > 0f && hasLastFollowAnchor && deltaTime > 1e-5f)
        {
            Vector3 sideAxis = subject.Yaw * Vector3.right;
            float lateralSpeed = Vector3.Dot(subject.AnchorPos - lastFollowAnchor, sideAxis) / deltaTime;

            // Low-pass the speed. A raw one-frame delta spikes on any hitch — a frame stall, an
            // avatar swap, a teleport, a hips snap — and an instant spike slams the side offset
            // closed and then eases back out, which reads as a jitter every so often.
            smoothedLateralSpeed = Mathf.Lerp(smoothedLateralSpeed, lateralSpeed,
                1f - Mathf.Exp(-LateralTrackingSpeedSmoothing * deltaTime));

            float referenceSpeed = LateralTrackingReferenceSpeed * subject.Scale;
            float closeIn = autoFollowLateralTracking * Mathf.Clamp01(Mathf.Abs(smoothedLateralSpeed) / referenceSpeed);
            sideOffset.x *= 1f - closeIn;
            appliedCloseIn = closeIn;
        }
        else
        {
            smoothedLateralSpeed = 0f;
        }
        lastFollowAnchor = subject.AnchorPos;
        hasLastFollowAnchor = true;

        Vector3 targetPosition = subject.AnchorPos + subject.Yaw * (sideOffset * subject.Scale);

        // Auto follow always aims at the player. (The old "Look At Me" toggle is gone — its off
        // state pointed the camera the way the player faced, i.e. away from them.)
        Vector3 toSubject = subject.LookPoint - targetPosition;

        Quaternion targetRotation = toSubject.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(toSubject, Vector3.up)
            : subject.Yaw;
        targetRotation *= Quaternion.Euler(autoFollowRotationOffset);

        bool snapped = Vector3.Distance(smoothedPosition, targetPosition) > autoFollowTeleportDistance * subject.Scale;

        if (CaptureFollowGizmoSample)
        {
            followGizmoSample = new FollowGizmoSample
            {
                Valid = true,
                Live = true,
                AnchorPos = subject.AnchorPos,
                GroundPos = subject.GroundPos,
                LookPoint = subject.LookPoint,
                Yaw = subject.Yaw,
                Scale = subject.Scale,
                AuthoredOffset = autoFollowPositionOffset,
                AppliedOffset = sideOffset,
                TargetPosition = targetPosition,
                TargetRotation = targetRotation,
                LateralSpeed = smoothedLateralSpeed,
                CloseIn = appliedCloseIn,
                Snapped = snapped,
            };
            followGizmoSampleFrame = Time.frameCount;
        }

        if (snapped)
        {
            smoothedPosition = targetPosition;
            smoothedRotation = targetRotation;
            return;
        }

        smoothedPosition = Vector3.Lerp(smoothedPosition, targetPosition, 1f - Mathf.Exp(-autoFollowPositionSmoothing * deltaTime));
        smoothedRotation = Quaternion.Slerp(smoothedRotation, targetRotation, 1f - Mathf.Exp(-autoFollowRotationSmoothing * deltaTime));

        // ApplyFollowZoom();  // Removed: it narrowed FOV to hold the subject the same size as
        // the camera dollied out, so the two cancelled and the Distance slider did nothing
        // visible. Distance is now a plain dolly (targetPosition above), which changes framing.
    }

    /// <summary>
    /// Drops auto-follow and pins the camera at an explicit world pose, holding it there so it
    /// stops chasing anything and can be picked up. The transform is set immediately as well as
    /// the smoothed pin targets, so there is no ease-in from wherever it was.
    /// </summary>
    public void PlaceWorldPinned(Vector3 position, Quaternion rotation)
    {
        autoFollowEnabled = false;
        PinSpace = CameraPinSpace.WorldSpace;
        smoothedPosition = position;
        smoothedRotation = rotation;
        if (HHC != null && HHC.captureCamera != null)
        {
            HHC.captureCamera.transform.SetPositionAndRotation(position, rotation);
        }
    }

    /// <summary>Sets the capture field of view directly. (Follow distance is a dolly, not a lens zoom.)</summary>
    public void SetFieldOfView(float value)
    {
        if (HHC != null && HHC.captureCamera != null)
        {
            HHC.captureCamera.fieldOfView = value;
        }
    }

    /// <summary>
    /// Resolves who the camera follows into a world pose. A remote target that has left falls back
    /// to the local player, so a disconnect can never strand the camera aimed at empty space.
    /// </summary>
    private FollowSubject ResolveFollowSubject()
    {
        if (followTargetPlayerId != 0)
        {
            if (Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers.TryGetValue(followTargetPlayerId, out var remote) &&
                remote != null && !remote.IsDestroyed)
            {
                if (TryResolveRemoteSubject(remote, out FollowSubject remoteSubject))
                {
                    return remoteSubject;
                }
            }
            else
            {
                followTargetPlayerId = 0;
            }
        }

        return ResolveLocalSubject();
    }

    private FollowSubject ResolveLocalSubject()
    {
        if (BasisLocalPlayer.Instance == null)
        {
            return default;
        }

        // Anchor to the player root, not AvatarTransform. The latter is the loaded avatar model
        // (BasisAvatarFactory assigns avatar.transform), so it carries every IK correction and
        // foot-plant as shake, its rotation is slammed to identity on teleport, and it is
        // replaced on every avatar swap. The root is what locomotion actually moves.
        BasisLocalPlayer.Instance.transform.GetPositionAndRotation(out Vector3 rootPos, out Quaternion anchorRot);
        Quaternion anchorYaw = Quaternion.Euler(0f, anchorRot.eulerAngles.y, 0f);

        float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

        // Height is measured from calibrated eye level, not the feet, so a zero offset films you
        // level with your eyeline on any avatar. GetTposeHeadHeight is already avatar-scaled, and
        // being calibration-derived it does not bob with crouching the way the live head does.
        Vector3 anchorPos = rootPos + Vector3.up * GetTposeHeadHeight();

        float hipsHeight = GetTposeHipsHeight();
        if (autoFollowPlayspace && hipsHeight > 0f && BasisLocalBoneDriver.HipsControl != null)
        {
            // Centre of mass. The hips carry their own height, so lift by only the calibrated
            // eye-above-hips gap and a zero offset still sits on your eyeline. Vertical now
            // tracks crouching, which the playspace root cannot do.
            Vector3 hips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.position;
            anchorPos = hips + Vector3.up * Mathf.Max(0f, GetTposeHeadHeight() - hipsHeight);
        }

        return new FollowSubject
        {
            Valid = true,
            AnchorPos = anchorPos,
            GroundPos = rootPos,
            Yaw = anchorYaw,
            LookPoint = GetAutoFollowLookTarget(new Vector3(anchorPos.x, rootPos.y, anchorPos.z), scale),
            Scale = scale,
        };
    }

    /// <summary>Nominal root-to-head height used only while a remote has no avatar root to read.</summary>
    private const float RemoteFallbackHeadHeight = 1.5f;

    /// <summary>
    /// Resolves a remote's world pose, or false while it has no transforms to read yet — still
    /// joining, or between avatars — so the caller holds the target instead of dropping it.
    /// </summary>
    /// <remarks>
    /// A remote has no player root object: <c>PlayerSelf</c> is only ever assigned for the local
    /// player. The root here is the avatar's animator transform, which the remote bone jobs write
    /// a full world pose to every frame. Its yaw is body facing; the mouth marker's is head
    /// facing, which would swing the camera every time the subject glanced sideways.
    /// </remarks>
    private bool TryResolveRemoteSubject(BasisRemotePlayer remote, out FollowSubject subject)
    {
        subject = default;

        Transform root = remote.AvatarAnimatorTransform;
        Transform headTransform = remote.MouthTransform;
        if (root == null && headTransform == null)
        {
            return false;
        }

        // Remotes are network-interpolated, so these are already smooth — no IK shake to dodge,
        // and no local T-pose to read, so height comes from the synced head transform when present.
        Vector3 rootPos;
        Quaternion rootRot;
        if (root != null)
        {
            root.GetPositionAndRotation(out rootPos, out rootRot);
        }
        else
        {
            headTransform.GetPositionAndRotation(out rootPos, out rootRot);
            rootPos -= Vector3.up * RemoteFallbackHeadHeight;
        }

        Quaternion yaw = Quaternion.Euler(0f, rootRot.eulerAngles.y, 0f);
        Vector3 lookPoint = headTransform != null
            ? headTransform.position
            : rootPos + Vector3.up * RemoteFallbackHeadHeight;

        subject = new FollowSubject
        {
            Valid = true,
            AnchorPos = new Vector3(rootPos.x, lookPoint.y, rootPos.z),
            GroundPos = rootPos,
            Yaw = yaw,
            // Head height (the synced head transform), matching the local path's default.
            LookPoint = new Vector3(rootPos.x, lookPoint.y + autoFollowLookAtHeightOffset, rootPos.z),
            // Remote avatar scale is not locally known; the default-metre offsets read best at 1.
            Scale = 1f,
        };
        return true;
    }

    /// <summary>Focus point (world head-height of the follow subject) for the DoF auto-focus, or null.</summary>
    public bool TryGetFollowFocusPoint(out Vector3 point)
    {
        FollowSubject subject = ResolveFollowSubject();
        point = subject.LookPoint;
        return subject.Valid;
    }

    /// <summary>
    /// The follow solve's own intermediates for this frame. When the solve did not run — follow is
    /// off, or the camera is in hand — the subject is resolved fresh and the offset is the authored
    /// one with no lateral close-in (which needs frame history), flagged by <c>Live == false</c> so
    /// the drawing can present it as a preview of where follow *would* place the camera.
    /// </summary>
    internal bool TryGetFollowGizmoSample(out FollowGizmoSample sample)
    {
        if (followGizmoSampleFrame == Time.frameCount && followGizmoSample.Valid)
        {
            sample = followGizmoSample;
            return true;
        }

        FollowSubject subject = ResolveFollowSubject();
        if (!subject.Valid)
        {
            sample = default;
            return false;
        }

        Vector3 targetPosition = subject.AnchorPos + subject.Yaw * (autoFollowPositionOffset * subject.Scale);
        Vector3 toSubject = subject.LookPoint - targetPosition;
        Quaternion targetRotation = toSubject.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(toSubject, Vector3.up)
            : subject.Yaw;

        sample = new FollowGizmoSample
        {
            Valid = true,
            Live = false,
            AnchorPos = subject.AnchorPos,
            GroundPos = subject.GroundPos,
            LookPoint = subject.LookPoint,
            Yaw = subject.Yaw,
            Scale = subject.Scale,
            AuthoredOffset = autoFollowPositionOffset,
            AppliedOffset = autoFollowPositionOffset,
            TargetPosition = targetPosition,
            TargetRotation = targetRotation * Quaternion.Euler(autoFollowRotationOffset),
        };
        return true;
    }

    /// <summary>The pin constraint's current offset pose — the pose follow and fly both write into.</summary>
    internal void GetPinnedTargetPose(out Vector3 position, out Quaternion rotation)
    {
        position = smoothedPosition;
        rotation = smoothedRotation;
    }

    /// <summary>
    /// The point auto-follow aims at: the player's head by default, so the shot frames the face,
    /// shifted toward the body by <see cref="autoFollowLookAtHeightOffset"/>.
    /// <para>
    /// The height comes from the avatar's T-pose, not from the live head. Live head position
    /// moves with every crouch, lean and head-bob, and the camera would swing to chase it —
    /// so only the root translates the aim point, and its height off the ground is fixed.
    /// </para>
    /// </summary>
    private Vector3 GetAutoFollowLookTarget(Vector3 rootPosition, float scale)
    {
        // Aim at head height by default so the shot frames the face; the offset drops it toward
        // the body when wanted.
        float headHeight = GetTposeHeadHeight();
        return rootPosition + Vector3.up * (headHeight + autoFollowLookAtHeightOffset * scale);
    }

    /// <summary>
    /// Height of the head above the avatar root in its T-pose, already scaled to the avatar
    /// as worn. Falls back to the measured avatar height before a T-pose snapshot exists.
    /// </summary>
    /// <summary>
    /// Height of the hips above the avatar root in its T-pose, already scaled to the avatar as
    /// worn. Returns 0 when no snapshot exists yet, which callers treat as "unavailable".
    /// </summary>
    private static float GetTposeHipsHeight()
    {
        if (BasisLocalAvatarDriver.HasTposeBoneSnapshot)
        {
            BasisLocalBoneControl hips = BasisLocalBoneDriver.HipsControl;
            if (hips != null && hips.TposeLocalScaled.position.y > 0.01f)
            {
                return hips.TposeLocalScaled.position.y;
            }
        }
        return 0f;
    }

    private static float GetTposeHeadHeight()
    {
        if (BasisLocalAvatarDriver.HasTposeBoneSnapshot)
        {
            BasisLocalBoneControl head = BasisLocalBoneDriver.HeadControl;
            if (head != null && head.TposeLocalScaled.position.y > 0.01f)
            {
                return head.TposeLocalScaled.position.y;
            }
        }
        return BasisHeightDriver.SelectedScaledAvatarHeight;
    }

    private float appliedCameraScale = -1f;

    private float GetDesktopFitScale()
    {
        if (BasisDeviceManagement.IsCurrentModeVR() || !BasisLocalCameraDriver.HasInstance)
        {
            return float.PositiveInfinity;
        }

        Camera playerCamera = BasisLocalCameraDriver.CameraInstance;
        if (playerCamera == null || playerCamera.aspect <= 0f)
        {
            return float.PositiveInfinity;
        }

        if (transform is not RectTransform rootRect)
        {
            return float.PositiveInfinity;
        }

        Vector2 size = rootRect.rect.size;
        if (size.x <= 0f || size.y <= 0f)
        {
            return float.PositiveInfinity;
        }

        playerCamera.transform.GetPositionAndRotation(out Vector3 eyePos, out Quaternion eyeRot);
        float distance = Vector3.Dot(transform.position - eyePos, eyeRot * Vector3.forward);

        return ComputeDesktopFitScale(size, distance, playerCamera.fieldOfView, playerCamera.aspect, desktopScreenFitFraction);
    }

    /// <summary>
    /// Largest root scale at which a rect of <paramref name="rectSize"/> local units still fits
    /// inside the view frustum at <paramref name="distance"/>, times <paramref name="fitFraction"/>.
    /// Returns +infinity for degenerate input so callers treat it as "no constraint".
    /// <para>
    /// Pure and static so the geometry can be tested without a camera or a scene — the same
    /// reason <see cref="Basis.BasisUI.BasisMenuMover.GetEyeModeScaleFactor"/> was extracted.
    /// </para>
    /// </summary>
    public static float ComputeDesktopFitScale(Vector2 rectSize, float distance, float verticalFovDegrees, float aspect, float fitFraction)
    {
        if (rectSize.x <= 0f || rectSize.y <= 0f || aspect <= 0f || verticalFovDegrees <= 0f || verticalFovDegrees >= 180f)
        {
            return float.PositiveInfinity;
        }

        if (distance <= 0.01f)
        {
            return float.PositiveInfinity;
        }

        float frustumHeight = 2f * distance * Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad);
        float frustumWidth = frustumHeight * aspect;

        return Mathf.Min(frustumWidth / rectSize.x, frustumHeight / rectSize.y) * fitFraction;
    }

    private void ApplyCameraScale()
    {
        float scale = cameraDefaultScale * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

        float desktopFit = GetDesktopFitScale();
        if (!float.IsPositiveInfinity(desktopFit))
        {
            scale = desktopFit;
        }

        if (Mathf.Approximately(scale, appliedCameraScale))
        {
            return;
        }

        appliedCameraScale = scale;
        transform.localScale = Vector3.one * scale;
    }

    private bool isPlayerManuallyUnlocked = false;
    private bool desktopSetup = false;
    private CameraPinSpace previousPinState = CameraPinSpace.HandHeld;

    // Motion state
    private Vector3 currentVelocity = Vector3.zero;
    private Vector3 targetVelocity = Vector3.zero;
    private Vector3 velocityMomentum = Vector3.zero;
    private float rotationMomentum = 0f;

    // Rotation state
    private float currentPitch = 0f;
    private float currentYaw = 0f;
    private float targetPitch = 0f;
    private float targetYaw = 0f;

    // Smoothed transform (for pin constraint offset)
    private Vector3 smoothedPosition = Vector3.zero;
    private Quaternion smoothedRotation = Quaternion.identity;

    private bool pauseMove = false;

    // VR fly mode state
    private bool isVRFlying = false;
    private bool vrThumbstickClickPrev = false;
    private Quaternion vrControllerRotation = Quaternion.identity;

    private bool selfieRotationEnabled = false;
    /// <summary>Where to pin the camera transform.</summary>
    public enum CameraPinSpace
    {
        /// <summary>Parented to the handheld object (local transform preserved).</summary>
        HandHeld,
        /// <summary>Pinned relative to the local player’s avatar transform.</summary>
        PlaySpace,
        /// <summary>Free in world space with no parent.</summary>
        WorldSpace,
    }

    /// <summary>
    /// Unity Start override: sets up locks, desktop state, captures camera references,
    /// subscribes to lifecycle events, and initializes constraints/fly controller.
    /// </summary>
    public new void Start()
    {
        base.Start();

        // force rigid ref null, pickup will use raw transform instead
        RigidRef = null;

        // disable base desktop “zoop”/rotate
        DesktopZoopSpeed = 0;
        DesktopRotateSpeed = 0;

        CanSelfSteal = false;

        // Desktop: lock player look/move for UI selection
        string className = nameof(BasisHandHeldCameraInteractable);
        bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
        if (inDesktop)
            LockPlayer(className);

        BasisCursorManagement.UnlockCursor(nameof(BasisHandHeldCamera),false);

        if (HHC.captureCamera == null)
        {
            HHC.captureCamera = gameObject.GetComponentInChildren<Camera>(true);
        }
        if (HHC.captureCamera == null)
        {
            BasisDebug.LogError($"Camera not found in children of {nameof(BasisHandHeldCamera)}, camera pinning will be broken");
        }
        else
        {
            HHC.captureCamera.transform.GetLocalPositionAndRotation(out cameraStartingLocalPos, out cameraStartingLocalRot);
        }

        OnInteractStartEvent.AddListener( OnInteractDesktopTweak );
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;

        BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;

        // scale camera to avatar size
        ApplyCameraScale();

        // run after player movement
        BasisLocalPlayer.AfterSimulateOnLate.AddAction(202, UpdateCamera);

        cameraPinConstraint = new BasisParentConstraint
        {
            sources = new BasisConstraintSourceData[] { new() { weight = 1f } },
            Enabled = false
        };

        flyCamera = new BasisFlyCamera();
    }

    /// <summary>Assigns the UI instance so orientation changes can be reflected.</summary>
    public void SetCameraUI(BasisHandHeldCameraUI ui) => cameraUI = ui;

    /// <summary>Desktop tweak to disable pickup’s internal update loop while in desktop mode.</summary>
    private void OnInteractDesktopTweak(BasisInput _input)
    {
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            // don’t poll pickup input update
            RequiresUpdateLoop = false;
        }
    }

    /// <summary>Rescales the camera when the local player’s avatar height changes.</summary>
    private void OnHeightChanged(BasisHeightDriver.HeightModeChange HeightModeChange)
    {
        // Must match the factor used when the prop is first sized, or this handler (which fires on every
        // avatar swap and scale change) permanently overwrites it with a different one. ScaledToMatchValue
        // is the authored->target ratio and is 1.0 for any avatar that was not auto-scaled, which left a
        // naturally-small avatar holding a full adult-sized camera.
        ApplyCameraScale();
    }

    /// <summary>
    /// Per-frame camera update (runs after player movement). Handles desktop head binding,
    /// initializes desktop constraint, and always updates pinning & fly movement where applicable.
    /// </summary>
    private void UpdateCamera()
    {
        if (this == null || HHC == null)
        {
            return;
        }

        bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
        CheckCameraOrientation();
        ApplyCameraScale();

        if (inDesktop)
        {
            if (Inputs.desktopCenterEye.Source == null) return;

            flyCamera.DetectInput();

            BasisCalibratedCoords Coords = Inputs.desktopCenterEye.BoneControl.OutgoingWorldData;
            Vector3 inPos = Coords.position;
            Quaternion inRot = Coords.rotation;

            if (BasisLocalCameraDriver.HasInstance)
            {
                PollDesktopControl(Inputs.desktopCenterEye.Source);

                if (!desktopSetup)
                {
                    // Camera constrains itself to initial spawn position until destroyed.
                    InteractableEnabled = false;

                    // compute initial offset in eye space
                    transform.GetPositionAndRotation(out Vector3 startPos, out Quaternion startRot);
                    desktopSpawnOffset = Quaternion.Inverse(inRot) * (startPos - inPos);
                    desktopOffsetRotation = Quaternion.Inverse(inRot) * startRot;

                    InputConstraint.Enabled = true;

                    desktopSetup = true;
                }

                // Pull it closer to the player's camera.
                InputConstraint.SetOffsetPositionAndRotation(0, desktopSpawnOffset * desktopEyeDistanceScale, desktopOffsetRotation);
            }
            else
            {
                return;
            }

            // always constrain to head movement
            InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);
            if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
            {
                transform.SetPositionAndRotation(pos, rot);
            }
        }
        else
        {
            // VR mode: handle fly mode toggle and controller input
            PollVRControl();
        }

        // Update pinning regardless of desktop/head-constraint logic
        PollCameraPin(Inputs.desktopCenterEye.Source);
    }
    public void SetSelfieRotationEnabled(bool enabled)
    {
        selfieRotationEnabled = enabled;
        handheldSmoothingInitialized = false;
    }
    /// <summary>Detects landscape vs portrait by camera roll and triggers UI orientation updates.</summary>
    private void CheckCameraOrientation()
    {
        if (Time.time < orientationCheckCooldown)
            return;

        if (HHC == null || HHC.captureCamera == null)
            return;

        Transform orientationSource =
            (HHC.HandHeld != null && HHC.HandHeld.uiOrientationReference != null)
                ? HHC.HandHeld.uiOrientationReference.transform
            : (HHC.HandHeld != null && HHC.HandHeld.cameraReference != null)
                ? HHC.HandHeld.cameraReference.transform
                : HHC.captureCamera.transform;

        Vector3 right = orientationSource.right;
        Vector3 up = orientationSource.up;

        BasisCameraOrientation newOrientation;

        if (Mathf.Abs(up.y) >= Mathf.Abs(right.y))
        {
            newOrientation = up.y >= 0f
                ? BasisCameraOrientation.Landscape
                : BasisCameraOrientation.LandscapeFlipped;
        }
        else
        {
            bool portraitCW = right.y >= 0f;

            newOrientation = portraitCW
                ? BasisCameraOrientation.PortraitCW
                : BasisCameraOrientation.PortraitCCW;
        }

        if (newOrientation != currentOrientation)
        {
            currentOrientation = newOrientation;
            orientationCheckCooldown = Time.time + 0.2f;
            HandleOrientationChanged(currentOrientation);
        }
    }

    /// <summary>Applies the new orientation to the UI and logs it.</summary>
    private void HandleOrientationChanged(BasisCameraOrientation newOrientation)
    {
        if (cameraUI != null)
        {
            cameraUI.SetUIOrientation(newOrientation);
        }
        BasisDebug.Log($"[Camera UI] Orientation changed to {newOrientation}");
    }

    /// <inheritdoc />
    public override bool IsInteractingWith(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
    }

    /// <inheritdoc />
    public override bool IsHoveredBy(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
    }

    /// <summary>
    /// Pins the capture camera to handheld/playspace/world and applies fly motion offsets
    /// through an internal parent-constraint.
    /// </summary>
    private void PollCameraPin(BasisInput DesktopEye)
    {
        if (HHC.captureCamera == null) return;

        switch (PinSpace)
        {
            case CameraPinSpace.HandHeld:
    if (previousPinState != CameraPinSpace.HandHeld)
    {
        cameraPinConstraint.Enabled = false;
        cameraPinConstraint.UpdateSourcePositionAndRotation(0, Vector3.zero, Quaternion.identity);
        cameraPinConstraint.SetOffsetPositionAndRotation(0, Vector3.zero, Quaternion.identity);
        handheldSmoothingInitialized = false;
    }

    UpdateVRHandheldSmoothing();
    previousPinState = PinSpace;
    return;

            case CameraPinSpace.PlaySpace:
                // Player root, not the avatar model — see MoveCameraAutoFollow. Pinning to the
                // avatar fed every IK correction into the constraint as shake, and the playspace
                // is the root by definition.
                BasisLocalPlayer.Instance.transform.GetPositionAndRotation(out Vector3 pinParentPos, out Quaternion pinParentRot);
                cameraPinConstraint.UpdateSourcePositionAndRotation(0, pinParentPos, pinParentRot);

                MoveCameraFlying();
                cameraPinConstraint.SetOffsetPositionAndRotation(0, smoothedPosition, smoothedRotation);

                if (previousPinState != CameraPinSpace.PlaySpace)
                {
                    cameraPinConstraint.Enabled = true;

                    HHC.captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
                    var offsetPos = Quaternion.Inverse(pinParentRot) * (camPos - pinParentPos);
                    var offsetRot = Quaternion.Inverse(pinParentRot) * camRot;
                    cameraPinConstraint.SetOffsetPositionAndRotation(0, offsetPos, offsetRot);
                }
                break;

            case CameraPinSpace.WorldSpace:
                cameraPinConstraint.UpdateSourcePositionAndRotation(0, Vector3.zero, Quaternion.identity);

                MoveCameraFlying();
                cameraPinConstraint.SetOffsetPositionAndRotation(0, smoothedPosition, smoothedRotation);

                if (previousPinState != CameraPinSpace.WorldSpace)
                {
                    cameraPinConstraint.Enabled = true;
                    HHC.captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
                    cameraPinConstraint.SetOffsetPositionAndRotation(0, camPos, camRot);
                }
                break;
        }

        if (cameraPinConstraint.Evaluate(out Vector3 pinPos, out Quaternion pinRot))
        {
            HHC.captureCamera.transform.SetPositionAndRotation(pinPos, pinRot);
        }

        previousPinState = PinSpace;
    }

    /// <summary>
    /// Destroys self on boot mode changes to avoid managing inputs/state across modes.
    /// </summary>
    public void OnBootModeChanged(string mode)
    {
        Destroy(gameObject);
    }

    /// <summary>
    /// Handles fly-mode toggling and desktop player lock/unlock cues based on mouse input.
    /// Middle click enters/exits fly mode; right mouse temporarily unlocks player controls.
    /// </summary>
    private void PollDesktopControl(BasisInput DesktopEye)
    {
        if (DesktopEye == null) return;
        bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
        if (!inDesktop) return;

        string className = nameof(BasisHandHeldCameraInteractable);

        bool isMiddleClick = DesktopEye.CurrentInputState.Secondary2DAxisClick;
        bool isRightClickHeld = Mouse.current != null && Mouse.current.rightButton.isPressed;

        // Enter/exit fly mode
        if (isMiddleClick && !pauseMove)
        {
            pauseMove = true;
            autoFollowEnabled = false;
            LookLock.Add(className);
            MovementLock.Add(className);
            CrouchingLock.Add(className);

            PinSpace = CameraPinSpace.WorldSpace;
            flyCamera.Enable();

            HHC.captureCamera.transform.GetPositionAndRotation(out smoothedPosition, out smoothedRotation);
        }
        else if (!isMiddleClick && pauseMove)
        {
            pauseMove = false;
            if (!LookLock.Remove(className)) BasisDebug.LogWarning($"{className} couldn't remove LookLock");
            if (!MovementLock.Remove(className)) BasisDebug.LogWarning($"{className} couldn't remove MovementLock");
            if (!CrouchingLock.Remove(className)) BasisDebug.LogWarning($"{className} couldn't remove CrouchingLock");

            flyCamera.Disable();
            velocityMomentum = Vector3.zero;
            rotationMomentum = 0f;
        }

        // Temporary manual unlock while holding RMB (when not flying)
        if (!pauseMove)
        {
            if (isRightClickHeld && !isPlayerManuallyUnlocked)
            {
                isPlayerManuallyUnlocked = true;
                UnlockPlayer(className);
            }
            else if (!isRightClickHeld && isPlayerManuallyUnlocked)
            {
                isPlayerManuallyUnlocked = false;
                if (inDesktop)
                    LockPlayer(className);
            }
        }
    }

    /// <summary>
    /// VR fly-mode control: toggles fly mode on thumbstick click (edge-detected)
    /// and captures controller rotation each frame for camera aiming.
    /// </summary>
    private void PollVRControl()
    {
        if (GetActiveVRInput(out BasisInputWrapper vrInput))
        {
            BasisInputState inputState = vrInput.Source.CurrentInputState;
            string className = nameof(BasisHandHeldCameraInteractable);

            // Toggle fly mode on thumbstick click (edge detection)
            bool thumbstickClick = inputState.Primary2DAxisClick;
            if (thumbstickClick && !vrThumbstickClickPrev)
            {
                if (isVRFlying)
                {
                    // Exit VR fly mode — return camera to hand
                    isVRFlying = false;
                    if (!LookLock.Remove(className)) BasisDebug.LogWarning($"{className} couldn't remove LookLock");
                    if (!MovementLock.Remove(className)) BasisDebug.LogWarning($"{className} couldn't remove MovementLock");
                    if (!CrouchingLock.Remove(className)) BasisDebug.LogWarning($"{className} couldn't remove CrouchingLock");

                    PinSpace = CameraPinSpace.HandHeld;
                    velocityMomentum = Vector3.zero;
                    rotationMomentum = 0f;
                }
                else
                {
                    // Enter VR fly mode
                    isVRFlying = true;
                    autoFollowEnabled = false;
                    LookLock.Add(className);
                    MovementLock.Add(className);
                    CrouchingLock.Add(className);

                    PinSpace = CameraPinSpace.WorldSpace;

                    HHC.captureCamera.transform.GetPositionAndRotation(out smoothedPosition, out smoothedRotation);

                    // Initialize rotation tracking from current camera orientation
                    Vector3 euler = smoothedRotation.eulerAngles;
                    currentPitch = targetPitch = NormalizeAngle(euler.x);
                    currentYaw = targetYaw = NormalizeAngle(euler.y);
                }
            }
            vrThumbstickClickPrev = thumbstickClick;

            if (isVRFlying)
            {
                // Store VR controller rotation for movement direction and camera aim
                vrControllerRotation = vrInput.BoneControl.OutgoingWorldData.rotation;
            }
        }
    }

    /// <summary>
    /// Retrieves the VR hand currently interacting with this object.
    /// </summary>
    private bool GetActiveVRInput(out BasisInputWrapper wrapper)
    {
        if (Inputs.leftHand.GetState() == BasisInteractInputState.Interacting)
        {
            wrapper = Inputs.leftHand;
            return true;
        }
        if (Inputs.rightHand.GetState() == BasisInteractInputState.Interacting)
        {
            wrapper = Inputs.rightHand;
            return true;
        }
        wrapper = default;
        return false;
    }

    /// <summary>Releases any player locks this interactable has taken.</summary>
    public void ReleasePlayerLocks()
    {
        string className = nameof(BasisHandHeldCameraInteractable);
        UnlockPlayer(className);
        isPlayerManuallyUnlocked = false;
    }

    /// <summary>Applies look/move locks to the player (desktop).</summary>
    private void LockPlayer(string className)
    {
        LookLock.Add(className);
        MovementLock.Add(className);
        // CrouchingLock.Add(className);
    }

    /// <summary>Removes look/move locks from the player (desktop).</summary>
    private void UnlockPlayer(string className)
    {
        LookLock.Remove(className);
        MovementLock.Remove(className);
        // CrouchingLock.Remove(className);
    }

    /// <summary>
    /// Fly camera step: handles input, acceleration/deceleration, momentum, auto-leveling,
    /// and computes smoothed position/rotation for the pin constraint offset.
    /// </summary>
    private void MoveCameraFlying()
    {
        float deltaTime = Time.deltaTime;

        // Selfie-stick grip: while the follow puck is held it is the master, so the camera snaps
        // to it. Releasing falls straight back through to auto-follow / fly below on the next frame.
        if (HHC != null && HHC.TryGetFollowPipPose(out Vector3 pipPos, out Quaternion pipRot))
        {
            smoothedPosition = pipPos;
            smoothedRotation = pipRot;
            return;
        }

        if (autoFollowEnabled)
        {
            MoveCameraAutoFollow(deltaTime);
            return;
        }

        if (HandleMovementInput(out Vector3 inputMovement, out float speedMultiplier))
        {
            UpdateMovement(inputMovement, speedMultiplier, deltaTime);
        }
        else if (useMomentum)
        {
            ApplyInertia(deltaTime);
        }
        else
        {
            currentVelocity = Vector3.zero;
            targetVelocity = Vector3.zero;
        }

        if (HandleRotationInput(out Vector2 rotationDelta))
        {
            UpdateRotation(rotationDelta, deltaTime);
        }

        if (useAutoLeveling)
        {
            ApplyAutoLeveling(deltaTime);
        }

        ApplySmoothedPosition(deltaTime);
    }

    /// <summary>Reads fly movement inputs and outputs a normalized movement vector + speed multiplier.</summary>
    private bool HandleMovementInput(out Vector3 movement, out float speedMultiplier)
    {
        movement = Vector3.zero;
        speedMultiplier = 1f;

        if (isVRFlying)
        {
            // VR path: read thumbstick from the interacting controller
            if (!GetActiveVRInput(out BasisInputWrapper vrInput))
                return false;

            BasisInputState state = vrInput.Source.CurrentInputState;
            Vector2 thumbstick = state.Primary2DAxisDeadZoned;

            // Thumbstick X = strafe, thumbstick Y = forward/back
            // Vertical movement comes from controller pitch (point up + push forward = fly up)
            movement = new Vector3(thumbstick.x, 0f, thumbstick.y);

            if (movement.magnitude < 0.01f)
                return false;

            if (movement.magnitude > 1f)
                movement.Normalize();

            // Grip = speed boost
            speedMultiplier = state.GripButton ? flyFastMultiplier : 1f;
            return true;
        }
        else
        {
            // Desktop path: read keyboard input
            var horizontalInput = flyCamera.horizontalMoveInput;
            var verticalInput = flyCamera.verticalMoveInput;
            var isFastMovement = flyCamera.isFastMovement;

            movement = new Vector3(horizontalInput.x, verticalInput, horizontalInput.y);

            if (movement.magnitude < 0.01f)
                return false;

            // prevent faster diagonal movement
            if (movement.magnitude > 1f)
                movement.Normalize();

            speedMultiplier = isFastMovement ? flyFastMultiplier : 1f;
            return true;
        }
    }

    /// <summary>Converts input to world velocity and applies acceleration and momentum.</summary>
    private void UpdateMovement(Vector3 inputMovement, float speedMultiplier, float deltaTime)
    {
        // In VR, move relative to controller orientation (point where you want to fly).
        // In desktop, move relative to the camera's current orientation.
        Quaternion orientationRef = isVRFlying ? vrControllerRotation : HHC.captureCamera.transform.rotation;
        Vector3 worldMovement = orientationRef * inputMovement;
        targetVelocity = worldMovement * flySpeed * speedMultiplier;
        currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, flyAcceleration * deltaTime);

        if (useMomentum)
        {
            velocityMomentum = Vector3.Lerp(velocityMomentum, currentVelocity * 0.1f, deltaTime * 2f);
        }
    }

    /// <summary>Applies exponential deceleration when no movement input is present.</summary>
    private void ApplyInertia(float deltaTime)
    {
        float decelerationFactor = Mathf.Pow(cinematicDamping, deltaTime * flyDeceleration);
        currentVelocity *= decelerationFactor;

        velocityMomentum = Vector3.Lerp(velocityMomentum, Vector3.zero, inertiaDamping * deltaTime);

        if (currentVelocity.magnitude < 0.01f)
        {
            currentVelocity = Vector3.zero;
            velocityMomentum = Vector3.zero;
        }
    }

    /// <summary>Reads fly rotation input (mouse delta) and outputs the delta if significant.</summary>
    private bool HandleRotationInput(out Vector2 rotationDelta)
    {
        rotationDelta = Vector2.zero;

        if (isVRFlying)
        {
            // VR: drive target rotation directly from controller orientation.
            // The actual rotation is applied in ApplySmoothedPosition (1:1 mapping).
            Vector3 euler = vrControllerRotation.eulerAngles;
            targetPitch = NormalizeAngle(euler.x);
            targetYaw = NormalizeAngle(euler.y);
            return false;
        }

        // Desktop: mouse delta
        var mouseInput = flyCamera.mouseInput;

        if (mouseInput.magnitude < 0.001f)
            return false;

        rotationDelta = mouseInput * mouseSensitivity;
        return true;
    }

    /// <summary>Updates target yaw/pitch from input and builds rotation momentum.</summary>
    private void UpdateRotation(Vector2 rotationDelta, float deltaTime)
    {
        targetYaw += rotationDelta.x;
        targetPitch -= rotationDelta.y;

        targetPitch = Mathf.Clamp(targetPitch, -90f, 90f);
        targetYaw = NormalizeAngle(targetYaw);

        float rotationSpeed = rotationDelta.magnitude;
        rotationMomentum = Mathf.Lerp(rotationMomentum, rotationSpeed * 0.1f, deltaTime * 5f);
    }

    /// <summary>Gradually levels pitch toward zero (eye level) when enabled.</summary>
    private void ApplyAutoLeveling(float deltaTime)
    {
        float targetLevelPitch = 0f;
        float pitchDifference = targetPitch - targetLevelPitch;

        if (Mathf.Abs(pitchDifference) > 5f)
        {
            float levelingForce = -pitchDifference * autoLevelStrength * deltaTime;
            targetPitch += levelingForce;
            targetPitch = Mathf.Clamp(targetPitch, -89.8f, 89.9f);
        }
    }

    /// <summary>
    /// Integrates velocity into <see cref="smoothedPosition"/> and applies smoothed rotation
    /// with momentum-influenced smoothing.
    /// </summary>
    private void ApplySmoothedPosition(float deltaTime)
    {
        Vector3 finalVelocity = currentVelocity + (useMomentum ? velocityMomentum : Vector3.zero);
        smoothedPosition += finalVelocity * deltaTime;

        if (isVRFlying)
        {
            // VR: 1:1 controller-to-camera rotation for responsive aiming
            currentPitch = targetPitch;
            currentYaw = targetYaw;
            smoothedRotation = vrControllerRotation;
        }
        else
        {
            // Desktop: smoothed rotation with momentum
            float enhancedRotationSmoothness = rotationSmoothing + rotationMomentum;

            currentPitch = Mathf.LerpAngle(currentPitch, targetPitch, enhancedRotationSmoothness * deltaTime);
            currentYaw = Mathf.LerpAngle(currentYaw, targetYaw, enhancedRotationSmoothness * deltaTime);

            Quaternion targetRotationQuat = Quaternion.Euler(currentPitch, currentYaw, 0f);
            smoothedRotation = Quaternion.Slerp(smoothedRotation, targetRotationQuat, rotationSmoothing * deltaTime);
        }
    }

    /// <summary>Normalizes an angle to the range [-180, 180].</summary>
    private float NormalizeAngle(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle < -180f) angle += 360f;
        return angle;
    }

    /// <summary>Clears all momentum/velocity state.</summary>
    public void ResetMomentum()
    {
        currentVelocity = Vector3.zero;
        targetVelocity = Vector3.zero;
        velocityMomentum = Vector3.zero;
        rotationMomentum = 0f;
    }
    private void UpdateVRHandheldSmoothing()
    {
        if (HHC == null || HHC.captureCamera == null)
            return;

        bool shouldSmooth =
            !BasisDeviceManagement.IsUserInDesktop() &&
            PinSpace == CameraPinSpace.HandHeld &&
            useVRHandheldSmoothing &&
            (!onlySmoothWhenStreamingToDesktop || HHC.enableRecordingView);

        Transform cameraTransform = HHC.captureCamera.transform;
        Transform cameraParent = cameraTransform.parent;

        if (cameraParent == null)
            return;

        Vector3 targetWorldPos = cameraParent.TransformPoint(cameraStartingLocalPos);

        Quaternion localTargetRot = cameraStartingLocalRot;

        if (selfieRotationEnabled)
        {
            localTargetRot *= Quaternion.Euler(0f, 180f, 0f);
        }

        Quaternion targetWorldRot = cameraParent.rotation * localTargetRot;

        if (useAutoLeveling)
        {
            Quaternion prevRot = cameraTransform.rotation;
            Vector3 prevEuler = prevRot.eulerAngles;

            float pitch = NormalizeAngle(prevEuler.x);
            float yaw = NormalizeAngle(targetWorldRot.eulerAngles.y);
            float roll = NormalizeAngle(prevEuler.z);

            pitch = Mathf.Lerp(pitch, 0f, autoLevelStrength * Time.deltaTime);
            roll = Mathf.Lerp(roll, 0f, autoLevelStrength * Time.deltaTime);

            targetWorldRot = Quaternion.Euler(pitch, yaw, roll);
        }

        if (!shouldSmooth)
        {
            handheldSmoothingInitialized = false;
            cameraTransform.SetPositionAndRotation(targetWorldPos, targetWorldRot);
            return;
        }

        if (!handheldSmoothingInitialized)
        {
            smoothedHandheldWorldPos = targetWorldPos;
            smoothedHandheldWorldRot = targetWorldRot;
            handheldSmoothingInitialized = true;
        }

        float dt = Time.deltaTime;
        smoothedHandheldWorldPos = Vector3.Lerp(
            smoothedHandheldWorldPos,
            targetWorldPos,
            vrHandheldPositionSmoothing * dt
        );
        smoothedHandheldWorldRot = Quaternion.Slerp(
            smoothedHandheldWorldRot,
            targetWorldRot,
            vrHandheldRotationSmoothing * dt
);

        cameraTransform.SetPositionAndRotation(smoothedHandheldWorldPos, targetWorldRot);
    }
    /// <summary>
    /// Unsubscribes events, releases locks, destroys highlight artifacts, shuts down fly camera,
    /// and then calls base destroy.
    /// </summary>
    public override void OnDestroy()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        OnInteractStartEvent.RemoveListener(OnInteractDesktopTweak);
        BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;

        BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(202, UpdateCamera);

        if (pauseMove || isVRFlying)
        {
            LookLock.Remove(nameof(BasisHandHeldCameraInteractable));
            MovementLock.Remove(nameof(BasisHandHeldCameraInteractable));
            CrouchingLock.Remove(nameof(BasisHandHeldCameraInteractable));
            isVRFlying = false;
        }
        if (HighlightClone != null)
        {
            Destroy(HighlightClone);
        }

        if (flyCamera != null)
        {
            flyCamera.OnDestroy();
        }

        BasisCursorManagement.LockCursor(nameof(BasisHandHeldCamera));
        base.OnDestroy();
    }
}
