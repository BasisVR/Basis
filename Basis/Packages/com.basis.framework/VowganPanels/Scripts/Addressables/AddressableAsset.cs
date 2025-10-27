using UnityEngine;
using UnityEngine.AddressableAssets;


namespace Basis.VowganUI
{
    public static class AddressableAssets
    {
        public static class Sprites
        {
            public static string Settings = "Packages/com.basis.sdk/Textures/Runtime/menuWhite.png";
            public static string Servers = "Packages/com.basis.sdk/Textures/Runtime/server-outline.png";
            public static string Avatars = "Packages/com.basis.sdk/Textures/Runtime/avatarWhite.png";
            public static string Respawn = "Packages/com.basis.sdk/Textures/Runtime/Teleport.png";
            public static string Camera = "Packages/com.basis.sdk/Textures/Runtime/camera-outline.png";
            public static string Exit = "Packages/com.basis.sdk/Textures/Runtime/exit-outline.png";
        }

        public static Sprite GetSprite(string path)
        {
            Sprite sprite = Addressables.LoadAssetAsync<Sprite>(path).WaitForCompletion();
            return sprite;
        }

        public static void Release(UnityEngine.Object obj)
        {
            Addressables.Release(obj);
        }

    }
}
