using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Boot_Sequence
{
    [DefaultExecutionOrder(-51)]
    public class BootManagerEditor : EditorWindow
    {
        private static bool isBootSequenceEnabled;

        [MenuItem("Basis/Boot Sequence/Toggle Basis Booting")]
        public static void ShowWindow()
        {
            GetWindow<BootManagerEditor>("Boot Sequence Toggle");
        }

        private void OnEnable()
        {
            // Load the value from EditorPrefs when the window is opened
            isBootSequenceEnabled = EditorPrefs.GetBool(BootManager.BootSequenceKey, true); // Default to true if not set
        }

        private void OnGUI()
        {
            GUILayout.Label("Boot Sequence Control", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Enable or disable the Boot Sequence at runtime.", MessageType.Info);

            bool newBootSequenceEnabled = EditorGUILayout.Toggle("Enable Booting", isBootSequenceEnabled);
            if (newBootSequenceEnabled != isBootSequenceEnabled)
            {
                isBootSequenceEnabled = newBootSequenceEnabled;
                EditorPrefs.SetBool(BootManager.BootSequenceKey, isBootSequenceEnabled); // Save the value persistently
            }
        }
    }
    [DefaultExecutionOrder(-51)]
    public static class BootManager
    {
        public const string BootSequenceKey = "BootSequenceEnabled"; // Key for storing the value in EditorPrefs

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoadRuntimeMethod()
        {
            // Read the saved value and assign it to BootSequence.WillBoot at runtime
            BootSequence.WillBoot = EditorPrefs.GetBool(BootSequenceKey, true);
        }
    }
}
