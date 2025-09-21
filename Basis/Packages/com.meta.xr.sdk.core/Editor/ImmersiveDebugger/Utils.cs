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
using System.Linq;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles.Contents;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    [InitializeOnLoad]
    internal static class Utils
    {
        internal const string PublicName = "Immersive Debugger";
        internal const string PublicTag = "[ID]";

        public static readonly string Description =
            "Displays the console and track Game Objects, MonoBehaviors and their members in real time within your headset." +
            "\n\nYou can track your components' members by using either of the following methods: " +
            $"\n• <i>In code:</i> Add the <b>{nameof(DebugMember)}</b> attribute to any member you want to track" +
            $"\n• <i>In scene:</i> Add and configure the <b>{nameof(DebugInspector)}</b> component to any GameObject you want to track" +
            $"\n<color={ColorToHex(NewColor)}>• [New]</color><i> Using the hierarchy view:</i> Directly within the Immersive Debugger, track any GameObject and its Components without any preconfiguration";

        internal static readonly TextureContent.Category ImmersiveDebuggerIcons = new("ImmersiveDebugger/Icons");
        internal static readonly TextureContent StatusIcon = TextureContent.CreateContent("ovr_icon_idf.png", ImmersiveDebuggerIcons, $"Open {PublicName}");

        private const string DocumentationUrl = "https://developer.oculus.com/documentation/unity/immersivedebugger-overview";


        internal static readonly ToolDescriptor ToolDescriptor = new ToolDescriptor()
        {
            Name = PublicName,
            MqdhCategoryId = "1062327272563816",
            MenuDescription = "Debug in headset",
            Description = Description,
            Color = Styles.Colors.AccentColor,
            Icon = StatusIcon,
            InfoTextDelegate = ComputeInfoText,
            PillIcon = ComputePillIcon,
            OnClickDelegate = OnStatusMenuClick,
            Order = 11,
            Experimental = false,
            CanBeNew = true,
            AddToStatusMenu = true,
            OnProjectSettingsGUI = Settings.OnGUI,
            Documentation = new List<Documentation>()
            {
                new Documentation()
                {
                    Title = PublicName,
                    Url = DocumentationUrl
                }
            }
        };

        public static (TextureContent, Color?, bool) ComputePillIcon() =>
            RuntimeSettings.Instance.ImmersiveDebuggerEnabled
                ? (CheckIcon, XR.Editor.UserInterface.Styles.Colors.SuccessColor, false)
                : (null, XR.Editor.UserInterface.Styles.Colors.DisabledColor, false);

        public static (string, Color?) ComputeInfoText() =>
            RuntimeSettings.Instance.ImmersiveDebuggerEnabled
                ? ("Enabled", XR.Editor.UserInterface.Styles.Colors.SuccessColor)
                : ("Disabled", XR.Editor.UserInterface.Styles.Colors.DisabledColor);

        private static void OnStatusMenuClick(Origins origin)
        {
            ToolDescriptor.OpenProjectSettings(origin);
        }

        internal static IEnumerable<InspectedMember> Filter(IEnumerable<InspectedMember> members, string queryString) => members.Where(member => member.Valid && Match(member.MemberInfo.BuildSignatureForDebugInspector(), queryString));

        private static bool Match(string memberSignature, string queryString)
        {
            if (String.IsNullOrEmpty(memberSignature)) return false;
            return string.IsNullOrEmpty(queryString) || memberSignature.Contains(queryString, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
