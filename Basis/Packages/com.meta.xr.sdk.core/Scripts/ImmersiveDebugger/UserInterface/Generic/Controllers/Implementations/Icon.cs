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
    /// This is a <see cref="MonoBehaviour"/> for the generic Icon UI element,
    /// used by icons on the in-headset panels of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Icon : Controller
    {
        private RawImage _image;
        internal RawImage RawImage => _image;

        /// <summary>
        /// The texture used by the image of the icon
        /// </summary>
        public virtual Texture2D Texture
        {
            internal get => (UnityEngine.Texture2D)_image.texture;
            set => _image.texture = value;
        }

        /// <summary>
        /// The color used by the image of the icon,
        /// normally used as the background color that can work together with events like hover/click
        /// </summary>
        public Color Color
        {
            set => _image.color = value;
        }

        /// <summary>
        /// Sets the Raycast Target property of the instantiated <see cref="RawImage"/>
        /// </summary>
        public bool RaycastTarget
        {
            set => _image.raycastTarget = value;
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);
            _image = GameObject.AddComponent<RawImage>();

            RaycastTarget = false;
        }
    }
}
