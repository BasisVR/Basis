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
using Meta.XR.Editor.StatusMenu;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.Contents;
using Styles = Meta.XR.Editor.UserInterface.Styles;

namespace Meta.XR.InputActions.Editor
{
    [InitializeOnLoad]
    internal static class Utils
    {
        internal const string PublicName = "Input Actions";
        internal const string PublicTag = "[IA]";

        public static readonly string Description =
            "<b>Input Actions</b> are a way to define how inputs from certain devices such as the Logitech MX Ink Stylus are made available through the Meta Core SDK.\n" +
            "Actions are defined using the Open XR Action specification, where an Action describes how that particular action, e.g. a button press, could be retrieved from a particular controller.\n" +
            " - The Action name describes how the action can be accessed in code.\n" +
            " - The Interaction Profile identifies which device the action applies to, e.g. <i>/interaction_profiles/oculus/touch_controller</i> would indicate this action should be used if the attached device is a Meta Quest Touch controller.\n" +
            " - The Paths identify which input is to be returned from the device, e.g. <i>/user/hand/left/input/grip/pose</i> would indicate the action should return the grip pose of the left controller.\n\n" +
            "Multiple actions can exist with the same name so long as they have different Interaction Profiles. When that action name is queried the Open XR runtime will determine the right action to use based on which devices are attached.";

        internal static readonly TextureContent.Category InputActionIcons = new("InputActions/Icons");
        internal static readonly TextureContent StatusIcon = TextureContent.CreateContent("ovr_icon_stylus.png", InputActionIcons, $"Open {PublicName}");

        private const string DocumentationURL = "https://developer.oculus.com/documentation/unity/unity-inputactions/";

        internal static ToolDescriptor ToolDescriptor = new ToolDescriptor()
        {
            Name = PublicName,
            Description = Description,
            Color = Styles.Colors.NewColor,
            Icon = StatusIcon,
            PillIcon = null,
            Order = 2,
            Experimental = true,
            AddToMenu = false,
            OnProjectSettingsGUI = InputSettings.OnGUI,
            Documentation = new List<Documentation>()
            {
                new Documentation()
                {
                    Title = PublicName,
                    Url = DocumentationURL
                }
            }
        };

        static Utils()
        {
            //StatusMenu.RegisterItem(Item);
        }
    }
}
