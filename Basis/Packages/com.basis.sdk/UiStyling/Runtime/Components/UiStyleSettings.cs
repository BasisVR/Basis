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
            #if UNITY_EDITOR
                // In the editor, use AssetDatabase directly instead of Addressable.
                if (Library == null)
                {
                    Library = AssetDatabase.LoadAssetAtPath<UiStyleLibrary>(
                        "Packages/com.basis.sdk/Settings/StyleLibrary.asset");
                }
                // In editor, never fall through to Addressables
                if (Library == null)
                {
                    BasisDebug.LogError("Missing Library! Asset not found at expected path.");
                }
                return Library;
            #else
                // At runtime, Addressables is the correct loading mechanism.
                if (Library == null)
                {
                    var Data = Addressables.LoadAssetAsync<UiStyleLibrary>("StyleLibrary");
                    Library = Data.WaitForCompletion();
                }
                if (Library == null)
                {
                    BasisDebug.LogError("Missing Library!");
                }
                return Library;
            #endif
        }

        public static UiStylePalette GetActivePalette()
        {
            #if UNITY_EDITOR
                if (Palette == null)
                {
                    Palette = AssetDatabase.LoadAssetAtPath<UiStylePalette>(
                        "Packages/com.basis.sdk/Settings/StylePalette.asset");
                }
                if (Palette == null)
                {
                    BasisDebug.LogError("Missing Palette! Asset not found at expected path.");
                }
                return Palette;
            #else
                if (Palette == null)
                {
                    var Data = Addressables.LoadAssetAsync<UiStylePalette>("StylePalette");
                    Palette = Data.WaitForCompletion();
                }
                if (Palette == null)
                {
                    BasisDebug.LogError("Missing Palette!");
                }
                return Palette;
            #endif
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
                Object.FindObjectsByType<BaseUiStyleComponent>(FindObjectsInactive.Include);

            foreach (BaseUiStyleComponent comp in components)
            {
                if (!comp || !comp.enabled) continue;

                UiStyleUtilities.RecordComponent(comp); // assuming this is runtime-safe
                comp.ApplyActiveStyle();
            }
        }
    }
}
