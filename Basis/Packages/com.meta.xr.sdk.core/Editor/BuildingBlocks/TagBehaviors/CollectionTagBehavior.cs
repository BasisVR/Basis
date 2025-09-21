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

using Meta.XR.Editor.Tags;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Utils;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.GUIStylesContainer;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal class CollectionTagBehavior : TagBehavior
    {
        protected override GUIStyle Style => Icon == null ? Styles.GUIStyles.CollectionTagStyle : Styles.GUIStyles.CollectionTagStyleWithIcon;
        private GUIStyle TagButtonStyle => XR.Editor.Tags.Styles.GUIStyles.TagIcon;
        private GUIContent TagCloseIcon => Utils.CollectionTagCloseIcon;
        protected override ColorStates BackgroundColorState => Styles.GUIStyles.TagBackgroundCollectionColors;
        internal Tag Tag => _tag;

        public string Description { get; set; }
        public TextureContent Thumbnail { get; set; }

        internal struct DefaultSettings
        {
            public static int Order = -10;
            public static Color Color = CollectionTagsColor;
            public static bool Show = false;
            public static bool CanFilterBy = true;
            public static bool ShowOverlay = false;
        }

        protected internal CollectionTagBehavior(Tag tag) : base(tag)
        {
            tag.OnValidate(true);
        }

        protected override bool DrawButton(string id, Rect rect, out bool hover)
        {
            return HoverHelper.Button(id, rect, TagCloseIcon, TagButtonStyle, out hover);
        }

        public override bool Draw(string controlId, Tag.TagListType listType, bool active, out bool hover, out bool clicked)
        {
            hover = false;
            clicked = false;

            var id = controlId + _tag.Name;
            var color = BackgroundColorState.GetColor(active, HoverHelper.IsHover(id));

            var rect = GUILayoutUtility.GetRect(Content, Style, GUILayout.Width(StyleWidth));

            using var backgroundColorScope = new ColorScope(ColorScope.Scope.Background, DarkGrayActive);
            GUI.Label(rect, Content, Style);

            rect.y += 1;
            DrawIcon(rect);

            rect.x += StyleWidth - 22;
            rect.width = 18;

            using var _ = new ColorScope(ColorScope.Scope.Content, color);
            clicked = DrawButton(id, rect, out hover);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            return true;
        }
    }
}
