#if !COMPILER_UDONSHARP && UNITY_EDITOR && UDONSHARP
using UdonSharpEditor;
using UnityEditor;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioLinkMiniPlayerController))]
    internal class AudioLinkMiniPlayerControllerInspector : UnityEditor.Editor
    {
        static bool _showObjectFoldout;

        SerializedProperty videoPlayerProperty;

        SerializedProperty urlInputProperty;
        SerializedProperty urlInputControlProperty;
        SerializedProperty progressSliderControlProperty;

        SerializedProperty stopIconProperty;
        SerializedProperty lockedIconProperty;
        SerializedProperty unlockedIconProperty;
        SerializedProperty loadIconProperty;
        SerializedProperty syncIconProperty;

        SerializedProperty progressSliderProperty;
        SerializedProperty statusTextProperty;
        SerializedProperty urlTextProperty;
        SerializedProperty placeholderTextProperty;

        private void OnEnable()
        {
            videoPlayerProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.videoPlayer));
            urlInputProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.urlInput));

            progressSliderControlProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.progressSliderControl));
            urlInputControlProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.urlInputControl));

            stopIconProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.stopIcon));
            lockedIconProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.lockedIcon));
            unlockedIconProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.unlockedIcon));
            loadIconProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.loadIcon));
            syncIconProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.syncIcon));

            statusTextProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.statusText));
            placeholderTextProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.placeholderText));
            urlTextProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.urlText));
            progressSliderProperty = serializedObject.FindProperty(nameof(AudioLinkMiniPlayerController.progressSlider));
        }

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target))
                return;

            EditorGUILayout.PropertyField(videoPlayerProperty);
            EditorGUILayout.Space();

            _showObjectFoldout = EditorGUILayout.Foldout(_showObjectFoldout, "Internal Object References");
            if (_showObjectFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(urlInputProperty);
                EditorGUILayout.PropertyField(urlInputControlProperty);
                EditorGUILayout.PropertyField(progressSliderControlProperty);
                EditorGUILayout.PropertyField(stopIconProperty);
                EditorGUILayout.PropertyField(lockedIconProperty);
                EditorGUILayout.PropertyField(unlockedIconProperty);
                EditorGUILayout.PropertyField(loadIconProperty);
                EditorGUILayout.PropertyField(syncIconProperty);
                EditorGUILayout.PropertyField(progressSliderProperty);
                EditorGUILayout.PropertyField(statusTextProperty);
                EditorGUILayout.PropertyField(urlTextProperty);
                EditorGUILayout.PropertyField(placeholderTextProperty);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.Space();

            if (serializedObject.hasModifiedProperties)
                serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
