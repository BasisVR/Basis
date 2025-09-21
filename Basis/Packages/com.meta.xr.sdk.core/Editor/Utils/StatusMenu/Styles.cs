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
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Colors;

namespace Meta.XR.Editor.StatusMenu
{
    internal static class Styles
    {
        public class GUIStylesContainer
        {
            internal readonly GUIStyle BackgroundAreaStyle = new GUIStyle()
            {
                stretchHeight = true,
                padding = new RectOffset(Border, Border, Border, 0),
                normal =
                {
                    background = CharcoalGray.ToTexture()
                }
            };
        }

        public static class Contents
        {
            internal static readonly TextureContent StatusIcon =
                TextureContent.CreateContent("ovr_icon_meta.png", TextureContent.Categories.Generic, null);

            public static readonly TextureContent MetaIcon =
                TextureContent.CreateContent("ovr_icon_meta_white.png", TextureContent.Categories.Generic);
        }

        public static class Constants
        {
            internal const float Width = 432f;
        }

        private static GUIStylesContainer _guiStyles;
        public static GUIStylesContainer GUIStyles => _guiStyles ??= new GUIStylesContainer();
    }
}
