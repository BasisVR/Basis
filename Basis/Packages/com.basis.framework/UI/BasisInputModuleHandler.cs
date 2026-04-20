using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Virtual_keyboard;
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
        public BasisUIRaycastProcess basisUIRaycastProcess = new BasisUIRaycastProcess();

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

            basisUIRaycastProcess.Initalize();
        }

        /// <summary>
        /// Unity disable hook. Tears down input actions and listeners.
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();

            tabAction.Disable();
            enterAction.Disable();
            keypadEnterAction.Disable();

            tabAction.performed -= OnTabPerformed;
            enterAction.performed -= OnEnterPerformed;
            keypadEnterAction.performed -= OnEnterPerformed;
            basisUIRaycastProcess.OnDeInitalize();
        }

        // Note: keyboard character input handlers kept for completeness; currently unused.
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
                if (CurrentSelectedTMP_InputField != null)
                {
                    if (CurrentSelectedTMP_InputField.text.Length > 0)
                    {
                        CurrentSelectedTMP_InputField.text = CurrentSelectedTMP_InputField.text.Remove(CurrentSelectedTMP_InputField.text.Length - 1);
                        CurrentSelectedTMP_InputField.onValueChanged.Invoke(CurrentSelectedTMP_InputField.text);
                    }
                }
                else if (CurrentSelectedInputField != null)
                {
                    if (CurrentSelectedInputField.text.Length > 0)
                    {
                        CurrentSelectedInputField.text = CurrentSelectedInputField.text.Remove(CurrentSelectedInputField.text.Length - 1);
                        CurrentSelectedInputField.onValueChanged.Invoke(CurrentSelectedInputField.text);
                    }
                }
            }
        }

        private void HandleTextCharacter(char character)
        {
            if (CurrentSelectedTMP_InputField != null)
            {
                CurrentSelectedTMP_InputField.text += character;
                CurrentSelectedTMP_InputField.onValueChanged.Invoke(CurrentSelectedTMP_InputField.text);
            }
            else if (CurrentSelectedInputField != null)
            {
                CurrentSelectedInputField.text += character;
                CurrentSelectedInputField.onValueChanged.Invoke(CurrentSelectedInputField.text);
            }
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
                    if (HasHoverONInput == false)
                    {
                        HasHoverONInput = true;
                        MovementLock.Add(nameof(BasisInputModuleHandler));
                        CrouchingLock.Add(nameof(BasisInputModuleHandler));
                        if (KeyboardRequired())
                        {
                            if (BasisVirtualKeyboard.HasInstance == false)
                            {
                                BasisVirtualKeyboard.CreateMenu(CurrentSelectedInputField, CurrentSelectedTMP_InputField);
                            }
                        }
                    }
                }
                else
                {
                    if (EventSystem.currentSelectedGameObject.TryGetComponent(out CurrentSelectedInputField))
                    {
                        if (HasHoverONInput == false)
                        {
                            HasHoverONInput = true;
                            MovementLock.Add(nameof(BasisInputModuleHandler));
                            CrouchingLock.Add(nameof(BasisInputModuleHandler));
                            if (KeyboardRequired())
                            {
                                if (BasisVirtualKeyboard.HasInstance == false)
                                {
                                    BasisVirtualKeyboard.CreateMenu(CurrentSelectedInputField, CurrentSelectedTMP_InputField);
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
                    CurrentSelectedTMP_InputField = null;
                    CurrentSelectedInputField = null;
                    MovementLock.Remove(nameof(BasisInputModuleHandler));
                    CrouchingLock.Remove(nameof(BasisInputModuleHandler));
                    var data = GetBaseEventData();
                    ExecuteEvents.Execute(EventSystem.currentSelectedGameObject, data, ExecuteEvents.submitHandler);
                }
            }
        }
        public bool KeyboardRequired()
        {
            return (BasisDeviceManagement.IsCurrentModeVR() || ForceKeyboard || BasisDeviceManagement.IsCurrentModeVR() == false && BasisDeviceManagement.IsMobileHardware());
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
