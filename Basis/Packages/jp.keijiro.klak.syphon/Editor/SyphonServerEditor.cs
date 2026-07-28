using UnityEngine;
using UnityEditor;

namespace Klak.Syphon {

[CanEditMultipleObjects]
[CustomEditor(typeof(SyphonServer))]
public class SyphonServerEditor : Editor
{
    #region Private members

    #pragma warning disable CS0649

    AutoProperty _serverName;
    AutoProperty _captureMethod;
    AutoProperty _sourceCamera;
    AutoProperty _sourceTexture;
    AutoProperty KeepAlpha;

    #pragma warning restore

    #endregion

    #region Editor implementation

    void OnEnable() => AutoProperty.Scan(this);

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Server Name
        EditorGUILayout.DelayedTextField(_serverName);

        // Capture Method
        EditorGUILayout.PropertyField(_captureMethod);

        EditorGUI.indentLevel++;

        // Source Camera
        if (_captureMethod.Target.hasMultipleDifferentValues ||
            _captureMethod.Target.enumValueIndex == (int)CaptureMethod.Camera)
        {
            EditorGUILayout.PropertyField(_sourceCamera);
            #if !KLAK_SYPHON_HAS_SRP
            EditorGUILayout.HelpBox
              ("Camera capture method is only available with SRP.", MessageType.Error);
            #endif
        }

        // Source Texture
        if (_captureMethod.Target.hasMultipleDifferentValues ||
            _captureMethod.Target.enumValueIndex == (int)CaptureMethod.Texture)
            EditorGUILayout.PropertyField(_sourceTexture);

        EditorGUI.indentLevel--;

        // Keep Alpha
        EditorGUILayout.PropertyField(KeepAlpha);

        serializedObject.ApplyModifiedProperties();
    }

    #endregion
}

} // namespace Klak.Syphon
