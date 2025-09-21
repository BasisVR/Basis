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

namespace Meta.XR.Editor.UserInterface
{
    /// <summary>
    /// Make a read-only label with bullet point.
    /// </summary>
    /// <remarks>
    /// Use <see cref="GuideStyles.ContentStatusType"/> to set color of the bullet based on status type.
    /// </remarks>
    internal class BulletedLabel : IUserInterfaceItem
    {
        public bool Hide { get; set; }
        private Label _labelItem;
        private Color _color;

        public BulletedLabel(string label, UIStyles.ContentStatusType contentStatusType = UIStyles.ContentStatusType.Normal, params GUILayoutOption[] options) :
            this(label, UIStyles.GUIStyles.Label, contentStatusType, options)
        {
        }

        public BulletedLabel(string label, GUIStyle style, UIStyles.ContentStatusType contentStatusType = UIStyles.ContentStatusType.Normal, params GUILayoutOption[] options)
        {
            Style = new GUIStyle(style);
            _labelItem = new Label(label, Style, options);
            SetStatus(contentStatusType);
        }

        public void Draw()
        {
            EditorGUILayout.BeginHorizontal();

            using (new Meta.XR.Editor.UserInterface.Utils.ColorScope(Meta.XR.Editor.UserInterface.Utils.ColorScope.Scope.Content, _color))
            {
                EditorGUILayout.LabelField(
                    UIStyles.Contents.DefaultIcon,
                    new GUIStyle(UIStyles.GUIStyles.IconStyle), GUILayout.Width(SmallIconSize),
                    GUILayout.Height(SmallIconSize));
            }

            _labelItem.Draw();

            EditorGUILayout.EndHorizontal();
        }

        public void SetStatus(UIStyles.ContentStatusType statusType) => _color = Utils.GetColorByStatus(statusType);
        public GUIStyle Style { get; }
        public float GetHeight(float contentWidth = UIStyles.Constants.DefaultWidth - LargeMargin) => Style.CalcHeight(_labelItem.LabelContent, contentWidth);
        public float GetWidth() => _labelItem.GetWidth() + SmallIconSize;
    }
}
