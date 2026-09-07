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
    public static class BasisLocalInputActions
    {
        public static InputActionAsset Asset;

        public static InputAction MoveAction;
        public static InputAction LookAction;
        public static InputAction JumpAction;
        public static InputAction CrouchAction;
        public static InputAction ProneAction;
        public static InputAction RunButton;
        public static InputAction Escape;
        public static InputAction Tab;
        public static InputAction PrimaryButtonGetState;
        public static InputAction PointerAction;

        public static InputAction DesktopSwitch;
        public static InputAction VRSwitch;
        public static InputAction XRSwitch;

        public static InputAction LeftMousePressed;
        public static InputAction RightMousePressed;
        public static InputAction MiddleMouseScroll;
        public static InputAction MiddleMouseScrollClick;

        public static InputAction MoveLocalUpDown;
        public static InputAction OpenChat;
        public static InputAction ToggleMicMute;
        public static InputAction ToggleThirdPerson;
        public static InputAction CameraZoomAction;

        public static float MouseSensitivity = 1f;
        public static float JoystickSensitivity = 1f;
        public static float KeyboardSensitivity = 5f;

        public static BasisLocalPlayer LocalPlayer;
        public static BasisLocalCharacterDriver LocalCharacterDriver;
        public static BasisDesktopEye DesktopEyeInput;

        public static BasisInputState InputState = new BasisInputState();

        private static BasisLocks.LockContext CrouchingLock;

        private const string FreeCursorMode = nameof(FreeCursorMode);

        public static bool IsFreeCursor { get; private set; }
        public static bool IsJumpHeld { get; private set; }
        public static bool IsCrouchHeld { get; private set; }
        public static bool IsRunHeld { get; private set; }

        public static Vector2 Pointer;

        public static bool HasCallbacksAndActions = false;

        private static float lastJumpPressTime = -1f;
        private const float DoublePressWindow = 0.3f;

        private const float deltaCoefficient = 0.1f;

        private static bool canZoomCamera = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyInputSystemOptimizations()
        {
            InputSystem.settings.SetInternalFeatureFlag("USE_OPTIMIZED_CONTROLS", true);
            InputSystem.settings.SetInternalFeatureFlag("USE_READ_VALUE_CACHING", true);
        }

        public static void Initialize(BasisLocalPlayer localPlayer, InputActionAsset asset, GameObject root)
        {
            LocalPlayer = localPlayer;
            LocalCharacterDriver = localPlayer.LocalCharacterDriver;
            Asset = asset;
            CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);

            ResolveActions();

            if (root != null) root.SetActive(true);

            var user = InputUser.CreateUserWithoutPairedDevices();
            foreach (var device in InputSystem.devices)
            {
                if (device is Keyboard || device is Mouse || device is Gamepad || device is Pointer)
                {
                    BasisDebug.Log($"Giving access to {device.displayName}", BasisDebug.LogTag.Input);
                    InputUser.PerformPairingWithDevice(device, user);
                }
            }

            SettingsProviderKeyboardBindings.LoadBindingOverrides(Asset);

            if (BasisDeviceManagement.IsCurrentModeVR() && BasisDeviceManagement.IsMobileHardware())
            {
                return;
            }

            HasCallbacksAndActions = true;
            EnableActions();
            AddCallbacks();

            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }

        private static void ResolveActions()
        {
            var map = Asset.FindActionMap("Player", throwIfNotFound: true);
            MoveAction = map.FindAction("Move", throwIfNotFound: true);
            LookAction = map.FindAction("Look Delta", throwIfNotFound: true);
            JumpAction = map.FindAction("Jump", throwIfNotFound: true);
            CrouchAction = map.FindAction("Crouch", throwIfNotFound: true);
            ProneAction = map.FindAction("Prone", throwIfNotFound: true);
            RunButton = map.FindAction("Running", throwIfNotFound: true);
            Escape = map.FindAction("Escape", throwIfNotFound: true);
            Tab = map.FindAction("Tab", throwIfNotFound: true);
            PrimaryButtonGetState = map.FindAction("Primary Controller A", throwIfNotFound: true);
            PointerAction = map.FindAction("Pointer Position", throwIfNotFound: true);
            DesktopSwitch = map.FindAction("HotSwitchToDesktop", throwIfNotFound: true);
            VRSwitch = map.FindAction("HotSwtichToVR", throwIfNotFound: true);
            XRSwitch = map.FindAction("HotSwtichToXR", throwIfNotFound: true);
            LeftMousePressed = map.FindAction("LeftMouse", throwIfNotFound: true);
            RightMousePressed = map.FindAction("RightMouse", throwIfNotFound: true);
            MiddleMouseScroll = map.FindAction("ScrollWheel", throwIfNotFound: true);
            MiddleMouseScrollClick = map.FindAction("MiddleMouse", throwIfNotFound: true);
            MoveLocalUpDown = map.FindAction("MoveLocalUpDown", throwIfNotFound: true);
            OpenChat = map.FindAction("OpenChat", throwIfNotFound: true);
            ToggleMicMute = map.FindAction("ToggleMicMute", throwIfNotFound: true);
            ToggleThirdPerson = map.FindAction("ToggleThirdPerson", throwIfNotFound: true);
            CameraZoomAction = map.FindAction("CameraZoom", throwIfNotFound: true);
        }

        private static void Shutdown()
        {
            if (HasCallbacksAndActions)
            {
                RemoveCallbacks();
                DisableActions();
                HasCallbacksAndActions = false;
            }
        }

        private static void EnableActions()
        {
            PointerAction.Enable();
            DesktopSwitch.Enable();
            XRSwitch.Enable();
            VRSwitch.Enable();
            MoveAction.Enable();
            LookAction.Enable();
            JumpAction.Enable();
            CrouchAction.Enable();
            ProneAction.Enable();
            RunButton.Enable();
            Escape.Enable();
            Tab.Enable();
            PrimaryButtonGetState.Enable();
            LeftMousePressed.Enable();
            RightMousePressed.Enable();
            MiddleMouseScroll.Enable();
            MiddleMouseScrollClick.Enable();
            MoveLocalUpDown.Enable();
            OpenChat.Enable();
            ToggleMicMute.Enable();
            ToggleThirdPerson.Enable();
            CameraZoomAction.Enable();
        }

        private static void DisableActions()
        {
            PointerAction?.Disable();
            DesktopSwitch?.Disable();
            XRSwitch?.Disable();
            VRSwitch?.Disable();
            MoveAction?.Disable();
            LookAction?.Disable();
            JumpAction?.Disable();
            CrouchAction?.Disable();
            ProneAction?.Disable();
            RunButton?.Disable();
            Escape?.Disable();
            Tab?.Disable();
            PrimaryButtonGetState?.Disable();
            LeftMousePressed?.Disable();
            RightMousePressed?.Disable();
            MiddleMouseScroll?.Disable();
            MiddleMouseScrollClick?.Disable();
            MoveLocalUpDown?.Disable();
            OpenChat?.Disable();
            ToggleMicMute?.Disable();
            ToggleThirdPerson?.Disable();
            CameraZoomAction?.Disable();
        }

        private static void AddCallbacks()
        {
            PointerAction.performed += OnPointerPerformed;
            PointerAction.canceled += OnPointerCancelled;

            CrouchAction.performed += OnCrouchPerformed;
            CrouchAction.canceled += OnCrouchCancelled;

            ProneAction.performed += OnPronePerformed;

            MoveAction.performed += OnMoveActionPerformed;
            MoveAction.canceled += OnMoveActionCancelled;

            LookAction.performed += OnLookActionPerformed;
            LookAction.canceled += OnLookActionCancelled;

            JumpAction.performed += OnJumpActionPerformed;
            JumpAction.canceled += OnJumpActionCancelled;

            RunButton.performed += OnRunStarted;
            RunButton.canceled += OnRunCancelled;

            Escape.performed += OnEscapePerformed;
            Escape.canceled += OnEscapeCancelled;

            Tab.performed += OnTabPerformed;
            Tab.canceled += OnTabCancelled;

            PrimaryButtonGetState.performed += OnPrimaryGet;
            PrimaryButtonGetState.canceled += OnCancelPrimaryGet;

            LeftMousePressed.performed += OnLeftMouse;
            LeftMousePressed.canceled += OnLeftMouse;

            RightMousePressed.performed += OnRightMouse;
            RightMousePressed.canceled += OnRightMouse;

            MiddleMouseScroll.performed += OnMouseScroll;
            MiddleMouseScroll.canceled += OnMouseScroll;

            MiddleMouseScrollClick.performed += OnMouseScrollClick;
            MiddleMouseScrollClick.canceled += OnMouseScrollClick;

            DesktopSwitch.performed += OnSwitchDesktop;
            DesktopSwitch.canceled += OnSwitchDesktop;

            VRSwitch.performed += OnSwitchOpenVR;
            XRSwitch.performed += OnSwitchOpenXR;

            OpenChat.performed += OnOpenChatPerformed;
            OpenChat.canceled += OnOpenChatCancelled;

            ToggleMicMute.performed += OnToggleMicMutePerformed;
            ToggleMicMute.canceled += OnToggleMicMuteCancelled;

            ToggleThirdPerson.performed += OnToggleThirdPerson;
            ToggleThirdPerson.canceled += OnToggleThirdPersonCanceled;

            CameraZoomAction.performed += OnCameraZoom;
            CameraZoomAction.canceled += OnCameraZoomCanceled;

            BasisCursorManagement.OnCursorStateChange += OnCursorStateChanged;
        }

        private static void SafeRemoveCallbacks(InputAction action,
            System.Action<InputAction.CallbackContext> performed,
            System.Action<InputAction.CallbackContext> canceled = null)
        {
            if (action == null) return;
            action.performed -= performed;
            if (canceled != null) action.canceled -= canceled;
        }

        private static void RemoveCallbacks()
        {
            SafeRemoveCallbacks(PointerAction, OnPointerPerformed, OnPointerCancelled);
            SafeRemoveCallbacks(CrouchAction, OnCrouchPerformed, OnCrouchCancelled);
            SafeRemoveCallbacks(ProneAction, OnPronePerformed);
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
            SafeRemoveCallbacks(ToggleThirdPerson, OnToggleThirdPerson, OnToggleThirdPersonCanceled);
            SafeRemoveCallbacks(CameraZoomAction, OnCameraZoom, OnCameraZoomCanceled);

            BasisCursorManagement.OnCursorStateChange -= OnCursorStateChanged;
        }

        private static void OnPointerCancelled(InputAction.CallbackContext context)
        {
            Pointer = Vector2.zero;
        }

        private static void OnPointerPerformed(InputAction.CallbackContext context)
        {
            Pointer = context.ReadValue<Vector2>();
        }

        public static void OnMoveActionPerformed(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetMovementVector(ctx.ReadValue<Vector2>());
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public static void OnMoveActionCancelled(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetMovementVector(Vector2.zero);
            if (IsMonoStableInput(ctx.control.device))
            {
                IsRunHeld = false;
                LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
            }
        }

        public static void OnLookActionPerformed(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance == null || BasisInputModuleHandler.Instance.IsTyping() == false)
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
                OnLookAction(ctx.ReadValue<Vector2>(), sensitivity, IsMonoStableInput(ctx.control.device));
            }
        }

        public static void OnLookAction(Vector2 delta, float sensitivity, bool isMonoStable = false)
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
            if (isMonoStable && canZoomCamera)
            {
                BasisLocalCameraDriver.Instance.ApplyZoom(lookDelta.y);
                lookDelta.x = 0f;
                lookDelta.y = 0f;
            }
            DesktopEyeInput?.SetLookRotationVector(lookDelta);
        }

        public static void OnLookActionCancelled(InputAction.CallbackContext ctx)
        {
            LocalCharacterDriver.SetCrouchBlendDelta(0f);
            DesktopEyeInput?.SetLookRotationVector(Vector2.zero);
        }

        public static void OnJumpActionPerformed(InputAction.CallbackContext ctx)
        {
            IsJumpHeld = true;
            LocalCharacterDriver.IsJumpHeld = true;
            LocalCharacterDriver.HandleJumpRequest();

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

        private static void TryToggleFlyMode()
        {
            if (!BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit))
            {
                return;
            }

            if (BasisLocomotionOverrides.Resolve().Has(BasisLocomotionField.Mode))
            {
                BasisDebug.Log("Fly toggle ignored: the movement mode is currently overridden.");
                return;
            }

            LocalCharacterDriver.BaselineMode = LocalCharacterDriver.CurrentModeKind == BasisLocalCharacterDriver.Mode.Fly
                ? BasisLocalCharacterDriver.Mode.Walk
                : BasisLocalCharacterDriver.Mode.Fly;
            LocalCharacterDriver.SetMode(LocalCharacterDriver.BaselineMode);
        }

        public static void OnJumpActionCancelled(InputAction.CallbackContext ctx)
        {
            IsJumpHeld = false;
            LocalCharacterDriver.IsJumpHeld = false;
        }

        public static void OnCrouchPerformed(InputAction.CallbackContext ctx)
        {
            if (ctx.interaction is TapInteraction) LocalCharacterDriver.CrouchToggle();
            if (ctx.interaction is HoldInteraction) CrouchStart();
        }

        public static void OnCrouchCancelled(InputAction.CallbackContext ctx)
        {
            if (ctx.interaction is HoldInteraction) CrouchEnd();
        }

        private static void CrouchStart()
        {
            if (CrouchingLock) return;
            IsCrouchHeld = true;
        }

        private static void CrouchEnd()
        {
            IsCrouchHeld = false;
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public static void OnPronePerformed(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;
            LocalCharacterDriver.ProneToggle();
        }

        public static void OnRunStarted(InputAction.CallbackContext ctx)
        {
            IsRunHeld = ctx.interaction is not TapInteraction || !IsRunHeld;
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public static void OnRunCancelled(InputAction.CallbackContext ctx)
        {
            IsRunHeld = false;
            LocalCharacterDriver.UpdateMovementSpeed(IsRunHeld);
        }

        public static void OnEscapePerformed(InputAction.CallbackContext ctx)
        {
            BasisMainMenu.Toggle();
        }

        public static void OnEscapeCancelled(InputAction.CallbackContext ctx) { }

        public static void OnOpenChatPerformed(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance == null || BasisInputModuleHandler.Instance.IsTyping() == false)
            {
                SettingsProvider.OpenToTab("settings.tab.chat");
            }
        }

        public static void OnOpenChatCancelled(InputAction.CallbackContext ctx) { }

        private static bool IsShortcutModifierHeld()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;

            return keyboard.ctrlKey.isPressed || keyboard.leftCommandKey.isPressed || keyboard.rightCommandKey.isPressed;
        }

        public static void OnToggleMicMutePerformed(InputAction.CallbackContext ctx)
        {
#if !BASIS_DISABLE_MICROPHONE
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;

            if (IsShortcutModifierHeld())
                return;

            switch (SMDMicrophone.Current.TalkMode)
            {
                case SMDMicrophone.BasisMicrophoneMode.OnActivation:
                    BasisLocalMicrophoneDriver.ToggleIsPaused();
                    break;

                case SMDMicrophone.BasisMicrophoneMode.PushToTalk:
                    if (BasisLocalMicrophoneDriver.isPaused)
                        BasisLocalMicrophoneDriver.ToggleIsPaused();
                    break;
            }
#endif
        }

        public static void OnToggleMicMuteCancelled(InputAction.CallbackContext ctx)
        {
#if !BASIS_DISABLE_MICROPHONE
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;

            if (SMDMicrophone.Current.TalkMode == SMDMicrophone.BasisMicrophoneMode.PushToTalk
                && BasisLocalMicrophoneDriver.isPaused == false)
            {
                BasisLocalMicrophoneDriver.ToggleIsPaused();
            }
#endif
        }

        public static void OnToggleThirdPerson(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;

            if (BasisLocalCameraDriver.HasInstance == false)
                return;

            if (ctx.interaction is TapInteraction && ctx.phase == InputActionPhase.Performed)
            {
                BasisLocalCameraDriver.Instance.ToggleThirdPerson();
            }
            if (ctx.interaction is HoldInteraction && ctx.phase == InputActionPhase.Performed)
            {
                canZoomCamera = true;
            }
        }

        public static void OnToggleThirdPersonCanceled(InputAction.CallbackContext ctx)
        {
            canZoomCamera = false;
        }

        public static void OnCameraZoom(InputAction.CallbackContext ctx)
        {
            if (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.IsTyping())
                return;

            float zoomDelta = ctx.ReadValue<float>() * 0.5f;

            if (!canZoomCamera) zoomDelta = 0f;

            if (DesktopEyeInput != null && DesktopEyeInput.HasRaycaster && DesktopEyeInput.BasisUIRaycast.HadRaycastUITarget)
                zoomDelta = 0f;

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

        public static void OnCameraZoomCanceled(InputAction.CallbackContext ctx) { }

        public static void OnTabPerformed(InputAction.CallbackContext ctx)
        {
            IsFreeCursor = true;
            BasisCursorManagement.UnlockCursor(FreeCursorMode);
        }

        public static void OnTabCancelled(InputAction.CallbackContext ctx)
        {
            IsFreeCursor = false;
            BasisCursorManagement.LockCursor(FreeCursorMode);
        }

        public static void OnPrimaryGet(InputAction.CallbackContext ctx) => InputState.PrimaryButtonGetState = true;
        public static void OnCancelPrimaryGet(InputAction.CallbackContext ctx) => InputState.PrimaryButtonGetState = false;

        public static async void OnSwitchDesktop(InputAction.CallbackContext ctx)
        {
            if (ctx.phase == InputActionPhase.Performed)
                await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
        }

        public static async void OnSwitchOpenXR(InputAction.CallbackContext ctx)
        {
            if (ctx.phase == InputActionPhase.Performed)
                await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenXRLoader);
        }

        public static async void OnSwitchOpenVR(InputAction.CallbackContext ctx)
        {
            if (ctx.phase == InputActionPhase.Performed)
                await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenVRLoader);
        }

        public static void OnLeftMouse(InputAction.CallbackContext ctx) => InputState.Trigger = ctx.ReadValue<float>();
        public static void OnRightMouse(InputAction.CallbackContext ctx) => InputState.SecondaryTrigger = ctx.ReadValue<float>();
        public static void OnMouseScroll(InputAction.CallbackContext ctx) => InputState.Secondary2DAxisRaw = ctx.ReadValue<Vector2>();
        public static void OnMouseScrollClick(InputAction.CallbackContext ctx) => InputState.Secondary2DAxisClick = ctx.ReadValue<float>() == 1;

        private static void OnCursorStateChanged(CursorLockMode lockMode, bool visible)
        {
            // When the cursor lock state changes, the cursor is already set to the center of the screen,
            // but the input event is not triggered. This means that the previous hover position is kept, even
            // when the cursor changes position. To ensure that the hover position is correct when altering the cursor state,
            // manually set the position to the center of the screen.
            Pointer = new Vector2(Screen.width / 2f, Screen.height / 2f);
        }

        private static bool IsMonoStableInput(InputDevice device)
        {
            return device is Gamepad || device is Joystick;
        }
    }
}
