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


using UnityEngine.UI;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the UI that can be vertically scrolled, specifically the part that's scrollable (viewport).
    /// Used by inspector debug data and console logs of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class ScrollViewport : Controller
    {
        private RawImage _image;
        private Mask _mask;
        private Flex _flex;

        internal Flex Flex => _flex;

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            var scrollView = owner as ScrollView;
            if (scrollView == null) return;

            _image = GameObject.AddComponent<RawImage>();
            _image.raycastTarget = true;

            _mask = GameObject.AddComponent<Mask>();
            _mask.showMaskGraphic = false;

            _flex = Append<Flex>("content");
            scrollView.ScrollRect.content = _flex.RectTransform;
            _flex.ScrollViewport = this;
        }
    }
}

