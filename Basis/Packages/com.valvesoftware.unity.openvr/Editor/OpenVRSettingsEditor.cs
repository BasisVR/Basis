using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Unity.XR.OpenVR;

namespace Unity.XR.OpenVR.Editor
{
    [CustomEditor(typeof(OpenVRSettings))]
    public class OpenVRSettingsEditor : UnityEditor.Editor
    {
        private const string kStereoRenderingMode = "StereoRenderingMode";

        static GUIContent s_StereoRenderingMode = EditorGUIUtility.TrTextContent("Stereo Rendering Mode");

        private SerializedProperty m_StereoRenderingMode;

        private const string kInitializationType = "InitializationType";

        static GUIContent s_InitializationType = EditorGUIUtility.TrTextContent("Application Type");

        private SerializedProperty m_InitializationType;

        private const string kMirrorViewModeKey = "MirrorView";

        static GUIContent s_MirrorViewMode = EditorGUIUtility.TrTextContent("Mirror View Mode");

        private SerializedProperty m_MirrorViewMode;

        private const string kPreInit = "PreInit";

        static GUIContent s_PreInit = EditorGUIUtility.TrTextContent("XR SDK PreInit");

        private SerializedProperty m_PreInit;

        public GUIContent WindowsTab;
        private int tab = 0;

        public void OnEnable()
        { 
            WindowsTab = new GUIContent("",  EditorGUIUtility.IconContent("BuildSettings.Standalone.Small").image);
        }
        public static void CloseWindowHelper()
        {
            var window = SettingsService.OpenProjectSettings("Project/XR Plug-in Management");
            if (window)
            {
                Debug.LogWarning("<b>[Community OpenXR]</b> Switching away from OpenVR Project settings window to avoid data corruption.");
            }
            EditorApplication.update -= CloseWindowHelper;
            closing = false;
        }
        private static bool closing = false;

        public override void OnInspectorGUI()
        {
            if (Application.isPlaying)
            {
                //need to close this window if we're in play mode. There's a bug that resets settings otherwise.
                EditorGUILayout.LabelField("Unity XR settings are unavailable while in play mode.");

                if (!closing)
                {
                    closing = true;
                    EditorApplication.update += CloseWindowHelper;
                }
                return;
            }

            if (serializedObject == null || serializedObject.targetObject == null)
                return;

            if (m_StereoRenderingMode == null)
            {
                m_StereoRenderingMode = serializedObject.FindProperty(kStereoRenderingMode);
            }
            if (m_InitializationType == null)
            {
                m_InitializationType = serializedObject.FindProperty(kInitializationType);
            }
            if (m_MirrorViewMode == null)
            {
                m_MirrorViewMode = serializedObject.FindProperty(kMirrorViewModeKey);
            }
            if (m_PreInit == null)
            {
                m_PreInit = serializedObject.FindProperty(kPreInit);
            }

            serializedObject.Update();

            int currentMode = m_MirrorViewMode.intValue;
            if (m_MirrorViewMode != null)

            tab = GUILayout.Toolbar(tab, new GUIContent[] {WindowsTab},EditorStyles.toolbarButton);
            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            if (tab == 0)
            {
                EditorGUILayout.PropertyField(m_InitializationType, s_InitializationType);
                EditorGUILayout.PropertyField(m_StereoRenderingMode, s_StereoRenderingMode);
                EditorGUILayout.PropertyField(m_MirrorViewMode, s_MirrorViewMode);
                EditorGUILayout.PropertyField(m_PreInit, s_PreInit);
            }
            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();

            /*
            //can't have the settings window open during play mode
            int newMode = m_MirrorViewMode.intValue;

            if (currentMode != newMode && Application.isPlaying)
            {
                OpenVRSettings.SetMirrorViewMode((ushort)newMode);
            }*/
        }
    }
}
