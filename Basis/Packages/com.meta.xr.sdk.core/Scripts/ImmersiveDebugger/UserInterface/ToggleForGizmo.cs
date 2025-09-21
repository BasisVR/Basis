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


using Meta.XR.ImmersiveDebugger.Manager;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    internal class ToggleForGizmo : Toggle
    {
        private GizmoHook _hook;

        public GizmoHook Hook
        {
            get => _hook;
            set
            {
                if (_hook == value) return;

                _hook = value;

                // As we're setting a new hook, we don't want to propagate the change of the state
                // for its initialization
                StateChanged = null;
                State = value.GetState?.Invoke() ?? false;
                StateChanged = value.SetState;
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            Callback = () => State = !State;
        }
    }
}
