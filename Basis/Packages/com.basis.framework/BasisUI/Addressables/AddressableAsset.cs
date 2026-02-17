using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;


namespace Basis.BasisUI
{
    public static class AddressableAssets
    {
        public static class Sprites
        {
            public static string Settings = "Packages/com.basis.sdk/Sprites/Icons/IonIcon Settings.png";
            public static string Servers = "Packages/com.basis.sdk/Textures/Runtime/server-outline.png";
            public static string Avatars = "Packages/com.basis.sdk/Textures/Runtime/avatarWhite.png";
            public static string Calibrate = "Packages/com.basis.sdk/Textures/Runtime/calibrateWhite.png";
            public static string Respawn = "Packages/com.basis.sdk/Textures/Runtime/Teleport.png";
            public static string Camera = "Packages/com.basis.sdk/Textures/Runtime/camera-outline.png";
            public static string Mirror = "Packages/com.basis.sdk/Textures/Runtime/Mirror.png";
            public static string Exit = "Packages/com.basis.sdk/Textures/Runtime/exit-outline.png";
            public static string Items = "Packages/com.basis.sdk/Textures/Runtime/items.png";
        }

        public static Sprite GetSprite(string path)
        {
            if (AddressExists(path))
                return Addressables.LoadAssetAsync<Sprite>(path).WaitForCompletion();

            Debug.LogWarning($"Could not find addressable at path \"{path}\"");
            return null;
        }

        public static bool AddressExists(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            AsyncOperationHandle<IList<IResourceLocation>> handle =
                Addressables.LoadResourceLocationsAsync(key, typeof(UnityEngine.Object));

            IList<IResourceLocation> locations = handle.WaitForCompletion();
            bool exists = locations != null && locations.Count > 0;

            Addressables.Release(handle);
            return exists;
        }

        public static void Release(UnityEngine.Object obj)
        {
            Addressables.Release(obj);
        }

    }
}
