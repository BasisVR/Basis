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
using System.Collections.Generic;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Utils;
using static Meta.XR.Editor.UserInterface.Styles.GUIStylesContainer;

namespace Meta.XR.Editor.Tags
{
    internal class TagBehavior
    {
        public static readonly Dictionary<Tag, TagBehavior> Registry = new();

        protected Tag _tag;

        public int Order { get; set; }
        public bool Automated { get; set; }
        public Color Color { get; set; } = HexToColor("#DDDDDD");
        public TextureContent Icon { get; set; }

        /// <summary>
        /// Show : Whether the tag is visually represented in the UI
        /// Some tags are purely for internal system use and not meant to be shown in the UI
        /// </summary>
        public bool Show { get; set; } = true;
        public bool ShowOverlay { get; set; }
        public bool CanFilterBy { get; set; }

        /// <summary>
        /// Visibility : Whether the block is actually visible
        /// These tags let us hide blocks, if they're for instance : Hidden, Obsolete, Internal
        /// </summary>
        public bool ToggleableVisibility { get; set; }
        public bool DefaultVisibility { get; set; } = true;
        public bool Visibility => VisibilitySetting.Value;
        private Settings.CustomBool _visibilitySetting;
        public Settings.CustomBool VisibilitySetting
            => _visibilitySetting ??= new Settings.UserBool()
            {
                Owner = _tag,
                Uid = $"Tag_{_tag.Name}_Visibility",
                Default = DefaultVisibility,
                Label = $"Show {_tag.Name} blocks",
            };

        private GUIStyle _style;
        private GUIStyle _inlineStyle;
        private GUIContent _content;
        private float? _styleWidth;

        protected virtual GUIStyle Style => _style ??=
            Icon != null ? Styles.GUIStyles.TagStyleWithIcon : Styles.GUIStyles.TagStyle;

        protected virtual GUIStyle InlineStyle => _inlineStyle ??=
            Icon != null ? Styles.GUIStyles.TagStyleInlinedWithIcon : Styles.GUIStyles.TagStyleInline;
        protected virtual ColorStates BackgroundColorState => Styles.GUIStyles.TagBackgroundColors;
        protected GUIContent Content => _content ??= new GUIContent(_tag.Name);
        public virtual float StyleWidth => _styleWidth ??= Style.CalcSize(Content).x + 1;

        public static TagBehavior GetBehavior(Tag tag)
        {
            if (tag == null)
            {
                throw new ArgumentNullException(nameof(tag));
            }

            if (!Registry.TryGetValue(tag, out var tagBehavior))
            {
                tagBehavior = new TagBehavior(tag);
            }
            return tagBehavior;
        }

        protected TagBehavior(Tag tag)
        {
            _tag = tag;
            Registry[tag] = this;
        }

        protected virtual void DrawIcon(Rect rect)
        {
            if (Icon == null) return;
            GUI.Label(rect, Icon, Styles.GUIStyles.TagIcon);
        }

        protected virtual bool DrawButton(string id, Rect rect, out bool hover)
        {
            return HoverHelper.Button(id, rect, Content, Style, out hover);
        }

        public bool ShouldDraw(Tag.TagListType listType)
        {
            if (!Show)
            {
                return false;
            }

            if (!Visibility)
            {
                return false;
            }

            switch (listType)
            {
                case Tag.TagListType.Filters when !CanFilterBy:
                case Tag.TagListType.Overlays when !ShowOverlay:
                    return false;
            }

            return true;
        }

        public virtual bool Draw(string controlId, Tag.TagListType listType, bool active, out bool hover, out bool clicked)
        {
            hover = false;
            clicked = false;

            if (!ShouldDraw(listType)) return false;

            var id = controlId + _tag.Name;
            var color = BackgroundColorState.GetColor(active, HoverHelper.IsHover(id));
            var rect = GUILayoutUtility.GetRect(Content, Style, GUILayout.Width(StyleWidth));
            using var backgroundColorScope = new ColorScope(ColorScope.Scope.Background, color);
            using var contentColorScope = new ColorScope(ColorScope.Scope.Content, Color);
            clicked = DrawButton(id, rect, out hover);
            DrawIcon(rect);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return true;
        }

        public void DrawSimple(bool inline = false)
        {
            var color = BackgroundColorState.GetColor(false, false);
            var rect = GUILayoutUtility.GetRect(Content, inline ? InlineStyle : Style, GUILayout.Width(StyleWidth));
            using var backgroundColorScope = new ColorScope(ColorScope.Scope.Background, color);
            using var contentColorScope = new ColorScope(ColorScope.Scope.Content, Color);
            DrawButton(_tag.Name, rect, out _);
            DrawIcon(rect);
        }
    }
}
