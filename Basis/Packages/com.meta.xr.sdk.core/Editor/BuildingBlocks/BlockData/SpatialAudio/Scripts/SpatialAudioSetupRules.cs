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

#if USING_META_XR_AUDIO_SDK

using System.Linq;
using Meta.XR.Editor.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    [Reflection]
    internal static class SpatialAudioSetupRules
    {
        [Reflection(Type = typeof(AudioSettings), Name = "SetConfiguration")]
        private static readonly StaticMethodInfoHandleWithWrapper<AudioConfiguration, bool> SetConfiguration = new();

        [Reflection(Type = typeof(AudioSettings), Name = "SetAmbisonicDecoderPluginName")]
        private static readonly StaticMethodInfoHandleWithWrapperAction<string> SetAmbisonicDecoderPluginName = new();

        [Reflection(Type = typeof(AudioSettings), Name = "GetAmbisonicDecoderPluginName")]
        private static readonly StaticMethodInfoHandleWithWrapper<string> GetAmbisonicDecoderPluginName = new();

        private const string PluginName = "Meta XR Audio";
        private const int BestLatencyDSPBufferSize = 256;

        static SpatialAudioSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                    Utils.GetBlocks(BlockDataIds.SpatialAudio)
                        .Select(block => block.GetComponent<AudioSource>())
                        .Where(audioSource => audioSource != null)
                        .All(audioSource => Mathf.Approximately(audioSource.spatialBlend, 1)),
                message:
                $"Spatial Blend must be set to 1 in the audio source of the Spatial Audio {Utils.BlockPublicName}",
                fix: _ =>
                {
                    var blocks = Utils.GetBlocks(BlockDataIds.SpatialAudio);
                    foreach (var block in blocks)
                    {
                        block.GetComponent<AudioSource>().spatialBlend = 1.0f;
                    }
                },
                fixMessage: "Set Spatial Blend to 1"
            );

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                    Utils.GetBlocks(BlockDataIds.SpatialAudio)
                        .Select(block => block.GetComponent<AudioSource>())
                        .Where(audioSource => audioSource != null)
                        .All(audioSource => audioSource.clip != null),
                message:
                $"The audio clip should be set in the audio source of the Spatial Audio {Utils.BlockPublicName}"
            );

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                    Utils.GetBlocks(BlockDataIds.SpatialAudio)
                        .Select(block => block.GetComponent<AudioSource>())
                        .Where(audioSource => audioSource != null)
                        .All(audioSource => audioSource.clip == null || audioSource.clip.channels == 1),
                message: $"Audio Source clips must be monophonic when using the Spatial Audio {Utils.BlockPublicName}",
                fix: _ =>
                {
                    var stereoClips = Utils.GetBlocks(BlockDataIds.SpatialAudio)
                        .Select(block => block.GetComponent<AudioSource>())
                        .Where(audioSource => audioSource != null)
                        .Select(audioSource => audioSource.clip)
                        .Where(clip => clip != null && clip.channels > 1);

                    foreach (var clip in stereoClips)
                    {
                        ForceToMono(clip);
                    }
                },
                fixMessage: "Set Audio Source clips to monophonic"
            );

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                    Utils.GetBlocks(BlockDataIds.SpatialAudio)
                        .All(block =>
                            block.GetComponent<AudioSource>() != null
                            && block.GetComponent<MetaXRAudioSource>() != null),
                message:
                $"The Spatial Audio {Utils.BlockPublicName} requires the audio source components to be present",
                fix: _ =>
                {
                    var blocks = Utils.GetBlocks(BlockDataIds.SpatialAudio);
                    foreach (var block in blocks)
                    {
                        AddComponentIfMissing<AudioSource>(block.gameObject);
                        AddComponentIfMissing<MetaXRAudioSource>(block.gameObject);
                    }
                },
                fixMessage: "Add the audio source components"
            );

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                {
                    var isUsingSpatialAudioBB = Utils.GetBlocks(BlockDataIds.SpatialAudio).Count > 0;
                    if (!isUsingSpatialAudioBB)
                    {
                        return true;
                    }

                    if (AudioSettings.GetConfiguration().dspBufferSize != BestLatencyDSPBufferSize)
                    {
                        return false;
                    }

                    if (AudioSettings.GetConfiguration().speakerMode != AudioSpeakerMode.Stereo)
                    {
                        return false;
                    }

                    return ValidatePluginNameSettings();
                },
                message:
                $"The Spatial Audio {Utils.BlockPublicName} requires the audio settings to be updated",
                fix: _ =>
                {
                    var audioConfig = AudioSettings.GetConfiguration();
                    audioConfig.dspBufferSize = BestLatencyDSPBufferSize;
                    audioConfig.speakerMode = AudioSpeakerMode.Stereo;
                    SetConfiguration.Invoke(audioConfig);

                    FixPluginNameSettings();
                },
                fixMessage: "Update the audio settings"
            );
        }

        private static void FixPluginNameSettings()
        {
            if (!AudioSettings.GetSpatializerPluginNames().Contains(PluginName))
            {
                return;
            }

            AudioSettings.SetSpatializerPluginName(PluginName);
            SetAmbisonicDecoderPluginName.Invoke(PluginName);
        }

        private static bool ValidatePluginNameSettings()
        {
            if (!AudioSettings.GetSpatializerPluginNames().Contains(PluginName))
            {
                return true;
            }

            if (AudioSettings.GetSpatializerPluginName() != PluginName)
            {
                return false;
            }

            var ambisonicDecoderPluginName = GetAmbisonicDecoderPluginName.Invoke();
            return ambisonicDecoderPluginName == PluginName;
        }

        private static void AddComponentIfMissing<T>(GameObject go) where T : Component
        {
            if (go.GetComponent<T>() == null)
            {
                go.AddComponent<T>();
            }
        }

        private static void ForceToMono(Object clip)
        {
            var assetPath = AssetDatabase.GetAssetPath(clip);
            var audioImporter = AssetImporter.GetAtPath(assetPath) as AudioImporter;

            if (audioImporter == null)
            {
                return;
            }

            audioImporter.forceToMono = true;
            audioImporter.SaveAndReimport();
        }
    }
}

#endif // USING_META_XR_AUDIO_SDK
