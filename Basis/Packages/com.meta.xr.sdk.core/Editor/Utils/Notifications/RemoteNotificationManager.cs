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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using UnityEditor;
using Oculus.VR.Editor.Utils;
using UnityEngine;
using Meta.XR.Editor.Settings;

namespace Meta.XR.Editor.Notifications
{
    [InitializeOnLoad]
    internal static class RemoteNotificationManager
    {
        private static readonly RemoteContentDownloader Downloader;
        private static readonly Validator Validator = new();
        private static readonly CustomBool InitSession = new OnlyOncePerSessionBool
        {
            Owner = null,
            Uid = "RemoteNotificationManager.InitSession",
            SendTelemetry = false
        };

        static RemoteNotificationManager()
        {
            if (!InitSession.Value)
            {
                return;
            }

            Downloader = new RemoteContentDownloader(
                cacheDurationInHours: 2,
                cacheFile: "remote_notifications.json",
                downloadPath: "https://www.facebook.com/developerframeworktools/unity/notifications");

#pragma warning disable CS4014
            InitializeAsync();
#pragma warning restore CS4014
        }

        private static async Task InitializeAsync()
        {
            var successfulLoad = await Reload();
            if (!successfulLoad)
            {
                await ForceDownloadAsync();
            }
        }


        private static async Task ForceDownloadAsync()
        {
            Downloader.ClearCache();
            await Reload();
        }

        private static async Task<bool> Reload()
        {
            var (downloadSuccess, jsonData) = await Downloader.RefreshAndLoad();
            if (!downloadSuccess)
            {
                return false;
            }

            var (parseSuccess, notifications) = GetNotificationsFromJsonString(jsonData);
            if (!parseSuccess)
            {
                return false;
            }

            EnqueueNotifications(notifications);
            return true;
        }

        private static (bool, IEnumerable<Notification>) GetNotificationsFromJsonString(string jsonData)
        {
            try
            {
                var response = JsonUtility.FromJson<NotificationsResponse>(jsonData);
                var notifications =
                    (response.notifications ?? Enumerable.Empty<NotificationData>())
                        .Where(data => data.IsNotificationValid(Validator))
                        .Select(data => data.BuildNotificationFromData());
                return (true, notifications);
            }
            catch (Exception)
            {
                return (false, Array.Empty<Notification>());
            }
        }

        private static void EnqueueNotifications(IEnumerable<Notification> notifications)
        {
            foreach (var notification in notifications)
            {
                notification.Enqueue(Origins.Remote);
            }
        }

    }
}
