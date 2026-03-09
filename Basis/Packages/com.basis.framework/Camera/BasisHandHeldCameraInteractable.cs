using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.InputSystem;
using Basis.Scripts.Device_Management;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.BasisSdk.Interactions;

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

    private const float cameraDefaultScale = 0.0003f;

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
        transform.localScale = Vector3.one * cameraDefaultScale * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

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
        transform.localScale = new Vector3(cameraDefaultScale, cameraDefaultScale, cameraDefaultScale) *  BasisHeightDriver.ScaledToMatchValue;
    }

    /// <summary>
    /// Per-frame camera update (runs after player movement). Handles desktop head binding,
    /// initializes desktop constraint, and always updates pinning & fly movement where applicable.
    /// </summary>
    private void UpdateCamera()
    {
        bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
        CheckCameraOrientation();

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
                    var offsetPos = Quaternion.Inverse(inRot) * (startPos - inPos);
                    var offsetRot = Quaternion.Inverse(inRot) * startRot;
                    InputConstraint.SetOffsetPositionAndRotation(0, offsetPos, offsetRot);
                    InputConstraint.Enabled = true;

                    desktopSetup = true;
                }
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

    /// <summary>Detects landscape vs portrait by camera roll and triggers UI orientation updates.</summary>
    private void CheckCameraOrientation()
    {
        if (Time.time < orientationCheckCooldown)
            return;

        if (HHC != null && HHC.captureCamera != null)
        {
            float roll = HHC.captureCamera.transform.eulerAngles.z;
            if (roll > 180f) roll -= 360f; // normalize to [-180, 180]

            // Snap to the nearest 90° step: -180, -90, 0, +90, +180
            int step = Mathf.RoundToInt(roll / 90f);
            step = Mathf.Clamp(step, -2, 2);

            BasisCameraOrientation newOrientation;
            switch (step)
            {
                case -2:
                case 2:
                    newOrientation = BasisCameraOrientation.LandscapeFlipped; // upside-down
                    break;
                case 1:
                    newOrientation = BasisCameraOrientation.PortraitCW;        // one portrait side
                    break;
                case -1:
                    newOrientation = BasisCameraOrientation.PortraitCCW;       // opposite portrait side
                    break;
                default:
                    newOrientation = BasisCameraOrientation.Landscape;
                    break;
            }

            if (newOrientation != currentOrientation)
            {
                currentOrientation = newOrientation;
                orientationCheckCooldown = Time.time + 0.5f;
                HandleOrientationChanged(currentOrientation);
            }
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
                    HHC.captureCamera.transform.SetLocalPositionAndRotation(cameraStartingLocalPos, cameraStartingLocalRot);
                }
                break;

            case CameraPinSpace.PlaySpace:
                BasisLocalPlayer.Instance.AvatarTransform.GetPositionAndRotation(out Vector3 pinParentPos, out Quaternion pinParentRot);
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
        if (asyncOperationHighlightMat.IsValid())
        {
            asyncOperationHighlightMat.Release();
        }

        if (flyCamera != null)
        {
            flyCamera.OnDestroy();
        }

        BasisCursorManagement.LockCursor(nameof(BasisHandHeldCamera));
        base.OnDestroy();
    }
}
