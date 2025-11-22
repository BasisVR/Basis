using UnityEditor;

namespace Basis.BasisUI.Styling
{

    public class UiStyleSettingsProvider
    {
        private static SerializedObject _serializedObject;
        private static SerializedProperty _propActiveStylePalette;
        private static SerializedProperty _propActiveStyleLibrary;

        [SettingsProvider]
        public static UnityEditor.SettingsProvider Provider()
        {
            UnityEditor.SettingsProvider provider = new UnityEditor.SettingsProvider(
                "Project/WorldUI Styles",
                SettingsScope.Project)
            {
                label = "WorldUI Settings",
                guiHandler = MenuHandler,
            };

            _serializedObject = new SerializedObject(UiStyleSettings.instance);
            _propActiveStylePalette = _serializedObject.FindProperty(nameof(UiStyleSettings.ActivePalette));
            _propActiveStyleLibrary = _serializedObject.FindProperty(nameof(UiStyleSettings.ActiveStyles));

            return provider;
        }

        private static void MenuHandler(string obj)
        {
            using EditorGUI.ChangeCheckScope changed = new EditorGUI.ChangeCheckScope();

            EditorGUILayout.LabelField("UI Styles");
            EditorGUILayout.PropertyField(_propActiveStylePalette);
            EditorGUILayout.PropertyField(_propActiveStyleLibrary);

            if (changed.changed)
                _serializedObject.ApplyModifiedProperties();
        }
    }
}