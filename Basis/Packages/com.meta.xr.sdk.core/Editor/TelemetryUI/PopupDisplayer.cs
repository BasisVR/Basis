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
using Meta.XR.Editor.Notifications;
using Meta.XR.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.TelemetryUI
{
    [InitializeOnLoad]
    internal static class PopupDisplayer
    {
        private static readonly CustomBool InitSession = new OnlyOncePerSessionBool
        {
            Owner = null,
            Uid = "InitPopupSession",
            SendTelemetry = false
        };

        static PopupDisplayer()
        {
            EditorApplication.update += Update;
        }

        private static void Update()
        {
            EditorApplication.update -= Update;

            if (!InitSession.Value)
            {
                return;
            }

            if (Application.isBatchMode)
            {
                return;
            }

            if (OVRPlugin.UnifiedConsent.ShouldShowTelemetryConsentWindow())
            {
                ShowConsentPopup();
            }
            else if (OVRPlugin.UnifiedConsent.ShouldShowTelemetryNotification())
            {
                ShowNoticePopup();
            }
        }

        private static void ShowConsentPopup()
        {
            ConsentEditorWindow.ShowWindow();
        }

        private static void ShowNoticePopup()
        {
            var noticeMarkdownText = OVRPlugin.UnifiedConsent.GetConsentNotificationMarkdownText();

            if (string.IsNullOrEmpty(noticeMarkdownText))
            {
                return;
            }

            var notification = new Notification("TelemetryNotice")
            {
                Icon = UserInterface.Styles.Contents.MetaWhiteIcon,
                Duration = 20f,
                ShowCloseButton = true,
                Items = Utils.GetGuideItemsForMarkdownText(noticeMarkdownText)
            };

            notification.OnShow += () => { OVRPlugin.UnifiedConsent.SetNotificationShown(); };

            notification.Enqueue(Origins.Notification);
        }
    }
}
