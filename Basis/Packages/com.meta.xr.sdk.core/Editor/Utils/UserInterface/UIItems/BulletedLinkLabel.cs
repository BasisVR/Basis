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

using Meta.XR.Editor.Id;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Constants;

namespace Meta.XR.Editor.UserInterface
{
    /// <summary>
    /// Make a read-only clickable label with bullet point.
    /// </summary>
    /// <remarks>
    /// Use <see cref="GuideStyles.ContentStatusType"/> to set color of the bullet based on status type.
    /// </remarks>
    internal class BulletedLinkLabel : LinkLabel
    {
        private readonly UIStyles.ContentStatusType _contentStatusType;
        private Color _color;

        public BulletedLinkLabel(GUIContent label, string url, IIdentified originData, params GUILayoutOption[] options) : this(label, url, originData, UIStyles.ContentStatusType.Normal, options) { }

        public BulletedLinkLabel(GUIContent label, string url, IIdentified originData, UIStyles.ContentStatusType contentStatusType,
            params GUILayoutOption[] options) : base(label, url, originData, options)
        {
            SetStatus(contentStatusType);
        }

        public BulletedLinkLabel(LinkDescription description, params GUILayoutOption[] options) : this(description, UIStyles.ContentStatusType.Normal, options) { }

        public BulletedLinkLabel(LinkDescription description, UIStyles.ContentStatusType contentStatusType, params GUILayoutOption[] options) : base(description, options)
        {
            SetStatus(contentStatusType);
        }

        public override void Draw()
        {
            EditorGUILayout.BeginHorizontal();

            using (new Meta.XR.Editor.UserInterface.Utils.ColorScope(Meta.XR.Editor.UserInterface.Utils.ColorScope.Scope.Content, _color))
            {
                EditorGUILayout.LabelField(UIStyles.Contents.DefaultIcon,
                    new GUIStyle(UIStyles.GUIStyles.IconStyle), GUILayout.Width(SmallIconSize - 2),
                    GUILayout.Height(SmallIconSize));
            }

            base.Draw();

            EditorGUILayout.EndHorizontal();
        }

        public void SetStatus(UIStyles.ContentStatusType statusType) => _color = Meta.XR.Editor.UserInterface.Utils.GetColorByStatus(statusType);
    }
}
