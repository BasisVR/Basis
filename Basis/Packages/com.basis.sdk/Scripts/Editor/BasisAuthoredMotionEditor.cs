using System.Collections.Generic;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;
using static BasisAuthoredMotion.Movement;

[CustomEditor(typeof(BasisAuthoredMotion))]
public class BasisAuthoredMotionEditor : Editor
{
    private readonly List<bool> _foldouts = new List<bool>();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        SerializedProperty movements = serializedObject.FindProperty("movements");

        EditorGUILayout.LabelField(L("movements.header", movements.arraySize), EditorStyles.boldLabel);

        while (_foldouts.Count < movements.arraySize) _foldouts.Add(true);
        while (_foldouts.Count > movements.arraySize) _foldouts.RemoveAt(_foldouts.Count - 1);

        if (movements.arraySize == 0)
        {
            GUIStyle emptyStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold, wordWrap = true };
            emptyStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            GUILayout.Label(L("movements.empty"), emptyStyle);
            GUILayout.Space(4);
        }

        int removeIndex = -1, swapA = -1, swapB = -1;
        for (int i = 0; i < movements.arraySize; i++)
            DrawMovementCard(movements, i, ref removeIndex, ref swapA, ref swapB);

        if (swapA >= 0) movements.MoveArrayElement(swapA, swapB);
        if (removeIndex >= 0) movements.DeleteArrayElementAtIndex(removeIndex);

        GUILayout.Space(4);
        if (GUILayout.Button(L("movements.add"), GUILayout.Height(30)))
        {
            movements.arraySize++;
            _foldouts.Add(true);
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawMovementCard(SerializedProperty movements, int i,
        ref int removeIndex, ref int swapA, ref int swapB)
    {
        SerializedProperty mv = movements.GetArrayElementAtIndex(i);
        var kind = (Kind)mv.FindPropertyRelative("kind").enumValueIndex;
        SerializedProperty label = mv.FindPropertyRelative("label");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        string title = string.IsNullOrEmpty(label.stringValue) ? L("label.placeholder") : label.stringValue;
        _foldouts[i] = EditorGUILayout.Foldout(_foldouts[i], $" [{i}]  {title}", true);
        GUILayout.Label(kind.ToString().ToUpper(), EditorStyles.miniLabel, GUILayout.Width(90));

        GUI.enabled = i > 0;
        if (GUILayout.Button("▲", GUILayout.Width(22), GUILayout.Height(18))) { swapA = i - 1; swapB = i; }
        GUI.enabled = i < movements.arraySize - 1;
        if (GUILayout.Button("▼", GUILayout.Width(22), GUILayout.Height(18))) { swapA = i; swapB = i + 1; }
        GUI.enabled = true;
        if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18))) removeIndex = i;
        EditorGUILayout.EndHorizontal();

        if (_foldouts[i])
        {
            GUILayout.Space(2);
            EditorGUI.indentLevel++;

            Field(mv, "kind", "kind");
            Field(mv, "label", "label");
            Field(mv, "enabled", "enabled");

            switch (kind)
            {
                case Kind.Oscillate:
                    Field(mv, "channel", "channel");
                    Field(mv, "axis", "axis");
                    Field(mv, "amplitude", "amplitude");
                    Field(mv, "frequencyHz", "frequencyHz");
                    Field(mv, "phase", "phase");
                    Field(mv, "waveform", "waveform");
                    var wf = (Waveform)mv.FindPropertyRelative("waveform").enumValueIndex;
                    if (wf == Waveform.Square || wf == Waveform.Pulse)
                        Field(mv, "pulseWidth", "pulseWidth");
                    Field(mv, "chain", "chain");
                    Field(mv, "chainPhaseStep", "chainPhaseStep");
                    Field(mv, "chainFalloff", "chainFalloff");
                    break;

                case Kind.Noise:
                    Field(mv, "channel", "channel");
                    Field(mv, "axis", "axis");
                    Field(mv, "amplitude", "amplitude");
                    Field(mv, "noiseSpeed", "noiseSpeed");
                    Field(mv, "seed", "seed");
                    Field(mv, "chain", "chain");
                    Field(mv, "chainFalloff", "chainFalloff");
                    break;

                case Kind.Rotate:
                    Field(mv, "target", "target");
                    Field(mv, "axis", "axis");
                    Field(mv, "speedDeg", "speedDeg");
                    break;

                case Kind.Orbit:
                    Field(mv, "target", "target");
                    Field(mv, "pivot", "pivot");
                    Field(mv, "axis", "axis");
                    Field(mv, "radius", "radius");
                    Field(mv, "orbitSpeedDeg", "orbitSpeedDeg");
                    break;

                case Kind.RandomSelect:
                    Field(mv, "selectTarget", "selectTarget");
                    Field(mv, "options", "options");
                    Field(mv, "idleWeight", "idleWeight");
                    Field(mv, "intervalRange", "intervalRange");
                    Field(mv, "attack", "attack");
                    Field(mv, "release", "release");
                    Field(mv, "preventRepeats", "preventRepeats");
                    Field(mv, "seed", "seed");
                    break;

                case Kind.Sequence:
                    Field(mv, "bakedClip", "bakedClip");
                    Field(mv, "sequenceRoot", "sequenceRoot");
                    Field(mv, "loop", "loop");
                    Field(mv, "sequenceSpeed", "sequenceSpeed");
                    break;
            }

            EditorGUI.indentLevel--;
            GUILayout.Space(5);
        }
        else
        {
            GUILayout.Space(3);
        }

        EditorGUILayout.EndVertical();
        GUILayout.Space(3);
    }

    private static void Field(SerializedProperty parent, string relative, string key)
    {
        SerializedProperty p = parent.FindPropertyRelative(relative);
        EditorGUILayout.PropertyField(p, new GUIContent(L($"field.{key}.label"), L($"field.{key}.tooltip")), true);
    }

    private static string L(string key) => BasisEditorLocalization.Get("sdk.authoredMotion." + key);
    private static string L(string key, params object[] args) => BasisEditorLocalization.Get("sdk.authoredMotion." + key, args);
}
