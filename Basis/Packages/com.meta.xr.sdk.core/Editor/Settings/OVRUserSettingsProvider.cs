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
using Meta.XR.Editor.StatusMenu;
using Meta.XR.Editor.ToolingSupport;
using UnityEditor;

internal class OVRUserSettingsProvider : SettingsProvider
{
    public static string SettingsPath => $"Preferences/{OVREditorUtils.MetaXRPublicName}";

    private static readonly SortedDictionary<string, Action> SettingsRegistry = new();

    private OVRUserSettingsProvider(string path, SettingsScope scopes, IEnumerable<string> keywords = null)
        : base(path, scopes, keywords)
    {
    }

    [SettingsProvider]
    public static SettingsProvider CreateSettingsProvider() => new OVRUserSettingsProvider(SettingsPath, SettingsScope.User);

    public override void OnGUI(string searchContext)
    {
        EditorGUILayout.Space();

        EditorGUI.indentLevel++;

        var previousLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 256.0f;

        EditorGUILayout.BeginVertical();
        {
            foreach (var setting in SettingsRegistry)
            {
                EditorGUILayout.LabelField(setting.Key, EditorStyles.boldLabel);
                setting.Value?.Invoke();
                EditorGUILayout.Space();
            }
        }
        EditorGUILayout.EndVertical();

        EditorGUIUtility.labelWidth = previousLabelWidth;
        EditorGUI.indentLevel--;
    }

    public override void OnTitleBarGUI()
    {
        OVREditorUtils.SettingsToolDescriptor.DrawHeaderFromSettingProvider(Origins.Self);
    }

    public static void OpenSettingsWindow(Origins origin)
    {
        SettingsService.OpenUserPreferences(SettingsPath);
    }

    public static void Register(string title, Action onSettingsGUIDelegate)
    {
        if (SettingsRegistry.ContainsKey(title))
        {
            SettingsRegistry[title] += onSettingsGUIDelegate;
        }
        else
        {
            SettingsRegistry[title] = onSettingsGUIDelegate;
        }
    }
}
