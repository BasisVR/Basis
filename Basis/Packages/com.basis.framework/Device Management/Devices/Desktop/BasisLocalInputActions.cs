using System;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI.UI_Panels;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.Common;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Interactions;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    [DefaultExecutionOrder(15003)]
    public class BasisLocalInputActions : MonoBehaviour
    {
        public static BasisLocalInputActions Instance;

        public InputActionReference MoveAction;
        public InputActionReference LookAction;
        public InputActionReference JumpAction;
        public InputActionReference CrouchAction;
        public InputActionReference RunButton;
        public InputActionReference Escape;
        public InputActionReference PrimaryButtonGetState;

        public InputActionReference DesktopSwitch;
        public InputActionReference XRSwitch;

        public InputActionReference LeftMousePressed;
        public InputActionReference RightMousePressed;

        public InputActionReference MiddleMouseScroll;
        public InputActionReference MiddleMouseScrollClick;

        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;
        [System.NonSerialized] public BasisLocalCharacterDriver LocalCharacterDriver;
        [System.NonSerialized] public BasisAvatarEyeInput AvatarEyeInput;

        public PlayerInput Input;

        [SerializeField] public BasisInputState InputState = new BasisInputState();

        [Obsolete("This is a short-term solution for listening for the menu action. Later changes will make this actually obsolete.")]
        public Action OnMenu = BasisHamburgerMenu.ToggleHamburgerMenuNow;

        private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);

        public bool IsCrouchHeld { get; private set; }
        public bool IsRunHeld { get; private set; }

        public void OnEnable()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }

            InputSystem.settings.SetInternalFeatureFlag("USE_OPTIMIZED_CONTROLS", true);
            InputSystem.settings.SetInternalFeatureFlag("USE_READ_VALUE_CACHING", true);
            BasisLocalCameraDriver.InstanceExists += SetupCamera;

            if (BasisDeviceManagement.IsMobile() == false)
            {
                EnableActions();
                AddCallbacks();
            }
        }

        public void OnDisable()
        {
            BasisLocalCameraDriver.InstanceExists -= SetupCamera;
            if (BasisDeviceManagement.IsMobile() == false)
            {
                RemoveCallbacks();
                DisableActions();
            }
        }

        public void SetupCamera()
        {
            Input.camera = BasisLocalCameraDriver.Instance.Camera;
        }

        public void Initialize(BasisLocalPlayer localPlayer)
        {
            LocalPlayer = localPlayer;
            LocalCharacterDriver = localPlayer.LocalCharacterDriver;
            gameObject.SetActive(true);
        }

        private void EnableActions()
        {
            DesktopSwitch.action.Enable();
            XRSwitch.action.Enable();
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
        }

        private void DisableActions()
        {
            DesktopSwitch.action.Disable();
            XRSwitch.action.Disable();
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
        }

        private void AddCallbacks()
        {
            CrouchAction.action.performed += OnCrouchStarted;
            DesktopSwitch.action.performed += OnSwitchDesktop;
            Escape.action.performed += OnEscapePerformed;
            JumpAction.action.performed += OnJumpActionPerformed;
            LeftMousePressed.action.performed += OnLeftMouse;
            MiddleMouseScroll.action.performed += OnMouseScroll;
            MiddleMouseScrollClick.action.performed += OnMouseScrollClick;
            MoveAction.action.performed += OnMoveActionStarted;
            PrimaryButtonGetState.action.performed += OnPrimaryGet;
            RightMousePressed.action.performed += OnRightMouse;
            RunButton.action.performed += OnRunStarted;
            LookAction.action.performed += OnLookActionStarted;
            XRSwitch.action.performed += OnSwitchOpenXR;

            CrouchAction.action.canceled += OnCrouchCancelled;
            DesktopSwitch.action.canceled += OnSwitchDesktop;
            Escape.action.canceled += OnEscapeCancelled;
            JumpAction.action.canceled += OnJumpActionCancelled;
            LeftMousePressed.action.canceled += OnLeftMouse;
            MiddleMouseScroll.action.canceled += OnMouseScroll;
            MiddleMouseScrollClick.action.canceled += OnMouseScrollClick;
            MoveAction.action.canceled += OnMoveActionCancelled;
            PrimaryButtonGetState.action.canceled += OnCancelPrimaryGet;
            RightMousePressed.action.canceled += OnRightMouse;
            RunButton.action.canceled += OnRunCancelled;
            LookAction.action.canceled += OnLookActionCancelled;
        }

        private void RemoveCallbacks()
        {
            CrouchAction.action.performed -= OnCrouchStarted;
            DesktopSwitch.action.performed -= OnSwitchDesktop;
            Escape.action.performed -= OnEscapePerformed;
            JumpAction.action.performed -= OnJumpActionPerformed;
            LeftMousePressed.action.performed -= OnLeftMouse;
            MiddleMouseScroll.action.performed -= OnMouseScroll;
            MiddleMouseScrollClick.action.performed -= OnMouseScrollClick;
            MoveAction.action.performed -= OnMoveActionStarted;
            PrimaryButtonGetState.action.performed -= OnPrimaryGet;
            RightMousePressed.action.performed -= OnRightMouse;
            RunButton.action.performed -= OnRunStarted;
            LookAction.action.performed -= OnLookActionStarted;
            XRSwitch.action.performed -= OnSwitchOpenXR;

            CrouchAction.action.canceled -= OnCrouchCancelled;
            DesktopSwitch.action.canceled -= OnSwitchDesktop;
            Escape.action.canceled -= OnEscapeCancelled;
            JumpAction.action.canceled -= OnJumpActionCancelled;
            LeftMousePressed.action.canceled -= OnLeftMouse;
            MiddleMouseScroll.action.canceled -= OnMouseScroll;
            MiddleMouseScrollClick.action.canceled -= OnMouseScrollClick;
            MoveAction.action.canceled -= OnMoveActionCancelled;
            PrimaryButtonGetState.action.canceled -= OnCancelPrimaryGet;
            RightMousePressed.action.canceled -= OnRightMouse;
            RunButton.action.canceled -= OnRunCancelled;
            LookAction.action.canceled -= OnLookActionCancelled;
        }

        // Handling normalized values from analog controls such as joysticks,
        // that aren't already handled elsewhere (eg: movement updates are handled in another script)
        private void Update()
        {
            if (AvatarEyeInput != null)
            {
                var lookDelta = LookAction.action.ReadValue<Vector2>();
                // locks vertical look to the crouch control
                if (IsCrouchHeld) lookDelta.y = 0;
                AvatarEyeInput.HandleLookRotation(lookDelta);
            }
        }

        // Input action methods
        public void OnMoveActionStarted(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetMovementVector(ctx.ReadValue<Vector2>());
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public void OnMoveActionCancelled(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetMovementVector(Vector2.zero);
            bool monoStableInput = false;
            monoStableInput |= ctx.control.device is Gamepad;
            monoStableInput |= ctx.control.device is Joystick;
            if (monoStableInput)
            {
                IsRunHeld = false;
                LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
            }
        }

        public void OnLookActionStarted(InputAction.CallbackContext ctx)
        {
            if (IsCrouchHeld)
                LocalCharacterDriver.UpdateCrouchBlend(ctx.ReadValue<Vector2>().y);
        }

        public void OnLookActionCancelled(InputAction.CallbackContext ctx) { }

        public void OnJumpActionPerformed(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.HandleJump();
        }

        public void OnJumpActionCancelled(InputAction.CallbackContext ctx)
        {
            // Logic for when jump is cancelled (if needed)
        }

        public void OnCrouchStarted(InputAction.CallbackContext ctx)
        {
            if (ctx.interaction is UnityEngine.InputSystem.Interactions.TapInteraction) LocalCharacterDriver.CrouchToggle();
            if (ctx.interaction is UnityEngine.InputSystem.Interactions.HoldInteraction) CrouchStart();
        }

        public void OnCrouchCancelled(InputAction.CallbackContext ctx)
        {
            if (ctx.interaction is UnityEngine.InputSystem.Interactions.HoldInteraction) CrouchEnd();
        }

        private void CrouchStart()
        {
            if (CrouchingLock) return;
            IsCrouchHeld = true;
        }

        private void CrouchEnd()
        {
            if (CrouchingLock) return;
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
#pragma warning disable CS0618
            OnMenu?.Invoke();
#pragma warning restore CS0618
        }

        public void OnEscapeCancelled(InputAction.CallbackContext ctx)
        {
            // Logic for escape action cancellation (if needed)
        }

        public void OnPrimaryGet(InputAction.CallbackContext ctx)
        {
            InputState.PrimaryButtonGetState = true;
        }

        public void OnCancelPrimaryGet(InputAction.CallbackContext ctx)
        {
            InputState.PrimaryButtonGetState = false;
        }

        public void OnSwitchDesktop(InputAction.CallbackContext ctx)
        {
            BasisDeviceManagement.ForceSetDesktop();
        }

        public void OnSwitchOpenXR(InputAction.CallbackContext ctx)
        {
            BasisDeviceManagement.ForceLoadXR();
        }

        public void OnLeftMouse(InputAction.CallbackContext ctx)
        {
            InputState.Trigger = ctx.ReadValue<float>();
        }

        public void OnRightMouse(InputAction.CallbackContext ctx)
        {
            InputState.SecondaryTrigger = ctx.ReadValue<float>();
        }

        public void OnMouseScroll(InputAction.CallbackContext ctx)
        {
            InputState.Secondary2DAxis = ctx.ReadValue<Vector2>();
        }

        public void OnMouseScrollClick(InputAction.CallbackContext ctx)
        {
            InputState.Secondary2DAxisClick = ctx.ReadValue<float>() == 1;
        }
    }
}
