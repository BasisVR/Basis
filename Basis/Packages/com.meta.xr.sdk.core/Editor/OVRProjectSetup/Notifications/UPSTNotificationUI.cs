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
using System.Text;
using Meta.XR.Editor.Notifications;
using Styles = Meta.XR.Editor.UserInterface.Styles;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.UserInterface;
using UnityEngine;

namespace Meta.XR.Editor.UPST.Notifications
{
    internal static class UPSTNotificationUI
    {
        public static void ShowNotification(this UPSTNotificationData notificationData)
        {
            var notification = new Notification("UPSTNotification")
            {
                Icon = Styles.Contents.MetaWhiteIcon,
                ShowCloseButton = true,
                Items = new IUserInterfaceItem[]
                {
                    new GroupedItem(GetGuideItemsForNotificationData(notificationData),
                        UserInterface.Utils.UIItemPlacementType.Vertical)
                },
                GradientColor = Meta.XR.Editor.UserInterface.Utils.HexToColor("#6a6a6a")
            };

            notificationData.CloseAction = () =>
            {
                notification.Remove(Origins.Self);
            };

            notification.Enqueue(Origins.Notification);
        }

        private static IEnumerable<IUserInterfaceItem> GetGuideItemsForNotificationData(
            UPSTNotificationData notificationData)
        {
            var oneFix = notificationData.TaskList.Count == 1;
            var dotColor = notificationData.Level == OVRProjectSetup.TaskLevel.Required ? "#ed5757" : "#e9974e";

            yield return new Label(
                $"Project Setup Tool {(oneFix ? "Fix" : "Fixes")} Available",
                UIStyles.GUIStyles.Title);

            yield return new Label(
                $"There {(oneFix ? "is" : "are")} {notificationData.TaskList.Count} outstanding <color=\"{dotColor}\">•</color> {notificationData.Level.ToString()} {(oneFix ? "fix" : "fixes")}:",
                UIStyles.GUIStyles.Label);

            const int maxNotificationsShown = 4;
            var tasksLabel = new StringBuilder();

            foreach (var task in notificationData.TaskList.Take(maxNotificationsShown))
            {
                tasksLabel.AppendLine($" • {task.Message.GetValue(notificationData.BuildTargetGroup)}");
            }

            if (notificationData.TaskList.Count > maxNotificationsShown)
            {
                tasksLabel.AppendLine(
                    " • (...)");
            }

            yield return new Label(
                tasksLabel.ToString(),
                UIStyles.GUIStyles.Label);

            yield return new GroupedItem(GetNotificationButtons(notificationData));
        }

        private static IEnumerable<IUserInterfaceItem> GetNotificationButtons(UPSTNotificationData notificationData)
        {
            if (notificationData.TaskList == null || notificationData.TaskList.Count == 0)
            {
                yield break;
            }

            yield return new AddSpace(flexibleSpace: true);

            var buttonLayout = new[] { GUILayout.Height(20f), GUILayout.Width(80f) };
            var highlightedButtonColor = Styles.Colors.LightMeta;

            yield return new Button(
                new ActionLinkDescription
                {
                    Content = new GUIContent("More Details"),
                    Action = () =>
                    {
                        OVRProjectSetupSettingsProvider.OpenSettingsWindow(Origins.Notification);
                        notificationData.CloseNotification();
                    }
                },
                buttonLayout);

            var canFix = notificationData.TaskList.Any(task => task.FixAction != null);

            if (canFix)
            {
                yield return new Button(
                    new ActionLinkDescription
                    {
                        Content = new GUIContent(notificationData.TaskList.Count == 1 ? "Fix" : "Fix All"),
                        Action = () => FixAvailableTasks(notificationData),
                        BackgroundColor = highlightedButtonColor
                    }, buttonLayout
                );
            }
        }

        private static void FixAvailableTasks(UPSTNotificationData notificationData)
        {
            foreach (var task in notificationData.TaskList)
            {
                if (task.FixAction == null)
                {
                    continue;
                }

                task.Fix(notificationData.BuildTargetGroup);
            }

            notificationData.CloseNotification();
        }
    }
}
