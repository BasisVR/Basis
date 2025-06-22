using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.InputSystem;
using Basis.Scripts.Device_Management;
using Basis.Scripts.BasisSdk.Players;

public abstract class BasisHandHeldCameraInteractable : PickupInteractable
{
    public BasisHandHeldCamera HHC;
    [Header("Camera Settings")]
    public CameraPinSpace PinSpace = CameraPinSpace.HandHeld;

    [Header("Flying Camera Settings")]
    public float flySpeed = 2f;
    public float flyFastMultiplier = 3f;
    public float flyAcceleration = 10f;
    public float flyDeceleration = 8f;
    public float flyMovementSmoothing = 12f;

    [Header("Camera Rotation")]
    public float mouseSensitivity = 0.5f;
    [Range(5f, 25f)]
    public float rotationSmoothing = 15f;

    [Header("Cinematic Controls")]
    public bool useMomentum = true;
    [Range(2f, 12f)]
    public float inertiaDamping = 5f;
    public bool useAutoLeveling = false;
    public float autoLevelStrength = 2f;
    [Range(0.1f, 0.9f)]
    public float cinematicDamping = 0.8f;

    // internal values
    private readonly BasisLocks.LockContext LookLock = BasisLocks.GetContext(BasisLocks.LookRotation);
    private readonly BasisLocks.LockContext MovementLock = BasisLocks.GetContext(BasisLocks.Movement);
    private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);
    private Vector3 cameraStartingLocalPos; // local space
    private Quaternion cameraStartingLocalRot; // local space

    [SerializeReference]
    private BasisParentConstraint cameraPinConstraint;
    [SerializeReference]
    private BasisFlyCamera flyCamera;

    const float cameraDefaultScale = 0.0003f;

    private bool isPlayerManuallyUnlocked = false;
    /// <summary>
    /// Space the camera is pinned to
    /// </summary>
    public enum CameraPinSpace
    {
        HandHeld,
        PlaySpace,
        WorldSpace,
    }

    // not a fan of doing new, but dont want to make a initialization framework to hook into - mriise
    public new void Start()
    {
        base.Start();
        // force rigid ref null, pickup will use raw transform instead 
        RigidRef = null;

        // "disable" desktop zoop for this
        DesktopZoopSpeed = 0;
        DesktopRotateSpeed = 0;

        CanSelfSteal = false;
        // CanNetworkSteal = false; // not networked anyway

        //Player Movement locking for UI Selection
        string className = nameof(BasisHandHeldCameraInteractable);
        LockPlayer(className);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;


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
            cameraStartingLocalPos = HHC.captureCamera.transform.localPosition;
            cameraStartingLocalRot = HHC.captureCamera.transform.localRotation;
        }

        OnInteractStartEvent += OnInteractDesktopTweak;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;

        BasisLocalPlayer.Instance.OnPlayersHeightChanged += OnHeightChanged;
        transform.localScale = new Vector3(cameraDefaultScale, cameraDefaultScale, cameraDefaultScale) * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;

        BasisLocalPlayer.Instance.AfterFinalMove.AddAction(202, UpdateCamera);

        cameraPinConstraint = new BasisParentConstraint();
        cameraPinConstraint.sources = new BasisParentConstraint.SourceData[] { new() { weight = 1f } };
        cameraPinConstraint.Enabled = false;

        flyCamera = new BasisFlyCamera();
    }

    private void OnInteractDesktopTweak(BasisInput _input)
    {
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            // dont poll pickup input update
            RequiresUpdateLoop = false;
        }
    }

    private void OnHeightChanged()
    {
            transform.localScale = new Vector3(cameraDefaultScale, cameraDefaultScale, cameraDefaultScale) * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
    }

    private bool desktopSetup = false;
    private CameraPinSpace previousPinState = CameraPinSpace.HandHeld;
    private void UpdateCamera()
    {

        bool inDesktop = BasisDeviceManagement.IsUserInDesktop();

        if (inDesktop)
        {
            if (Inputs.desktopCenterEye.Source == null) return;
            flyCamera.DetectInput();

            Vector3 inPos;
            Quaternion inRot;

            inPos = Inputs.desktopCenterEye.BoneControl.OutgoingWorldData.position;
            inRot = Inputs.desktopCenterEye.BoneControl.OutgoingWorldData.rotation;

            if (BasisLocalCameraDriver.Instance != null && BasisLocalCameraDriver.Instance.Camera != null)
            {
                PollDesktopControl(Inputs.desktopCenterEye.Source);

                if (!desktopSetup)
                {
                    // do not remove, important!!!
                    // on desktop the camera contrains itself to the initial spawn position until destroyed.
                    // does not reset since we force destroy on boot mode change.
                    InteractableEnabled = false;

                    // offset
                    transform.GetPositionAndRotation(out Vector3 startPos, out Quaternion startRot);
                    var offsetPos = Quaternion.Inverse(inRot) * (startPos - inPos);
                    var offsetRot = Quaternion.Inverse(inRot) * startRot;
                    InputConstraint.SetOffsetPositionAndRotation(0, offsetPos, offsetRot);
                    InputConstraint.Enabled = true;

                    desktopSetup = true;
                }
            }
            else return;
    
            // always constrain to head movement
            InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);
            
            if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
            {
                transform.SetPositionAndRotation(pos, rot);
            }
        }

        // 
        PollCameraPin(Inputs.desktopCenterEye.Source);
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

    private void PollCameraPin(BasisInput DesktopEye)
    {

        // --- camera pinning --- 
        if (HHC.captureCamera == null) return;
        

        switch (PinSpace)
        {
            // handheld is a child of the pickup, setup local transform and let unity handle things
            case CameraPinSpace.HandHeld:
                if (previousPinState != CameraPinSpace.HandHeld)
                {
                    cameraPinConstraint.Enabled = false;
                    // zero out source
                    cameraPinConstraint.UpdateSourcePositionAndRotation(0, Vector3.zero, Quaternion.identity);
                    cameraPinConstraint.SetOffsetPositionAndRotation(0, Vector3.zero, Quaternion.identity);

                    HHC.captureCamera.transform.localPosition = cameraStartingLocalPos;
                    HHC.captureCamera.transform.localRotation = cameraStartingLocalRot;
                }
                break;
            case CameraPinSpace.PlaySpace:
                BasisLocalPlayer.Instance.BasisAvatarTransform.GetPositionAndRotation(out Vector3 pinParentPos, out Quaternion pinParentRot);
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
                // world offset is zero/identity
                cameraPinConstraint.UpdateSourcePositionAndRotation(0, Vector3.zero, Quaternion.identity);

                MoveCameraFlying();
                cameraPinConstraint.SetOffsetPositionAndRotation(0, smoothedPosition, smoothedRotation);
                    
                if (previousPinState != CameraPinSpace.WorldSpace)
                {
                    cameraPinConstraint.Enabled = true;

                    HHC.captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
                    // use current world pos
                    cameraPinConstraint.SetOffsetPositionAndRotation(0, camPos, camRot);
                }
                break;
            default:
                break;
        }

        // update pin constraint
        if (cameraPinConstraint.Evaluate(out Vector3 pinPos, out Quaternion pinRot))
        {
            HHC.captureCamera.transform.SetPositionAndRotation(pinPos, pinRot);
        }

        previousPinState = PinSpace;
    }

    public void OnBootModeChanged(string mode)
    {
        // To not manage things across boot mode changes (inputs actions, ect) destroy self.
        // User can respawn camera if they want it
        Destroy(gameObject);
    }


    private Vector3 currentVelocity = Vector3.zero;
    private Vector3 targetVelocity = Vector3.zero;
    private Vector3 velocityMomentum = Vector3.zero;
    private float rotationMomentum = 0f;
    
    private float currentPitch = 0f;
    private float currentYaw = 0f;
    private float targetPitch = 0f;
    private float targetYaw = 0f;
    
    private Vector3 smoothedPosition = Vector3.zero;
    private Quaternion smoothedRotation = Quaternion.identity;
    


    private bool pauseMove = false;
    private void PollDesktopControl(BasisInput DesktopEye)
    {
        if (DesktopEye == null) return;
        string className = nameof(BasisHandHeldCameraInteractable);

        bool isMiddleClick = DesktopEye.CurrentInputState.Secondary2DAxisClick;
        bool isRightClickHeld = Mouse.current != null && Mouse.current.rightButton.isPressed;

        // Entering Fly Mode (middle-click)
        if (isMiddleClick && !pauseMove)
        {
            pauseMove = true;
            LookLock.Add(nameof(BasisHandHeldCameraInteractable));
            MovementLock.Add(nameof(BasisHandHeldCameraInteractable));
            CrouchingLock.Add(nameof(BasisHandHeldCameraInteractable));

            PinSpace = CameraPinSpace.WorldSpace;
            flyCamera.Enable();

            smoothedRotation = HHC.captureCamera.transform.rotation;
            smoothedPosition = HHC.captureCamera.transform.position;
        }
        else if (!isMiddleClick && pauseMove)
        {
            pauseMove = false;
            if (!LookLock.Remove(className))
                BasisDebug.LogWarning($"{className} couldn't remove LookLock");
            if (!MovementLock.Remove(className))
                BasisDebug.LogWarning($"{className} couldn't remove MovementLock");
            if (!CrouchingLock.Remove(className))
                BasisDebug.LogWarning($"{className} couldn't remove CrouchingLock");

            flyCamera.Disable();
            velocityMomentum = Vector3.zero;
            rotationMomentum = 0f;
        }

        if (!pauseMove) // only do this when NOT flying
        {
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
                    LockPlayer(className);
                }
            }
        }
    }
    public void ReleasePlayerLocks()
    {
        string className = nameof(BasisHandHeldCameraInteractable);
        UnlockPlayer(className);
        isPlayerManuallyUnlocked = false;
    }
    private void LockPlayer(string className)
    {
        LookLock.Add(className);
        MovementLock.Add(className);
        CrouchingLock.Add(className);
    }
    private void UnlockPlayer(string className)
    {
        LookLock.Remove(className);
        MovementLock.Remove(className);
        CrouchingLock.Remove(className);
    }


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
            // Stop immediately if inertia disabled
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

    private bool HandleMovementInput(out Vector3 movement, out float speedMultiplier)
    {
        movement = Vector3.zero;
        speedMultiplier = 1f;
        
        var horizontalInput = flyCamera.horizontalMoveInput;
        var verticalInput = flyCamera.verticalMoveInput;
        var isFastMovement = flyCamera.isFastMovement;
        
        movement = new Vector3(horizontalInput.x, verticalInput, horizontalInput.y);
        
        if (movement.magnitude < 0.01f)
            return false;
            
        // Normalize to prevent faster diagonal movement
        if (movement.magnitude > 1f)
            movement.Normalize();
            
        speedMultiplier = isFastMovement ? flyFastMultiplier : 1f;
        return true;
    }
        
    private void UpdateMovement(Vector3 inputMovement, float speedMultiplier, float deltaTime)
    {
        // Transform movement to camera space
        Vector3 worldMovement = HHC.captureCamera.transform.TransformDirection(inputMovement);
                
        targetVelocity = worldMovement * flySpeed * speedMultiplier;
        
        currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, flyAcceleration * deltaTime);
        
        // Add momentum back
        if (useMomentum)
        {
            velocityMomentum = Vector3.Lerp(velocityMomentum, currentVelocity * 0.1f, deltaTime * 2f);
        }
    }
    private void ApplyInertia(float deltaTime)
    {
        // deceleration with exponential falloff
        float decelerationFactor = Mathf.Pow(cinematicDamping, deltaTime * flyDeceleration);
        currentVelocity *= decelerationFactor;
        
        velocityMomentum = Vector3.Lerp(velocityMomentum, Vector3.zero, inertiaDamping * deltaTime);
        
        if (currentVelocity.magnitude < 0.01f)
        {
            currentVelocity = Vector3.zero;
            velocityMomentum = Vector3.zero;
        }
    }
    
    private bool HandleRotationInput(out Vector2 rotationDelta)
    {
        rotationDelta = Vector2.zero;
        var mouseInput = flyCamera.mouseInput;
        
        if (mouseInput.magnitude < 0.001f)
            return false;
            
        rotationDelta = mouseInput * mouseSensitivity;
        return true;
    }
    
    private void UpdateRotation(Vector2 rotationDelta, float deltaTime)
    {
        targetYaw += rotationDelta.x;
        targetPitch -= rotationDelta.y;
        
        // Clamp pitch to prevent over-rotation
        targetPitch = Mathf.Clamp(targetPitch, -90f, 90f);
        
        targetYaw = NormalizeAngle(targetYaw);
        
        float rotationSpeed = rotationDelta.magnitude;
        rotationMomentum = Mathf.Lerp(rotationMomentum, rotationSpeed * 0.1f, deltaTime * 5f);
    }
    
    private void ApplyAutoLeveling(float deltaTime)
    {
        // Gradually level the pitch to bring camera back to eye level (0 degrees)
        float targetLevelPitch = 0f; // Eye level
        float pitchDifference = targetPitch - targetLevelPitch;
        
        // Only apply leveling if we're looking significantly up or down
        if (Mathf.Abs(pitchDifference) > 5f) // 5 degree dead zone
        {
            float levelingForce = -pitchDifference * autoLevelStrength * deltaTime;
            targetPitch += levelingForce;
            
            // Keep within bounds
            targetPitch = Mathf.Clamp(targetPitch, -89.8f, 89.9f);
        }
    }

    private void ApplySmoothedPosition(float deltaTime)
    {
        // Add momentum to movement
        Vector3 finalVelocity = currentVelocity + (useMomentum ? velocityMomentum : Vector3.zero);
        smoothedPosition += finalVelocity * deltaTime;

        // Enhanced rotation smoothing with momentum influence
        float enhancedRotationSmoothness = rotationSmoothing + rotationMomentum;
        
        // Smooth rotation interpolation
        currentPitch = Mathf.LerpAngle(currentPitch, targetPitch, enhancedRotationSmoothness * deltaTime);
        currentYaw = Mathf.LerpAngle(currentYaw, targetYaw, enhancedRotationSmoothness * deltaTime);
        
        // Create final rotation
        Quaternion targetRotationQuat = Quaternion.Euler(currentPitch, currentYaw, 0f);
        
        // Additional smoothing for ultra-cinematic feel
        smoothedRotation = Quaternion.Slerp(smoothedRotation, targetRotationQuat, rotationSmoothing * deltaTime);
    }

    // Utility function to normalize angles to [-180, 180] range
    private float NormalizeAngle(float angle)
    {
        while (angle > 180f)
            angle -= 360f;
        while (angle < -180f)
            angle += 360f;
        return angle;
    }
    
    public void ResetMomentum()
    {
        currentVelocity = Vector3.zero;
        targetVelocity = Vector3.zero;
        velocityMomentum = Vector3.zero;
        rotationMomentum = 0f;
    }
    

    public override void StartRemoteControl()
    {
    }
    public override void StopRemoteControl()
    {
    }

    public override void OnDestroy()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        OnInteractStartEvent -= OnInteractDesktopTweak;
        BasisLocalPlayer.Instance.OnPlayersHeightChanged -= OnHeightChanged;

        BasisLocalPlayer.Instance.AfterFinalMove.RemoveAction(202, UpdateCamera);


        if (pauseMove)
        {
            LookLock.Remove(nameof(BasisHandHeldCameraInteractable));
            MovementLock.Remove(nameof(BasisHandHeldCameraInteractable));
            CrouchingLock.Remove(nameof(BasisHandHeldCameraInteractable));
        }

        Destroy(HighlightClone);

        if (asyncOperationHighlightMat.IsValid())
        {
            asyncOperationHighlightMat.Release();
        }

        if (flyCamera != null)
        {
            flyCamera.OnDestroy();
        }
        base.OnDestroy();
    }
}
