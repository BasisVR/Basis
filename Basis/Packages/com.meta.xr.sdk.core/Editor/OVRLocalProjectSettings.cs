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

using UnityEngine;
using System.IO;

namespace Oculus.VR.Editor
{
    [System.Serializable]
    public class OVRLocalProjectSettings
    {
        // Private constructor for singleton
        private OVRLocalProjectSettings() { }

        [System.NonSerialized] private const string RELATIVE_JSON_PATH = "../Library/Meta/OVRLocalProjectSettingsData.json";
        [System.NonSerialized] private static OVRLocalProjectSettings _instance = null;
        public static OVRLocalProjectSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    string jsonPath = Path.Combine(Application.dataPath, RELATIVE_JSON_PATH);
                    if (!File.Exists(jsonPath))
                    {
                        _instance = new OVRLocalProjectSettings();
                        _instance.SaveDataObject();

                        // also force save OVRProjectConfig to remove stale ovrplugin hashes
                        OVRProjectConfig.CommitProjectConfig(OVRProjectConfig.CachedProjectConfig);
                        UnityEditor.AssetDatabase.SaveAssetIfDirty(OVRProjectConfig.CachedProjectConfig);
                    }
                    else
                    {
                        string json = File.ReadAllText(jsonPath);
                        _instance = JsonUtility.FromJson<OVRLocalProjectSettings>(json);
                    }
                }
                return _instance;
            }
        }

        private void SaveDataObject()
        {
            string json = JsonUtility.ToJson(this);
            string jsonPath = Path.Combine(Application.dataPath, RELATIVE_JSON_PATH);
            FileInfo file = new(jsonPath);
            file.Directory.Create();
            File.WriteAllText(jsonPath, json);
        }

        // Store the checksum of native plugins to compare and prompt for editor restarts when changed
        [SerializeField] private string _ovrPluginMd5Win64 = string.Empty;
        public string OVRPluginMd5Win64
        {
            get => _ovrPluginMd5Win64;
            set
            {
                if (_ovrPluginMd5Win64 == value)
                {
                    return;
                }
                _ovrPluginMd5Win64 = value;
                SaveDataObject();
            }
        }

        [SerializeField] private string _ovrPluginMd5Android = string.Empty;
        public string OVRPluginMd5Android
        {
            get => _ovrPluginMd5Android;
            set
            {
                if (_ovrPluginMd5Android == value)
                {
                    return;
                }
                _ovrPluginMd5Android = value;
                SaveDataObject();
            }
        }
    }
}
