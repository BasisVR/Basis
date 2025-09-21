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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEngine;
using Styles = Meta.XR.Editor.UserInterface.Styles;

namespace Meta.XR.Guides.Editor
{
    [GuideItems]
    internal class OcclusionBlockSetupInfo : GuidedSetup
    {
        private static GuideWindow _window;

        private static string ImportantTag = $"<color={XR.Editor.UserInterface.Utils.ColorToHex(Styles.Colors.Yellow)}>[Important]</color>";

        public static void Show(bool forceShow = false)
        {
            new OcclusionBlockSetupInfo().ShowWindow(Origins.Component, forceShow);
        }

        [GuideItems]
        private List<IUserInterfaceItem> GetItems()
        {
            var graphicsAPIInfo = "Set the <b>Graphics API</b> to <b>Vulkan</b> only in Unity Player Settings.";
            var quest3Info = "It's a <b>Quest 3</b> only feature and set the target device to <b>Quest 3</b>.";
            return new List<IUserInterfaceItem>
            {
                new BulletedLabel(graphicsAPIInfo, UIStyles.ContentStatusType.Warning),
                new BulletedLabel(quest3Info, UIStyles.ContentStatusType.Warning),
                new AddSpace(),
                new Label("<b>Additional notes:</b>"),
                new BulletedLabel(
                    "Occlusion / DepthAPI requires access to spatial data. <b>Scene Support</b> has been enabled."),
                new BulletedLabel("Minimum required Unity Editor version: <b>2022.3.1</b> or <b>2023.2.</b>"),
                new AddSpace(),
                new GroupedItem(new List<IUserInterfaceItem>
                {
                    new Label("Visit the following link for more details:"),
                    new LinkLabel(new GUIContent("DepthAPI documentation"), "https://developer.oculus.com/documentation/unity/unity-depthapi/", this),
                    new AddSpace(flexibleSpace: true)
                })
            };
        }

        internal override GuideWindow CreateWindow()
        {
            if (_window != null) return _window;

            var title = "Occlusion Building Block";
            var description = ImportantTag + " Occlusion Building Block made the following critical changes in the current project.";
            _window = Guide.Create(title, description, this);
            return _window;
        }
    }
}
