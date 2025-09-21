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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.UserInterface;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Telemetry;

namespace Meta.XR.Editor.Notifications
{
    internal class Notification : IIdentified
    {
        private static class Manager
        {
            // Although it's going to be treated like a queue, we actually may have to be able to remove
            // any item an any time, so a list is more appropriate.
            private static readonly List<Notification> Notifications = new();

            public static void Enqueue(Notification notification, Origins origin)
            {
                if (Notifications.Contains(notification)) return;

                // We're still considering Open and Close telemetry to be linked to the request of showing the notification
                // independently of its actual stacking/queuing
                OVRTelemetry.Start(MarkerId.PageOpen)
                    .AddAnnotation(AnnotationType.Origin, origin.ToString())
                    .AddAnnotation(AnnotationType.Action, Origins.Notification.ToString())
                    .AddAnnotation(AnnotationType.ActionData, notification.Id)
                    .AddAnnotation(AnnotationType.ActionType, notification.GetType().Name)
                    .Send();

                Notifications.Add(notification);

                // Show the first one
                Notifications.First().Show();
            }

            public static void Remove(Notification notification, Origins origin)
            {
                if (!Notifications.Contains(notification)) return;

                // We're still considering Open and Close telemetry to be linked to the request of showing the notification
                // independently of its actual stacking/queuing
                OVRTelemetry.Start(MarkerId.PageClose)
                    .AddAnnotation(AnnotationType.Origin, origin.ToString())
                    .AddAnnotation(AnnotationType.Action, Origins.Notification.ToString())
                    .AddAnnotation(AnnotationType.ActionData, notification.Id)
                    .AddAnnotation(AnnotationType.ActionType, notification.GetType().Name)
                    .Send();

                // Hide if not hidden already
                notification.Hide();

                Notifications.Remove(notification);

                // Show the first one
                Notifications.FirstOrDefault()?.Show();
            }
        }
        public string Id { get; }

        public TextureContent Icon { get; set; }
        public IEnumerable<IUserInterfaceItem> Items { get; set; }
        public bool ShowCloseButton { get; set; }
        public float Duration { get; set; } = -1;
        public event System.Action OnShow;
        public Color GradientColor { get; set; } = UserInterface.Styles.Colors.Meta;
        public float ExpectedWidth { get; set; } = Styles.Constants.Width;

        private double _timestamp;
        private NotificationWindow _window;
        private bool Shown => _window != null;
        public bool DurationHasPassed => Duration > 0.0f && Time.realtimeSinceStartup - _timestamp > Duration;

        public Notification(string id)
        {
            Id = id;
        }

        public void Enqueue(Origins origin)
        {
            Manager.Enqueue(this, origin);
        }

        public void Remove(Origins origin)
        {
            Manager.Remove(this, origin);
        }

        private void Show()
        {
            if (Shown) return;

            if (!Meta.XR.Editor.UserInterface.Utils.ShouldRenderEditorUI()) return;

            _window = ScriptableObject.CreateInstance<NotificationWindow>();
            _window.Setup(this);
            _window.ShowAsTooltip();

            _timestamp = Time.realtimeSinceStartup;

            OnShow?.Invoke();
        }

        private void Hide()
        {
            if (!Shown) return;

            _window.Setup(null);
            _window.Close();
        }
    }
}
