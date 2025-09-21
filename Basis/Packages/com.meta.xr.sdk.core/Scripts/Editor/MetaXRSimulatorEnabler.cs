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

#if OVR_UNITY_ASSET_STORE

#if USING_XR_MANAGEMENT && (USING_XR_SDK_OCULUS || USING_XR_SDK_OPENXR)
#define USING_XR_SDK
#endif

#if UNITY_2020_1_OR_NEWER
#define REQUIRES_XR_SDK
#endif

using System.Diagnostics;

using UnityEngine;
using UnityEditor;

// TODO: rename to MetaXRSimulatorDownloader after UPM migration
public class MetaXRSimulatorEnabler : MonoBehaviour
{
#if !USING_META_XR_SIMULATOR
    const string kDownloadSimulator = "Meta/Download Meta XR Simulator";

    [MenuItem(kDownloadSimulator, false, 10000)]
    private static void DownloadSimulator()
    {
        if (EditorUtility.DisplayDialog("Meta XR Simulator", "Download Meta XR Simulator from Oculus server as an UPM tarball, which can be installed through Package Manager.", "Download", "Cancel"))
        {
            string downloadUrl = "https://developer.oculus.com/downloads/package/meta-xr-simulator/";
            UnityEngine.Debug.LogFormat("Open Meta XR Simulator URL: {0}", downloadUrl);
            Application.OpenURL(downloadUrl);
        }
    }
#endif
}
#endif // #if OVR_UNITY_ASSET_STORE
