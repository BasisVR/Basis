#if UNITY_EDITOR // (AUDIT) This utility class is irrelevant in builds.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms.Editor
{
    // HEF V0.0.9991
    public static class HaiEFCommon
    {
/*
MIT License

Copyright (c) 2025 Haï~ (@vr_hai github.com/hai-vr)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

/* Notes to self:
    - Use SessionState to make something persist while the editor is open. Data lost when editor is closed.
    - Use EditorPrefs to make something persist editor-wide. This is not project-wide, data is shared across projects.
    - (ANNOYING) Use ScriptableObject to make something persist in the project.

    - Use ObjectNames.NicifyVariableName(fieldName) to get the nice name of a field-like string.
*/

        public static T ColoredBackground<T>(bool isActive, Color bgColor, Func<T> inside)
        {
            var col = GUI.color;
            try
            {
                if (isActive) GUI.color = bgColor;
                return inside();
            }
            finally
            {
                GUI.color = col;
            }
        }

        public static void ColoredBackgroundVoid(bool isActive, Color bgColor, Action inside)
        {
            ColoredBackground(isActive, bgColor, () =>
            {
                inside();
                return (object)null;
            });
        }

        public static bool LilFoldout(string title, string help, bool display, ref bool changed, bool customColor = false, Color color = default)
            => HaiEFExternals_LilGUI.LilFoldout(title, help, display, ref changed, customColor, color);

        public static bool SaveDialog(string title, string extension, Func<byte[]> dataProducer, out string saveLocation, string lastSaveLocation = "", bool pingIfSavedAsAsset = false)
        {
            var savePath = EditorUtility.SaveFilePanel(title, lastSaveLocation == "" ? Application.dataPath : lastSaveLocation, "", extension);
            if (savePath == null || savePath.Trim() == "")
            {
                saveLocation = null;
                return false;
            }

            TrySaveAtLocation(savePath, dataProducer, pingIfSavedAsAsset);
            saveLocation = savePath;

            return true;
        }

        private static void TrySaveAtLocation(string savePath, Func<byte[]> dataProducer, bool pingIfSavedAsAsset)
        {
            var bytes = dataProducer.Invoke();
            File.WriteAllBytes(savePath, bytes);

            var isAnAsset = savePath.StartsWith(Application.dataPath);
            if (isAnAsset)
            {
                AssetDatabase.Refresh();
                var assetPath = "Assets" + savePath.Substring(Application.dataPath.Length);

                if (pingIfSavedAsAsset)
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath));
                }
            }
        }
    }

    internal static class HaiEFExternals_LilGUI
    {
/*
MIT License

Copyright (c) 2020-2023 lilxyzw

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */
        private static bool _init;
        private static GUIStyle _foldoutStyle;

        private static void Init()
        {
            if (_init) return;
            _init = true;

            _foldoutStyle = new GUIStyle("ShurikenModuleTitle")
            {
                font = EditorStyles.label.font,
                fontSize = EditorStyles.label.fontSize,
                fontStyle = EditorStyles.label.fontStyle,
                border = new RectOffset(15, 7, 4, 4),
                contentOffset = new Vector2(20f, -2f),
                fixedHeight = 22
            };
        }

        internal static bool LilFoldout(string title, string help, bool display, ref bool changed, bool customColor = false, Color color = default)
        {
            Init();

            var rect = GUILayoutUtility.GetRect(16f, 20f, _foldoutStyle);
            rect.width += 8f;
            rect.x -= 8f;
            HaiEFCommon.ColoredBackground(customColor, color, () =>
            {
                GUI.Box(rect, new GUIContent(title, help), _foldoutStyle);
                return true;
            });

            var e = Event.current;

            var toggleRect = new Rect(rect.x + 4f, rect.y + 2f, 13f, 13f);
            if(e.type == EventType.Repaint) {
                EditorStyles.foldout.Draw(toggleRect, false, false, display, false);
            }

            rect.width -= 24;
            if(e.type == EventType.MouseDown && rect.Contains(e.mousePosition)) {
                display = !display;
                changed = true;
                e.Use();
            }

            return display;
        }
    }
}
#endif
