#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioReactiveLight))]
    public class AudioReactiveLightEditor : AudioReactiveCommon
    {
        public override void OnInspectorGUI()
        {
            StandardReactiveEditorHeader("AudioLink Reactive Light:\nChanges the Intensity or Hue of a *Realtime* Light.\n\nNote: Realtime lights are *really* compute expensive,\nplease use a Shader alternative instead.");

            StandardReactiveEditor();

            EditorGUILayout.Space();
        }
    }
}
#endif