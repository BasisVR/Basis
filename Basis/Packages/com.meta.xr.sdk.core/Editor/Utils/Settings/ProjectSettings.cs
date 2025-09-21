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
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.Settings
{
    [Serializable]
    internal class ProjectSettings : ScriptableObject
    {
        internal class SettingBool : CustomBool
        {
            public SettingBool()
            {
                Get = () => GetProjectConfig(create: false)?.GetProjectBool(Key, Default) ??
                            Default;
                Set = value =>
                {
                    if (value == Default)
                    {
                        // If back to Default, we remove it from the dictionary to avoid clutter
                        GetProjectConfig()?.RemoveProjectBool(Key);
                    }
                    else
                    {
                        GetProjectConfig()?.SetProjectBool(Key, value);
                    }
                };
            }
        }

        [Serializable]
        public class BoolProperties : SerializableDictionary<string, bool>
        {
        }

        [Serializable]
        public class IntProperties : SerializableDictionary<string, int>
        {
        }

        [SerializeField] private BoolProperties boolProperties = new();
        [SerializeField] private IntProperties intProperties = new();

        private static ProjectSettings _config;

        public bool HasBool(string key)
        {
            return boolProperties.ContainsKey(key);
        }

        public bool GetProjectBool(string key, bool defaultValue)
        {
            if (!boolProperties.TryGetValue(key, out var value))
            {
                // To avoid clutter, getter doesn't add to the dictionary
                value = defaultValue;
            }

            return value;
        }

        public void SetProjectBool(string key, bool value)
        {
            boolProperties[key] = value;
            EditorUtility.SetDirty(this);
        }

        public void RemoveProjectBool(string key)
        {
            boolProperties.Remove(key);
            EditorUtility.SetDirty(this);
        }

        public int GetProjectInt(string key, int defaultValue)
        {
            if (!intProperties.TryGetValue(key, out var value))
            {
                value = defaultValue;
            }

            return value;
        }

        public void SetProjectInt(string key, int value)
        {
            intProperties[key] = value;
            EditorUtility.SetDirty(this);
        }

        public void RemoveProjectInt(string key)
        {
            intProperties.Remove(key);
            EditorUtility.SetDirty(this);
        }

        public static ProjectSettings GetProjectConfig(bool refresh = false, bool create = true)
        {
            if (_config != null && !refresh)
            {
                return _config;
            }

            const string assetName = "MetaXRProjectSettings.asset";
            const string dirPath = "Assets/MetaXR";

            if (!AssetDatabase.IsValidFolder(dirPath))
            {
                AssetDatabase.CreateFolder("Assets", "MetaXR");
            }

            var assetPath = Path.Combine(dirPath, assetName);

            try
            {
                _config = AssetDatabase.LoadAssetAtPath(assetPath, typeof(ProjectSettings)) as ProjectSettings;
            }
            catch (Exception e)
            {
                Debug.LogWarningFormat("Unable to load ProjectConfig from {0}, error {1}", assetPath, e.Message);
            }

            if (_config == null && create && !BuildPipeline.isBuildingPlayer)
            {
                Debug.LogFormat("Creating ProjectConfig at path {0}", assetPath);
                _config = CreateInstance<ProjectSettings>();
                AssetDatabase.CreateAsset(_config, assetPath);
            }

            return _config;
        }
    }
}
