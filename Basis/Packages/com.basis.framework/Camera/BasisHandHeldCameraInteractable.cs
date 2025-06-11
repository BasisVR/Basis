using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using UnityEngine;
using Basis.Scripts.Device_Management;
using Basis.Scripts.BasisSdk.Players;

public abstract class BasisHandHeldCameraInteractable : PickupInteractable
{
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
    public bool useAdaptiveSmoothing = true;

    [Header("Capture Camera Reference")]
    [SerializeField] internal Camera captureCamera;

    // internal values
    private string pauseRequestName;
    private Vector3 cameraStartingLocalPos; // local space
    private Quaternion cameraStartingLocalRot; // local space

    [SerializeReference]
    private BasisParentConstraint cameraPinConstraint;
    [SerializeReference]
    private BasisFlyCamera flyCamera;

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

        pauseRequestName = $"{nameof(BasisHandHeldCameraInteractable)}-{gameObject.GetInstanceID()}";

        // "disable" desktop zoop for this
        DesktopZoopSpeed = 0;
        DesktopRotateSpeed = 0;

        CanSelfSteal = false;
        CanNetworkSteal = false; // not networked anyway

        if (captureCamera == null)
        {
            captureCamera = gameObject.GetComponentInChildren<Camera>(true);
        }
        if (captureCamera == null)
        {
            BasisDebug.LogError($"Camera not found in children of {nameof(BasisHandHeldCamera)}, camera pinning will be broken");
        }
        else
        {
            cameraStartingLocalPos = captureCamera.transform.localPosition;
            cameraStartingLocalRot = captureCamera.transform.localRotation;
        }

        OnInteractStartEvent += OnInteractCameraTweak;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        BasisLocalPlayer.Instance.AfterFinalMove.AddAction(202, UpdateCamera);

        cameraPinConstraint = new BasisParentConstraint();
        cameraPinConstraint.sources = new BasisParentConstraint.SourceData[] { new() { weight = 1f } };
        cameraPinConstraint.Enabled = false;

        flyCamera = new BasisFlyCamera();
    }


    private void OnInteractCameraTweak(BasisInput _input)
    {
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            // poll manually only
            RequiresUpdateLoop = false;
        }
    }

    private bool desktopSetup = false;
    private CameraPinSpace previousPinState = CameraPinSpace.HandHeld;
    private void UpdateCamera()
    {

        bool inDesktop = BasisDeviceManagement.IsUserInDesktop();

        if (inDesktop)
        {
            if (Inputs.desktopCenterEye.Source == null) return;
            
            Vector3 inPos;
            Quaternion inRot;

            inPos = Inputs.desktopCenterEye.BoneControl.OutgoingWorldData.position;
            inRot = Inputs.desktopCenterEye.BoneControl.OutgoingWorldData.rotation;

            if (BasisLocalCameraDriver.Instance != null && BasisLocalCameraDriver.Instance.Camera != null)
            {
                PollDesktopControl(Inputs.desktopCenterEye.Source);

                if (!desktopSetup)
                {
                    // important!!!
                    // not reset since we force destroy on boot mode change
                    DisableInfluence = true;

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

            InputConstraint.UpdateSourcePositionAndRotation(0, inPos, inRot);

            if (InputConstraint.Evaluate(out Vector3 pos, out Quaternion rot))
            {
                transform.SetPositionAndRotation(pos, rot);
            }
        }

        // --- camera pinning --- 
        if (captureCamera == null) return;
        

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

                    captureCamera.transform.localPosition = cameraStartingLocalPos;
                    captureCamera.transform.localRotation = cameraStartingLocalRot;
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

                    captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);

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

                    captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
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
            captureCamera.transform.SetPositionAndRotation(pinPos, pinRot);
        }

        previousPinState = PinSpace;
    }

    public void OnBootModeChanged(string mode)
    {
        // To not manage things across boot mode changes (inputs actions, ect) destroy self.
        // User can respawn camera if they want it
        Destroy(this);
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

        if (DesktopEye.InputState.Secondary2DAxisClick)
        {
            // set pause requests
            if (!pauseMove)
            {
                pauseMove = true;
                BasisAvatarEyeInput.Instance.PauseHead(pauseRequestName);
                BasisLocalInputActions.PauseCrouch(pauseRequestName);
                BasisLocalInputActions.PauseMovement(pauseRequestName);

                PinSpace = CameraPinSpace.WorldSpace;
                flyCamera.Enable();

                smoothedRotation = captureCamera.transform.rotation;
                smoothedPosition = captureCamera.transform.position;
            }
        }
        else if (pauseMove) // clean up requests
        {
            pauseMove = false;
            if (!BasisAvatarEyeInput.Instance.UnPauseHead(pauseRequestName))
            {
                BasisDebug.LogWarning(nameof(BasisHandHeldCamera) + " was unable to un-pause head movement, this is a bug!");
            }
            if (!BasisLocalInputActions.UnPauseCrouch(pauseRequestName))
            {
                BasisDebug.LogWarning(nameof(BasisHandHeldCamera) + " was unable to un-pause crouch, this is a bug!");
            }
            if (!BasisLocalInputActions.UnPauseMovement(pauseRequestName))
            {
                BasisDebug.LogWarning(nameof(BasisHandHeldCamera) + " was unable to un-pause movement, this is a bug!");
            }
            flyCamera.Disable();
            velocityMomentum = Vector3.zero;
            rotationMomentum = 0f;
        }
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
        Vector3 worldMovement = captureCamera.transform.TransformDirection(inputMovement);
                
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
        OnInteractStartEvent -= OnInteractCameraTweak;
        BasisLocalPlayer.Instance.AfterFinalMove.RemoveAction(202, UpdateCamera);


        if (pauseMove)
        {
            BasisAvatarEyeInput.Instance.UnPauseHead(pauseRequestName);
            BasisLocalInputActions.UnPauseCrouch(pauseRequestName);
            BasisLocalInputActions.UnPauseMovement(pauseRequestName);
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
