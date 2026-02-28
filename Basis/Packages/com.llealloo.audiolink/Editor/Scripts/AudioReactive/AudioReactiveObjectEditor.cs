#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioReactiveObject))]
    public class AudioReactiveObjectEditor : AudioReactiveCommon
    {
        public override void OnInspectorGUI()
        {
            StandardReactiveEditorHeader("AudioLink Reactive Object:\nMove, Rotate, or Scale a GameObject and it's children relative to it's inital Transform.");

            StandardReactiveEditor();

            EditorGUILayout.Space();
        }
    }
}
#endif