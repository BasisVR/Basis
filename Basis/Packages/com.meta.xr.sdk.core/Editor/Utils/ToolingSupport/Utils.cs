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
using Meta.XR.Editor.Reflection;
using UnityEditor;

namespace Meta.XR.Editor.ToolingSupport
{
    [Reflection]
    internal static class Utils
    {
        internal static readonly string MetaPublicName = "Meta";
        internal static readonly string MetaXRPublicName = "Meta XR";
        internal static readonly string MetaMenuPath = $"{MetaPublicName}/Tools/";


        internal const string MqdhUrl = "odh://feedback-hub";
        private const string ShowFeedbackForm = "showSubmitFeedback";
        private const string PlatformIdKey = "platformID";
        private const string PlatformId = "1249239062924997"; // Unity
        private const string CategoryIdKey = "categoryID";
        private const string LogsKey = "file";

        [Reflection(Type = typeof(Menu), Name = "AddMenuItem")]
        private static readonly StaticMethodInfoHandle<Action<string, string, bool, int, Action, Func<bool>>> AddMenuItemMethodInfo = new();

        public static void AddMenuItem(string menuPath, Action callback, string shortcut = null, int order = -1)
        {
            AddMenuItemMethodInfo.Invoke(menuPath, shortcut, false, order, callback, null);
        }

        private static string GetUnityLogsPath()
        {
#if UNITY_EDITOR_WIN
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity","Editor","Editor.log");
#else
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Unity", "Editor.log");
#endif
        }
        internal static string GetMqdhDeeplink(string toolCategory)
        {
            var parameters = new List<string>()
            {
                ShowFeedbackForm,
                PlatformIdKey + "=" + PlatformId,
                LogsKey + "=" + GetUnityLogsPath()
            };

            if (!string.IsNullOrEmpty(toolCategory))
            {
                parameters.Add(CategoryIdKey + "=" + toolCategory);
            }

            return MqdhUrl + "?" + string.Join("&", parameters);
        }
    }
}
