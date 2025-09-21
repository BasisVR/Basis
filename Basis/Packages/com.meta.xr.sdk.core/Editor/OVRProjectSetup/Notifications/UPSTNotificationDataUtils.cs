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
using System.Globalization;
using System.Linq;
using UnityEditor;

namespace Meta.XR.Editor.UPST.Notifications
{
    internal static class UPSTNotificationDataUtils
    {
        public static void CloseNotification(this UPSTNotificationData notificationData)
        {
            notificationData.CloseAction?.Invoke();
        }

        public static UPSTNotificationData GetNotificationDataToShow(this OVRConfigurationTaskProcessor processor)
        {
            if (processor is not OVRConfigurationTaskUpdater updater)
            {
                return UPSTNotificationData.Empty;
            }

            var summary = updater.Summary;
            if (summary == null)
            {
                return UPSTNotificationData.Empty;
            }

            var highestLevel = summary.HighestFixLevel ?? OVRProjectSetup.TaskLevel.Optional;
            if (highestLevel == OVRProjectSetup.TaskLevel.Optional)
            {
                return UPSTNotificationData.Empty;
            }

            var fixableTasks =
                summary.GetFixableTasksOfLevel(highestLevel)
                    .Where(task => !task.Tags.HasFlag(OVRProjectSetup.TaskTags.ManuallyFixable))
                    .ToList();

            if (fixableTasks.Count == 0)
            {
                return UPSTNotificationData.Empty;
            }

            if (HasShownNotificationRecently(fixableTasks, highestLevel))
            {
                return UPSTNotificationData.Empty;
            }

            return new UPSTNotificationData
            {
                ShouldShow = true,
                TaskList = fixableTasks,
                Level = highestLevel,
                BuildTargetGroup = summary.BuildTargetGroup
            };
        }

        private static string GetNotificationShownKey(string notificationId) =>
            $"NotificationsScheduler.ShownNotification.{notificationId}";

        private static bool HasShownNotificationRecently(IReadOnlyCollection<OVRConfigurationTask> list, OVRProjectSetup.TaskLevel level)
        {
            var taskId = GetNotificationShownKey(GetTaskListId(list, level));
            return SessionState.GetBool(taskId, false)
                   || HasShownNotificationInThePreviousHours(taskId, 24);
        }

        private static bool HasShownNotificationInThePreviousHours(string taskId, int nHours)
        {
            var lastNotificationTimeString = EditorPrefs.GetString(taskId);
            if (!DateTime.TryParse(lastNotificationTimeString, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var lastNotificationTime))
            {
                return false;
            }

            var timeSinceLastNotification = DateTime.Now - lastNotificationTime;
            return timeSinceLastNotification.TotalHours < nHours;
        }

        public static void MarkNotificationShown(this UPSTNotificationData notificationData)
        {
            var taskId = GetNotificationShownKey(GetTaskListId(notificationData.TaskList, notificationData.Level));
            SessionState.SetBool(taskId, true);
            EditorPrefs.SetString(taskId, DateTime.Now.ToString(CultureInfo.InvariantCulture));
        }

        private static string GetTaskListId(IReadOnlyCollection<OVRConfigurationTask> list, OVRProjectSetup.TaskLevel level) =>
            list.Count switch
            {
                0 => string.Empty,
                1 => list.First().Id,
                _ => Enum.GetName(typeof(OVRProjectSetup.TaskLevel), level) // Group notifications with multiple tasks per level
            };
    }
}
