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
using System.Threading.Tasks;
using Oculus.VR.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Oculus.VR.Editor.OVRProjectSetup
{
    [InitializeOnLoad]
    internal static class UPSTContentManager
    {
        private const double CacheDurationInHours = 6;
        private const string DownloadUrl = "https://www.facebook.com/upst_configuration";

        private static readonly RemoteContentDownloader Downloader;
        private static HashSet<string> _disabledRuleIds = new HashSet<string>();

        static UPSTContentManager()
        {
            Downloader = new RemoteContentDownloader(CacheDurationInHours, "upst_configuration.json", DownloadUrl);
#pragma warning disable CS4014
            InitializeAsync();
#pragma warning restore CS4014
        }

        public static async Task InitializeAsync()
        {
            var successfulLoad = await Reload(false);
            if (!successfulLoad)
            {
                await Reload(true);
            }
        }

        public static bool IsTaskDisabled(OVRConfigurationTask task)
        {
            return _disabledRuleIds.Contains(task.Uid.ToString());
        }

        public static async Task<bool> Reload(bool forceRedownload)
        {
            if (forceRedownload)
            {
                Downloader.ClearCache();
            }
            var result = await Downloader.RefreshAndLoad();
            if (result.Item1)
            {
                return LoadContentJsonData(result.Item2);
            }
            else
            {
                return false;
            }
        }

        [Serializable]
        internal struct UpstConfiguration
        {
            public DisabledRule[] disabled_rules;
        }

        [Serializable]
        internal struct DisabledRule
        {
            public string uid;
        }

        public static bool LoadContentJsonData(string jsonData)
        {
            var response = ParseJsonData(jsonData);
            if (response.disabled_rules != null)
            {
                _disabledRuleIds.Clear();
                foreach (var rule in response.disabled_rules)
                {
                    _disabledRuleIds.Add(rule.uid);
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        public static UpstConfiguration ParseJsonData(string jsonData)
        {
            UpstConfiguration response;
            try
            {
                response = JsonUtility.FromJson<UpstConfiguration>(jsonData);
            }
            catch (Exception)
            {
                response = default;
            }

            return response;
        }
    }
}
