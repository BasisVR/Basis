#if !COMPILER_UDONSHARP && UNITY_EDITOR
using System.Reflection;

#if UDONSHARP

using UdonSharpEditor;
#endif

using UnityEditor;
using UnityEngine;

namespace AudioLink.Editor
{
    [CustomEditor(typeof(AudioLink))]
    public class AudioLinkEditor : UnityEditor.Editor
    {
        private readonly static GUIContent DisableReadbackButtonContent = EditorGUIUtility.TrTextContent("Disable readback", "Disables asynchronous readback, which is required for audio-reactive Udon scripts to function. This feature comes with a slight performance penalty.");
        private readonly static GUIContent EnableReadbackButtonContent = EditorGUIUtility.TrTextContent("Enable readback", "Enables asynchronous readback, which is required for audio-reactive Udon scripts to function. This feature comes with a slight performance penalty.");

        public void OnEnable()
        {
            AudioLink audioLink = (AudioLink)target;
            if (audioLink.audioData2D == null)
            {
                audioLink.audioData2D =
                    AssetDatabase.LoadAssetAtPath<Texture2D>(
                        AssetDatabase.GUIDToAssetPath("b07c8466531ac5e4e852f3e276e4baca"));
                Debug.Log(nameof(AudioLink) + ": restored audioData2D reference");
            }
        }

        public override void OnInspectorGUI()
        {
            AudioLink audioLink = (AudioLink)target;
#if UDONSHARP
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
#endif

            if (Camera.main == null)
            {
                EditorGUILayout.HelpBox("The current scene might be missing a main camera, this could cause issues with the AudioLink camera.", MessageType.Warning);
            }

            if (audioLink.audioSource == null)
            {
                EditorGUILayout.HelpBox("No audio source assigned. AudioLink will not work.", MessageType.Warning);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Link all sound reactive objects to this AudioLink instance",
                    "Links all scripts with 'audioLink' parameter to this object.")))
            {
                LinkAll();
            }

            EditorGUILayout.Space();
            base.OnInspectorGUI();
            EditorGUILayout.Space();

            if (audioLink.audioDataToggle)
            {
                GUI.backgroundColor = Color.red;
                if (GUILayout.Button(DisableReadbackButtonContent))
                {
                    audioLink.DisableReadback();
                    EditorUtility.SetDirty(audioLink);
                }
            }
            else
            {
                GUI.backgroundColor = Color.green;
                if (GUILayout.Button(EnableReadbackButtonContent))
                {
                    audioLink.EnableReadback();
                    EditorUtility.SetDirty(audioLink);
                }
            }
        }

        public void LinkAll()
        {
            LinkAll(target as AudioLink);
        }

        public static void LinkAll(AudioLink target)
        {
#if UDONSHARP
            MonoBehaviour[] allBehaviours = FindObjectsOfType<MonoBehaviour>();
#else
            MonoBehaviour[] allBehaviours = FindObjectsByType<MonoBehaviour>( FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
#endif
            // this handles all reasonable cases of referencing audiolink
            // (it doesn't handle referencing it multiple times in one monobehaviour, or referencing it as it's Base type)
            foreach (var behaviour in allBehaviours)
            {
                FieldInfo fieldInfo = behaviour.GetType().GetField("audioLink");
                // in case the field isn't called "audioLink"
                if (fieldInfo == null)
                {
                    foreach (FieldInfo field in behaviour.GetType().GetFields())
                    {
                        if (field.FieldType == typeof(AudioLink))
                        {
                            fieldInfo = field;
                            break;
                        }
                    }
                }
                if (fieldInfo != null && fieldInfo.FieldType == typeof(AudioLink))
                {
                    fieldInfo.SetValue(behaviour, target);
                    EditorUtility.SetDirty(behaviour);

                    if (PrefabUtility.IsPartOfPrefabInstance(behaviour))
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
                    }
                }
            }
        }
    }
}
#endif
