using UnityEngine;
using UnityEditor;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;
using Basis.Scripts.Virtual_keyboard;
using static Basis.Scripts.Virtual_keyboard.Editor.KeyboardLayoutDataEditor;

[CustomEditor(typeof(KeyboardLayoutData))]
public partial class KeyboardLayoutDataEditor : Editor
{
    private SerializedProperty languagesAndStylesProp;

    private void OnEnable()
    {
        languagesAndStylesProp = serializedObject.FindProperty("languagesAndStyles");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(languagesAndStylesProp, true);
        if (GUILayout.Button("Set Colors To match Grey Black Theme"))
        {
            KeyboardLayoutData keyboardLayoutData = (KeyboardLayoutData)target;
            for (int LanguageIndex = 0; LanguageIndex < keyboardLayoutData.languagesAndStyles.Count; LanguageIndex++)
            {
                LanguageStyle LanguageStyle = keyboardLayoutData.languagesAndStyles[LanguageIndex];
                for (int Index = 0; Index < LanguageStyle.SpecialKeys.Count; Index++)
                {
                    SpecialKeySizes Key = LanguageStyle.SpecialKeys[Index];
                    UnityEngine.UI.ColorBlock ColorBlock = new UnityEngine.UI.ColorBlock();
                        ColorBlock.normalColor = new Color(0.2735849f, 0.2735849f, 0.2735849f);
                        ColorBlock.highlightedColor = new Color(0.3018868f, 0.3018868f, 0.3018868f);
                        ColorBlock.pressedColor = new Color(0.4811321f, 0.4811321f, 0.4811321f);
                        ColorBlock.selectedColor = new Color(0.5849056f, 0.5849056f, 0.5849056f);
                        ColorBlock.disabledColor = new Color(0.7843137f, 0.7843137f, 0.5019608f);
                        ColorBlock.colorMultiplier = 1;
                        ColorBlock.fadeDuration = 0.1f;
                        Key.ColorBlock = ColorBlock;
                        LanguageStyle.SpecialKeys[Index] = Key;
                        keyboardLayoutData.languagesAndStyles[LanguageIndex] = LanguageStyle;
                }
            }
            EditorUtility.SetDirty(keyboardLayoutData);
            AssetDatabase.Refresh();
        }
        if (GUILayout.Button("Reset Languages And Styles"))
        {
            if (EditorUtility.DisplayDialog("Reset Confirmation", "Are you sure you want to reset the languages and styles list?", "Yes", "No"))
            {
                ResetLanguagesAndStyles();
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void ResetLanguagesAndStyles()
    {
        KeyboardLayoutData keyboardLayoutData = (KeyboardLayoutData)target;
        keyboardLayoutData.languagesAndStyles.Clear();
        // Optionally, you can add default items after clearing:
        keyboardLayoutData.languagesAndStyles.AddRange(BasisVirtualKeyboardDefaultLanguagesAndStyles.DefaultLanguagesAndStyles());

    }
}