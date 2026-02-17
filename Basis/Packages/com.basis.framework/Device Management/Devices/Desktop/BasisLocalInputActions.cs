using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.Common;
using Basis.BasisUI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Interactions;
using Basis.Scripts.UI;
using UnityEngine.InputSystem.Users;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    /// <summary>
    /// Handles all local input actions for desktop devices.
    /// Provides movement, look, jump, crouch, run, UI, and device switching functionality
    /// by wiring up Unity Input System <see cref="InputAction"/> events to the <see cref="BasisLocalPlayer"/>
    /// and <see cref="BasisLocalCharacterDriver"/>.
    /// </summary>
    [DefaultExecutionOrder(15003)]
    public class BasisLocalInputActions : MonoBehaviour
    {
        /// <summary>Singleton reference for global access.</summary>
        public static BasisLocalInputActions Instance;

        #region Input Actions

        [Header("Core Actions")]
        public InputActionReference MoveAction;
        public InputActionReference LookAction;
        public InputActionReference JumpAction;
        public InputActionReference CrouchAction;
        public InputActionReference RunButton;
        public InputActionReference Escape;
        public InputActionReference PrimaryButtonGetState;
        public InputActionReference PointerAction;

        [Header("Mode Switching")]
        public InputActionReference DesktopSwitch;
        public InputActionReference VRSwitch;
        public InputActionReference XRSwitch;

        [Header("Mouse")]
        public InputActionReference LeftMousePressed;
        public InputActionReference RightMousePressed;
        public InputActionReference MiddleMouseScroll;
        public InputActionReference MiddleMouseScrollClick;

        public InputActionReference MoveLocalUpDown;
        #endregion

        [Header("Sensitivity Settings")]
        public float MouseSensitivity = 1f;
        public float JoystickSensitivity = 1f;
        public float KeyboardSensitivity = 5f;

        #region References

        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;
        [System.NonSerialized] public BasisLocalCharacterDriver LocalCharacterDriver;
        [System.NonSerialized] public BasisDesktopEye DesktopEyeInput;

        public PlayerInput Input;

        [SerializeField] public BasisInputState InputState = new BasisInputState();

        #endregion

        private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);
        /// <summary>Whether crouch is currently held down.</summary>
        public bool IsJumpHeld { get; private set; }

        /// <summary>Whether crouch is currently held down.</summary>
        public bool IsCrouchHeld { get; private set; }

        /// <summary>Whether run is currently held down.</summary>
        public bool IsRunHeld { get; private set; }

        private Vector2 manualMoveVector = Vector2.zero;

        private const float deltaCoefficient = 0.1f;

        #region Unity Lifecycle

        public void OnEnable()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
            InputSystem.settings.SetInternalFeatureFlag("USE_OPTIMIZED_CONTROLS", true);
            InputSystem.settings.SetInternalFeatureFlag("USE_READ_VALUE_CACHING", true);
            BasisLocalCameraDriver.InstanceExists += SetupCamera;
            // Create user (or you may already have one from PlayerInput, etc.)
            var user = InputUser.CreateUserWithoutPairedDevices();

            foreach (var device in InputSystem.devices)
            {
                if (device is Keyboard || device is Mouse || device is Gamepad || device is Pointer)
                {
                    BasisDebug.Log($"Giving access to {device.displayName}", BasisDebug.LogTag.Input);
                    InputUser.PerformPairingWithDevice(device, user);
                }
            }

            if (BasisDeviceManagement.IsCurrentModeVR() && BasisDeviceManagement.IsMobileHardware())
            {

            }
            else
            {
                HasCallbacksAndActions = true;
                EnableActions();
                AddCallbacks();
            }
        }
        public static bool HasCallbacksAndActions = false;
        public void OnDisable()
        {
            BasisLocalCameraDriver.InstanceExists -= SetupCamera;

            if (HasCallbacksAndActions)
            {
                RemoveCallbacks();
                DisableActions();
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Sets up the input system camera reference when <see cref="BasisLocalCameraDriver"/> exists.
        /// </summary>
        public void SetupCamera()
        {
            Input.camera = BasisLocalCameraDriver.Instance.Camera;
        }

        /// <summary>
        /// Initializes this input handler for the specified local player.
        /// </summary>
        /// <param name="localPlayer">The local player instance.</param>
        public void Initialize(BasisLocalPlayer localPlayer)
        {
            LocalPlayer = localPlayer;
            LocalCharacterDriver = localPlayer.LocalCharacterDriver;
            this.gameObject.SetActive(true);
        }

        #endregion

        #region Input Action Management

        private void EnableActions()
        {
            PointerAction.action.Enable();
            DesktopSwitch.action.Enable();
            XRSwitch.action.Enable();
            VRSwitch.action.Enable();
            MoveAction.action.Enable();
            LookAction.action.Enable();
            JumpAction.action.Enable();
            CrouchAction.action.Enable();
            RunButton.action.Enable();
            Escape.action.Enable();
            PrimaryButtonGetState.action.Enable();
            LeftMousePressed.action.Enable();
            RightMousePressed.action.Enable();
            MiddleMouseScroll.action.Enable();
            MiddleMouseScrollClick.action.Enable();
            MoveLocalUpDown.action.Enable();
        }

        private void DisableActions()
        {
            PointerAction.action.Disable();
            DesktopSwitch.action.Disable();
            XRSwitch.action.Disable();
            VRSwitch.action.Disable();
            MoveAction.action.Disable();
            LookAction.action.Disable();
            JumpAction.action.Disable();
            CrouchAction.action.Disable();
            RunButton.action.Disable();
            Escape.action.Disable();
            PrimaryButtonGetState.action.Disable();
            LeftMousePressed.action.Disable();
            RightMousePressed.action.Disable();
            MiddleMouseScroll.action.Disable();
            MiddleMouseScrollClick.action.Disable();
            MoveLocalUpDown.action.Disable();
        }

        private void AddCallbacks()
        {
            // Register all performed/canceled handlers
            PointerAction.action.performed += OnPointerPerformed;
            PointerAction.action.canceled += OnPointerCancelled;

            CrouchAction.action.performed += OnCrouchPerformed;
            CrouchAction.action.canceled += OnCrouchCancelled;

            MoveAction.action.performed += OnMoveActionPerformed;
            MoveAction.action.canceled += OnMoveActionCancelled;

            LookAction.action.performed += OnLookActionPerformed;
            LookAction.action.canceled += OnLookActionCancelled;

            JumpAction.action.performed += OnJumpActionPerformed;
            JumpAction.action.canceled += OnJumpActionCancelled;

            RunButton.action.performed += OnRunStarted;
            RunButton.action.canceled += OnRunCancelled;

            Escape.action.performed += OnEscapePerformed;
            Escape.action.canceled += OnEscapeCancelled;

            PrimaryButtonGetState.action.performed += OnPrimaryGet;
            PrimaryButtonGetState.action.canceled += OnCancelPrimaryGet;

            LeftMousePressed.action.performed += OnLeftMouse;
            LeftMousePressed.action.canceled += OnLeftMouse;

            RightMousePressed.action.performed += OnRightMouse;
            RightMousePressed.action.canceled += OnRightMouse;

            MiddleMouseScroll.action.performed += OnMouseScroll;
            MiddleMouseScroll.action.canceled += OnMouseScroll;

            MiddleMouseScrollClick.action.performed += OnMouseScrollClick;
            MiddleMouseScrollClick.action.canceled += OnMouseScrollClick;

            DesktopSwitch.action.performed += OnSwitchDesktop;
            DesktopSwitch.action.canceled += OnSwitchDesktop;

            VRSwitch.action.performed += OnSwitchOpenVR;
            XRSwitch.action.performed += OnSwitchOpenXR;
        }

        private void RemoveCallbacks()
        {
            // Unregister all callbacks
            PointerAction.action.performed -= OnPointerPerformed;
            PointerAction.action.canceled -= OnPointerCancelled;

            CrouchAction.action.performed -= OnCrouchPerformed;
            CrouchAction.action.canceled -= OnCrouchCancelled;

            MoveAction.action.performed -= OnMoveActionPerformed;
            MoveAction.action.canceled -= OnMoveActionCancelled;

            LookAction.action.performed -= OnLookActionPerformed;
            LookAction.action.canceled -= OnLookActionCancelled;

            JumpAction.action.performed -= OnJumpActionPerformed;
            JumpAction.action.canceled -= OnJumpActionCancelled;

            RunButton.action.performed -= OnRunStarted;
            RunButton.action.canceled -= OnRunCancelled;

            Escape.action.performed -= OnEscapePerformed;
            Escape.action.canceled -= OnEscapeCancelled;

            PrimaryButtonGetState.action.performed -= OnPrimaryGet;
            PrimaryButtonGetState.action.canceled -= OnCancelPrimaryGet;

            LeftMousePressed.action.performed -= OnLeftMouse;
            LeftMousePressed.action.canceled -= OnLeftMouse;

            RightMousePressed.action.performed -= OnRightMouse;
            RightMousePressed.action.canceled -= OnRightMouse;

            MiddleMouseScroll.action.performed -= OnMouseScroll;
            MiddleMouseScroll.action.canceled -= OnMouseScroll;

            MiddleMouseScrollClick.action.performed -= OnMouseScrollClick;
            MiddleMouseScrollClick.action.canceled -= OnMouseScrollClick;

            DesktopSwitch.action.performed -= OnSwitchDesktop;
            DesktopSwitch.action.canceled -= OnSwitchDesktop;

            VRSwitch.action.performed -= OnSwitchOpenVR;
            XRSwitch.action.performed -= OnSwitchOpenXR;
        }
        #endregion

        #region Input Action Handlers
        public Vector2 Pointer;
        private void OnPointerCancelled(InputAction.CallbackContext context)
        {
            Pointer = Vector2.zero;
        }

        private void OnPointerPerformed(InputAction.CallbackContext context)
        {
            Pointer = context.ReadValue<Vector2>();
        }
        public void OnMoveActionPerformed(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetMovementVector(ctx.ReadValue<Vector2>());
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public void OnMoveActionCancelled(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetMovementVector(Vector2.zero);
            if (IsMonoStableInput(ctx.control.device))
            {
                IsRunHeld = false;
                LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
            }
        }

        public void OnLookActionPerformed(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance.IsTyping() == false)
            {
                float sensitivity;
                if (ctx.control.device is Mouse)
                {
                    sensitivity = MouseSensitivity;
                }
                else if (IsMonoStableInput(ctx.control.device))
                {
                    sensitivity = JoystickSensitivity;
                }
                else
                {
                    sensitivity = KeyboardSensitivity;
                }
                OnLookAction(ctx.ReadValue<Vector2>(), sensitivity);
            }
        }
        public void OnLookAction(Vector2 delta, float sensitivity)
        {
            var lookDelta = delta * (deltaCoefficient * sensitivity);
            if (SMModuleControllerSettings.HasInvertedMouse)
            {
                lookDelta.y *= -1f;
            }
            if (IsCrouchHeld)
            {
                LocalCharacterDriver.SetCrouchBlendDelta(lookDelta.y);
                lookDelta.y = 0;
            }
            DesktopEyeInput?.SetLookRotationVector(lookDelta);
        }

        public void OnLookActionCancelled(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetCrouchBlendDelta(0f);
            DesktopEyeInput?.SetLookRotationVector(Vector2.zero);
        }

        public void OnJumpActionPerformed(InputAction.CallbackContext ctx)
        {
            IsJumpHeld = true;
            LocalCharacterDriver.IsJumpHeld = true;
            LocalCharacterDriver.HandleJumpRequest();
        }

        public void OnJumpActionCancelled(InputAction.CallbackContext ctx)
        {
            IsJumpHeld = false;
            LocalCharacterDriver.IsJumpHeld = false;
        }

        public void OnCrouchPerformed(InputAction.CallbackContext ctx)
        {
            if (ctx.interaction is TapInteraction) LocalCharacterDriver.CrouchToggle();
            if (ctx.interaction is HoldInteraction) CrouchStart();
        }

        public void OnCrouchCancelled(InputAction.CallbackContext ctx)
        {
            if (ctx.interaction is HoldInteraction) CrouchEnd();
        }

        private void CrouchStart()
        {
            if (CrouchingLock) return;
            IsCrouchHeld = true;
        }

        private void CrouchEnd()
        {
            IsCrouchHeld = false;
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public void OnRunStarted(InputAction.CallbackContext ctx)
        {
            IsRunHeld = ctx.interaction is not TapInteraction || !IsRunHeld;
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public void OnRunCancelled(InputAction.CallbackContext ctx)
        {
            IsRunHeld = false;
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public void OnEscapePerformed(InputAction.CallbackContext ctx)
        {
            BasisMainMenu.Toggle();
        }

        public void OnEscapeCancelled(InputAction.CallbackContext ctx) { }

        public void OnPrimaryGet(InputAction.CallbackContext ctx) => InputState.PrimaryButtonGetState = true;
        public void OnCancelPrimaryGet(InputAction.CallbackContext ctx) => InputState.PrimaryButtonGetState = false;

        public async void OnSwitchDesktop(InputAction.CallbackContext ctx)
        {
            if (ctx.phase == InputActionPhase.Performed)
                await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
        }

        public async void OnSwitchOpenXR(InputAction.CallbackContext ctx)
        {
            if (ctx.phase == InputActionPhase.Performed)
                await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenXRLoader);
        }

        public async void OnSwitchOpenVR(InputAction.CallbackContext ctx)
        {
            if (ctx.phase == InputActionPhase.Performed)
                await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenVRLoader);
        }

        public void OnLeftMouse(InputAction.CallbackContext ctx) => InputState.Trigger = ctx.ReadValue<float>();
        public void OnRightMouse(InputAction.CallbackContext ctx) => InputState.SecondaryTrigger = ctx.ReadValue<float>();
        public void OnMouseScroll(InputAction.CallbackContext ctx) => InputState.Secondary2DAxisRaw = ctx.ReadValue<Vector2>();
        public void OnMouseScrollClick(InputAction.CallbackContext ctx) => InputState.Secondary2DAxisClick = ctx.ReadValue<float>() == 1;

        #endregion

        #region Helpers

        /// <summary>
        /// Determines whether the given input device is "mono-stable" (gamepad/joystick).
        /// </summary>
        private static bool IsMonoStableInput(InputDevice device)
        {
            return device is Gamepad || device is Joystick;
        }

        #endregion
    }
}
