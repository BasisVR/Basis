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
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Assets.Oculus.VR.Editor;
using UnityEditor;

namespace Oculus.VR.Editor.Utils
{
    internal class RemoteContentDownloader
    {
        public static HttpClient Client = new HttpClient();
        private readonly double _cacheDurationInHours;
        private readonly string _cacheFilePath;
        private readonly string _downloadPath;
        private readonly string _lastDownloadTimestampKey;
        private static OVRPlatformTool.EditorCoroutine _editorCoroutine;

        private static string CacheDirectory => Path.Combine(Path.GetTempPath(), "Meta", "Unity", "Editor");
        private static readonly Version VersionZero = new(0, 0, 0);

        private static int? SdkVersion
        {
            get
            {
                if (OVRPlugin.wrapperVersion == null || OVRPlugin.wrapperVersion == VersionZero)
                {
                    return null;
                }

                return OVRPlugin.wrapperVersion.Minor - 32;
            }
        }

        public RemoteContentDownloader(double cacheDurationInHours, string cacheFile, string downloadPath)
        {
            _cacheDurationInHours = cacheDurationInHours;
            _cacheFilePath = Path.Combine(CacheDirectory, cacheFile);
            _downloadPath = downloadPath;
            _lastDownloadTimestampKey =
                $"RemoteContentDownloader.LastDownloadTimestamp.{cacheFile}.{SdkVersion.GetValueOrDefault(0)}";
        }

        public async Task<(bool, string)> RefreshAndLoad()
        {
            if (HasValidCache())
            {
                return (true, ReadCache());
            }

            var result = await DownloadContent();
            if (result.Item1)
            {
                await StoreData(result.Item2);
            }

            return result;
        }

        #region Data Caching
        private bool HasValidCache()
        {
            var lastDownloadTimestamp = SessionState.GetString(_lastDownloadTimestampKey, null);
            if (!File.Exists(_cacheFilePath) || string.IsNullOrEmpty(lastDownloadTimestamp))
            {
                return false;
            }

            var lastDownloadTime = DateTime.Parse(lastDownloadTimestamp, CultureInfo.InvariantCulture);
            return DateTime.Now - lastDownloadTime <= TimeSpan.FromHours(_cacheDurationInHours);
        }

        public string ReadCache()
        {
            return File.ReadAllText(_cacheFilePath);
        }

        private async Task StoreData(string jsonData)
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllTextAsync(_cacheFilePath, jsonData);
            SessionState.SetString(_lastDownloadTimestampKey, DateTime.Now.ToString(CultureInfo.InvariantCulture));
        }

        public void ClearCache()
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }

            SessionState.EraseString(_lastDownloadTimestampKey);
        }

        #endregion

        #region Data Download

        private async Task<(bool, string)> DownloadContent()
        {
            using var marker = new OVRTelemetryMarker(OVRTelemetryConstants.Utils.MarkerId.DownloadContent);

            var path = _downloadPath;

            if (SdkVersion.HasValue)
            {
                path += $"?sdk_version={SdkVersion.Value}";
            }

            try
            {
                var response = await Client.GetAsync(path);
                marker.AddAnnotation(OVRTelemetryConstants.Utils.AnnotationType.StatusCode, response.StatusCode.ToString());
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType.MediaType == "application/json")
                {
                    var result = await response.Content.ReadAsStringAsync();
                    marker.SetResult(OVRPlugin.Qpl.ResultType.Success);
                    return (true, result);
                }
                else
                {
                    marker.AddAnnotation(OVRTelemetryConstants.Utils.AnnotationType.ContentType, response.Content.Headers.ContentType.MediaType);
                    marker.SetResult(OVRPlugin.Qpl.ResultType.Fail);
                    throw new Exception("Unexpected media type");
                }

            }
            catch (Exception ex)
            {
                // exception is thrown even when the result doesn't have expected format
                marker.AddAnnotation(OVRTelemetryConstants.Utils.AnnotationType.ErrorMessage, ex.Message);
                marker.SetResult(OVRPlugin.Qpl.ResultType.Fail);
                return (false, "");
            }
        }
        #endregion
    }
}
