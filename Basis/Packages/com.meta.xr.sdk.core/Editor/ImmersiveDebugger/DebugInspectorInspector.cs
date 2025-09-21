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
using System.Linq;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Styles.Constants;
using static Meta.XR.Editor.UserInterface.Styles.Contents;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    [CustomEditor(typeof(DebugInspector), true)]
    internal class DebugInspectorInspector : UnityEditor.Editor
    {
        private readonly HashSet<object> _foldouts = new();
        private List<Component> previousComponents = new();
        private bool _hasChanged;
        private float _dialogNoticeHorizontalPadding;
        private float _dialogNoticeVerticalPadding;
        private GUIContent _dialogNotice;
        private string _filterComponent;
        private SerializedProperty _category;

        private void Awake()
        {
            _dialogNotice = new GUIContent($"<b>{target.GetType().Name}</b> is part of the <b>{Utils.PublicName}</b>." +
                                           $"\nUse it to toggle tracking of any methods, properties or fields" +
                                           $" in any components of your game object.");

            _dialogNoticeHorizontalPadding = 3
                                             + GUIStyles.DialogIconStyle.fixedWidth
                                             + GUIStyles.DialogBox.padding.left
                                             + GUIStyles.DialogBox.padding.right;

            _dialogNoticeVerticalPadding = GUIStyles.DialogBox.padding.bottom + GUIStyles.DialogBox.padding.top;
        }

        private void OnEnable()
        {
            _category = serializedObject.FindProperty("_category");
        }

        private bool Foldout(object handle)
        {
            var rect = GUILayoutUtility.GetRect(12, EditorGUIUtility.singleLineHeight + 4);
            rect.x += 12;

            var foldout = _foldouts.Contains(handle);
            var newFoldout = EditorGUI.Foldout(rect, foldout, "", Styles.GUIStyles.FoldoutLeft);
            if (foldout != newFoldout)
            {
                foldout = newFoldout;
                if (newFoldout)
                {
                    _foldouts.Add(handle);
                }
                else
                {
                    _foldouts.Remove(handle);
                }
            }

            return newFoldout;
        }

        private void ShowHeaderGUI()
        {
            var currentWidth = EditorGUIUtility.currentViewWidth;
            var expectedButtonHeight = Meta.XR.Editor.ToolingSupport.Styles.Constants.Height;

            GUILayout.BeginArea(new Rect(0, 0, currentWidth, expectedButtonHeight));
            EditorGUILayout.BeginHorizontal();
            Utils.ToolDescriptor.DrawButton(null, false, true, Origins.Component);
            EditorGUILayout.EndVertical();
            GUILayout.EndArea();
            GUILayoutUtility.GetRect(currentWidth, expectedButtonHeight);

            var infoWidth = currentWidth - _dialogNoticeHorizontalPadding;
            var expectedInfoHeight = GUIStyles.DialogTextStyle.CalcHeight(_dialogNotice, infoWidth);
            expectedInfoHeight += _dialogNoticeVerticalPadding;
            GUILayout.BeginArea(new Rect(0, expectedButtonHeight, currentWidth, expectedInfoHeight));
            var rect = EditorGUILayout.BeginHorizontal(GUIStyles.DialogBox);
            EditorGUILayout.LabelField(DialogIcon, GUIStyles.DialogIconStyle,
                GUILayout.Width(GUIStyles.DialogIconStyle.fixedWidth));
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(_dialogNotice, GUIStyles.DialogTextStyle);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUILayoutUtility.GetRect(currentWidth, expectedInfoHeight);
        }

        private void DrawToggle(InspectedItemBase item)
        {
            EditorGUI.EndDisabledGroup();
            using (new ColorScope(ColorScope.Scope.All,
                       item.enabled ? Styles.Colors.AccentColorBrighter : Color.white))
            {
                using (var changedCheckScope = new EditorGUI.ChangeCheckScope())
                {
                    item.enabled = EditorGUILayout.Toggle(item.enabled, GUILayout.Width(16.0f));
                    _hasChanged |= changedCheckScope.changed;
                }
            }

            EditorGUI.BeginDisabledGroup(!item.enabled);
        }

        private void ShowMemberGUI(InspectedMember member)
        {
            if (!member.Valid) return;

            using (var _ = new EditorGUI.DisabledScope(!member.enabled))
            {
                EditorGUILayout.BeginVertical(Styles.GUIStyles.ContentBox);

                using (new IndentScope(0))
                {
                    EditorGUILayout.BeginHorizontal();
                    var foldout = Foldout(member);
                    EditorGUILayout.LabelField(member.MemberInfo.BuildSignatureForDebugInspector(), Styles.GUIStyles.Label);
                    GUILayout.FlexibleSpace();
                    DrawToggle(member);
                    EditorGUILayout.EndHorizontal();

                    if (foldout)
                    {
                        var attribute = member.attribute;

                        using (var changedCheckScope = new EditorGUI.ChangeCheckScope())
                        {
                            member.DrawTweak();
                            member.DrawGizmo();

                            EditorGUI.BeginChangeCheck();
                            var color = EditorGUILayout.ColorField(
                                new GUIContent("Color",
                                    "Optional color used for DebugGizmo line drawing and inspector row pill icon"),
                                attribute.Color,
                                false, false, false);
                            if (EditorGUI.EndChangeCheck())
                            {
                                attribute.Color = color;
                            }

                            attribute.Category = EditorGUILayout.TextField(
                                new GUIContent("Category", "Optional category for a specific tab in Inspector Panel"),
                                attribute.Category);

                            attribute.DisplayName = EditorGUILayout.TextField(
                                new GUIContent("Display Name", DebugMember.DisplayNameTooltip),
                                attribute.DisplayName);

                            attribute.Description = EditorGUILayout.TextField(
                                new GUIContent("Description", "Optional description to be shown in the Inspector Panel."),
                                attribute.Description);

                            _hasChanged |= changedCheckScope.changed;
                        }
                    }
                }

                EditorGUILayout.EndVertical();
            }
        }

        private string ComputeComponentName(InspectedHandle handle)
        {
            var prefix = handle.InstanceHandle.Type.IsPublic
                ? "public"
                : (handle.InstanceHandle.Type.IsNestedPrivate ? "private" : "internal");
            return $"<i>{prefix} class</i> <b>{handle.InstanceHandle.Type.Name}</b>";
        }

        private void ShowHandleGUI(InspectedHandle handle)
        {
            if (!handle.Valid) return;

            using (new EditorGUI.DisabledScope(!handle.enabled))
            {
                EditorGUILayout.BeginVertical(Styles.GUIStyles.ContentBox);
                EditorGUILayout.BeginHorizontal();
                var foldout = Foldout(handle);
                EditorGUILayout.LabelField(ComputeComponentName(handle), Styles.GUIStyles.Label);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(handle.InstanceHandle.Instance, typeof(MonoBehaviour), true);
                }

                DrawToggle(handle);
                EditorGUILayout.EndHorizontal();

                if (foldout)
                {
                    EditorGUILayout.BeginVertical();
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Members", GUIStyles.BoldLabel);
                    using (new IndentScope(EditorGUI.indentLevel + 2))
                    {
                        var filteredMembers = Utils.Filter(handle.inspectedMembers, _filterComponent);
                        foreach (var member in filteredMembers)
                        {
                            ShowMemberGUI(member);
                        }
                    }

                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.EndVertical();
            }
        }

        public override void OnInspectorGUI()
        {
            var inspector = target as DebugInspector;

            CheckForComponentChanges();

            ShowHeaderGUI();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField(Utils.PublicName, Utils.ComputeInfoText().Item1);
                EditorGUILayout.ObjectField("Tracked instance", inspector.gameObject, typeof(GameObject), true);
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_category, false);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Components", GUIStyles.BoldLabel);

            _filterComponent = EditorGUILayout.TextField(_filterComponent, GUI.skin.FindStyle("SearchTextField"));

            var registry = inspector.Registry;
            foreach (var handle in registry.Handles)
            {
                ShowHandleGUI(handle);
            }

            if (_hasChanged)
            {
                EditorUtility.SetDirty(target);
                _hasChanged = false;
            }
        }

        private void CheckForComponentChanges()
        {
            var inspector = target as DebugInspector;

            var currentComponents = inspector.GetComponents<Component>().ToList();

            // Check for added components
            if (currentComponents.Any(component => !previousComponents.Contains(component))
                || previousComponents.Any(component => !currentComponents.Contains(component)))
            {
                inspector.Initialize();
            }

            previousComponents = currentComponents;
        }
    }
}
