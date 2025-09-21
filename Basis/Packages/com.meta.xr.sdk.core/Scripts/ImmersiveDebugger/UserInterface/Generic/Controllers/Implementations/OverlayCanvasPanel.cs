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
    /// This is a <see cref="MonoBehaviour"/> serves as the base of all the panels for Immersive Debugger.
    /// Basically it utilizes the <see cref="OVROverlay"/> (within the customized <see cref="OverlayCanvas"/>)
    /// so the rendering is done in overlay layer (composition layer) instead of projection layer.
    /// Note the panel will only use the overlay layer in the runtime (not in Editor / Link).
    /// It could help the panels display much sharper text. See more in the [OVROverlay doc](https://developer.oculus.com/documentation/unity/unity-ovroverlay/).
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class OverlayCanvasPanel : Panel
    {
        private OverlayCanvas _overlayCanvas;

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            // If we are not using overlay, there is no need to create the OverlayCanvas
            if (!RuntimeSettings.Instance.ShouldUseOverlay) return;

            _canvas.sortingOrder = -100;
            _canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.Normal |
                                               AdditionalCanvasShaderChannels.TexCoord1 |
                                               AdditionalCanvasShaderChannels.Tangent;
            _overlayCanvas = GameObject.AddComponent<OverlayCanvas>();
            _overlayCanvas.Panel = this;
        }
    }
}

