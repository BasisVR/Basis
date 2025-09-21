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


namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> used for Button UI element that is represented by a label text.
    /// Used by mainly the debug data's action button UI in the in-headset Inspector panel in Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class ButtonWithLabel : Button
    {
        protected Label _label;
        protected Background _background;
        /// <summary>
        /// The background of the button
        /// </summary>
        public Background Background => _background;

        protected ImageStyle _backgroundStyle;
        /// <summary>
        /// The style of the background, can specify the detailed properties such as sprite and pixel density multiplier.
        /// Upon setting the style, a refresh of the style would be invoked to reflect in UI.
        /// </summary>
        public ImageStyle BackgroundStyle
        {
            set
            {
                _backgroundStyle = value;
                _background.Sprite = value.sprite;
                _background.PixelDensityMultiplier = value.pixelDensityMultiplier;
                RefreshStyle();
            }
        }

        /// <summary>
        /// The style (including alignment, color, font) of the text that is being displayed on top of the button UI
        /// </summary>
        public TextStyle TextStyle
        {
            set => _label.TextStyle = value;
        }

        /// <summary>
        /// The layout style (used for margins and sizing) of the text that is being displayed on top of the button UI
        /// </summary>
        public LayoutStyle LabelLayoutStyle
        {
            set => _label.LayoutStyle = value;
        }

        /// <summary>
        /// String of the label text that is being displayed on top of the button UI.
        /// </summary>
        public string Label
        {
            get => _label.Content;
            set => _label.Content = value;
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            _background = Append<Background>("background");
            _background.LayoutStyle = Style.Load<LayoutStyle>("Fill");

            _label = Append<Label>("label");
            _label.LayoutStyle = Style.Load<LayoutStyle>("Fill");
        }

        protected override void OnHoverChanged()
        {
            base.OnHoverChanged();
            RefreshStyle();
        }

        protected void RefreshStyle()
        {
            UpdateBackground();
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            _backgroundStyle.colorHover.a = Transparent ? 0.6f : 1f;
            _background.Color = Transparent ? _backgroundStyle.colorOff : _backgroundStyle.color;
        }

        protected virtual void UpdateBackground()
        {
            if (_backgroundStyle != null && _backgroundStyle.enabled)
            {
                _background.Show();
                _background.Color = Hover ? _backgroundStyle.colorHover : (Transparent ? _backgroundStyle.colorOff : _backgroundStyle.color);
                _background.RaycastTarget = true;
            }
            else
            {
                _background.Hide();
            }
        }
    }
}

