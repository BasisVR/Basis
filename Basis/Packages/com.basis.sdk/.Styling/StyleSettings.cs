using UnityEditor;
using UnityEngine;

namespace Basis.BasisUI.StylingOLD
{
    [CreateAssetMenu(fileName = "StyleSettings", menuName = "Basis/Style Palette Settings")]
    public class StyleSettings : ScriptableObject
    {
        public static StyleSettings Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = Resources.Load<StyleSettings>(StyleSettingsResourcesPath);
                }

                return _instance;
            }
        }

        public static string StyleSettingsResourcesPath = "StyleSettings";
        private static StyleSettings _instance;

        public StylePaletteObject ActivePalette;

        public static void SetActivePalette(StylePaletteObject palette)
        {
            SerializedObject so = new SerializedObject(Instance);
            so.FindProperty(nameof(ActivePalette)).objectReferenceValue = palette;
            so.ApplyModifiedProperties();

            string[] prefabGuids = AssetDatabase.FindAssets("t:prefab", new []{"Assets", "Packages"});
            bool changed = false;
            foreach (string guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                StyleComponent[] prefabComponents = asset.GetComponentsInChildren<StyleComponent>();
                if (prefabComponents.Length == 0) continue;
                changed = true;
                foreach (StyleComponent component in prefabComponents)
                {
                    component.ApplyColor();
                }
            }

            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            StyleComponent[] sceneComponents = FindObjectsByType<StyleComponent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (StyleComponent component in sceneComponents)
            {
                component.ApplyColor();
            }
        }
    }
}
