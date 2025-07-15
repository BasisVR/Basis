using UnityEditor;
using UnityEngine;
#if UNITY_EDITOR_WIN
using UnityEngine.Windows;
#endif

namespace Metaphira.EditorTools.Utils
{
    [System.Serializable]
    public class FolderReference
    {
        public string GUID;
#if UNITY_EDITOR
        public string Path => AssetDatabase.GUIDToAssetPath(GUID);
#endif
    }

#if UNITY_EDITOR_WIN
    [CustomPropertyDrawer(typeof(FolderReference))]
    public class FolderReferencePropertyDrawer : PropertyDrawer
    {
        private bool initialized;
        private SerializedProperty guid;
        private Object obj;

        private void Init(SerializedProperty property)
        {
            initialized = true;
            guid = property.FindPropertyRelative("GUID");
            obj = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(guid.stringValue));
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (!initialized) Init(property);

            GUIContent guiContent = EditorGUIUtility.ObjectContent(obj, typeof(DefaultAsset));

            Rect r = EditorGUI.PrefixLabel(position, label);

            Rect textFieldRect = r;
            textFieldRect.width -= 19f;

            GUIStyle textFieldStyle = new GUIStyle("TextField")
            {
                imagePosition = obj ? ImagePosition.ImageLeft : ImagePosition.TextOnly
            };

            if (GUI.Button(textFieldRect, guiContent, textFieldStyle) && obj)
                EditorGUIUtility.PingObject(obj);

            if (textFieldRect.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.DragUpdated)
                {
                    Object reference = DragAndDrop.objectReferences[0];
                    string path = AssetDatabase.GetAssetPath(reference);
                    DragAndDrop.visualMode = Directory.Exists(path) ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.DragPerform)
                {
                    Object reference = DragAndDrop.objectReferences[0];
                    string path = AssetDatabase.GetAssetPath(reference);
                    if (Directory.Exists(path))
                    {
                        obj = reference;
                        guid.stringValue = AssetDatabase.AssetPathToGUID(path);
                    }
                    Event.current.Use();
                }
            }

            Rect objectFieldRect = r;
            objectFieldRect.x = textFieldRect.xMax + 1f;
            objectFieldRect.width = 19f;

            if (GUI.Button(objectFieldRect, "", GUI.skin.GetStyle("IN ObjectField")))
            {
                string path = EditorUtility.OpenFolderPanel("Select a folder", "Assets", "");
                if (path.Contains(Application.dataPath))
                {
                    path = "Assets" + path.Substring(Application.dataPath.Length);
                    obj = AssetDatabase.LoadAssetAtPath(path, typeof(DefaultAsset));
                    guid.stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(obj));
                }
                else Debug.LogError("The path must be in the Assets folder");
            }
        }
    }
#endif
}