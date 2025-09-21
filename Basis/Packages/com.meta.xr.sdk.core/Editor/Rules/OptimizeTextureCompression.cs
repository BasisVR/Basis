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
using Meta.XR.Editor.Reflection;
using UnityEditor;

namespace Meta.XR.Editor.Rules
{
    [InitializeOnLoad]
    [Reflection]
    internal static class OptimizeTextureCompression
    {
        static OptimizeTextureCompression()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Quality,
                platform: BuildTargetGroup.Android,
                isDone: IsDone,
                conditionalMessage: ComputeMessage,
                fix: Fix,
                fixMessage: FixMessage
            );
        }

        public const string Title = "Optimize Texture Compression";
        public const string Description = "For GPU performance, please use ASTC. In some cases, ETC2 is also a viable solution.";
        public const string UnknownDescription = "Unable to detect format, please ensure it is either ASTC or ETC2.";
        private const string Message = Title + ": " + Description;
        private const string UnknownMessage = Title + ": " + UnknownDescription;
        private const string FixMessage = "EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC";

        private enum TextureCompressionFormat
        {
            Unknown,
            Other,
            ASTC,
            ETC2
        }

        private static TextureCompressionFormat GetCurrentCompressionFormat(BuildTargetGroup targetGroup)
        {
            switch (EditorUserBuildSettings.androidBuildSubtarget)
            {
                case MobileTextureSubtarget.ETC2:
                    return TextureCompressionFormat.ETC2;

                case MobileTextureSubtarget.ASTC:
                    return TextureCompressionFormat.ASTC;

                case MobileTextureSubtarget.Generic:
                {
#if UNITY_6000_0_OR_NEWER
                    // GetDefaultTextureCompressionFormat takes a BuildTarget as parameter in Unity 6 onward
                    var target = targetGroup.GetBuildTarget();
#else
                    // GetDefaultTextureCompressionFormat takes a BuildTargetGroup as parameter before Unity 6
                    var target = targetGroup;
#endif
                    var compressionFormat = GetDefaultTextureCompressionFormat.Invoke(target);
                    var name = compressionFormat?.ToString();
                    switch (name)
                    {
                        case "ASTC":
                            return TextureCompressionFormat.ASTC;
                        case "ETC2":
                            return TextureCompressionFormat.ETC2;
                        case null:
                            return TextureCompressionFormat.Unknown;
                        default:
                            return TextureCompressionFormat.Other;
                    }
                }

                default:
                    return TextureCompressionFormat.Other;
            }
        }

        public static bool IsDone(BuildTargetGroup targetGroup)
        {
            return GetCurrentCompressionFormat(targetGroup)
                is TextureCompressionFormat.ETC2 or TextureCompressionFormat.ASTC;
        }

        public static string ComputeMessage(BuildTargetGroup targetGroup)
            => GetCurrentCompressionFormat(targetGroup) switch
            {
                TextureCompressionFormat.Unknown => UnknownMessage,
                _ => Message
            };

        public static string ComputeDescriptionMessage(BuildTargetGroup targetGroup)
            => GetCurrentCompressionFormat(targetGroup) switch
            {
                TextureCompressionFormat.Unknown => UnknownDescription,
                _ => Description
            };

        public static void Fix(BuildTargetGroup targetGroup) =>
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        [Reflection(Type = typeof(PlayerSettings), Name = "GetDefaultTextureCompressionFormat")]
#if UNITY_6000_0_OR_NEWER
        // GetDefaultTextureCompressionFormat takes a BuildTarget as parameter in Unity 6 onward
        private static readonly StaticMethodInfoHandleWithWrapper<BuildTarget, Enum>
#else
        // GetDefaultTextureCompressionFormat takes a BuildTargetGroup as parameter before Unity 6
        private static readonly StaticMethodInfoHandleWithWrapper<BuildTargetGroup, Enum>
#endif
            GetDefaultTextureCompressionFormat = new();
    }
}
