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
    /// <summary>
    /// This is a <see cref="ScriptableObject"/> that's storing the style of the text used by Immersive Debugger.
    /// Containing properties like alignment type, font, size and color.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class TextStyle : Style
    {
        /// <summary>
        /// Where the anchor of the text is placed. There are 9 possible alignment styles,
        /// check out details in the <see cref="TextAnchor"/>.
        /// </summary>
        public TextAnchor textAlignement;
        /// <summary>
        /// The font of the text using Unity's builtin <see cref="Font"/> type. Assign it with font assets.
        /// </summary>
        public Font font;
        /// <summary>
        /// The font size of the text, by default to 14.
        /// </summary>
        public int fontSize = 14;
        /// <summary>
        /// The color for the text with Unity's builtin <see cref="Color"/> type. by default using white color.
        /// </summary>
        public Color color = Color.white;
    }
}

