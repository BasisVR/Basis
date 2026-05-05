using UnityEditor;

namespace Basis.Editor.Localization
{
    public static class BasisEditorLocalizationMenu
    {
        [MenuItem("Basis/Localization/Editor Language/Reload")]
        private static void Reload()
        {
            BasisEditorLocalization.ReloadTables();
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            SettingsProvider provider = new SettingsProvider("Preferences/Basis Editor Language", SettingsScope.User)
            {
                label = "Basis Editor Language",
                guiHandler = _ =>
                {
                    BasisEditorLocalization.Initialize();
                    var available = BasisEditorLocalization.Available;
                    string[] codes = new string[available.Count];
                    string[] names = new string[available.Count];
                    int selected = 0;
                    for (int i = 0; i < available.Count; i++)
                    {
                        codes[i] = available[i].Code;
                        names[i] = $"{available[i].NativeName} ({available[i].Code})";
                        if (string.Equals(available[i].Code, BasisEditorLocalization.CurrentLanguage, System.StringComparison.OrdinalIgnoreCase))
                        {
                            selected = i;
                        }
                    }

                    int next = EditorGUILayout.Popup("Editor Language", selected, names);
                    if (next != selected)
                    {
                        BasisEditorLocalization.SetLanguage(codes[next]);
                    }

                    if (UnityEngine.GUILayout.Button("Reload Language Tables"))
                    {
                        BasisEditorLocalization.ReloadTables();
                    }
                },
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "basis", "language", "localization", "editor" }),
            };
            return provider;
        }
    }
}
