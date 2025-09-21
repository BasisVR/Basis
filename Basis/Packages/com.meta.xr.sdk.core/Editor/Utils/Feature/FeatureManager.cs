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
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Meta.XR.Editor.Settings;
using Meta.XR.Util;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Meta.XR.Editor.Features
{
    internal static class FeatureManager
    {
        private static readonly UserString Features = new()
        {
            Default = "",
            SendTelemetry = false,
            Owner = null,
            Uid = "features-key"
        };

        public static async Task<string> GetFeaturesInScene(Scene scene)
        {
            HashSet<Feature> features = new();
            try
            {
                List<GameObject> rootObjects = new();
                rootObjects.AddRange(scene.GetRootGameObjects());

                foreach (var rootObject in rootObjects)
                {
                    await GetFeaturesGameObject(rootObject, features);
                }
            }
            catch (Exception)
            {
                return Features.Value;
            }

            Features.SetValue(string.Join(", ", features));
            return Features.Value;
        }

        private static async Task GetFeaturesGameObject(GameObject gameObject, HashSet<Feature> features)
        {
            List<Component> componentList = new();
            gameObject.GetComponentsInChildren(componentList);
            foreach (var component in componentList)
            {
                await GetFeaturesComponent(component, features);
            }
        }

        private static async Task GetFeaturesComponent(Component component, HashSet<Feature> features)
        {
            TypeInfo typeInfo = component.GetType().GetTypeInfo();
            var attributes = typeInfo.GetCustomAttributes(typeof(FeatureAttribute), false);
            await Task.Run(() =>
            {
                foreach (FeatureAttribute attribute in attributes)
                {
                    features.Add(attribute.Feature);
                }
            });
        }

        public static string GetFeatureStatusInSettings()
        {
            var projectConfig = OVRProjectConfig.CachedProjectConfig;
            if (projectConfig == null)
            {
                return "";
            }

            StringBuilder builder = new();

            builder.Append("HandTrackingSupport:");
            builder.Append(projectConfig.handTrackingSupport);
            builder.Append(",");

            builder.Append("HandTrackingFrequency:");
            builder.Append(projectConfig.handTrackingFrequency);
            builder.Append(",");

            builder.Append("HandTrackingVersion:");
            builder.Append(projectConfig.handTrackingVersion);
            builder.Append(",");

            builder.Append("AnchorSupport:");
            builder.Append(projectConfig.anchorSupport);
            builder.Append(",");

            builder.Append("SharedAnchorSupport:");
            builder.Append(projectConfig.sharedAnchorSupport);
            builder.Append(",");

            builder.Append("TrackedKeyboardSupport:");
            builder.Append(projectConfig.trackedKeyboardSupport);
            builder.Append(",");

            builder.Append("BodyTrackingSupport:");
            builder.Append(projectConfig.bodyTrackingSupport);
            builder.Append(",");

            builder.Append("FaceTrackingSupport:");
            builder.Append(projectConfig.faceTrackingSupport);
            builder.Append(",");

            builder.Append("EyeTrackingSupport:");
            builder.Append(projectConfig.eyeTrackingSupport);
            builder.Append(",");

            builder.Append("VirtualKeyboardSupport:");
            builder.Append(projectConfig.virtualKeyboardSupport);
            builder.Append(",");

            builder.Append("SceneSupport:");
            builder.Append(projectConfig.sceneSupport);
            builder.Append(",");

            builder.Append("RequiresSystemKeyboard:");
            builder.Append(projectConfig.requiresSystemKeyboard);
            builder.Append(",");

            builder.Append("PassthroughSupport:");
            builder.Append(projectConfig.insightPassthroughSupport);
            builder.Append(",");

            builder.Append("SystemLoadingScreenBackground:");
            builder.Append(projectConfig.systemLoadingScreenBackground);

            OVRRuntimeSettings runtimeSettings = OVRRuntimeSettings.Instance;
            if (runtimeSettings == null)
            {
                return builder.ToString();
            }

            builder.Append(",");

            builder.Append("BodyTrackingFidelity:");
            builder.Append(runtimeSettings.BodyTrackingFidelity);
            builder.Append(",");

            builder.Append("BodyTrackingJointSet:");
            builder.Append(runtimeSettings.BodyTrackingJointSet);
            builder.Append(",");


            builder.Append("VisualFaceTracking:");
            builder.Append(runtimeSettings.RequestsVisualFaceTracking);
            builder.Append(",");

            builder.Append("AudioFaceTracking:");
            builder.Append(runtimeSettings.RequestsAudioFaceTracking);
            builder.Append(",");

            builder.Append("HandSkeletonVersion:");
            builder.Append(runtimeSettings.HandSkeletonVersion);
            builder.Append(",");

            builder.Append("UseISDKOpenXRHand:");
#if ISDK_OPENXR_HAND
            builder.Append(true);
#else
            builder.Append(false);
#endif

            return builder.ToString();
        }
    }
}
