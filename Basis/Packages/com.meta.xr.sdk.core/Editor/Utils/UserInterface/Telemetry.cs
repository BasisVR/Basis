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

using static OVRTelemetry;

namespace Meta.XR.Editor.UserInterface
{
    public static class Telemetry
    {
        [Markers]
        public static class MarkerId
        {
            public const int LinkClick = 163057622;
            public const int PageOpen = 163063708;
            public const int PageClose = 163065149;
            public const int SettingsChanged = 163056413;
        }

        public static class AnnotationType
        {
            public const string Label = "label";
            public const string Url = "url";
            public const string Type = "type";
            public const string Origin = "origin";
            public const string OriginData = "origin_data";
            public const string Action = "action";
            public const string ActionData = "action_data";
            public const string ActionType = "action_type";
            public const string Value = "value";
            public const string SubOrigin = "sub_origin";
        }
    }
}
