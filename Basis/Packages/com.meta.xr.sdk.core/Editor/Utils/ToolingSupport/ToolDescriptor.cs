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
using System.ComponentModel;
using System.Diagnostics;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Tags;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using System.Linq;
using Meta.XR.Editor.Settings;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.Editor.ToolingSupport
{
    internal class ToolDescriptor : IIdentified
    {
        public delegate (string, Color?) TextDelegate();

        public delegate (TextureContent, Color?, bool) PillIconDelegate();

        public string Name;
        public string Id => Name;
        public string MqdhCategoryId;
        public string Description;
        public string MenuDescription;
        public Color Color;
        public bool IsStatusMenuItemDarker;
        public int Order;
        public TextureContent Icon;
        public List<LinkDescription> HeaderIcons;
        public PillIconDelegate PillIcon;
        public TextDelegate InfoTextDelegate;
        public Action<Origins> OnClickDelegate;
        public bool CloseOnClick = true;
        public bool Experimental = false;
        public bool CanBeNew = false;
        public bool ShowHeader = true;
        public bool AddToStatusMenu = false;
        public bool AddToMenu = true;
        public string MenuPathShortcut;
        public Action<Origins, string> OnUserSettingsGUI;
        public Action<Origins, string> OnProjectSettingsGUI;
        public IReadOnlyList<Documentation> Documentation;
        public Action<GenericMenu> BuildOptionsMenuDelegate;

        private CustomBool _new;

        private CustomBool New => _new ??=
            new UserBool()
            {
                Owner = this,
                Uid = Name + "_item_new_flag",
                Default = CanBeNew,
                SendTelemetry = false
            };

        private CustomBool _showOverview;

        internal CustomBool ShowOverview => _showOverview ??=
            new UserBool()
            {
                Owner = this,
                Uid = Name + "_item_show_overview",
                Default = true,
                SendTelemetry = false,
                Label = "Display Overview Header"
            };

        private string DefaultDocumentationUrl => Documentation?.FirstOrDefault()?.Url ?? null;

        private string MenuPath => $"{Utils.MetaMenuPath}{Name}";

        private List<LinkDescription> _builtHeaderIcons;


        public ToolDescriptor()
        {

            ToolRegistry.Register(this);
        }

        public void Initialize()
        {
            SetupMenuPath();
        }

        private void SetupMenuPath()
        {
            if (OnClickDelegate == null || !AddToMenu) return;

            Utils.AddMenuItem(MenuPath, () => OnClickDelegate(Origins.Menu), MenuPathShortcut, Order);
        }

        private Vector2 _headerSize = Vector2.zero;

        public void DrawButton(Action onClick, bool showHeaderIcons, bool prependOpen, Origins origin)
        {
            using var indentScope = new IndentScope(0);
            var buttonRect = EditorGUILayout.BeginVertical(Styles.GUIStyles.ItemDiv);
            var hover = buttonRect.Contains(Event.current.mousePosition);
            {
                EditorGUILayout.BeginHorizontal(GUIStyle.none);

                DrawIcon(hover);
                DrawLabel(hover, prependOpen);

                if (showHeaderIcons)
                {
                    ShowHeaderIcons(origin);
                }

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            EditorGUIUtility.AddCursorRect(buttonRect, MouseCursor.Link);
            if (hover && Event.current.type == EventType.MouseUp)
            {
                OnClickDelegate?.Invoke(origin);
                if (CloseOnClick)
                {
                    onClick?.Invoke();
                }

                New.SetValue(false, Origins.Self, this);
            }
        }

        private void BuildHeaderIcons()
        {
            _builtHeaderIcons = new();

            // Configuration Icon
            _builtHeaderIcons.Add(new ActionLinkDescription()
            {
                Content = Contents.ConfigIcon,
                Style = Meta.XR.Editor.UserInterface.Styles.GUIStyles.MiniButton,
                Color = LightGray,
                Action = BuildOptionMenu,
                OriginData = this
            });

            // Documentation Icon
            var defaultDocumentationUrl = DefaultDocumentationUrl;
            if (defaultDocumentationUrl != null)
            {
                _builtHeaderIcons.Add(new UrlLinkDescription()
                {
                    Content = Contents.DocumentationIcon,
                    Style = Meta.XR.Editor.UserInterface.Styles.GUIStyles.MiniButton,
                    Color = LightGray,
                    URL = defaultDocumentationUrl,
                    OriginData = this
                });
            }

            // Custom Icons
            if (HeaderIcons != null)
            {
                foreach (var customHeaderIcon in HeaderIcons)
                {
                    customHeaderIcon.Style = Meta.XR.Editor.UserInterface.Styles.GUIStyles.MiniButton;
                    customHeaderIcon.OriginData = this;
                    _builtHeaderIcons.Add(customHeaderIcon);
                }
            }


            // Internal Bug Report / Feedback Icons
            _builtHeaderIcons.Add(new ActionLinkDescription()
            {
                Content = Contents.FeedbackIcon,
                Style = Meta.XR.Editor.UserInterface.Styles.GUIStyles.MiniButton,
                Color = LightGray,
                Action = OnFeedbackIconClicked,
                OriginData = this
            });

            _builtHeaderIcons = _builtHeaderIcons.OrderBy(item => item.Order).ToList();
        }

        private void OnFeedbackIconClicked()
        {
            var submitFeedbackEvent = OVRTelemetry.Start(OVRTelemetryConstants.Feedback.MarkerId.SubmitFeedback);
            try
            {
                using Process process = new Process();
                process.StartInfo.FileName = Utils.GetMqdhDeeplink(MqdhCategoryId);
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
            catch (Win32Exception)
            {
                submitFeedbackEvent.SetResult(OVRPlugin.Qpl.ResultType.Fail);
                if (EditorUtility.DisplayDialog("Install Meta Quest Developer Hub",
                        "Meta Quest Developer Hub is not installed on this machine.", "Get Meta Quest Developer Hub", "Cancel"))
                {
                    Application.OpenURL(
                        "https://developers.meta.com/horizon/documentation/unity/ts-odh-getting-started/");
                }
            }

            submitFeedbackEvent.AddAnnotation(OVRTelemetryConstants.Feedback.AnnotationType.ToolName, Name).Send();
        }

        internal void ShowHeaderIcons(Origins origin)
        {
            if (!ShowHeader)
            {
                return;
            }

            if (_builtHeaderIcons == null)
            {
                BuildHeaderIcons();
            }

            foreach (var icon in _builtHeaderIcons)
            {
                icon.Origin = origin;
                icon.Draw();
            }
        }

        private void DrawLabel(bool hover, bool prependOpen)
        {
            EditorGUILayout.BeginVertical(GUIStyle.none);
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal(GUIStyle.none);
                var label = prependOpen ? $"Open {Name}" : Name;
                var width = Styles.GUIStyles.Title.CalcSize(new GUIContent(label));
                EditorGUILayout.LabelField(label, hover ? Styles.GUIStyles.TitleHover : Styles.GUIStyles.Title, GUILayout.Width(width.x));
                if (New.Value)
                {
                    var tag = new Tag("New");
                    tag.Draw();
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                ShowInfoText();
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndVertical();
        }

        private void ShowInfoText()
        {
            EditorGUILayout.BeginHorizontal();

            // Menu Description
            DrawMenuDescription();

            // Dynamic Information
            DrawInfo();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawMenuDescription()
        {
            var content = new GUIContent(MenuDescription);
            var width = Styles.GUIStyles.Subtitle.CalcSize(content);
            EditorGUILayout.LabelField(content, Styles.GUIStyles.Subtitle, GUILayout.Width(width.x));
        }

        private void DrawPill()
        {
            if (PillIcon == null) return;

            var (_, pillColor, _) = PillIcon();

            var pillStyle = new GUIStyle(Styles.GUIStyles.Pill)
            {
                normal =
                {
                    textColor = pillColor ?? LightGray
                }
            };
            var content = new GUIContent("●");
            var width = pillStyle.CalcSize(content);
            EditorGUILayout.LabelField(content, pillStyle, GUILayout.Width(width.x));
        }

        private void DrawInfo()
        {
            if (InfoTextDelegate == null) return;
            var (content, color) = InfoTextDelegate();
            if (content == null) return;

            var style = new GUIStyle(Styles.GUIStyles.Subtitle);
            var pillStyle = new GUIStyle(Styles.GUIStyles.Pill);
            if (color.HasValue)
            {
                style.normal.textColor = color.Value;
                pillStyle.normal.textColor = color.Value;
            }

            var pill = new GUIContent("●");
            var width = pillStyle.CalcSize(pill);
            EditorGUILayout.LabelField(pill, pillStyle, GUILayout.Width(width.x));
            EditorGUILayout.LabelField(content, style);
        }

        private void DrawIcon(bool hover)
        {
            using var _ = new ColorScope(ColorScope.Scope.Content, hover ? UnityEngine.Color.white : LightGray);
            EditorGUILayout.LabelField(Icon, Styles.GUIStyles.IconStyle, GUILayout.Width(Styles.GUIStyles.IconStyle.fixedWidth));
        }

        private void UpdateCurrentWidth()
        {
            // Computing the correct width, without access to the current rect
            // Assumption : We're in the middle of rendering the HeaderGUI, in a Vertical block
            _headerSize.y = GUIStyles.Header.fixedHeight;
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical();
            var rect = EditorGUILayout.BeginHorizontal(GUILayout.Height(1));
            EditorGUILayout.LabelField("");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            _headerSize.x = rect.width != 0.0f ? rect.width : _headerSize.x;
        }

        internal void DrawHeader(Origins origin, float width = 0.0f)
        {
            if (width > 0.0f)
            {
                EditorGUILayout.BeginHorizontal(GUIStyles.Header, GUILayout.Width(width));
            }
            else
            {
                EditorGUILayout.BeginHorizontal(GUIStyles.Header);
            }
            {
                using (new ColorScope(ColorScope.Scope.Content, Color))
                {
                    EditorGUILayout.LabelField(Icon, GUIStyles.HeaderIconStyle, GUILayout.Width(32.0f),
                        GUILayout.ExpandWidth(false));
                }
                EditorGUILayout.LabelField(Name, GUIStyles.HeaderLabel);

                EditorGUILayout.Space(0, true);

                ShowHeaderIcons(origin);
            }
            EditorGUILayout.EndHorizontal();
        }

        internal void DrawHeaderFromSettingProvider(Origins origin)
        {
            UpdateCurrentWidth();

            GUILayout.BeginArea(new Rect(0, 0, _headerSize.x, _headerSize.y));
            {
                DrawHeader(origin, _headerSize.x);
            }
            GUILayout.EndArea();


            if (Experimental)
            {
                DrawExperimentalNotice();
            }
        }

        internal void DrawHeaderFromWindow(Origins origin)
        {
            DrawHeader(origin);


            if (Experimental)
            {
                DrawExperimentalNotice();
            }
        }


        private string ExperimentalNotice =>
            $"<b>{Name}</b> is currently an experimental feature.";

        private void DrawExperimentalNotice()
        {
            EditorGUILayout.BeginHorizontal(GUIStyles.ExperimentalNoticeBox);
            var internalTag = new Tag("Experimental");
            internalTag.Draw();
            EditorGUILayout.LabelField(ExperimentalNotice, GUIStyles.ExperimentalNoticeTextStyle);
            EditorGUILayout.EndHorizontal();
        }

        public void DrawDocumentation(Origins origin)
        {
            if (Documentation == null || Documentation.Count == 0) return;

            EditorGUILayout.BeginVertical(GUIStyles.DocumentationBox);
            EditorGUILayout.LabelField("Documentation", GUIStyles.DocumentationLabelStyle);
            foreach (var item in Documentation)
            {
                item.Link.Origin = origin;
                item.Link.Draw();
            }
            EditorGUILayout.EndVertical();
        }

        public void DrawOverview(string description)
        {
            EditorGUILayout.BeginVertical(GUIStyles.OverviewBox);
            EditorGUILayout.LabelField("Overview", GUIStyles.DocumentationLabelStyle);
            EditorGUILayout.LabelField(description, GUIStyles.DialogTextStyle);
            EditorGUILayout.Space();
            EditorGUILayout.EndVertical();
        }

        public void DrawDescriptionHeader(string description, Origins origin)
        {
            if (!ShowOverview.Value)
            {
                EditorGUILayout.BeginHorizontal(GUIStyles.OverviewSeparator);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal(GUIStyles.OverviewNoticeBox);
                DrawOverview(description);
                DrawDocumentation(origin);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void BuildOptionMenu()
        {
            var menu = new GenericMenu();
            if (OnUserSettingsGUI != null)
            {
                menu.AddItem(new GUIContent("Go to User Preferences"), false, () => OpenUserSettings(Origins.HeaderIcons));
                menu.AddSeparator(string.Empty);
            }
            ShowOverview.DrawForMenu(menu, Origins.HeaderIcons, this);
            BuildOptionsMenuDelegate?.Invoke(menu);
            menu.ShowAsContext();
        }

        public void OpenProjectSettings(Origins origin)
            => ProjectSettingsProvider.Open(this, origin);

        public void OpenUserSettings(Origins origin)
            => UserSettingsProvider.Open(this, origin);
    }
}
