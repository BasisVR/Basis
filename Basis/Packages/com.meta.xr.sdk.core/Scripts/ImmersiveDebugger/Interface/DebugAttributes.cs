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
using UnityEngine;
using UnityEngine.Scripting;

namespace Meta.XR.ImmersiveDebugger
{
    public enum DebugColor
    {
        Red,
        Gray
    }

    /// <summary>
    /// Annotate field, property, functions with this will show in Immersive Debugger panel in runtime.
    /// Without additional parameters specified, by default we're watching fields/properties,
    /// and provide a button to call function without parameter.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Enum)]
    [Serializable]
    public class DebugMember : PreserveAttribute
    {
        public const string DisplayNameTooltip = "Optional name override to be used in the Inspector Panel";

        private static readonly Dictionary<DebugColor, Color> ParsedColors = new()
        {
            { DebugColor.Red, Color.red },
            { DebugColor.Gray, Color.gray }
        };

        /// <summary>
        /// The type of the gizmo to draw in space according to the runtime value of the field/property data.
        /// The gizmoType must match the runtime value's type, check out <see cref="DebugGizmoType"/> for reference.
        /// </summary>
        public DebugGizmoType GizmoType = DebugGizmoType.None;
        /// <summary>
        /// Whether the gizmo will be turned on by default at startup.
        /// You can always turn the gizmo off by clicking the "eye" button next to the row of the debug option.
        /// </summary>
        public bool ShowGizmoByDefault = false;
        /// <summary>
        /// The color used for DebugGizmo line drawing and inspector row pill icon,
        /// note it doesn't apply to the Axis typed gizmo as it's drawing R/G/B colors for 3 axis.
        /// </summary>
        public Color Color = Color.gray;
        /// <summary>
        /// Specify whether this field/property is tweakable, will show control UI in headset panel (inspector).
        /// For now, it only supports two types: 1. boolean with checkboxes shown in headset panel, and
        /// 2. float/int which can be used together with <see cref="Min"/>, <see cref="Max"/> param with slider shown in headset panel.
        /// By default, to true and can be turned off if no need.
        /// </summary>
        public bool Tweakable = true;
        /// <summary>
        /// Minimum value for the tweak slider, only applicable for float/int data when <see cref="Tweakable"/> is true
        /// </summary>
        public float Min;
        /// <summary>
        /// Maximum value for the tweak slider, only applicable for float/int data when <see cref="Tweakable"/> is true
        /// </summary>
        public float Max = 1.0f;

        /// <summary>
        /// Optional category for a specific tab in Inspector Panel
        /// </summary>
        public string Category;

        /// <summary>
        /// Description for the attributed field to be used in the Inspector Panel.
        /// </summary>
        public string Description;

        /// <summary>
        /// Optional name override to be used in the Inspector Panel
        /// </summary>
        [Tooltip(DisplayNameTooltip)]
        public string DisplayName;

        /// <summary>
        /// Constructor of the DebugMember
        /// </summary>
        /// <param name="color">The DebugColor typed color used for DebugGizmo line drawing and inspector row pill icon, default to Gray</param>
        public DebugMember(DebugColor color = DebugColor.Gray)
        {
            ParsedColors.TryGetValue(color, out Color);
        }

        /// <summary>
        /// Constructor of the DebugMember
        /// </summary>
        /// <param name="colorString">The string typed color used for DebugGizmo line drawing and inspector row pill icon, default to Gray.
        /// Could be Hex code or literal colors from Unity https://docs.unity3d.com/ScriptReference/ColorUtility.TryParseHtmlString.html</param>
        public DebugMember(string colorString)
        {
            if (!string.IsNullOrEmpty(colorString))
            {
                Color = ColorUtility.TryParseHtmlString(colorString, out var color) ? color : Color.gray;
            }
        }
    }
}
