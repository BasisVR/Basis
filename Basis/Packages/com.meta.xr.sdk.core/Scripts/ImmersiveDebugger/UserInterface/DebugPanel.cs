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


using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> representing Panel UI (base class of actual panels) of Immersive Debugger.
    /// Inheriting from <see cref="OverlayCanvasPanel"/> that's using the OVROverlay layer for rendering.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class DebugPanel : OverlayCanvasPanel
    {
        private Label _title;
        private ButtonWithIcon _closeIcon;

        private const float DynamicPixelsPerUnit =
#if !UNITY_EDITOR
            10.0f
#else
            2.0f
#endif
            ;

        /// <summary>
        /// The Icon of the panel, used for displaying on the <see cref="DebugBar"/> and toggling visibility of the panel itself.
        /// </summary>
        public Texture2D Icon { get; set; }

        /// <summary>
        /// The Title of the panel, used for displaying at the bottom center of the panel to identify the panel.
        /// </summary>
        public string Title
        {
            get => _title.Content;
            set => _title.Content = value;
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            _title = Append<Label>("title");
            _title.LayoutStyle = Style.Load<LayoutStyle>("PanelTitle");
            _title.TextStyle = Style.Load<TextStyle>("PanelTitle");

            _closeIcon = Append<ButtonWithIcon>("CloseButton");
            _closeIcon.LayoutStyle = Style.Load<LayoutStyle>("CloseButton");
            _closeIcon.BackgroundStyle = Style.Load<ImageStyle>("CloseButtonBackground");
            _closeIcon.Icon = Resources.Load<Texture2D>("Textures/minimize_icon");
            _closeIcon.IconStyle = Style.Load<ImageStyle>("CloseButtonIcon");
            _closeIcon.Callback = Hide;

            SetExpectedPixelsPerUnit(1000.0f, DynamicPixelsPerUnit, 2.24f);
        }
    }
}

