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
using UnityEditor;

namespace Meta.XR.Editor.UserInterface
{
    internal class Toggle : IUserInterfaceItem
    {
        public bool Hide { get; set; }
        public bool State { get; set; }
        private readonly bool _toggleOnLeft;
        private readonly string _label;
        private readonly Action<bool> _onToggleChanged;

        public Toggle(string label = "", bool toggleOnLeft = true, Action<bool> onToggleChanged = null)
        {
            _label = label;
            _toggleOnLeft = toggleOnLeft;
            _onToggleChanged = onToggleChanged;
        }

        public void Draw()
        {
            var newState = _toggleOnLeft ? EditorGUILayout.ToggleLeft(_label, State) : EditorGUILayout.Toggle(_label, State);
            if (newState != State)
            {
                _onToggleChanged?.Invoke(newState);
            }
            State = newState;
        }
    }
}
