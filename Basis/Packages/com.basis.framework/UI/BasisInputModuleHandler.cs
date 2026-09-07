using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
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

        private readonly List<char> pendingCharacters = new List<char>();
        private bool textEventsObserved;
        private bool hasTextLengthSnapshot;
        private int lastTextLength;
        private Key heldTextKey = Key.None;
        private float textKeyNextRepeatTime;
        private bool capsLockActive;

        private TMP_InputField lastCaretVisibilityField;
        private int lastCaretVisibilityPosition;
        private int lastCaretVisibilityTextLength;

        private UnityEngine.Object caretArbiterTarget;
        private bool hasCaretArbiterSnapshot;
        private int caretArbiterPosition;
        private int caretArbiterLength;
        private bool fieldDrivesOwnCaret;

        private bool compositionActive;

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
            ResetPhysicalKeyboardText();
            ResetCaretArbiter();
            compositionActive = false;
            BasisTextFieldCaret.RestoreOverflowMode();

            tabAction.Disable();
            enterAction.Disable();
            keypadEnterAction.Disable();

            tabAction.performed -= OnTabPerformed;
            enterAction.performed -= OnEnterPerformed;
            keypadEnterAction.performed -= OnEnterPerformed;
            basisUIRaycastProcess.OnDeInitialize();
        }

        private void EnsurePhysicalKeyboardSubscription()
        {
            if (!ShouldForwardPhysicalKeyboard()) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == subscribedKeyboard && physicalKeyboardSubscribed) return;

            UnsubscribePhysicalKeyboard();
            SubscribePhysicalKeyboard();
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
            textEventsObserved = true;
            pendingCharacters.Add(character);
        }

        private void HandlePhysicalKeyboardText()
        {
            ResolveTextTargets(out TMP_InputField tmp, out InputField legacy);
            if (tmp == null && legacy == null)
            {
                ResetPhysicalKeyboardText();
                return;
            }

            if (textEventsObserved == false)
            {
                ScanPhysicalKeyboardKeys();
            }

            int length = ReadTextLength(tmp, legacy);
            if (hasTextLengthSnapshot == false)
            {
                hasTextLengthSnapshot = true;
                lastTextLength = length;
                return;
            }

            if (length != lastTextLength)
            {
                pendingCharacters.Clear();
                lastTextLength = length;
                return;
            }

            int count = pendingCharacters.Count;
            for (int Index = 0; Index < count; Index++)
            {
                char character = pendingCharacters[Index];
                if (char.IsControl(character))
                {
                    HandleControlCharacter(character, tmp, legacy);
                }
                else
                {
                    HandleTextCharacter(character, tmp, legacy);
                }
            }

            if (count != 0)
            {
                pendingCharacters.Clear();
                lastTextLength = ReadTextLength(tmp, legacy);
            }
        }

        private void ScanPhysicalKeyboardKeys()
        {
            Keyboard keyboard = subscribedKeyboard;
            if (keyboard == null) return;

            if (keyboard[Key.CapsLock].wasPressedThisFrame)
            {
                capsLockActive = !capsLockActive;
            }

            if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed)
            {
                heldTextKey = Key.None;
                return;
            }

            bool shift = keyboard.shiftKey.isPressed;
            float time = Time.unscaledTime;

            Key[] keys = BasisPhysicalKeyboardText.TextKeys;
            int count = keys.Length;
            for (int Index = 0; Index < count; Index++)
            {
                Key key = keys[Index];
                if (keyboard[key].wasPressedThisFrame == false) continue;
                if (BasisPhysicalKeyboardText.TryGetCharacter(key, shift, capsLockActive, out char character) == false) continue;

                pendingCharacters.Add(character);
                heldTextKey = key;
                textKeyNextRepeatTime = time + CaretRepeatDelay;
            }

            if (heldTextKey == Key.None) return;

            if (keyboard[heldTextKey].isPressed == false)
            {
                heldTextKey = Key.None;
                return;
            }

            if (time < textKeyNextRepeatTime) return;

            if (BasisPhysicalKeyboardText.TryGetCharacter(heldTextKey, shift, capsLockActive, out char repeated))
            {
                pendingCharacters.Add(repeated);
            }
            textKeyNextRepeatTime = time + CaretRepeatInterval;
        }

        private void ResetPhysicalKeyboardText()
        {
            pendingCharacters.Clear();
            hasTextLengthSnapshot = false;
            lastTextLength = 0;
            heldTextKey = Key.None;
        }

        private void ResolveTextTargets(out TMP_InputField tmp, out InputField legacy)
        {
            tmp = CurrentSelectedTMP_InputField;
            legacy = CurrentSelectedInputField;
            if (tmp == null && legacy == null && BasisMenuVirtualKeyboardPanel.HasInstance)
            {
                tmp = BasisMenuVirtualKeyboardPanel.Instance.TMPInputField;
                legacy = BasisMenuVirtualKeyboardPanel.Instance.InputField;
            }
        }

        private static int ReadTextLength(TMP_InputField tmp, InputField legacy)
        {
            if (tmp != null)
            {
                return tmp.text != null ? tmp.text.Length : 0;
            }
            if (legacy != null)
            {
                return legacy.text != null ? legacy.text.Length : 0;
            }
            return 0;
        }

        private void HandleControlCharacter(char character, TMP_InputField tmp, InputField legacy)
        {
            if (character == '\b') // Backspace
            {
                BasisTextFieldCaret.DeleteBeforeCaret(tmp, legacy);
            }
        }

        private void HandleTextCharacter(char character, TMP_InputField tmp, InputField legacy)
        {
            BasisTextFieldCaret.InsertAtCaret(tmp, legacy, character.ToString());
        }

        /// <summary>
        /// Core event processing loop. Manages focus, movement locks, virtual keyboard, and selection state.
        /// </summary>
        public override void Process()
        {
            var localPlayer = BasisLocalPlayer.Instance; // currently unused but kept for context
            basisUIRaycastProcess.Simulate();

            // Selection moving to a non-field selectable — a dropdown option after typing in its
            // search box, any button — must release the typing locks exactly like a null selection,
            // or crouch and movement stay locked until something clears UI selection entirely.
            bool selectionIsInputField = false;
            if (EventSystem.currentSelectedGameObject != null)
            {
                var data = GetBaseEventData();

                if (EventSystem.currentSelectedGameObject.TryGetComponent(out CurrentSelectedTMP_InputField))
                {
                    selectionIsInputField = true;
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
                        selectionIsInputField = true;
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

            if (selectionIsInputField == false && HasHoverONInput)
            {
                HasHoverONInput = false;
                UnsubscribePhysicalKeyboard();
                CurrentSelectedTMP_InputField = null;
                CurrentSelectedInputField = null;
                MovementLock.Remove(nameof(BasisInputModuleHandler));
                CrouchingLock.Remove(nameof(BasisInputModuleHandler));
            }

            if (HasHoverONInput)
            {
                compositionActive = ReadCompositionActive();
                EnsurePhysicalKeyboardSubscription();
                HandleCaretNavigation();
                HandlePhysicalKeyboardText();
                TrackCaretVisibility();
            }
            else
            {
                compositionActive = false;
                caretHeldDirection = 0;
                lastCaretVisibilityField = null;
                ResetCaretArbiter();
                ResetPhysicalKeyboardText();
                BasisTextFieldCaret.RestoreOverflowMode();
            }
        }

        private bool ReadCompositionActive()
        {
            BaseInput baseInput = input;
            if (baseInput == null)
            {
                return false;
            }
            return string.IsNullOrEmpty(baseInput.compositionString) == false;
        }

        /// <summary>
        /// True while an IME composition is being converted, in which case Enter/Tab belong to the
        /// IME rather than to UI submit/navigation.
        /// </summary>
        public bool IsComposingText()
        {
            if (HasHoverONInput == false)
            {
                return false;
            }
            return compositionActive || ReadCompositionActive();
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
            ResolveTextTargets(out TMP_InputField tmp, out InputField legacy);
            if (tmp == null && legacy == null)
            {
                caretHeldDirection = 0;
                ResetCaretArbiter();
                return;
            }

            UnityEngine.Object target = tmp != null ? tmp : (UnityEngine.Object)legacy;
            if (target != caretArbiterTarget)
            {
                caretArbiterTarget = target;
                hasCaretArbiterSnapshot = false;
                fieldDrivesOwnCaret = false;
            }

            bool focused = tmp != null ? tmp.isFocused : legacy.isFocused;
            if (focused == false)
            {
                fieldDrivesOwnCaret = false;
            }

            int caretPosition = ReadCaretPosition(tmp, legacy);
            int textLength = ReadTextLength(tmp, legacy);
            bool caretMovedElsewhere = hasCaretArbiterSnapshot && textLength == caretArbiterLength && caretPosition != caretArbiterPosition;

            int direction = 0;
            bool shift = false;
            bool ctrl = false;
            bool fromKeyboard = false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                bool left = keyboard.leftArrowKey.isPressed;
                bool right = keyboard.rightArrowKey.isPressed;
                if (left != right)
                {
                    direction = right ? 1 : -1;
                    fromKeyboard = true;
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
                SnapshotCaretArbiter(tmp, legacy);
                return;
            }

            if (fromKeyboard && focused)
            {
                if (caretMovedElsewhere && (caretPosition > caretArbiterPosition ? 1 : -1) == direction)
                {
                    fieldDrivesOwnCaret = true;
                }
                if (fieldDrivesOwnCaret)
                {
                    caretHeldDirection = 0;
                    SnapshotCaretArbiter(tmp, legacy);
                    return;
                }
            }

            float time = Time.unscaledTime;
            if (direction != caretHeldDirection)
            {
                caretHeldDirection = direction;
                caretNextRepeatTime = time + CaretRepeatDelay;
            }
            else if (time < caretNextRepeatTime)
            {
                SnapshotCaretArbiter(tmp, legacy);
                return;
            }
            else
            {
                caretNextRepeatTime = time + CaretRepeatInterval;
            }

            BasisTextFieldCaret.MoveCaret(tmp, legacy, direction, shift, ctrl);
            SnapshotCaretArbiter(tmp, legacy);
        }

        private static int ReadCaretPosition(TMP_InputField tmp, InputField legacy)
        {
            if (tmp != null)
            {
                return tmp.stringPosition;
            }
            if (legacy != null)
            {
                return legacy.caretPosition;
            }
            return 0;
        }

        private void SnapshotCaretArbiter(TMP_InputField tmp, InputField legacy)
        {
            caretArbiterPosition = ReadCaretPosition(tmp, legacy);
            caretArbiterLength = ReadTextLength(tmp, legacy);
            hasCaretArbiterSnapshot = true;
        }

        private void ResetCaretArbiter()
        {
            caretArbiterTarget = null;
            hasCaretArbiterSnapshot = false;
            fieldDrivesOwnCaret = false;
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
                if (IsComposingText())
                {
                    return;
                }
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
                if (IsComposingText())
                {
                    return;
                }
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
