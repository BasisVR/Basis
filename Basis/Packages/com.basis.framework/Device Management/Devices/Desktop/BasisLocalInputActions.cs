using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.BasisUI;
using BasisPermissions;
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
        public InputActionReference Tab;
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
        public InputActionReference OpenChat;
        public InputActionReference ToggleMicMute;
        public InputActionReference ToggleThirdPerson;
        public InputActionReference CameraZoomAction;
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

        private const string FreeCursorMode = nameof(FreeCursorMode);

        /// <summary>Whether the free-cursor mode (Tab held) is active.</summary>
        public bool IsFreeCursor { get; private set; }

        /// <summary>Whether crouch is currently held down.</summary>
        public bool IsJumpHeld { get; private set; }

        /// <summary>Whether crouch is currently held down.</summary>
        public bool IsCrouchHeld { get; private set; }

        /// <summary>Whether run is currently held down.</summary>
        public bool IsRunHeld { get; private set; }

        private Vector2 manualMoveVector = Vector2.zero;

        private float lastJumpPressTime = -1f;
        private const float DoublePressWindow = 0.3f;

        private const float deltaCoefficient = 0.1f;

        #region Unity Lifecycle

        // Enable Unity Input System internal optimizations once at app startup so every input path
        // (desktop + XR) benefits. Previously these were in OnEnable and only fired for the desktop
        // input component, leaving the XR path un-optimized.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyInputSystemOptimizations()
        {
            InputSystem.settings.SetInternalFeatureFlag("USE_OPTIMIZED_CONTROLS", true);
            InputSystem.settings.SetInternalFeatureFlag("USE_READ_VALUE_CACHING", true);
        }

        public void OnEnable()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
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

            SettingsProviderKeyboardBindings.LoadBindingOverrides(Input.actions);

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
            Tab.action.Enable();
            PrimaryButtonGetState.action.Enable();
            LeftMousePressed.action.Enable();
            RightMousePressed.action.Enable();
            MiddleMouseScroll.action.Enable();
            MiddleMouseScrollClick.action.Enable();
            MoveLocalUpDown.action.Enable();
            OpenChat.action.Enable();
            ToggleMicMute.action.Enable();
            ToggleThirdPerson.action.Enable();
            CameraZoomAction.action.Enable();
        }

        private void DisableActions()
        {
            PointerAction?.action?.Disable();
            DesktopSwitch?.action?.Disable();
            XRSwitch?.action?.Disable();
            VRSwitch?.action?.Disable();
            MoveAction?.action?.Disable();
            LookAction?.action?.Disable();
            JumpAction?.action?.Disable();
            CrouchAction?.action?.Disable();
            RunButton?.action?.Disable();
            Escape?.action?.Disable();
            Tab?.action?.Disable();
            PrimaryButtonGetState?.action?.Disable();
            LeftMousePressed?.action?.Disable();
            RightMousePressed?.action?.Disable();
            MiddleMouseScroll?.action?.Disable();
            MiddleMouseScrollClick?.action?.Disable();
            MoveLocalUpDown?.action?.Disable();
            OpenChat?.action?.Disable();
            ToggleMicMute?.action?.Disable();
            ToggleThirdPerson?.action?.Disable();
            CameraZoomAction?.action?.Disable();
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

            Tab.action.performed += OnTabPerformed;
            Tab.action.canceled += OnTabCancelled;

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

            OpenChat.action.performed += OnOpenChatPerformed;
            OpenChat.action.canceled += OnOpenChatCancelled;

            ToggleMicMute.action.performed += OnToggleMicMutePerformed;
            ToggleMicMute.action.canceled += OnToggleMicMuteCancelled;

            ToggleThirdPerson.action.performed += OnToggleThirdPerson;

            CameraZoomAction.action.performed += OnCameraZoom;
            CameraZoomAction.action.canceled += OnCameraZoomCanceled;

            BasisCursorManagement.OnCursorStateChange += OnCursorStateChanged;
        }

        private static void SafeRemoveCallbacks(InputActionReference actionRef,
            System.Action<InputAction.CallbackContext> performed,
            System.Action<InputAction.CallbackContext> canceled = null)
        {
            if (actionRef == null || actionRef.action == null) return;
            actionRef.action.performed -= performed;
            if (canceled != null) actionRef.action.canceled -= canceled;
        }

        private void RemoveCallbacks()
        {
            // Unregister all callbacks
            SafeRemoveCallbacks(PointerAction, OnPointerPerformed, OnPointerCancelled);
            SafeRemoveCallbacks(CrouchAction, OnCrouchPerformed, OnCrouchCancelled);
            SafeRemoveCallbacks(MoveAction, OnMoveActionPerformed, OnMoveActionCancelled);
            SafeRemoveCallbacks(LookAction, OnLookActionPerformed, OnLookActionCancelled);
            SafeRemoveCallbacks(JumpAction, OnJumpActionPerformed, OnJumpActionCancelled);
            SafeRemoveCallbacks(RunButton, OnRunStarted, OnRunCancelled);
            SafeRemoveCallbacks(Escape, OnEscapePerformed, OnEscapeCancelled);
            SafeRemoveCallbacks(Tab, OnTabPerformed, OnTabCancelled);
            SafeRemoveCallbacks(PrimaryButtonGetState, OnPrimaryGet, OnCancelPrimaryGet);
            SafeRemoveCallbacks(LeftMousePressed, OnLeftMouse, OnLeftMouse);
            SafeRemoveCallbacks(RightMousePressed, OnRightMouse, OnRightMouse);
            SafeRemoveCallbacks(MiddleMouseScroll, OnMouseScroll, OnMouseScroll);
            SafeRemoveCallbacks(MiddleMouseScrollClick, OnMouseScrollClick, OnMouseScrollClick);
            SafeRemoveCallbacks(DesktopSwitch, OnSwitchDesktop, OnSwitchDesktop);
            SafeRemoveCallbacks(VRSwitch, OnSwitchOpenVR);
            SafeRemoveCallbacks(XRSwitch, OnSwitchOpenXR);
            SafeRemoveCallbacks(OpenChat, OnOpenChatPerformed, OnOpenChatCancelled);
            SafeRemoveCallbacks(ToggleMicMute, OnToggleMicMutePerformed, OnToggleMicMuteCancelled);
            SafeRemoveCallbacks(ToggleThirdPerson, OnToggleThirdPerson);
            SafeRemoveCallbacks(CameraZoomAction, OnCameraZoom, OnCameraZoomCanceled);

            BasisCursorManagement.OnCursorStateChange -= OnCursorStateChanged;
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
                lookDelta.y = 0f;
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

            // Admin double-press fly toggle (desktop only)
            if (!BasisDeviceManagement.IsCurrentModeVR())
            {
                float now = Time.unscaledTime;
                if (now - lastJumpPressTime <= DoublePressWindow)
                {
                    TryToggleFlyMode();
                    lastJumpPressTime = -1f;
                }
                else
                {
                    lastJumpPressTime = now;
                }
            }
        }

        private void TryToggleFlyMode()
        {
            if (!BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit))
            {
                return;
            }

            if (LocalCharacterDriver.CurrentModeKind == BasisLocalCharacterDriver.Mode.Fly)
            {
                LocalCharacterDriver.SetMode(BasisLocalCharacterDriver.Mode.Walk);
            }
            else
            {
                LocalCharacterDriver.SetMode(BasisLocalCharacterDriver.Mode.Fly);
            }
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

        public void OnOpenChatPerformed(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance.IsTyping() == false)
            {
                SettingsProvider.OpenToTab("settings.tab.chat");
            }
        }

        public void OnOpenChatCancelled(InputAction.CallbackContext ctx) { }

        public void OnToggleMicMutePerformed(InputAction.CallbackContext ctx)
        {
#if !BASIS_DISABLE_MICROPHONE
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;
            BasisLocalMicrophoneDriver.ToggleIsPaused();
#endif
        }

        public void OnToggleMicMuteCancelled(InputAction.CallbackContext ctx) { }

        public void OnToggleThirdPerson(InputAction.CallbackContext ctx)
        {

            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;

            if (BasisLocalCameraDriver.HasInstance == false)
                return;

            if (ctx.interaction is TapInteraction && ctx.phase == InputActionPhase.Performed)
            {
                BasisLocalCameraDriver.Instance.ToggleThirdPerson();
            }
            else if (ctx.interaction is HoldInteraction && ctx.phase == InputActionPhase.Performed)
            {
                // Todo: Handle zooming in/out when using a controller specifically
            }
        }

        public void OnCameraZoom(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;

            float zoomDelta = ctx.ReadValue<float>() * 0.5f;

            // Disable zoom when interacting with UI
            if (DesktopEyeInput != null && DesktopEyeInput.HasRaycaster && DesktopEyeInput.BasisUIRaycast.HadRaycastUITarget)
                zoomDelta = 0f;

            // Disable zoom when holding a physics object
            if (Basis.Scripts.BasisSdk.Interactions.BasisPlayerInteract.Instance != null && DesktopEyeInput != null)
            {
                var interactSystem = Basis.Scripts.BasisSdk.Interactions.BasisPlayerInteract.Instance;
                for (int i = 0; i < interactSystem.InteractInputs.Length; i++)
                {
                    var input = interactSystem.InteractInputs[i];
                    if (input.input != null && input.input.UniqueDeviceIdentifier == DesktopEyeInput.UniqueDeviceIdentifier)
                    {
                        if (input.lastTarget != null && input.lastTarget.IsInteractingWith(DesktopEyeInput))
                            zoomDelta = 0f;
                    }
                }
            }

            if (BasisLocalCameraDriver.HasInstance)
            {
                BasisLocalCameraDriver.Instance.ApplyZoom(zoomDelta);
            }
        }

        public void OnCameraZoomCanceled(InputAction.CallbackContext ctx) { }

        public void OnTabPerformed(InputAction.CallbackContext ctx)
        {
            IsFreeCursor = true;
            BasisCursorManagement.UnlockCursor(FreeCursorMode);
        }

        public void OnTabCancelled(InputAction.CallbackContext ctx)
        {
            IsFreeCursor = false;
            BasisCursorManagement.LockCursor(FreeCursorMode);
        }

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

        private void OnCursorStateChanged(CursorLockMode lockMode, bool visible)
        {
            // When the cursor lock state changes, the cursor is already set to the center of the screen,
            // but the input event is not triggered. This means that the previous hover position is kept, even
            // when the cursor changes position. To ensure that the hover position is correct when altering the cursor state,
            // manually set the position to the center of the screen.
            Pointer = new Vector2(Screen.width / 2f, Screen.height / 2f);
        }

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
