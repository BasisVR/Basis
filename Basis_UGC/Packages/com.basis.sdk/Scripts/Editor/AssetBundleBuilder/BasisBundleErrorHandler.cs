using System;
using UnityEditor;
using UnityEngine;

public static class BasisBundleErrorHandler
{
    public static void HandleBuildError(Exception ex, GameObject prefab, bool wasModified, string tempStoragePath)
    {
        Debug.LogError($"Error while building AssetBundle from prefab: {ex.Message}\n{ex.StackTrace}");

        if (wasModified)
        {
            TemporaryStorageHandler.ClearTemporaryStorage(tempStoragePath);
            AssetDatabase.Refresh();
            Debug.LogError("Temporary modifications and storage have been reset.");
        }
    }
}
