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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
public class MetraXRBootConfig : UnityEditor.Build.IPreprocessBuildWithReport
{
    public int callbackOrder => 1;
    private static readonly Dictionary<string, string> AndroidBootConfigVars = new Dictionary<string, string>()
        {
            { "xr-meta-enabled", "1" },
            { "xr-vulkan-extension-fragment-density-map-enabled", "1" },
        };
    /// <summary>
    /// Small utility class for reading, updating and writing boot config.
    /// </summary>
    internal class BootConfig
    {
        private const string XrBootSettingsKey = "xr-boot-settings";

        private readonly Dictionary<string, string> bootConfigSettings;
        private readonly string buildTargetName;



        public BootConfig(UnityEditor.Build.Reporting.BuildReport report)
        {
            bootConfigSettings = new Dictionary<string, string>();
            buildTargetName = UnityEditor.BuildPipeline.GetBuildTargetName(report.summary.platform);
        }

        public void ReadBootConfig()
        {
            bootConfigSettings.Clear();

            string xrBootSettings = UnityEditor.EditorUserBuildSettings.GetPlatformSettings(buildTargetName, XrBootSettingsKey);
            if (!string.IsNullOrEmpty(xrBootSettings))
            {
                // boot settings string format
                // <boot setting>:<value>[;<boot setting>:<value>]*
                var bootSettings = xrBootSettings.Split(';');
                foreach (var bootSetting in bootSettings)
                {
                    var setting = bootSetting.Split(':');
                    if (setting.Length == 2 && !string.IsNullOrEmpty(setting[0]) && !string.IsNullOrEmpty(setting[1]))
                    {
                        bootConfigSettings.Add(setting[0], setting[1]);
                    }
                }
            }
        }

        public void SetValueForKey(string key, string value) => bootConfigSettings[key] = value;

        public bool TryGetValue(string key, out string value) => bootConfigSettings.TryGetValue(key, out value);

        public void ClearEntryForKeyAndValue(string key, string value)
        {
            if (bootConfigSettings.TryGetValue(key, out string dictValue) && dictValue == value)
            {
                bootConfigSettings.Remove(key);
            }
        }

        public void WriteBootConfig()
        {
            // boot settings string format
            // <boot setting>:<value>[;<boot setting>:<value>]*
            bool firstEntry = true;
            var sb = new System.Text.StringBuilder();
            foreach (var kvp in bootConfigSettings)
            {
                if (!firstEntry)
                {
                    sb.Append(";");
                }

                sb.Append($"{kvp.Key}:{kvp.Value}");
                firstEntry = false;
            }

            UnityEditor.EditorUserBuildSettings.SetPlatformSettings(buildTargetName, XrBootSettingsKey, sb.ToString());
        }
    }

    public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
    {
        // write Android Meta tags to bootconfig
        var bootConfig = new BootConfig(report);
        bootConfig.ReadBootConfig();

        foreach (var entry in AndroidBootConfigVars)
        {
            bootConfig.SetValueForKey(entry.Key, entry.Value);
        }

        bootConfig.WriteBootConfig();
    }
}
#endif
