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

using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Colors;

namespace Meta.XR.Editor.ToolingSupport
{
    internal static class Styles
    {
        public class GUIStylesContainer
        {
            internal readonly GUIStyle ItemDiv = new GUIStyle()
            {
                fixedHeight = Constants.Height,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, Border),

                normal =
                {
                    background = DarkGray.ToTexture()
                },
                hover =
                {
                    background = DarkGrayHover.ToTexture()
                }
            };

            public readonly GUIStyle Title = new GUIStyle(UserInterface.Styles.GUIStyles.BoldLabel)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };

            public readonly GUIStyle TitleHover = new GUIStyle(UserInterface.Styles.GUIStyles.BoldLabel)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = Color.white },
            };
            internal readonly GUIStyle Pill = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.LowerLeft,
                fontSize = 8,
                padding = new RectOffset(0, 0, 0, 2),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = UserInterface.Styles.Colors.LightGray }
            };

            internal readonly GUIStyle Subtitle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.LowerLeft,
                fontSize = 11,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = UserInterface.Styles.Colors.LightGray }
            };

            internal readonly GUIStyle IconStyle = new GUIStyle(EditorStyles.label)
            {
                // There are unexpected offsets that we are trying to compensate here
                fixedWidth = Constants.Height - 6,
                fixedHeight = Constants.Height - 4,
                padding = new RectOffset(22, 22, 22, 22),
                margin = new RectOffset(0, 0, 0, 0)
            };
        }

        public static class Constants
        {
            internal const float Height = 72f;
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
