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


#if USING_META_XR_VOICE_SDK

using UnityEditor;
using Oculus.Voice.Dictation;
using Meta.WitAi.TTS;
using Object = UnityEngine.Object;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class VoiceSDKConfigurationSetupRules
    {
        static VoiceSDKConfigurationSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ => !UsesVoiceSDKComponents() ||
                    getProjectSettings().FindProperty("ForceInternetPermission").boolValue,
                message: "Using Voice SDK features in Android required internet access permission.",
                fix: _ =>
                {
                    var settings = getProjectSettings();
                    settings.FindProperty("ForceInternetPermission").boolValue = true;
                    settings.ApplyModifiedProperties();
                },
                fixMessage: "Force internet access permission."
            );
        }

        static SerializedObject getProjectSettings() {
            return new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]
            );
        }

        static bool UsesVoiceSDKComponents() {
            return Object.FindObjectOfType<AppDictationExperience>() != null
                || Object.FindObjectOfType<TTSService>() != null;
        }
    }
}

#endif // USING_META_XR_VOICE_SDK
