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
using Meta.XR.ImmersiveDebugger.Manager;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the switch UI element.
    /// It's used for the boolean debug data's tweaking in the Inspector panel of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Switch : ButtonWithIcon
    {
        private Texture2D _toggleIconOn;
        private Texture2D _toggleIconOff;

        internal Tweak Tweak { get; set; }
        /// <summary>
        /// State of the switch, the style will be set respectively for on and off states.
        /// </summary>
        public bool State
        {
            get => Tweak != null && Math.Abs(Tweak.Tween - 1.0f) < Mathf.Epsilon;
            set
            {
                Tweak.Tween = value ? 1.0f : 0.0f;
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

        private void Start()
        {
            State = Tweak.Tween > 0;
            UpdateIcon();
        }

        internal void SetToggleIcons(Texture2D onState, Texture2D offState)
        {
            _toggleIconOn = onState;
            _toggleIconOff = offState;
        }

        protected override void UpdateIcon()
        {
            Icon = State ? _toggleIconOn : _toggleIconOff;
            _icon.Color = Hover ? _iconStyle.colorHover : State ? _iconStyle.color : _iconStyle.colorOff;
            _icon.RaycastTarget = _backgroundStyle == null || !_backgroundStyle.enabled;
        }
    }
}

