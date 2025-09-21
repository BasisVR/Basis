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


namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the toggle UI element which the label content can be customized (not a switch).
    /// It can be used for certain toggle buttons for Immersive Debugger UIs.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class ToggleWithLabel : ButtonWithLabel
    {
        private bool _state;

        /// <summary>
        /// State of the toggle, the style will be set respectively for on and off states.
        /// </summary>
        public bool State
        {
            get => _state;
            set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;
                OnStateChanged();
            }
        }

        /// <summary>
        /// The event that would be invoked when the state is changed by setting the <see cref="State"/> property.
        /// </summary>
        public Action<bool> StateChanged { get; set; }

        private void OnStateChanged()
        {
            StateChanged?.Invoke(State);
            RefreshStyle();
        }

        protected override void UpdateBackground()
        {
            if (_backgroundStyle != null && _backgroundStyle.enabled)
            {
                _background.Show();
                _background.Color = Hover ? _backgroundStyle.colorHover : State ? _backgroundStyle.colorHover : _backgroundStyle.color;
                _background.RaycastTarget = true;
            }
            else
            {
                _background.Hide();
            }
        }
    }
}

