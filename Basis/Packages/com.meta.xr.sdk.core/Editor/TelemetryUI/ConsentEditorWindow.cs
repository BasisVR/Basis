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

using System.Linq;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.TelemetryUI
{
    internal class ConsentEditorWindow : EditorWindow
    {
        private Vector2 _size = new(600f, 320f);
        private GroupedItem _groupedUIItems;
        private string _consentMarkdownText;

        public static void ShowWindow()
        {
            var consentTitle = OVRPlugin.UnifiedConsent.GetConsentTitle();
            if (string.IsNullOrEmpty(consentTitle))
            {
                return;
            }

            var consentText = OVRPlugin.UnifiedConsent.GetConsentMarkdownText();
            if (string.IsNullOrEmpty(consentText))
            {
                return;
            }

            var window = CreateInstance<ConsentEditorWindow>();
            window.titleContent = new GUIContent(consentTitle);
            window._consentMarkdownText = consentText;
            window.ShowUtility();
        }

        private void OnGUI()
        {
            if (_groupedUIItems == null)
            {
                var contentItems = Utils.GetGuideItemsForMarkdownText(_consentMarkdownText).ToList();
                if (contentItems is not { Count: > 0 })
                {
                    return;
                }

                _groupedUIItems = new GroupedItem(contentItems, UserInterface.Utils.UIItemPlacementType.Vertical);
            }

            var rect = EditorGUILayout.BeginVertical();
            _groupedUIItems.Draw();

            EditorGUILayout.BeginHorizontal();

            var buttonLayout = GUILayout.Height(40);

            if (GUILayout.Button("Only share essential data", buttonLayout))
            {
                RecordConsent(false);
            }

            using (new UserInterface.Utils.ColorScope(UserInterface.Utils.ColorScope.Scope.Background,
                       Utils.ButtonAcceptColor))
            {
                if (GUILayout.Button("Share additional data", buttonLayout))
                {
                    RecordConsent(true);
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            UpdateHeight(rect);
        }

        private void RecordConsent(bool consent)
        {
            OVRTelemetryConsent.SetTelemetryEnabled(consent);
            Close();
        }

        private void UpdateHeight(Rect rect)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            const float bottomMargin = 3;
            _size.y = rect.height + bottomMargin;
            minSize = _size;
            maxSize = _size;
        }
    }
}
