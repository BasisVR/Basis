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
using UnityEngine.Events;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the console line UI element on console panel of Immersive Debugger.
    /// Contains UI elements like Pill, Background, Log label, Counter badge (representing multiple identical logs).
    /// Display the content of the <see cref="LogEntry"/> and make sure layout is correct with clamping.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class ConsoleLine : InteractableController
    {
        private Label _label;
        private Flex _flex;
        private Background _background;
        private Background _pill;
        private LogEntry _entry;
        internal UnityEvent<LogEntry> OnClick = new();
        private Label _counterLabel;
        private Background _counterBackground;
        private ImageStyle _backgroundImageStyle;

        private const int MaxLabelCharacterSize = 116;
        private const int DefaultCounterBackgroundWidth = 16;
        private const int MaxCounterBackgroundWidth = 64;

        internal LogEntry Entry
        {
            get => _entry;
            set
            {
                if (_entry == value) return;

                _entry = value;
                Label = Utils.ClampText(value.Label, MaxLabelCharacterSize);
                PillStyle = value.Severity.PillStyle;

                RefreshLogCounter();
            }
        }

        internal string Label
        {
            get => _label.Content;
            set => _label.Content = value;
        }

        internal ImageStyle BackgroundStyle
        {
            set
            {
                _background.Sprite = value.sprite;
                _background.Color = value.color;
                _background.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        internal ImageStyle PillStyle
        {
            set
            {
                _pill.Sprite = value.sprite;
                _pill.Color = value.color;
                _pill.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            // Background
            _background = Append<Background>("background");
            _background.LayoutStyle = Style.Load<LayoutStyle>("Fill");
            _backgroundImageStyle = Style.Load<ImageStyle>("ConsoleLineBackground");
            BackgroundStyle = _backgroundImageStyle;
            _background.RaycastTarget = true;

            // Flex
            _flex = Append<Flex>("line");
            _flex.LayoutStyle = Style.Load<LayoutStyle>("ConsoleLineFlex");

            // Pill
            _pill = _flex.Append<Background>("pill");
            _pill.LayoutStyle = Style.Load<LayoutStyle>("PillVertical");

            // Label
            _label = _flex.Append<Label>("log");
            _label.LayoutStyle = Style.Load<LayoutStyle>("ConsoleLineLabel");
            _label.TextStyle = Style.Load<TextStyle>("ConsoleLineLabel");
            _label.Text.verticalOverflow = VerticalWrapMode.Truncate;

            // Log Counter
            _counterBackground = _flex.Append<Background>("counterbackground");
            _counterBackground.LayoutStyle = Instantiate(Style.Load<LayoutStyle>("MiniCounter"));
            var style = Style.Load<ImageStyle>("MiniCounter");
            _counterBackground.Sprite = style.sprite;
            _counterBackground.Color = style.color;
            _counterBackground.PixelDensityMultiplier = style.pixelDensityMultiplier;

            _counterLabel = _counterBackground.Append<Label>("counter");
            _counterLabel.LayoutStyle = Instantiate(Style.Load<LayoutStyle>("MiniCounterValue"));
            _counterLabel.TextStyle = Style.Load<TextStyle>("ConsoleLogCounter");
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            _backgroundImageStyle.colorHover.a = Transparent ? 0.6f : 1f;
            _background.Color = Transparent ? _backgroundImageStyle.colorOff : _backgroundImageStyle.color;
        }

        private void RefreshLogCounter()
        {
            if (_counterBackground == null || _counterLabel == null) return;

            var collapse = Entry.Severity.Owner.LogCollapseMode;
            var showCounter = Entry.Count > 1 && collapse;
            ShowCounter(showCounter);
            if (showCounter)
            {
                _counterLabel.Content = Entry.Count.ToString();
                _counterBackground.LayoutStyle.size.x = Mathf.Clamp(_counterLabel.Text.preferredWidth + 8, DefaultCounterBackgroundWidth, MaxCounterBackgroundWidth);
                _counterBackground.RefreshLayout();
            }

            _label.RefreshLayout();
        }

        private void ShowCounter(bool show = true)
        {
            if (show)
            {
                _counterBackground.Show();
                _counterLabel.Show();
            }
            else
            {
                _counterBackground.Hide();
                _counterLabel.Hide();
            }
        }

        /// <summary>
        /// Handler for the OnPointerClick event, when clicked the console will display a stacktrace of the log entry.
        /// </summary>
        public override void OnPointerClick() => Entry?.DisplayDetails();

        protected override void OnHoverChanged()
        {
            base.OnHoverChanged();
            _background.Color = Hover ? _backgroundImageStyle.colorHover : Transparent ? _backgroundImageStyle.colorOff : _backgroundImageStyle.color;
        }
    }

    internal class ProxyConsoleLine : ProxyController<ConsoleLine>
    {
        public LogEntry Entry { get; set; }

        protected override void Fill()
        {
            Target.Entry = Entry;
        }
    }
}
