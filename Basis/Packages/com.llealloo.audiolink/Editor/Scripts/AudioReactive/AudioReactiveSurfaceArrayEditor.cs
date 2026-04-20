#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioReactiveSurfaceArray))]
    public class AudioReactiveSurfaceArrayEditor : AudioReactiveCommon
    {
        public override void OnInspectorGUI()
        {
            StandardReactiveEditorHeader("AudioLink Reactive Surface Array:\nConfigure an array of AudioLinkSurfaces' Shader.\n\nNote: Children should have AudioReactiveSurface shader applied!");

            StandardReactiveEditor();

            if (EditorApplication.isPlaying)
                ((AudioReactiveSurfaceArray)target).UpdateChildren();

            EditorGUILayout.Space();
        }
    }
}
#endif