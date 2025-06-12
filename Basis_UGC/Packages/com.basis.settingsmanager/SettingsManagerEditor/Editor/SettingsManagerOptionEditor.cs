#if UNITY_EDITOR
using BattlePhaze.SettingsManager.DebugSystem;
using BattlePhaze.SettingsManager.Style;
using UnityEditor;
using UnityEngine;
using static BattlePhaze.SettingsManager.SettingsManagerOption;

namespace BattlePhaze.SettingsManager
{
    [CustomEditor(typeof(SettingsManagerOption), true)]
    public class SettingsManagerOptionEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            SettingsmanagerStyle.Style();
            SettingsManagerOption targetOption = (SettingsManagerOption)target;
            DisplaySelection(targetOption);

            if (GUI.changed)
            {
                EditorUtility.SetDirty(targetOption);
                Undo.RecordObject(targetOption, "Modify SettingsManagerOption");
            }
        }

        public static SettingsManager Instance;

        public static void DisplaySelection(SettingsManagerOption moduleOption)
        {
            EditorGUILayout.BeginVertical(SettingsmanagerStyle.BackGroundStyling);
            Instance = SettingsManager.Instance;
            if (Instance == null)
            {
                Instance = MonoBehaviour.FindFirstObjectByType<SettingsManager>();
            }

            if (Instance == null)
            {
                GUILayout.Label("Settings Manager could not be found, please assign one (make sure your settings manager is set up in a scene)");
                Instance = (SettingsManager)EditorGUILayout.ObjectField(Instance, typeof(SettingsManager), true);
            }

            if (Instance == null)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.BeginChangeCheck();
            if (GUILayout.Button("Add Option", SettingsmanagerStyle.ButtonStyling))
            {
                Undo.RecordObject(moduleOption, "Add Option");
                moduleOption.ManagerModuleOptions.Add(new SettingsManagerOptionValues());
                EditorUtility.SetDirty(moduleOption);
            }

            if (Instance.Options.Count != 0 && moduleOption != null && moduleOption.ManagerModuleOptions.Count != 0)
            {
                for (int optionValuesIndex = 0; optionValuesIndex < moduleOption.ManagerModuleOptions.Count; optionValuesIndex++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Option Index " + FriendlyInteger(optionValuesIndex + 1), SettingsmanagerStyle.DescriptorStyling);
                    int value = GetIndex(moduleOption.ManagerModuleOptions[optionValuesIndex].OptionName);
                    SettingsManagerOptionValues output = moduleOption.ManagerModuleOptions[optionValuesIndex];
                    int current = EditorGUILayout.Popup(value, GetArrayOfOptions());
                    SettingsMenuInput input = Instance.Options[current];

                    if (input != null)
                    {
                        Undo.RecordObject(moduleOption, "Modify Option");
                        output.Parse = input.ParseController;
                        output.Type = input.Type;
                        output.OptionName = input.Name;
                        output.Explanation = input.Explanation;

                        if (GUILayout.Button("Remove Option", SettingsmanagerStyle.ButtonStyling))
                        {
                            Undo.RecordObject(moduleOption, "Remove Option");
                            moduleOption.ManagerModuleOptions.RemoveAt(optionValuesIndex);
                            SettingsManagerDebug.Log("Removing Option " + optionValuesIndex);
                            EditorUtility.SetDirty(moduleOption);
                            continue;
                        }

                        moduleOption.ManagerModuleOptions[optionValuesIndex] = output;
                        EditorUtility.SetDirty(moduleOption);
                    }

                    GUILayout.EndHorizontal();
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(moduleOption);
            }

            EditorGUILayout.EndVertical();
        }

        public static void DisplaySelection(ref string optionName, ref string optionExplanation, ref SettingsManagerEnums.ItemParse itemParse, ref SettingsManagerEnums.IsType isType, string name)
        {
            if (Instance == null)
            {
                Instance = MonoBehaviour.FindFirstObjectByType<SettingsManager>();
            }

            if (Instance == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Option " + name);

            if (Instance.Options.Count != 0)
            {
                int value = GetIndex(optionName);
                SettingsMenuInput input = Instance.Options[EditorGUILayout.Popup(value, GetArrayOfOptions())];
                itemParse = input.ParseController;
                isType = input.Type;
                optionName = input.Name;
                optionExplanation = input.Explanation;
            }

            GUILayout.EndHorizontal();
        }

        public static int GetIndex(string optionName)
        {
            for (int optionsIndex = 0; optionsIndex < Instance.Options.Count; optionsIndex++)
            {
                if (optionName == Instance.Options[optionsIndex].Name)
                {
                    return optionsIndex;
                }
            }
            return 0;
        }

        public static string[] GetArrayOfOptions()
        {
            string[] optionsArray = new string[Instance.Options.Count];
            for (int optionsIndex = 0; optionsIndex < Instance.Options.Count; optionsIndex++)
            {
                optionsArray[optionsIndex] = Instance.Options[optionsIndex].Name;
            }
            return optionsArray;
        }

        static string[] ones = new string[] { string.Empty, "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
        static string[] teens = new string[] { "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        static string[] tens = new string[] { "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

        private static string FriendlyInteger(int n)
        {
            if (n == 0)
            {
                return "Zero";
            }

            if (n < 0)
            {
                return "Minus " + FriendlyInteger(-n);
            }

            string friendlyInt;
            if (n < 10)
            {
                friendlyInt = ones[n];
            }
            else if (n < 20)
            {
                friendlyInt = teens[n - 10];
            }
            else if (n < 100)
            {
                friendlyInt = tens[n / 10 - 2] + " " + FriendlyInteger(n % 10);
            }
            else if (n < 1000)
            {
                friendlyInt = ones[n / 100] + " Hundred " + FriendlyInteger(n % 100);
            }
            else if (n < 1000000)
            {
                friendlyInt = FriendlyInteger(n / 1000) + " Thousand " + FriendlyInteger(n % 1000);
            }
            else if (n < 1000000000)
            {
                friendlyInt = FriendlyInteger(n / 1000000) + " Million " + FriendlyInteger(n % 1000000);
            }
            else
            {
                friendlyInt = FriendlyInteger(n / 1000000000) + " Billion " + FriendlyInteger(n % 1000000000);
            }

            return friendlyInt.Trim();
        }
    }
}
#endif