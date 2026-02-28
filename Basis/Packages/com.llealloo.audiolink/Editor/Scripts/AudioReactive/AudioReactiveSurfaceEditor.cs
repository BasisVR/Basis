#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioReactiveSurface))]
    public class AudioReactiveSurfaceEditor : AudioReactiveCommon
    {
        public override void OnInspectorGUI()
        {
            StandardReactiveEditorHeader("AudioLink Reactive Surface:\nConfigure a single instance of an AudioReactiveSurface Shader.\n\nNote: To use custom mesh, swap mesh in Mesh Filter component above.");

            StandardReactiveEditor();

            if (EditorApplication.isPlaying)
                ((AudioReactiveSurface)target).UpdateMaterial();

            EditorGUILayout.Space();
        }
    }
}
#endif