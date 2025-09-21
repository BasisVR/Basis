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
    /// This is a <see cref="MonoBehaviour"/> used for generic background of UI element.
    /// Allows using Sprite texture or pure Color, can set <see cref="PixelDensityMultiplier"/>.
    /// Used in Immersive Debugger for all the UI elements' background.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Background : Controller
    {
        private UnityEngine.UI.Image _image;

        /// <summary>
        /// The sprite texture used for the <see cref="Image"/> of the background.
        /// Can be null if not using it.
        /// </summary>
        public Sprite Sprite
        {
            set => _image.sprite = value;
        }

        /// <summary>
        /// The Color used for the <see cref="Image"/> of the background.
        /// Can work together with the <see cref="Sprite"/> property and change based on events like hover/click.
        /// </summary>
        public Color Color
        {
            set => _image.color = value;
        }

        /// <summary>
        /// The pixelsPerUnitMultiplier property for the <see cref="Image"/> of the background.
        /// Which is the pixel per unit modifier to change how sliced sprites are generated.
        /// </summary>
        public float PixelDensityMultiplier
        {
            set => _image.pixelsPerUnitMultiplier = value;
        }

        /// <summary>
        /// Sets the Raycast Target property of the instantiated <see cref="Image"/>
        /// </summary>
        public bool RaycastTarget
        {
            set => _image.raycastTarget = value;
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);
            _image = GameObject.AddComponent<UnityEngine.UI.Image>();
            _image.type = UnityEngine.UI.Image.Type.Sliced;

            RaycastTarget = false;
        }
    }
}
