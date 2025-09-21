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

#if USING_XR_SDK_OPENXR
#if UNITY_EDITOR

using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;

namespace Meta.XR
{
    /// <summary>
    /// Automatically enables the MetaXRFeature feature
    /// </summary>
    [InitializeOnLoad]
    public class MetaXRFeatureEnabler : MonoBehaviour
    {
        static MetaXRFeatureEnabler()
        {
            EditorApplication.update += EnableMetaXRFeature;
        }

        private static void EnableMetaXRFeature()
        {
            EditorApplication.update -= EnableMetaXRFeature;

            bool needEnable = false;
            var featureSetStandalone =
                OpenXRFeatureSetManager.GetFeatureSetWithId(BuildTargetGroup.Standalone, MetaXRFeatureSet.featureSetId);
            var featureSetAndroid =
                OpenXRFeatureSetManager.GetFeatureSetWithId(BuildTargetGroup.Android, MetaXRFeatureSet.featureSetId);

            if (featureSetStandalone != null && !featureSetStandalone.isEnabled)
                needEnable = true;

            if (featureSetAndroid != null && !featureSetAndroid.isEnabled)
                needEnable = true;

            bool promptDeclined = EditorPrefs.GetBool("meta_xr_feature_declined", false);
            if (promptDeclined)
                return;

            if (needEnable && !Application.isBatchMode)
            {
                bool result =
                    EditorUtility.DisplayDialog("Enable Meta XR Feature Set",
                        "Meta XR Feature Set must be enabled in OpenXR Feature Groups to support Meta Quest Features. Do you want to enable it now?",
                        "Yes", "Not Now");
                if (!result)
                {
                    needEnable = false;
                    EditorUtility.DisplayDialog("Meta XR Feature not enabled",
                        "You can enable Meta XR Feature Set in XR Plugin-in Management / OpenXR for using Meta Quest Features. Please enable it in both Standalone and Android settings.",
                        "Ok");
                    EditorPrefs.SetBool("meta_xr_feature_declined", true);
                }
            }

            if (needEnable)
            {
                if (featureSetStandalone != null && !featureSetStandalone.isEnabled)
                {
                    Debug.Log("Meta XR Feature Set enabled on Standalone");
                    featureSetStandalone.isEnabled = true;
                    OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(BuildTargetGroup.Standalone);
                }

                if (featureSetAndroid != null && !featureSetAndroid.isEnabled)
                {
                    Debug.Log("Meta XR Feature Set enabled on Android");
                    featureSetAndroid.isEnabled = true;
                    OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(BuildTargetGroup.Android);
                }
            }
        }
    }
}

#endif
#endif
