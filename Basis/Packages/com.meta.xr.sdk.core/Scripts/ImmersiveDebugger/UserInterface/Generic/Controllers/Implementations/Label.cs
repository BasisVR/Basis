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
using UnityEngine.UI;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the generic Label UI element,
    /// used by labels on the in-headset panels of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Label : Controller
    {
        internal Text Text { get; private set; }

        private TextStyle _textStyle;

        /// <summary>
        /// String that is being used for displaying text content of the label.
        /// Might be truncated if too long (with overflow)
        /// </summary>
        public string Content
        {
            get => Text.text;
            set => Text.text = value;
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);
            Text = GameObject.AddComponent<Text>();

            Text.horizontalOverflow = HorizontalWrapMode.Overflow;
            Text.verticalOverflow = VerticalWrapMode.Overflow;
            Text.text = "";

            Text.raycastTarget = false;
        }

        /// <summary>
        /// The style used for the text, containing properties such as font, size, alignment and color.
        /// </summary>
        public TextStyle TextStyle
        {
            get => _textStyle;
            set
            {
                _textStyle = value;
                Text.font = value.font;
                Text.fontSize = value.fontSize;
                Text.alignment = value.textAlignement;
                Text.color = value.color;
            }
        }
    }
}

