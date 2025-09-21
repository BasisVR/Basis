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


using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    internal static class Styles
    {
        public static class Colors
        {
            public static readonly Color AccentColor = HexToColor("#4285f4");
            public static readonly Color AccentColorBrighter = HexToColor("#89b5ff");
        }

        public static class Constants
        {
            public const float LabelWidth = 256.0f;
        }

        public class GUIStylesContainer
        {
            public readonly GUIStyle FoldoutLeft = new GUIStyle(EditorStyles.foldout)
            {
                stretchWidth = true,
                stretchHeight = true
            };

            public readonly GUIStyle Label = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                fontSize = 11
            };

            public readonly GUIStyle ContentBox = new GUIStyle(EditorStyles.helpBox)
            {
                margin = new RectOffset(Margin, Margin, Margin, Margin),
                padding = new RectOffset(Margin, Margin, Margin, Margin)
            };
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}

