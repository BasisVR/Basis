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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Reflection;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using Object = UnityEngine.Object;
using Utils = Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.Editor.Notifications
{
    [Reflection]
    internal class NotificationWindow : EditorWindow
    {
        [Reflection(AssemblyTypeReference = typeof(UnityEditor.Editor), TypeName = "UnityEditor.MainView")]
        private static readonly TypeHandle MainViewType = new();

        [Reflection(AssemblyTypeReference = typeof(UnityEditor.Editor), TypeName = "UnityEditor.View",
            Name = "screenPosition")]
        private static readonly PropertyInfoHandle<Rect> Position = new();

        [Reflection(AssemblyTypeReference = typeof(UnityEditor.Editor),
            TypeName = "UnityEditor.EditorWindow",
            Name = "ShowTooltip")]
        private static readonly MethodInfoHandle ShowTooltip = new();

        private static Object _mainView;
        private static Object MainView => _mainView ??= FetchMainView();
        private static Rect MainViewRect => Position.Get(MainView);

        private static Object FetchMainView()
        {
            var views = Resources.FindObjectsOfTypeAll(MainViewType.Target);
            if (views == null || views.Length == 0)
            {
                return null;
            }

            return views[0];
        }

        private Notification _notification;

        public void Setup(Notification notification)
        {
            _notification = notification;
        }

        private void Update()
        {
            if (_notification?.DurationHasPassed ?? false)
            {
                _notification.Remove(Origins.Timeout);
            }
        }

        private Tween _transitionIn;

        private void OnBecameVisible()
        {
            if (_notification == null) return;

            _transitionIn = Tween.Fetch(_notification);
            _transitionIn.Reset();
            _transitionIn.Start = 0.0f;
            _transitionIn.Speed = 8.0f;
            _transitionIn.Epsilon = 0.01f;
            _transitionIn.Target = 1.0f;
            _transitionIn.Activate();
        }

        private void OnGUI()
        {
            if (_notification == null)
            {
                Close();
                return;
            }

            var rect = EditorGUILayout.BeginHorizontal(Styles.GUIStyles.NotificationBox);

            // Rounded border and background
            GUI.DrawTexture(rect, DarkGray.ToTexture(), ScaleMode.ScaleAndCrop, false, 1f,
                GUI.color, Vector4.zero, Styles.Constants.RoundedBorderVectors);
            GUI.DrawTexture(rect, Styles.Contents.NotificationGradientNeutral.Image, ScaleMode.StretchToFill, false, 16f,
                _notification.GradientColor, Vector4.zero, Styles.Constants.RoundedBorderVectors);

            // Left Icon
            if (_notification.Icon != null)
            {
                var iconRect = EditorGUILayout.BeginVertical(Styles.GUIStyles.NotificationIconBox);
                EditorGUILayout.Space();
                using (new Meta.XR.Editor.UserInterface.Utils.ColorScope(
                           Meta.XR.Editor.UserInterface.Utils.ColorScope.Scope.Content,
                           UserInterface.Styles.Colors.Meta))
                {
                    GUI.Button(iconRect, _notification.Icon, Styles.GUIStyles.NotificationIconStyle);
                }
                EditorGUILayout.EndVertical();
            }

            // List of IGuideItems
            EditorGUILayout.BeginVertical(Styles.GUIStyles.NotificationContentBox);
            if (_notification.Items != null)
            {
                foreach (var item in _notification.Items)
                {
                    item.Draw();
                }
            }
            EditorGUILayout.EndVertical();

            // Optional Close Button
            if (_notification is { ShowCloseButton: true })
            {
                EditorGUILayout.BeginVertical(Styles.GUIStyles.NotificationCloseIconBox);
                if (DrawButton(UserInterface.Styles.Contents.CloseIcon))
                {
                    _notification.Remove(Origins.Self);
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndHorizontal();

            UpdateHeight(rect);

            if (_transitionIn.Active)
            {
                Repaint();
            }
        }

        private void UpdateHeight(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;

            var expectedRect = MainViewRect;
            var expectedHeight = rect.height;

            // The x position is the right of the main window minus the necessary width and a bit of a margin
            var x = expectedRect.x + expectedRect.width - _notification.ExpectedWidth - DoubleMargin;

            // This is how far from the expected position we want to be
            var expectedOffset = Mathf.Max((expectedHeight + DoubleMargin + 24) * _transitionIn.Current, 1);

            // This is the anchor position, bottom right of the Main window
            var parentOffset = expectedRect.y + expectedRect.height;

            // The expected y would be the combination of the two
            var y = parentOffset - expectedOffset;

            // But unfortunately, because Unity automatically fit to screen, we need to compute if (y > parentOffset)
            // and limit the height by that different
            var actualHeight = Mathf.Min(expectedHeight, parentOffset - y);

            position = new Rect(x, y, _notification.ExpectedWidth, actualHeight);
            minSize = new Vector2(_notification.ExpectedWidth, actualHeight);
            maxSize = new Vector2(_notification.ExpectedWidth, actualHeight);
        }

        private bool DrawButton(TextureContent icon)
        {
            var id = icon.Name;
            var hover = HoverHelper.IsHover(id);
            using var color = new Meta.XR.Editor.UserInterface.Utils.ColorScope(
                Meta.XR.Editor.UserInterface.Utils.ColorScope.Scope.Content,
                hover ? OffWhite : SelectedWhite);
            var hit = HoverHelper.Button(id, icon, Styles.GUIStyles.NotificationCloseIconStyle, out _);
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            return hit;
        }

        public void OnDisable()
        {
            // In case the window was not closed by a known path, we still need to propagate up the information
            _notification?.Remove(Origins.Unknown);
        }

        public void ShowAsTooltip()
        {
            ShowTooltip.Target.Invoke(this, new object[] { });
        }
    }
}
