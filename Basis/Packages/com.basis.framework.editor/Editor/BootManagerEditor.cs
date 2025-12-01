namespace Basis.Scripts.Boot_Sequence
{
#if UNITY_EDITOR
    using UnityEditor;
    using UnityEngine;

    [DefaultExecutionOrder(-51)]
    public class BootManagerEditor : EditorWindow
    {
        private static bool isBootSequenceEnabled;

        [MenuItem("Basis/Boot Sequence/Toggle Basis Booting")]
        public static void ShowWindow() => GetWindow<BootManagerEditor>("Boot Sequence Toggle");

        private void OnEnable()
        {
            isBootSequenceEnabled = EditorPrefs.GetBool(BootManager.BootSequenceKey, true);
        }

        private void OnGUI()
        {
            GUILayout.Label("Boot Sequence Control", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Enable or disable the Boot Sequence at runtime.", MessageType.Info);

            bool newVal = EditorGUILayout.Toggle("Enable Booting", isBootSequenceEnabled);
            if (newVal != isBootSequenceEnabled)
            {
                isBootSequenceEnabled = newVal;
                EditorPrefs.SetBool(BootManager.BootSequenceKey, isBootSequenceEnabled);
                // keep PlayerPrefs in sync for play-in-editor behavior parity (optional)
                PlayerPrefs.SetInt(BootManager.BootSequenceKey, isBootSequenceEnabled ? 1 : 0);
            }
        }
    }
#endif

    [DefaultExecutionOrder(-51)]
    public static class BootManager
    {
        public const string BootSequenceKey = "BootSequenceEnabled";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoadRuntimeMethod()
        {
#if UNITY_EDITOR
            bool enabled = UnityEditor.EditorPrefs.GetBool(BootSequenceKey, true);
            // also allow overriding via PlayerPrefs (e.g., playmode tweaks)
            enabled = PlayerPrefs.GetInt(BootSequenceKey, enabled ? 1 : 0) != 0;
#else
            bool enabled = PlayerPrefs.GetInt(BootSequenceKey, 1) != 0; // default on in builds
#endif
            BootSequence.WillBoot = enabled;
        }
    }
}
