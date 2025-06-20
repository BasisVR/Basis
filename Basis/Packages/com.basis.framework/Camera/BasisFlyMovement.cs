using System;
using UnityEngine;
using UnityEngine.InputSystem;

[Serializable]
public class BasisFlyCamera
{
    // TODO: VR controls

    // Input Actions
    private InputActionMap flyingCameraActionMap;
    private InputAction mouseLookAction;
    private InputAction movementAction; // 2D Vector composite (WASD)
    private InputAction verticalMovementAction; // 1D Axis composite (Space/Ctrl)
    private InputAction speedModifierAction;

    // Input fields
    public Vector2 mouseInput;
    public Vector2 horizontalMoveInput;
    public float verticalMoveInput;
    public bool isFastMovement;

    private bool isActive = false;
    private bool isInitialized = false;
    private bool isControlling;

    public void Initialize()
    {
        if (isInitialized)
            return;

        try
        {
            SetupInputActions();
            isInitialized = true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to initialize BasisFlyCamera: {e.Message}");
        }
    }

    private void SetupInputActions()
    {
        // Create input action map
        flyingCameraActionMap = new InputActionMap("FlyingCamera");;

        // Mouse look action (Vector2 composite for X/Y delta)
        mouseLookAction = flyingCameraActionMap.AddAction("MouseLook", InputActionType.Value, binding: "<Mouse>/delta");
        if (mouseLookAction != null)
        {
            mouseLookAction.performed += OnMouseLook;
            mouseLookAction.canceled += OnMouseLook;
        }
        // Horizontal movement action (2D Vector composite for WASD)
        movementAction = flyingCameraActionMap.AddAction("HorizontalMovement", InputActionType.Value);
        movementAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        movementAction.performed += OnHorizontalMovement;
        movementAction.canceled += OnHorizontalMovement;

        // Vertical movement action (1D Axis composite for Space/Ctrl)
        verticalMovementAction = flyingCameraActionMap.AddAction("VerticalMovement", InputActionType.Value);
        verticalMovementAction.AddCompositeBinding("1DAxis")
            .With("positive", "<Keyboard>/space")
            .With("negative", "<Keyboard>/leftCtrl");
        verticalMovementAction.performed += OnVerticalMovement;
        verticalMovementAction.canceled += OnVerticalMovement;

        // Speed modifier action (Button with multiple bindings)
        speedModifierAction = flyingCameraActionMap.AddAction("SpeedModifier", InputActionType.Button);
        speedModifierAction.AddBinding("<Keyboard>/leftShift");
        speedModifierAction.performed += OnSpeedModifier;
        speedModifierAction.canceled += OnSpeedModifier;
    }
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
        // Enable input actions
        flyingCameraActionMap?.Enable();

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        isControlling = false;
    }

    public void Disable()
    {
        isActive = false;

        // Disable input actions
        flyingCameraActionMap?.Disable();

        // Reset input values
        mouseInput = Vector2.zero;
        horizontalMoveInput = Vector2.zero;
        verticalMoveInput = 0f;
        isFastMovement = false;
    }

    // Input action callbacks
    private void OnMouseLook(InputAction.CallbackContext context)
    {
        if (isActive && context.performed)
        {
            mouseInput = context.ReadValue<Vector2>();
        }
        else if (context.canceled)
        {
            mouseInput = Vector2.zero; // Reset to zero when input stops
        }
    }

    private void OnHorizontalMovement(InputAction.CallbackContext context)
    {
        if (isActive)
            horizontalMoveInput = context.ReadValue<Vector2>();
    }

    private void OnVerticalMovement(InputAction.CallbackContext context)
    {
        if (isActive)
            verticalMoveInput = context.ReadValue<float>();
    }

    private void OnSpeedModifier(InputAction.CallbackContext context)
    {
        if (isActive)
            isFastMovement = context.performed;
    }

    public void OnDestroy()
    {
        // Disable and dispose of input actions
        if (flyingCameraActionMap != null)
        {
            flyingCameraActionMap.Disable();
            flyingCameraActionMap.Dispose();
        }
    }
}
