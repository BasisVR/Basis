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

using System.Collections.Generic;
using System.Linq;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Colors;

namespace Meta.XR.Editor.TelemetryUI
{
    [InitializeOnLoad]
    internal static class Settings
    {
        private static List<IUserInterfaceItem> _consentGuideItems;
        private static bool? _isSettingsChangeEnabled;

        static Settings()
        {
            OVRUserSettingsProvider.Register("Telemetry", OnSettingsGUI);
        }

        private static List<IUserInterfaceItem> FetchConsentGuideItems()
        {
            var telemetryConsentText = OVRPlugin.UnifiedConsent.GetConsentSettingsChangeText();

            if (string.IsNullOrEmpty(telemetryConsentText))
            {
                return new List<IUserInterfaceItem>();
            }

            var items =
                Utils.GetGuideItemsForMarkdownText(telemetryConsentText)
                    .Select(item =>
                    {
                        switch (item)
                        {
                            case Label label:
                                label.GUIStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                                return label;
                            case LinkLabel link:
                                return new GroupedItem(new List<IUserInterfaceItem>
                                {
                                    new AddSpace(16),
                                    link,
                                    new AddSpace(flexibleSpace: true)
                                });
                            default:
                                return item;
                        }
                    }).ToList();

            foreach (var item in items)
            {
                if (item is Label label)
                {
                    label.GUIStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                }
            }

            return items;
        }

        private static void OnSettingsGUI()
        {
            _isSettingsChangeEnabled ??= OVRPlugin.UnifiedConsent.IsConsentSettingsChangeEnabled();

            using (var check = new EditorGUI.ChangeCheckScope())
            {
                using var disabledScope = new EditorGUI.DisabledScope(!_isSettingsChangeEnabled.Value);

                var telemetryEnabled =
                    EditorGUILayout.Toggle(new GUIContent("Share Additional Data"),
                        OVRTelemetryConsent.ShareAdditionalData);
                if (check.changed)
                {
                    OVRTelemetryConsent.SetTelemetryEnabled(telemetryEnabled);
                }
            }

            _consentGuideItems ??= FetchConsentGuideItems();
            if (_consentGuideItems.Any())
            {
                var group = new GroupedItem(_consentGuideItems, UserInterface.Utils.UIItemPlacementType.Vertical);
                group.Draw();
            }

        }
    }
}
