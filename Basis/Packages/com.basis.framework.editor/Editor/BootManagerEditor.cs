using Unity.Scripting.LifecycleManagement;
namespace Basis.Scripts.Boot_Sequence
{
#if UNITY_EDITOR
    using UnityEditor;
    using UnityEngine;
    using Basis.Editor.Localization;

    [DefaultExecutionOrder(-51)]
    [NoAutoStaticsCleanup]
    public class BootManagerEditor : EditorWindow
    {
        private static bool isBootSequenceEnabled;

        [MenuItem("Basis/Debug/Toggle Boot Sequence", false, 643)]
        public static void ShowWindow() => GetWindow<BootManagerEditor>(BasisEditorLocalization.Get("editor.bootManager.windowTitle"));

        private void OnEnable()
        {
            isBootSequenceEnabled = EditorPrefs.GetBool(BootManager.BootSequenceKey, true);
        }

        private void OnGUI()
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.bootManager.header"), EditorStyles.boldLabel);
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.bootManager.help"), MessageType.Info);

            bool newVal = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.bootManager.enableBooting"), isBootSequenceEnabled);
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
            BasisBootSequence.WillBoot = enabled;
        }
    }
}
