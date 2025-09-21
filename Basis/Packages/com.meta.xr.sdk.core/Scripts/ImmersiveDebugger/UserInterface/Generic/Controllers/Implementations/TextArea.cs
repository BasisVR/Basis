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


using System;
using UnityEngine;
using UnityEngine.UI;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the generic TextArea UI element,
    /// used by texts on the in-headset panels of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class TextArea : Value
    {
        private Text Text => Label.Text;

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            Text.horizontalOverflow = HorizontalWrapMode.Wrap;
            Text.verticalOverflow = VerticalWrapMode.Overflow;
            Text.text = "";
        }

        internal override string Content
        {
            get => Text.text;
            set
            {
                var newlineFormatted = value.Replace("\\n", Environment.NewLine);
                Text.text = newlineFormatted;
                UpdateLayoutSize();
            }
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            Text.color = Transparent ? Color.white : TextStyle.color;
        }

        internal void UpdateLayoutSize()
        {
            LayoutStyle.size.y = TextAreaHeight + Owner.LayoutStyle.spacing + Label.LayoutStyle.margin.y * 2;
            RefreshLayout();
        }

        internal float TextAreaHeight => CalculateHeight(LayoutStyle.size.x);

        private float CalculateHeight(float textWidth)
        {
            var settings = new TextGenerationSettings();

            settings.generationExtents = new Vector2(textWidth, 0);
            settings.fontSize = Text.fontSize;

            // Other settings
            settings.textAnchor = Text.alignment;
            settings.alignByGeometry = Text.alignByGeometry;
            settings.scaleFactor = Text.pixelsPerUnit;
            settings.color = Text.color;
            settings.font = Text.font;
            settings.pivot = RectTransform.pivot;
            settings.richText = false;
            settings.lineSpacing = Text.lineSpacing;
            settings.fontStyle = Text.fontStyle;
            settings.resizeTextForBestFit = false;
            settings.updateBounds = true;
            settings.horizontalOverflow = Text.horizontalOverflow;
            settings.verticalOverflow = Text.verticalOverflow;

            var textGenerator = new TextGenerator();
            textGenerator.Populate(Text.text, settings);
            return textGenerator.rectExtents.height / Text.pixelsPerUnit;
        }
    }
}
