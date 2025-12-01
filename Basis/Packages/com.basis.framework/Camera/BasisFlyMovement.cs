using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Desktop fly-camera input wrapper using the new Input System.
/// Provides mouse-look, WASD (horizontal), Space/Ctrl (vertical),
/// and a speed modifier (Shift). It does not move a camera directly;
/// callers read normalized inputs each frame and apply their own motion.
/// </summary>
[Serializable]
public class BasisFlyCamera
{
    // TODO: VR controls

    // --- Input Actions ---
    private InputActionMap flyingCameraActionMap;
    private InputAction mouseLookAction;
    private InputAction movementAction;          // 2D Vector composite (WASD)
    private InputAction verticalMovementAction;  // 1D Axis composite (Space/Ctrl)
    private InputAction speedModifierAction;     // Shift

    // --- Input Fields (read by owner each frame) ---
    /// <summary>Mouse delta (X,Y) in pixels per frame.</summary>
    public Vector2 mouseInput;

    /// <summary>Normalized horizontal move (x = left/right, y = forward/back) from WASD.</summary>
    public Vector2 horizontalMoveInput;

    /// <summary>Normalized vertical move (+up with Space, -down with LeftCtrl).</summary>
    public float verticalMoveInput;

    /// <summary>True while speed modifier is held (e.g., Left Shift).</summary>
    public bool isFastMovement;

    private bool isActive = false;
    private bool isInitialized = false;

    /// <summary>Whether RMB “control” is currently captured by DetectInput().</summary>
    private bool isControlling;

    /// <summary>
    /// Lazily creates the InputAction map and bindings. Safe to call multiple times.
    /// </summary>
    public void Initialize()
    {
        if (isInitialized)
            return;

        try
        {
            SetupInputActions();
            isInitialized = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize BasisFlyCamera: {e.Message}");
        }
    }

    /// <summary>
    /// Builds input bindings:
    /// - MouseLook: &lt;Mouse&gt;/delta
    /// - HorizontalMovement: WASD (2DVector)
    /// - VerticalMovement: Space/Ctrl (1DAxis)
    /// - SpeedModifier: LeftShift (Button)
    /// </summary>
    private void SetupInputActions()
    {
        // Create input action map
        flyingCameraActionMap = new InputActionMap("FlyingCamera");

        // Mouse look (Vector2 delta)
        mouseLookAction = flyingCameraActionMap.AddAction("MouseLook", InputActionType.Value, binding: "<Mouse>/delta");
        if (mouseLookAction != null)
        {
            mouseLookAction.performed += OnMouseLook;
            mouseLookAction.canceled += OnMouseLook;
        }

        // Horizontal movement (WASD)
        movementAction = flyingCameraActionMap.AddAction("HorizontalMovement", InputActionType.Value);
        movementAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        movementAction.performed += OnHorizontalMovement;
        movementAction.canceled += OnHorizontalMovement;

        // Vertical movement (Space/Ctrl)
        verticalMovementAction = flyingCameraActionMap.AddAction("VerticalMovement", InputActionType.Value);
        verticalMovementAction.AddCompositeBinding("1DAxis")
            .With("positive", "<Keyboard>/space")
            .With("negative", "<Keyboard>/leftCtrl");
        verticalMovementAction.performed += OnVerticalMovement;
        verticalMovementAction.canceled += OnVerticalMovement;

        // Speed modifier (Shift)
        speedModifierAction = flyingCameraActionMap.AddAction("SpeedModifier", InputActionType.Button);
        speedModifierAction.AddBinding("<Keyboard>/leftShift");
        speedModifierAction.performed += OnSpeedModifier;
        speedModifierAction.canceled += OnSpeedModifier;
    }

    /// <summary>
    /// Simple “capture control” helper: while RMB is held, lock the cursor
    /// and hide it (look mode). Releasing RMB frees the cursor and clears inputs.
    /// </summary>
    public void DetectInput()
    {
        bool rightClickHeld = Mouse.current?.rightButton?.isPressed == true;

        if (rightClickHeld && !isControlling)
        {
            isControlling = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (!rightClickHeld && isControlling)
        {
            isControlling = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Clear inputs when control stops
            mouseInput = Vector2.zero;
            horizontalMoveInput = Vector2.zero;
            verticalMoveInput = 0f;
            isFastMovement = false;
        }
    }

    /// <summary>
    /// Explicitly toggles whether this fly camera should read inputs.
    /// Enables/disables the individual actions and clears residual state on disable.
    /// </summary>
    public void SetControlState(bool controlling)
    {
        isControlling = controlling;

        if (isControlling)
        {
            mouseLookAction?.Enable();
            movementAction?.Enable();
            verticalMovementAction?.Enable();
            speedModifierAction?.Enable();
        }
        else
        {
            mouseLookAction?.Disable();
            movementAction?.Disable();
            verticalMovementAction?.Disable();
            speedModifierAction?.Disable();

            // clear any residual input
            mouseInput = Vector2.zero;
            horizontalMoveInput = Vector2.zero;
            verticalMoveInput = 0f;
            isFastMovement = false;
        }
    }

    /// <summary>
    /// Enables the fly camera input map (initializing if needed) and prepares cursor state.
    /// </summary>
    public void Enable()
    {
        if (!isInitialized)
        {
            Initialize();
        }

        if (!isInitialized)
        {
            BasisDebug.LogError("Basis Flycamera controls were unable to initialize");
        }

        isActive = true;

        // Enable action map
        flyingCameraActionMap?.Enable();

        // Start in “not controlling” (RMB will toggle via DetectInput)
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        isControlling = false;
    }

    /// <summary>
    /// Disables input and resets the exposed input fields to neutral values.
    /// </summary>
    public void Disable()
    {
        isActive = false;

        flyingCameraActionMap?.Disable();

        // Reset input values
        mouseInput = Vector2.zero;
        horizontalMoveInput = Vector2.zero;
        verticalMoveInput = 0f;
        isFastMovement = false;
    }

    // --- Input callbacks ---

    /// <summary>Reads mouse delta while active; zeroes on cancel.</summary>
    private void OnMouseLook(InputAction.CallbackContext context)
    {
        if (isActive && context.performed)
        {
            mouseInput = context.ReadValue<Vector2>();
        }
        else if (context.canceled)
        {
            mouseInput = Vector2.zero;
        }
    }

    /// <summary>Reads WASD 2D vector while active; zeroed by canceled event.</summary>
    private void OnHorizontalMovement(InputAction.CallbackContext context)
    {
        if (isActive)
            horizontalMoveInput = context.ReadValue<Vector2>();
    }

    /// <summary>Reads Space/Ctrl axis while active; zeroed by canceled event.</summary>
    private void OnVerticalMovement(InputAction.CallbackContext context)
    {
        if (isActive)
            verticalMoveInput = context.ReadValue<float>();
    }

    /// <summary>Sets the speed modifier flag while active (e.g., Left Shift).</summary>
    private void OnSpeedModifier(InputAction.CallbackContext context)
    {
        if (isActive)
            isFastMovement = context.performed;
    }

    /// <summary>
    /// Disables and disposes of the input action map. Call when the owner is destroyed.
    /// </summary>
    public void OnDestroy()
    {
        if (flyingCameraActionMap != null)
        {
            flyingCameraActionMap.Disable();
            flyingCameraActionMap.Dispose();
        }
    }
}
