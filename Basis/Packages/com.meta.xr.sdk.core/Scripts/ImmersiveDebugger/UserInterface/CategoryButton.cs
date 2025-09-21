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
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the Immersive Debugger UIs.
    /// It represents a Category in the Inspector Panel and handles its bespoke behaviour.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    internal class CategoryButton : Toggle
    {
        private Category _category;
        private int _counter;

        private Label _label;
        private Label _subLabel;
        private Flex _flex;

        internal Category Category
        {
            get => _category;
            set
            {
                _category = value;
                _label.Content = _category.Label;
            }
        }

        internal int Counter
        {
            get => _counter;
            set
            {
                _counter = value;
                _counter = Math.Max(0, _counter);
                _subLabel.Content = _counter switch
                {
                    0 => "No objects tracked",
                    1 => "1 object tracked",
                    _ => $"{_counter} objects tracked"
                };
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            // Flex
            _flex = Append<Flex>("flex");
            _flex.LayoutStyle = Style.Load<LayoutStyle>("CategoryButtonFlex");

            // Label
            _label = _flex.Append<Label>("label");
            _label.LayoutStyle = Style.Load<LayoutStyle>("CategoryLabel");
            _label.TextStyle = Style.Load<TextStyle>("CategoryLabel");

            // SubLabel
            _subLabel = _flex.Append<Label>("sublabel");
            _subLabel.LayoutStyle = Style.Load<LayoutStyle>("CategorySubLabel");
            _subLabel.TextStyle = Style.Load<TextStyle>("CategorySubLabel");

            IconStyle = Style.Load<ImageStyle>("None");
            BackgroundStyle = Style.Instantiate<ImageStyle>("CategoryButtonBackground");
        }
    }
}
