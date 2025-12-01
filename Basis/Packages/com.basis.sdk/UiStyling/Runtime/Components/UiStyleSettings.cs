using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
namespace Basis.BasisUI.Styling
{
    public static class UiStyleSettings
    {
        public static UiStyleLibrary Library;
        public static UiStylePalette Palette;
        public static UiStyleLibrary GetActiveStyles()
        {
            if (Library == null)
            {
                var Data = Addressables.LoadAssetAsync<UiStyleLibrary>("StyleLibrary");
                Library = Data.WaitForCompletion();
            }
#if UNITY_EDITOR
            if (Library == null)
            {

                Library = AssetDatabase.LoadAssetAtPath<UiStyleLibrary>("Packages/com.basis.sdk/Settings/StyleLibrary.asset");
            }
#endif
            if (Library == null)
            {
                BasisDebug.LogError("Misssing Library!");
            }
            return Library;

        }

        public static UiStylePalette GetActivePalette()
        {
            if (Palette == null)
            {
                var Data = Addressables.LoadAssetAsync<UiStylePalette>("StylePalette");
                Palette = Data.WaitForCompletion();
            }
#if UNITY_EDITOR
            if (Palette == null)
            {

                Palette = AssetDatabase.LoadAssetAtPath<UiStylePalette>("Packages/com.basis.sdk/Settings/StylePalette.asset");
            }
#endif
            if (Palette == null)
            {
                BasisDebug.LogError("Misssing Palette!");
            }
            return Palette;
        }

        public static void SetActiveStyles(UiStyleLibrary library)
        {
            Library = library;
            UpdateAllStyleComponents();

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(Library);
#endif
        }

        public static void SetActivePalette(UiStylePalette palette)
        {
            Palette = palette;
            UpdateAllStyleComponents();

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(Palette);
#endif
        }

        public static void UpdateAllStyleComponents()
        {
            // This works at runtime too (2022+). If you're on older Unity, switch to Object.FindObjectsOfType<BaseUiStyleComponent>()
            BaseUiStyleComponent[] components =
                Object.FindObjectsByType<BaseUiStyleComponent>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);

            foreach (BaseUiStyleComponent comp in components)
            {
                if (!comp || !comp.enabled) continue;

                UiStyleUtilities.RecordComponent(comp); // assuming this is runtime-safe
                comp.ApplyActiveStyle();
            }
        }
    }
}
