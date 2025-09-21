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

using System.Linq;
using UnityEditor;
using Meta.XR.Editor.Callbacks;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    internal class AssetPostProcessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload)
        {
            InitializeOnLoad.Register(LoadInspectedDataAssets);
        }

        private static void LoadInspectedDataAssets()
        {
            if (!RuntimeSettings.Instance.ImmersiveDebuggerEnabled)
            {
                return;
            }

            // Load all the assets from the project
            var guids = AssetDatabase.FindAssets("t:InspectedData");
            var collectedAssets = guids.ToList().ConvertAll(guid =>
                AssetDatabase.LoadAssetAtPath<InspectedData>(AssetDatabase.GUIDToAssetPath(guid)));

            var inspectedDataAssets = RuntimeSettings.Instance.InspectedDataAssets;
            var inspectedDataEnabled = RuntimeSettings.Instance.InspectedDataEnabled;
            // Remove non-existed assets
            for (var i = inspectedDataAssets.Count - 1; i >= 0; i--)
            {
                if (collectedAssets.Contains(inspectedDataAssets[i])) continue;
                inspectedDataAssets.RemoveAt(i);
                inspectedDataEnabled.RemoveAt(i);
            }
            // Add newly discovered assets
            foreach (var guid in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<InspectedData>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null) continue;
                if (inspectedDataAssets.Contains(asset)) continue;
                inspectedDataAssets.Add(asset);
                inspectedDataEnabled.Add(true);
            }
        }
    }
}
