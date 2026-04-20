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
            public static string Library = "Packages/com.basis.sdk/Textures/Runtime/library.png";
            public static string Search = "Packages/com.basis.sdk/Textures/Runtime/search.png";
            public static string Add = "Packages/com.basis.sdk/Textures/Runtime/add.png";
            public static string List = "Packages/com.basis.sdk/Textures/Runtime/list.png";
            public static string Network = "Packages/com.basis.sdk/Textures/Runtime/network.png";
            public static string World = "Packages/com.basis.sdk/Textures/Runtime/worlds.png";
            public static string Locked = "Packages/com.basis.sdk/Textures/Runtime/padlock-locked.png";
            public static string Unlocked = "Packages/com.basis.sdk/Textures/Runtime/padlock-unlocked.png";
            public static string FileTray = "Packages/com.basis.sdk/Textures/Runtime/file-tray.png";
            public static string HourGlass = "Packages/com.basis.sdk/Textures/Runtime/hour-glass.png";
            public static string Clock = "Packages/com.basis.sdk/Textures/Runtime/clock.png";
            public static string Pin = "Packages/com.basis.sdk/Textures/Runtime/pin.png";
            public static string Computer = "Packages/com.basis.sdk/Textures/Runtime/computer.png";
            public static string Information = "Packages/com.basis.sdk/Textures/Runtime/information.png";
            public static string Admin = "Packages/com.basis.sdk/Textures/Runtime/admin.png";

            public static string Microphone = "Packages/com.basis.sdk/Textures/Runtime/microphone-solid.png";
            public static string MicrophoneMute = "Packages/com.basis.sdk/Textures/Runtime/microphone-mute-solid.png";

            // embedded items
            public static string Embedded = "Packages/com.basis.sdk/Textures/Runtime/embedded.png";

            // metadata performance metrics
            public static string Polygons = "Packages/com.basis.sdk/Textures/Runtime/polygons.png";
            public static string Materials = "Packages/com.basis.sdk/Textures/Runtime/materials.png";
            public static string Bones = "Packages/com.basis.sdk/Textures/Runtime/bones.png";

            // platform sprites
            public static string PlatformMobileAndroid = "Packages/com.basis.sdk/Textures/Runtime/Platform Icons/logo-android.png";
            public static string PlatformMobileiOS = "Packages/com.basis.sdk/Textures/Runtime/Platform Icons/logo-ios.png";
            public static string PlatformStandaloneOSX = "Packages/com.basis.sdk/Textures/Runtime/Platform Icons/logo-mac.png";
            public static string PlatformStandaloneLinux64 = "Packages/com.basis.sdk/Textures/Runtime/Platform Icons/logo-tux.png";
            public static string PlatformStandaloneWindows64 = "Packages/com.basis.sdk/Textures/Runtime/Platform Icons/logo-windows.png";
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
