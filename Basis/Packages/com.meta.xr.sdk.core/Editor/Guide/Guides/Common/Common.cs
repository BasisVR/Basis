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
using UnityEditor;
using UnityEngine;

#if USING_META_XR_PLATFORM_SDK
using Oculus.Platform;
#endif // USING_META_XR_PLATFORM_SDK

namespace Meta.XR.Guides.Editor
{
    internal static class Common
    {

#if USING_META_XR_PLATFORM_SDK
        internal const string DefaultAppIdFieldText = "Paste you App Id here";
        internal static bool ValidAppId(string text) => !string.IsNullOrEmpty(text) && text.All(char.IsDigit);

        internal static IUserInterfaceItem PlatformSettingsButtonGroup(IIdentified owner, EditorWindow window)
        {
            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new Button(new ActionLinkDescription()
                {
                    Content = new GUIContent("Open Platform Settings"),
                    Action = () => Selection.activeObject = PlatformSettings.Instance,
                    ActionData = null,
                    Origin = Origins.GuidedSetup,
                    OriginData = owner,
                    Id = "OpenPlatformSettingsButton"
                }),
                new Button(new ActionLinkDescription()
                {
                    Content = new GUIContent("Close"),
                    Action = window.Close,
                    ActionData = null,
                    Origin = Origins.GuidedSetup,
                    OriginData = owner,
                    Id = "CloseButton"
                }),
            });
        }

        internal static bool HasAppId()
        {
#if UNITY_ANDROID
            return !string.IsNullOrEmpty(PlatformSettings.MobileAppID);
#else
            return !string.IsNullOrEmpty(PlatformSettings.AppID);
#endif
        }

        internal static bool SetAppId(string appId)
        {
            if (!ValidAppId(appId)) return false;

#if UNITY_ANDROID
            PlatformSettings.MobileAppID = appId;
#else
            PlatformSettings.AppID = appId;
#endif
            Selection.activeObject = PlatformSettings.Instance;

            OVRTelemetry.Start(OVRTelemetryConstants.GuidedSetup.MarkerId.SetAppIdFromGuidedSetup).Send();
            return true;
        }
#endif // USING_META_XR_PLATFORM_SDK
    }
}
