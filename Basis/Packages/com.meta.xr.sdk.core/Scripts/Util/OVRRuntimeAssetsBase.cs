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
using System;

#if UNITY_EDITOR
using UnityEditor;
using System.Linq;
#endif // UNITY_EDITOR

/// <summary>
/// Base class for runtime assets with common functions.
/// </summary>
public class OVRRuntimeAssetsBase : ScriptableObject
{
#if UNITY_EDITOR
    internal static string GetAssetPath(string assetName)
    {
        string resourcesPath = Path.Combine(Application.dataPath, "Resources");
        if (!Directory.Exists(resourcesPath))
        {
            Directory.CreateDirectory(resourcesPath);
        }

        string assetPath = Path.GetFullPath(Path.Combine(resourcesPath, $"{assetName}.asset"));
        Uri configUri = new Uri(assetPath);
        Uri projectUri = new Uri(Application.dataPath);
        Uri relativeUri = projectUri.MakeRelativeUri(configUri);

        return relativeUri.ToString();
    }

    public bool AddToPreloadedAssets()
    {
        var preloadedAssets = PlayerSettings.GetPreloadedAssets().ToList();

        if (!preloadedAssets.Contains(this))
        {
            preloadedAssets.Add(this);
            PlayerSettings.SetPreloadedAssets(preloadedAssets.ToArray());
            return true;
        }

        return false;
    }

    public bool RemoveFromPreloadedAssets()
    {
        var preloaded = PlayerSettings.GetPreloadedAssets().ToList();
        if (preloaded.RemoveAll(a => a == this) > 0)
        {
            PlayerSettings.SetPreloadedAssets(preloaded.ToArray());
            return true;
        }

        return false;
    }
#endif


    internal static void LoadAsset<T>(out T assetInstance, string assetName, Action<T> onCreateAsset = null) where T : OVRRuntimeAssetsBase
    {
        assetInstance = null;
#if UNITY_EDITOR
        string instanceAssetPath = GetAssetPath(assetName);
        try
        {
            assetInstance =
                AssetDatabase.LoadAssetAtPath(instanceAssetPath, typeof(T)) as T;
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("Unable to load {0} from {1}, error {2}", assetName, instanceAssetPath,
                e.Message);
        }

        if (assetInstance == null && !BuildPipeline.isBuildingPlayer)
        {
            assetInstance = ScriptableObject.CreateInstance<T>();

            AssetDatabase.CreateAsset(assetInstance, GetAssetPath(assetName));
            onCreateAsset?.Invoke(assetInstance);
        }
#else
        assetInstance = Resources.Load<T>(assetName);
#endif // UNITY_EDITOR

    }
}
