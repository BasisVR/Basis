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

using MarkerPoint = OVRTelemetry.MarkerPoint;
using static OVRTelemetry;

internal static class OVRTelemetryConstants
{
    public static class OVRManager
    {
        [Markers]
        public static class MarkerId
        {
            public const int Init = 163069401;
        }

        public static class AnnotationTypes
        {
            public const string ProjectName = "ProjectName";
            public const string ProjectGuid = "ProjectGuid";
            public const string Origin = "Origin";
            public const string BatchMode = "BatchMode";
            public const string ProcessorType = "ProcessorType";
        }

        public static readonly MarkerPoint InitializeInsightPassthrough = new("InitializeInsightPassthrough");

        public static readonly MarkerPoint InitPermissionRequest = new("InitPermissionRequest");
    }

    public static class Editor
    {
        [Markers]
        public static class MarkerId
        {
            public const int Start = 163067235;
            public const int Build = 163066733;
            public const int ComponentAdd = 163060094;
            public const int FeaturesInScene = 163069415;
        }

        public static class AnnotationType
        {
            public const string ComponentName = "ComponentName";
            public const string AssemblyName = "AssemblyName";
            public const string UsesProSkin = "UsesProSkin";
            public const string Origin = "Origin";
        }
    }

    public static class BB
    {
        [Markers]
        public static class MarkerId
        {
            public const int OpenWindow = 163062905;
            public const int AddBlock = 163060420;
            public const int UpdateBlock = 163064521;
            public const int RunBlock = 163063912;
            public const int InstallSDK = 163067801;
            public const int RemoveSDK = 163067560;
            public const int InstallBlockData = 163065449;
            public const int OpenSceneWithBlock = 163063649;
            public const int VariantsWindowFlow = 163065580;
            public const int VariantsWindowOpen = 163068476;
        }

        public static class AnnotationType
        {
            public const string BlockId = "BlockId";
            public const string BlockName = "BlockName";
            public const string InstanceId = "InstanceId";
            public const string Version = "Version";
            public const string ActionTrigger = "action_trigger";
            public const string Error = "error";
            public const string SceneSizeInB = "SceneSizeInB";
            public const string InstallationRoutineId = "InstallationRoutineId";
            public const string InstallationRoutineData = "InstallationRoutineData";
            public const string BlocksCount = "BlocksCount";
        }

        public static class Origins
        {
        }
    }

    public static class GuidedSetup
    {
        [Markers]
        public static class MarkerId
        {
            public const int SetAppIdFromGuidedSetup = 163061548;
        }

        public static class AnnotationType
        {
            public const string HasAppId = "app_id_exist";
            public const string HasNewVersionAvailable = "new_version_available";
        }
    }

    public static class Scene
    {
        [Markers]
        public static class MarkerId
        {
            public const int UseOVRSceneManager = 163061745;
            public const int UseDefaultSceneModelLoader = 163059869;
            public const int SceneOpen = 163063049;
            public const int SceneClose = 163068562;
        }

        public static class AnnotationType
        {
            public const string UsingBasicPrefabs = "basic_prefabs";
            public const string UsingPrefabOverrides = "prefab_overrides";
            public const string ActiveRoomsOnly = "active_rooms_only";
            public const string Guid = "Guid";
            public const string BuildTarget = "BuildTarget";
            public const string RuntimePlatform = "RuntimePlatform";
            public const string Features = "Features";
            public const string EnabledSettings = "FeaturesSupportInSettings";
        }
    }

    public static class Feedback
    {
        [Markers]
        public static class MarkerId
        {
            public const int SubmitFeedback = 163059978;
        }

        public static class AnnotationType
        {
            public const string ToolName = "ToolName";
        }
    }

    public static class Utils
    {
        [Markers]
        public static class MarkerId
        {
            public const int DownloadContent = 163067281;
        }

        public static class AnnotationType
        {
            public const string ErrorMessage = "error_message";
            public const string StatusCode = "status_code";
            public const string ContentType = "content_type";
        }
    }

    public static class ProjectSettings
    {
        [Markers]
        public static class MarkerId
        {
            public const int RenderThreadingMode = 163060994;
            public const int RenderingPath = 163068301;
            public const int XrPluginType = 163069107;
        }

        public static class AnnotationType
        {
            public const string RenderThreadingMode = "render_threading_mode";
            public const string RenderingPath = "rendering_path";
            public const string XrPluginType = "xr_plugin_type";
        }

        public enum FoveatedRenderingMode
        {
            Unknown,
            FixedFoveatedRendering,
            EyeTrackedFoveatedRendering,
        }

        public enum FoveatedRenderingAPI
        {
            Unknown,
            Legacy,
            SRP
        }

        public enum DepthSubmissionMode
        {
            Unknown,
            None,
            Depth16Bit,
            Depth24Bit,
        }

        public enum RenderThreadingMode
        {
            Unknown,
            Multithreaded,
            LegacyGraphicsJobs,
            NativeGraphicsJobs
        }

        public enum RenderingPath
        {
            Unknown,
            Forward,
            ForwardPlus,
            Deferred
        }

        public enum XrPlugin
        {
            Unknown,
            Oculus,
            OpenXR
        }
    }

}
