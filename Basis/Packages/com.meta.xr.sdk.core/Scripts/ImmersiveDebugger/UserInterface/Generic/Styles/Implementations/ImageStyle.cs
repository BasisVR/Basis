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
    /// This is a <see cref="ScriptableObject"/> that's storing the style of the image used by Immersive Debugger.
    /// Containing properties like sprite, colors under different states, pixel density multiplier etc.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class ImageStyle : Style
    {
        /// <summary>
        /// Whether this image is enabled or not. By default to true, only the None styled image is false.
        /// </summary>
        public bool enabled = true;
        /// <summary>
        /// The icon used by the image. Can be null.
        /// </summary>
        public Texture2D icon;
        /// <summary>
        /// The sprite texture used by the image, it can be null if not using it.
        /// </summary>
        public Sprite sprite;
        /// <summary>
        /// The default color of the image, can be used together with the sprite and shown for the default state.
        /// Default to white if not specified.
        /// </summary>
        public Color color = Color.white;
        /// <summary>
        /// The color of the image when it's being hovered, can be used together with the sprite.
        /// Default to white if not specified.
        /// This is color change is handled by Immersive Debugger UI code.
        /// </summary>
        public Color colorHover = Color.white;
        /// <summary>
        /// The color of the image when it's off, can be used together with the sprite.
        /// Default to white if not specified.
        /// It's being used for Toggle/Switch UI from Immersive Debugger.
        /// </summary>
        public Color colorOff = Color.white;
        /// <summary>
        /// The pixel density of the image
        /// </summary>
        public float pixelDensityMultiplier = 1.0f;
    }
}

