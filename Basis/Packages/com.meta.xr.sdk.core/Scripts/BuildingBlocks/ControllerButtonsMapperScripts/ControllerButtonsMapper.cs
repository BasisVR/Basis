/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

#if ENABLE_INPUT_SYSTEM && UNITY_NEW_INPUT_SYSTEM_INSTALLED
using UnityEngine.InputSystem;
#endif

// ReSharper disable HeuristicUnreachableCode
#pragma warning disable CS0162 // Unreachable code detected

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// A utility class for mapping controller buttons easily.
    /// </summary>
    /// <example>
    /// <code>
    /// // Instantiate a new button action
    /// var buttonAction = new ControllerButtonsMapper.ButtonClickAction
    /// {
    ///     Title = "Spawn Object",
    ///     Button = OVRInput.Button.PrimaryIndexTrigger,
    ///     ButtonMode = ControllerButtonsMapper.ButtonClickAction.ButtonClickMode.OnButtonUp,
    ///     Callback = new UnityEvent()
    /// };
    /// </code>
    /// </example>
    public class ControllerButtonsMapper : MonoBehaviour
    {
        /// <summary>
        /// A struct to consolidate all the options for a button action.
        /// </summary>
        /// <example>
        /// <code>
        /// // Instantiate a new button action
        /// var buttonAction = new ControllerButtonsMapper.ButtonClickAction
        /// {
        ///     Title = "Spawn Object",
        ///     Button = OVRInput.Button.PrimaryIndexTrigger,
        ///     ButtonMode = ControllerButtonsMapper.ButtonClickAction.ButtonClickMode.OnButtonUp,
        ///     Callback = new UnityEvent()
        /// };
        /// </code>
        /// </example>
        [Serializable]
        public struct ButtonClickAction
        {
            /// <summary>
            /// Button click mode types.
            /// </summary>
            /// <remarks>
            /// OnButtonDown will trigger on the first frame when the button is down.
            /// OnButtonUp will trigger on the first frame when the user presses releases the button.
            /// OnButton triggers repeatedly when the user holds the button down.
            /// </remarks>
            public enum ButtonClickMode
            {
                OnButtonUp,
                OnButtonDown,
                OnButton
            }

            /// <summary>
            /// A title for this button action.
            /// </summary>
            public string Title;

            /// <summary>
            /// Sets the button that will trigger the <see cref="Callback"/> when the <see cref="ButtonMode"/> is detected (usually ButtonMode.OnButtonUp).
            /// </summary>
            public OVRInput.Button Button;

            /// <summary>
            /// Button click type: OnButtonUp, OnButtonDown, and OnButton. Use OnButtonUp to trigger the callback when the user releases the button.
            /// </summary>
            public ButtonClickMode ButtonMode;

#if ENABLE_INPUT_SYSTEM && UNITY_NEW_INPUT_SYSTEM_INSTALLED
            public InputActionReference InputActionReference;

            /// <summary>
            /// Dispatches when <see cref="InputActionReference"/> is performed with additional context as a parameter.
            /// </summary>
            public UnityEvent<InputAction.CallbackContext> CallbackWithContext;

            public void OnCallbackWithContext(InputAction.CallbackContext callbackContext)
            {
                CallbackWithContext?.Invoke(callbackContext);
            }
#endif

            /// <summary>
            /// Dispatches when <see cref="Button"/> matches the chosen <see cref="ButtonMode"/>.
            /// </summary>
            public UnityEvent Callback;
        }

        [SerializeField] private List<ButtonClickAction> _buttonClickActions;

        /// <summary>
        /// A list of <see cref="ButtonClickAction"/> to trigger.
        /// </summary>
        public List<ButtonClickAction> ButtonClickActions
        {
            get => _buttonClickActions;
            set => _buttonClickActions = value;
        }

#if ENABLE_INPUT_SYSTEM && UNITY_NEW_INPUT_SYSTEM_INSTALLED
        internal const bool UseNewInputSystem = true;

        private void OnEnable()
        {
            foreach (var buttonClickAction in ButtonClickActions)
            {
                if (buttonClickAction.InputActionReference == null)
                {
                    continue;
                }

                buttonClickAction.InputActionReference.action.Enable();
                buttonClickAction.InputActionReference.action.performed += buttonClickAction.OnCallbackWithContext;
            }
        }

        private void OnDisable()
        {
            foreach (var buttonClickAction in ButtonClickActions)
            {
                if (buttonClickAction.InputActionReference == null)
                {
                    continue;
                }

                buttonClickAction.InputActionReference.action.Disable();
                buttonClickAction.InputActionReference.action.performed -= buttonClickAction.OnCallbackWithContext;
            }
        }
#else
        internal const bool UseNewInputSystem = false;
#endif

#if ENABLE_LEGACY_INPUT_MANAGER || !UNITY_NEW_INPUT_SYSTEM_INSTALLED
        internal const bool UseLegacyInputSystem = true;
#else
        internal const bool UseLegacyInputSystem = false;
#endif

        private void Update()
        {
            foreach (var buttonClickAction in ButtonClickActions)
            {
                if (IsActionTriggered(buttonClickAction))
                {
                    buttonClickAction.Callback?.Invoke();
                }
            }
        }

        private static bool IsActionTriggered(ButtonClickAction buttonClickAction) =>
            IsLegacyInputActionTriggered(buttonClickAction.ButtonMode, buttonClickAction.Button) ||
            IsNewInputSystemActionTriggered(buttonClickAction);

        private static bool IsLegacyInputActionTriggered(ButtonClickAction.ButtonClickMode buttonMode,
            OVRInput.Button button)
        {
            if (!UseLegacyInputSystem)
            {
                return false;
            }

            if (button == OVRInput.Button.None)
            {
                return false;
            }

            return buttonMode switch
            {
                ButtonClickAction.ButtonClickMode.OnButtonUp => OVRInput.GetUp(button),
                ButtonClickAction.ButtonClickMode.OnButtonDown => OVRInput.GetDown(button),
                ButtonClickAction.ButtonClickMode.OnButton => OVRInput.Get(button),
                _ => false
            };
        }

        private static bool IsNewInputSystemActionTriggered(ButtonClickAction buttonClickAction)
        {
#if ENABLE_INPUT_SYSTEM && UNITY_NEW_INPUT_SYSTEM_INSTALLED
            if (!UseNewInputSystem)
            {
                return false;
            }

            return buttonClickAction.InputActionReference != null &&
                   buttonClickAction.InputActionReference.action.triggered;
#endif
            return false;
        }
    }
}
