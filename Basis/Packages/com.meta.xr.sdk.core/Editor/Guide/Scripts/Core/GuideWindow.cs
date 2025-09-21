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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.Guides.Editor
{
    internal class GuideWindow : EditorWindow, IIdentified
    {
        public string Id => _populatorId;

        [SerializeField] private string _title = "Meta XR Guide";
        [SerializeField] private string _description = "Description placeholder.";
        private bool _isVisible;
        private Vector2 _scrollPosition = Vector2.zero;

        public Action OnWindowFocus;
        public Action OnWindowLostFocus;
        public Action OnWindowDraw;
        public Action OnWindowDestroy;
        public Action<Rect> DrawCustomHeaderTitle;
        public Action DrawCustomNotice;
        public Func<OVRTelemetryMarker, OVRTelemetryMarker> AddAdditionalTelemetryAnnotations;

        private CustomBool _dontShowAgain;
        private CustomBool DontShowAgain => _dontShowAgain ??= new UserBool()
        {
            Owner = this,
            Label = "Don't Show Again",
            Uid = "DontShowAgain",
            SendTelemetry = true,
            Default = false
        };

        private Button _closeButton;
        public Button CloseButton => _closeButton ??= new Button(new ActionLinkDescription()
        {
            Content = new GUIContent("Close"),
            Action = Close,
            ActionData = this,
            Origin = Origins.GuidedSetup,
            OriginData = this,
            Id = "CloseButton"
        }, GUILayout.MinWidth(192.0f));

        [SerializeField] private GuideOptions _guideOptions;
        [SerializeField] private string _populatorId;

        private List<IUserInterfaceItem> Items { get; set; }

        [Serializable]
        public struct GuideOptions
        {
            public bool ShowCloseButton;
            public bool ShowDontShowAgainOption;
            public bool InvertDontShowAgain;
            public GUIContent OverrideDontShowAgainContent;
            public int MinWindowWidth;
            public int MaxWindowWidth;
            public int MinWindowHeight;
            public int MaxWindowHeight;
            public TextureContent HeaderImage;
            public int HeaderHeight;
            public int BottomMargin;

            public GuideOptions(GuideOptions options)
            {
                ShowCloseButton = options.ShowCloseButton;
                ShowDontShowAgainOption = options.ShowDontShowAgainOption;
                InvertDontShowAgain = options.InvertDontShowAgain;
                OverrideDontShowAgainContent = options.OverrideDontShowAgainContent;
                MinWindowWidth = options.MinWindowWidth;
                MaxWindowWidth = options.MaxWindowWidth;
                MinWindowHeight = options.MinWindowHeight;
                MaxWindowHeight = options.MaxWindowHeight;
                HeaderImage = options.HeaderImage;
                HeaderHeight = options.HeaderHeight;
                BottomMargin = options.BottomMargin;
            }
        }

        public static readonly GuideOptions DefaultOptions = new()
        {
            ShowCloseButton = true,
            ShowDontShowAgainOption = true,
            InvertDontShowAgain = false,
            OverrideDontShowAgainContent = null,
            MinWindowWidth = GuideStyles.Constants.DefaultWidth,
            MaxWindowWidth = GuideStyles.Constants.DefaultWidth,
            MinWindowHeight = GuideStyles.Constants.DefaultHeight,
            MaxWindowHeight = GuideStyles.Constants.DefaultHeight,
            HeaderHeight = GuideStyles.Constants.DefaultHeaderHeight,
            BottomMargin = LargeMargin - Margin,
        };

        public void Setup(string title, string description, IIdentified populator, GuideOptions guideOptions)
        {
            _guideOptions = guideOptions;
            _populatorId = populator.Id;
            SetupWindow(title, description);
        }

        private void SetupWindow(
            string windowTitle,
            string description)
        {
            _title = windowTitle;
            _description = description;
            name = windowTitle;
            titleContent = new GUIContent(windowTitle);
            minSize = new Vector2(_guideOptions.MinWindowWidth, _guideOptions.MinWindowHeight);
            maxSize = new Vector2(_guideOptions.MaxWindowWidth, _guideOptions.MaxWindowHeight);
        }

        internal void Show(Origins origin, bool ignoreDontShowAgainFlag = false)
        {
            if (Application.isBatchMode) return;

            if ((ignoreDontShowAgainFlag || !_guideOptions.ShowDontShowAgainOption || !DontShowAgain.Value) && !_isVisible)
            {
                OnOpen(origin);
                base.Show();
            }
        }

        internal void OnGUI()
        {
            // Initialization, only once
            if (Items == null)
            {
                // Search for the bespoke initialization method and call it
                GuideProcessor.InitializeWindow(_populatorId, this);

                // Search for the bespoke items
                Items = GuideProcessor.GetItems(_populatorId);

                // If initialization failed, give up and close
                if (Items == null)
                {
                    Close();
                    return;
                }
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, Styles.GUIStyles.NoMargin);

            DrawHeader();

            XRGuideBeginVertical();
            BeforeItemDraw();

            foreach (var item in Items)
            {
                if (item == null || item.Hide) continue;
                item.Draw();
            }

            AfterItemDraw();
            XRGuideEndVertical();

            DrawFooters();
            EditorGUILayout.Space(_guideOptions.BottomMargin);
            OnWindowDraw?.Invoke();

            EditorGUILayout.EndScrollView();

            UpdateMinimumSize();
        }

        private void UpdateMinimumSize()
        {
            // Only update size when doing a repaint. That's best practice has doing it doing the layout may provide
            // rects that have not been computed yet
            if (Event.current.type != EventType.Repaint) return;

            // When docked, minSize and maxSize have no effect, there is no need to refresh it
            if (docked) return;

            // We'll want to limit min and max widths and heights to screen size when possible
            // Get the screen resolution, but then apply the DPI scaling (that's when there is a scale applied to the
            // resolution (like for Retina display, or recommended scaling by windows for high resolutions)
            var mainViewSize = new Vector2(Screen.currentResolution.width, Screen.currentResolution.height);
            var scale = 96.0f / Screen.dpi; // 96 is the standard DPI, the 100%
            mainViewSize *= scale;
            mainViewSize *= 0.8f; // Some buffer padding

            var idealMinWidth = Mathf.Min(_guideOptions.MinWindowWidth, mainViewSize.x);
            var idealMinHeight = Mathf.Min(_guideOptions.MinWindowHeight, mainViewSize.y);
            var idealMinSize = new Vector2(idealMinWidth, idealMinHeight);
            minSize = idealMinSize;

            var idealMaxWidth = Mathf.Min(_guideOptions.MaxWindowWidth, mainViewSize.x);
            var idealMaxHeight = Mathf.Min(_guideOptions.MaxWindowHeight, mainViewSize.y);
            var idealMaxSize = new Vector2(idealMaxWidth, idealMaxHeight);
            maxSize = idealMaxSize;
        }

        private void OnBecameVisible()
        {
            Guide.GuideWindowIntances.TryAdd(GetHashCode(), this);
            _isVisible = true;
        }

        private void OnBecameInvisible()
        {
            _isVisible = false;
        }

        private void OnFocus() => OnWindowFocus?.Invoke();
        private void OnLostFocus() => OnWindowLostFocus?.Invoke();
        private void OnDestroy()
        {
            OnClose();
            OnWindowDestroy?.Invoke();
            Guide.GuideWindowIntances.Remove(GetHashCode());
        }

        private void OnClose()
        {
            var marker = OVRTelemetry.Start(XR.Editor.UserInterface.Telemetry.MarkerId.PageClose);
            marker = AddTelemetryAnnotations(marker, Origins.Self);
            marker.Send();
        }

        private void OnOpen(Origins origin)
        {
            var marker = OVRTelemetry.Start(XR.Editor.UserInterface.Telemetry.MarkerId.PageOpen);
            marker = AddTelemetryAnnotations(marker, origin);
            marker.Send();
        }

        private OVRTelemetryMarker AddTelemetryAnnotations(OVRTelemetryMarker marker, Origins origin)
        {
            marker = marker
                .AddAnnotation(XR.Editor.UserInterface.Telemetry.AnnotationType.Origin, origin.ToString())
                .AddAnnotation(XR.Editor.UserInterface.Telemetry.AnnotationType.Action, Origins.GuidedSetup.ToString())
                .AddAnnotation(XR.Editor.UserInterface.Telemetry.AnnotationType.ActionData, Id)
                .AddAnnotation(XR.Editor.UserInterface.Telemetry.AnnotationType.ActionType, GetType().Name);

            if (AddAdditionalTelemetryAnnotations != null)
            {
                marker = AddAdditionalTelemetryAnnotations(marker);
            }

            return marker;
        }

        public override int GetHashCode() => Guide.GetGuideHash(_title, _description);

        private void DrawHeader()
        {
            DrawHeaderImage();
            DrawHeaderTitle();
            DrawNotice();
        }

        private void DrawHeaderImage()
        {
            var headerImage = _guideOptions.HeaderImage;
            var isHeaderImageValid = headerImage?.Valid ?? false;
            var image = isHeaderImageValid ? headerImage.Image : GuideStyles.Contents.BannerImage.Image;
            var expectedHeight = _guideOptions.HeaderHeight;
            var rect = GUILayoutUtility.GetRect(EditorGUIUtility.currentViewWidth, expectedHeight);
            GUI.DrawTexture(rect, image, ScaleMode.ScaleAndCrop);
        }

        private void DrawNotice()
        {
            if (DrawCustomNotice != null)
            {
                DrawCustomNotice();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_description, UIStyles.GUIStyles.SubtitleLabel, GUILayout.Width(position.width - LargeMargin));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeaderTitle()
        {
            var expectedHeight = _guideOptions.HeaderHeight;
            var headerTitleRect = new Rect(0, 0, EditorGUIUtility.currentViewWidth,
                expectedHeight);
            GUILayout.BeginArea(headerTitleRect);

            if (DrawCustomHeaderTitle != null)
            {
                DrawCustomHeaderTitle(headerTitleRect);
                GUILayout.EndArea();
                return;
            }

            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal(UIStyles.GUIStyles.Header);
            using (new ColorScope(ColorScope.Scope.Content, OffWhite))
            {
                EditorGUILayout.LabelField(GuideStyles.Contents.HeaderIcon, UIStyles.GUIStyles.HeaderIconStyle, GUILayout.Width(32.0f),
                    GUILayout.ExpandWidth(false));
            }

            var titleGuiContent = new GUIContent(_title);
            EditorGUILayout.LabelField(titleGuiContent, UIStyles.GUIStyles.HeaderBoldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            GUILayout.EndArea();
        }

        private void DrawFooters()
        {
            if (!_guideOptions.ShowCloseButton && !_guideOptions.ShowDontShowAgainOption) return;

            XRGuideBeginVertical();
            EditorGUILayout.BeginHorizontal();
            if (_guideOptions.ShowDontShowAgainOption)
            {
                DontShowAgain.DrawForGUI(new CustomBool.DrawOptions()
                {
                    origin = Origins.Self,
                    originData = this,
                    callback = null,
                    OnLeft = true,
                    Inverted = _guideOptions.InvertDontShowAgain,
                    Content = string.IsNullOrEmpty(_guideOptions.OverrideDontShowAgainContent?.text) ? DontShowAgain.Content : _guideOptions.OverrideDontShowAgainContent,
                });
            }

            GUILayout.FlexibleSpace();

            if (_guideOptions.ShowCloseButton)
            {
                CloseButton.Draw();
            }
            EditorGUILayout.EndHorizontal();
            XRGuideEndVertical();
        }

        /// <summary>
        /// Adds custom GUI elements before drawing <see cref="IUserInterfaceItem"/>(s).
        /// </summary>
        protected virtual void BeforeItemDraw()
        {
        }

        /// <summary>
        /// Adds custom GUI elements after drawing <see cref="IUserInterfaceItem"/>(s).
        /// </summary>
        protected virtual void AfterItemDraw()
        {
        }

        internal Rect XRGuideBeginVertical() => EditorGUILayout.BeginVertical(UIStyles.GUIStyles.ContentMargin);
        internal void XRGuideEndVertical() => EditorGUILayout.EndVertical();
    }
}
