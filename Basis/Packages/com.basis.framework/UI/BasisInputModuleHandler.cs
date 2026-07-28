using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Custom input module that manages text input focus, virtual keyboard spawning, and navigation
    /// for TMP and legacy InputFields. Handles Tab/Enter flows and locks player movement while typing.
    /// </summary>
    public class BasisInputModuleHandler : BaseInputModule
    {
        /// <summary>
        /// Reference to the active <see cref="UnityEngine.EventSystems.EventSystem"/>.
        /// </summary>
        public EventSystem EventSystem;

        private InputAction tabAction;
        private InputAction enterAction;
        private InputAction keypadEnterAction;

        private bool physicalKeyboardSubscribed;
        private Keyboard subscribedKeyboard;

        public const float CaretRepeatDelay = 0.5f;
        public const float CaretRepeatInterval = 0.1f;
        public const float CaretStickThreshold = 0.5f;

        private int caretHeldDirection;
        private float caretNextRepeatTime;

        private TMP_InputField lastCaretVisibilityField;
        private int lastCaretVisibilityPosition;
        private int lastCaretVisibilityTextLength;

        /// <summary>
        /// Currently selected TMP input field (if any).
        /// </summary>
        public TMP_InputField CurrentSelectedTMP_InputField;

        /// <summary>
        /// Currently selected legacy <see cref="InputField"/> (if any).
        /// </summary>
        public InputField CurrentSelectedInputField;

        /// <summary>
        /// Indicates whether the module currently has focus over an input field.
        /// </summary>
        public bool HasHoverONInput = false;

        /// <summary>
        /// Forces the on-screen keyboard even outside XR.
        /// </summary>
        public bool ForceKeyboard = false;

        /// <summary>
        /// UI raycast helper used during processing.
        /// </summary>
        [System.NonSerialized] public BasisUIRaycastProcess basisUIRaycastProcess = new BasisUIRaycastProcess();

        /// <summary>
        /// Singleton-style reference to the active handler.
        /// </summary>
        public static BasisInputModuleHandler Instance;

        private readonly BasisLocks.LockContext MovementLock = BasisLocks.GetContext(BasisLocks.Movement);
        private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);

        /// <summary>
        /// Unity enable hook. Sets up input actions and initializes the raycast helper.
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            Instance = this;

            // Initialize the input actions for Tab and Enter keys
            tabAction = new InputAction(binding: "<Keyboard>/tab");
            tabAction.performed += OnTabPerformed;
            tabAction.Enable();

            enterAction = new InputAction(binding: "<Keyboard>/enter");
            enterAction.performed += OnEnterPerformed;
            enterAction.Enable();

            // Keypad Enter
            keypadEnterAction = new InputAction(binding: "<Keyboard>/numpadEnter");
            keypadEnterAction.performed += OnEnterPerformed;
            keypadEnterAction.Enable();

            basisUIRaycastProcess.Initialize();
        }

        /// <summary>
        /// Unity disable hook. Tears down input actions and listeners.
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();

            UnsubscribePhysicalKeyboard();
            BasisTextFieldCaret.RestoreOverflowMode();

            tabAction.Disable();
            enterAction.Disable();
            keypadEnterAction.Disable();

            tabAction.performed -= OnTabPerformed;
            enterAction.performed -= OnEnterPerformed;
            keypadEnterAction.performed -= OnEnterPerformed;
            basisUIRaycastProcess.OnDeInitialize();
        }

        private void SubscribePhysicalKeyboard()
        {
            if (physicalKeyboardSubscribed) return;
            if (!ShouldForwardPhysicalKeyboard()) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            keyboard.onTextInput += OnTextInput;
            subscribedKeyboard = keyboard;
            physicalKeyboardSubscribed = true;
        }

        private void UnsubscribePhysicalKeyboard()
        {
            if (!physicalKeyboardSubscribed) return;

            if (subscribedKeyboard != null)
            {
                subscribedKeyboard.onTextInput -= OnTextInput;
            }
            subscribedKeyboard = null;
            physicalKeyboardSubscribed = false;
        }

        private static bool ShouldForwardPhysicalKeyboard()
        {
            return BasisDeviceManagement.IsCurrentModeVR() || BasisDeviceManagement.IsMobileHardware();
        }

        private void OnTextInput(char character)
        {
            if (char.IsControl(character))
            {
                HandleControlCharacter(character);
            }
            else
            {
                HandleTextCharacter(character);
            }
        }

        private void HandleControlCharacter(char character)
        {
            if (character == '\b') // Backspace
            {
                BasisTextFieldCaret.DeleteBeforeCaret(CurrentSelectedTMP_InputField, CurrentSelectedInputField);
            }
        }

        private void HandleTextCharacter(char character)
        {
            BasisTextFieldCaret.InsertAtCaret(CurrentSelectedTMP_InputField, CurrentSelectedInputField, character.ToString());
        }

        /// <summary>
        /// Core event processing loop. Manages focus, movement locks, virtual keyboard, and selection state.
        /// </summary>
        public override void Process()
        {
            var localPlayer = BasisLocalPlayer.Instance; // currently unused but kept for context
            basisUIRaycastProcess.Simulate();

            if (EventSystem.currentSelectedGameObject != null)
            {
                var data = GetBaseEventData();

                if (EventSystem.currentSelectedGameObject.TryGetComponent(out CurrentSelectedTMP_InputField))
                {
                    CurrentSelectedInputField = null;
                    if (BasisMenuVirtualKeyboardPanel.HasInstance)
                    {
                        BasisMenuVirtualKeyboardPanel.Instance.RetargetInput(null, CurrentSelectedTMP_InputField);
                    }
                    if (HasHoverONInput == false)
                    {
                        HasHoverONInput = true;
                        MovementLock.Add(nameof(BasisInputModuleHandler));
                        CrouchingLock.Add(nameof(BasisInputModuleHandler));
                        SubscribePhysicalKeyboard();
                        if (KeyboardRequired())
                        {
                            if (BasisMenuVirtualKeyboardPanel.HasInstance == false)
                            {
                                BasisMenuVirtualKeyboardPanel.CreateNew(CurrentSelectedInputField, CurrentSelectedTMP_InputField);
                            }
                        }
                    }
                }
                else
                {
                    if (EventSystem.currentSelectedGameObject.TryGetComponent(out CurrentSelectedInputField))
                    {
                        CurrentSelectedTMP_InputField = null;
                        if (BasisMenuVirtualKeyboardPanel.HasInstance)
                        {
                            BasisMenuVirtualKeyboardPanel.Instance.RetargetInput(CurrentSelectedInputField, null);
                        }
                        if (HasHoverONInput == false)
                        {
                            HasHoverONInput = true;
                            MovementLock.Add(nameof(BasisInputModuleHandler));
                            CrouchingLock.Add(nameof(BasisInputModuleHandler));
                            SubscribePhysicalKeyboard();
                            if (KeyboardRequired())
                            {
                                if (BasisMenuVirtualKeyboardPanel.HasInstance == false)
                                {
                                    BasisMenuVirtualKeyboardPanel.CreateNew(CurrentSelectedInputField, CurrentSelectedTMP_InputField);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                if (HasHoverONInput)
                {
                    HasHoverONInput = false;
                    UnsubscribePhysicalKeyboard();
                    CurrentSelectedTMP_InputField = null;
                    CurrentSelectedInputField = null;
                    MovementLock.Remove(nameof(BasisInputModuleHandler));
                    CrouchingLock.Remove(nameof(BasisInputModuleHandler));
                    var data = GetBaseEventData();
                    ExecuteEvents.Execute(EventSystem.currentSelectedGameObject, data, ExecuteEvents.submitHandler);
                }
            }

            if (HasHoverONInput)
            {
                HandleCaretNavigation();
                TrackCaretVisibility();
            }
            else
            {
                caretHeldDirection = 0;
                lastCaretVisibilityField = null;
                BasisTextFieldCaret.RestoreOverflowMode();
            }
        }

        private void TrackCaretVisibility()
        {
            TMP_InputField tmp = CurrentSelectedTMP_InputField;
            if (tmp == null && BasisMenuVirtualKeyboardPanel.HasInstance)
            {
                tmp = BasisMenuVirtualKeyboardPanel.Instance.TMPInputField;
            }
            if (tmp == null)
            {
                lastCaretVisibilityField = null;
                return;
            }

            int position = tmp.stringPosition;
            int length = tmp.text != null ? tmp.text.Length : 0;
            if (tmp != lastCaretVisibilityField || position != lastCaretVisibilityPosition || length != lastCaretVisibilityTextLength)
            {
                lastCaretVisibilityField = tmp;
                lastCaretVisibilityPosition = position;
                lastCaretVisibilityTextLength = length;
                BasisTextFieldCaret.EnsureCaretVisible(tmp);
            }
        }

        private void HandleCaretNavigation()
        {
            TMP_InputField tmp = CurrentSelectedTMP_InputField;
            InputField legacy = CurrentSelectedInputField;
            if (tmp == null && legacy == null && BasisMenuVirtualKeyboardPanel.HasInstance)
            {
                tmp = BasisMenuVirtualKeyboardPanel.Instance.TMPInputField;
                legacy = BasisMenuVirtualKeyboardPanel.Instance.InputField;
            }
            if (tmp == null && legacy == null)
            {
                caretHeldDirection = 0;
                return;
            }

            int direction = 0;
            bool shift = false;
            bool ctrl = false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                bool left = keyboard.leftArrowKey.isPressed;
                bool right = keyboard.rightArrowKey.isPressed;
                if (left != right)
                {
                    direction = right ? 1 : -1;
                }
                shift = keyboard.shiftKey.isPressed;
                ctrl = keyboard.ctrlKey.isPressed;
            }

            if (direction == 0)
            {
                float stickX = ReadLeftHandStickX();
                if (stickX <= -CaretStickThreshold)
                {
                    direction = -1;
                }
                else if (stickX >= CaretStickThreshold)
                {
                    direction = 1;
                }
            }

            if (direction == 0)
            {
                caretHeldDirection = 0;
                return;
            }

            float time = Time.unscaledTime;
            if (direction != caretHeldDirection)
            {
                caretHeldDirection = direction;
                caretNextRepeatTime = time + CaretRepeatDelay;
            }
            else if (time < caretNextRepeatTime)
            {
                return;
            }
            else
            {
                caretNextRepeatTime = time + CaretRepeatInterval;
            }

            BasisTextFieldCaret.MoveCaret(tmp, legacy, direction, shift, ctrl);
        }

        private static float ReadLeftHandStickX()
        {
            BasisDeviceManagement deviceManagement = BasisDeviceManagement.Instance;
            if (deviceManagement == null)
            {
                return 0f;
            }
            var devices = deviceManagement.AllInputDevices;
            int count = devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = devices[Index];
                if (input != null && input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.LeftHand)
                {
                    return input.CurrentInputState.Primary2DAxisDeadZoned.x;
                }
            }
            return 0f;
        }
        public bool KeyboardRequired()
        {
            if (ForceKeyboard) return true;
            return BasisDeviceManagement.IsCurrentModeVR() || BasisDeviceManagement.IsMobileHardware();
        }

        /// <summary>
        /// Handles Tab navigation by selecting the next selectable UI element below the current one.
        /// </summary>
        private void OnTabPerformed(InputAction.CallbackContext context)
        {
            if (context.performed)
            {
                GameObject CurrentGameObject = EventSystem.currentSelectedGameObject;
                if (CurrentGameObject == null)
                {
                    return;
                }
                GameObject next = FindNextSelectable(CurrentGameObject);
                if (next != null)
                {
                    EventSystem.SetSelectedGameObject(next);
                }
            }
        }

        /// <summary>
        /// Handles Enter/KeypadEnter by submitting the current object and moving to the next selectable.
        /// </summary>
        private void OnEnterPerformed(InputAction.CallbackContext context)
        {
            if (context.performed)
            {
                GameObject current = EventSystem.currentSelectedGameObject;
                if (current != null)
                {
                    ExecuteEvents.Execute(current, new BaseEventData(EventSystem), ExecuteEvents.submitHandler);
                    EventSystem.SetSelectedGameObject(FindNextSelectable(current));
                }
            }
        }

        /// <summary>
        /// Finds the next selectable UI element below the current object.
        /// </summary>
        /// <param name="current">The currently selected GameObject.</param>
        /// <returns>The next selectable's GameObject, or null if none exists.</returns>
        private GameObject FindNextSelectable(GameObject current)
        {
            if (current.TryGetComponent(out Selectable Selectable))
            {
                Selectable nextSelectable = Selectable.FindSelectableOnDown();
                return nextSelectable != null ? nextSelectable.gameObject : null;
            }
            return null;
        }
        public bool IsTyping()
        {
            if (CurrentSelectedTMP_InputField != null)
                return CurrentSelectedTMP_InputField.isFocused;

            if (CurrentSelectedInputField != null)
                return CurrentSelectedInputField.isFocused;

            return false;
        }
    }
}
