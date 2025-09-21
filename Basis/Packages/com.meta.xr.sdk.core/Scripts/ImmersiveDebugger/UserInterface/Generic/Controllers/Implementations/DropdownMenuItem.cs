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

using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    internal class DropdownMenuItem : ButtonWithLabel
    {
        private Dropdown _dropdown;

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);
            LayoutStyle = Style.Load<LayoutStyle>("DropdownValueItem");
            TextStyle = Style.Instantiate<TextStyle>("MemberValue");
            BackgroundStyle = Style.Instantiate<ImageStyle>("DropdownValueBackground");
        }

        internal void RegisterDropdownSourceMenu(Dropdown dropdown) => _dropdown = dropdown;

        public override void OnPointerClick()
        {
            base.OnPointerClick();
            _dropdown?.OnMenuItemClick(this);
        }
    }
}
