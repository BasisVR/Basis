using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static BasisParameterDriver;
using static BasisParameterDriver.Operation;

[CustomEditor(typeof(BasisParameterDriver))]
public class BasisParameterDriverEditor : Editor
{
    private readonly List<bool> _foldouts = new List<bool>();

    public override void OnInspectorGUI()
    {
        var driver = (BasisParameterDriver)target;
        serializedObject.Update();

        // ── Settings panel ────────────────────────────────────────────────
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUI.BeginChangeCheck();
        bool localOnly = EditorGUILayout.Toggle(
            new GUIContent("Local Only",
                "When true, operations only run on the local instance (recommended for Add and Random)."),
            driver.localOnly);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(driver, "Change Local Only");
            driver.localOnly = localOnly;
            EditorUtility.SetDirty(driver);
        }

        EditorGUI.BeginChangeCheck();
        string debugString = EditorGUILayout.TextField(
            new GUIContent("Debug Label",
                "Optional label shown in the editor for identification."),
            driver.debugString ?? "");
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(driver, "Change Debug Label");
            driver.debugString = debugString;
            EditorUtility.SetDirty(driver);
        }

        EditorGUILayout.EndVertical();

        // ── Operations header ─────────────────────────────────────────────
        if (driver.operations == null) driver.operations = Array.Empty<Operation>();
        EditorGUILayout.LabelField($"Operations  ({driver.operations.Length})", EditorStyles.boldLabel);

        // Sync foldout list
        while (_foldouts.Count < driver.operations.Length) _foldouts.Add(true);
        while (_foldouts.Count > driver.operations.Length) _foldouts.RemoveAt(_foldouts.Count - 1);

        // ── Operations list ───────────────────────────────────────────────
        if (driver.operations.Length == 0)
        {
            GUIStyle emptyStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                wordWrap  = true
            };
            emptyStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            GUILayout.Label("No operations — click '+ Add Operation' below.", emptyStyle);
            GUILayout.Space(4);
        }

        int removeIndex = -1;
        int swapA = -1, swapB = -1;

        for (int i = 0; i < driver.operations.Length; i++)
            DrawOperationCard(driver, i, ref removeIndex, ref swapA, ref swapB);

        // Apply deferred mutations
        if (swapA >= 0)
        {
            Undo.RecordObject(driver, "Reorder Operation");
            (driver.operations[swapA], driver.operations[swapB]) = (driver.operations[swapB], driver.operations[swapA]);
            EditorUtility.SetDirty(driver);
        }
        if (removeIndex >= 0)
        {
            Undo.RecordObject(driver, "Remove Operation");
            var list = new List<Operation>(driver.operations);
            list.RemoveAt(removeIndex);
            driver.operations = list.ToArray();
            EditorUtility.SetDirty(driver);
        }

        // ── Add button ────────────────────────────────────────────────────
        GUILayout.Space(4);
        if (GUILayout.Button("+ Add Operation", GUILayout.Height(30)))
        {
            Undo.RecordObject(driver, "Add Operation");
            var list = new List<Operation>(driver.operations) { new Operation() };
            driver.operations = list.ToArray();
            _foldouts.Add(true);
            EditorUtility.SetDirty(driver);
        }

        serializedObject.ApplyModifiedProperties();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Operation card
    // ─────────────────────────────────────────────────────────────────────

    private void DrawOperationCard(BasisParameterDriver driver, int i,
        ref int removeIndex, ref int swapA, ref int swapB)
    {
        var op = driver.operations[i];

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // ── Header row ────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal();

        string destLabel = string.IsNullOrEmpty(op.destination) ? "<destination>" : op.destination;
        _foldouts[i] = EditorGUILayout.Foldout(_foldouts[i], $" [{i}]  {destLabel}", true);

        GUILayout.Label(op.type.ToString().ToUpper(), EditorStyles.miniLabel, GUILayout.Width(58));

        GUI.enabled = i > 0;
        if (GUILayout.Button("▲", GUILayout.Width(22), GUILayout.Height(18))) { swapA = i - 1; swapB = i; }

        GUI.enabled = i < driver.operations.Length - 1;
        if (GUILayout.Button("▼", GUILayout.Width(22), GUILayout.Height(18))) { swapA = i; swapB = i + 1; }

        GUI.enabled = true;
        if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18))) removeIndex = i;

        EditorGUILayout.EndHorizontal();

        // ── Body ──────────────────────────────────────────────────────────
        if (_foldouts[i])
        {
            GUILayout.Space(2);
            EditorGUI.indentLevel += 2;

            // Type
            EditorGUI.BeginChangeCheck();
            var newType = (OperationType)EditorGUILayout.EnumPopup("Type", op.type);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(driver, "Change Operation Type");
                driver.operations[i].type = newType;
                EditorUtility.SetDirty(driver);
            }

            // Destination
            EditorGUI.BeginChangeCheck();
            string newDest = EditorGUILayout.TextField("Destination", op.destination ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(driver, "Change Destination");
                driver.operations[i].destination = newDest;
                EditorUtility.SetDirty(driver);
            }

            // Type-specific fields
            switch (op.type)
            {
                case OperationType.Set:
                case OperationType.Add:
                    EditorGUI.BeginChangeCheck();
                    float val = EditorGUILayout.FloatField("Value", op.value);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(driver, "Change Value");
                        driver.operations[i].value = val;
                        EditorUtility.SetDirty(driver);
                    }
                    break;

                case OperationType.Random:
                    EditorGUI.BeginChangeCheck();
                    float minV    = EditorGUILayout.FloatField("Min Value", op.minValue);
                    float maxV    = EditorGUILayout.FloatField("Max Value", op.maxValue);
                    float chance  = EditorGUILayout.Slider(
                        new GUIContent("Chance  (bool)", "Probability the bool is set to true."),
                        op.chance, 0f, 1f);
                    bool noRepeat = EditorGUILayout.Toggle(
                        new GUIContent("Prevent Repeats  (int)", "Prevents the same int from being chosen twice in a row."),
                        op.preventRepeats);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(driver, "Change Random Operation");
                        driver.operations[i].minValue       = minV;
                        driver.operations[i].maxValue       = maxV;
                        driver.operations[i].chance         = chance;
                        driver.operations[i].preventRepeats = noRepeat;
                        EditorUtility.SetDirty(driver);
                    }
                    break;

                case OperationType.Copy:
                    EditorGUI.BeginChangeCheck();
                    string src = EditorGUILayout.TextField(
                        new GUIContent("Source", "Animator parameter to read from."),
                        op.source ?? "");
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(driver, "Change Source");
                        driver.operations[i].source = src;
                        EditorUtility.SetDirty(driver);
                    }

                    EditorGUILayout.Space(2);

                    EditorGUI.BeginChangeCheck();
                    bool remap = EditorGUILayout.Toggle(
                        new GUIContent("Remap Range", "Remap the source range to a different destination range."),
                        op.remapRange);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(driver, "Toggle Remap Range");
                        driver.operations[i].remapRange = remap;
                        EditorUtility.SetDirty(driver);
                    }

                    if (op.remapRange)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUI.BeginChangeCheck();
                        float srcMin = EditorGUILayout.FloatField("Source Min", op.sourceMin);
                        float srcMax = EditorGUILayout.FloatField("Source Max", op.sourceMax);
                        float dstMin = EditorGUILayout.FloatField("Dest Min",   op.destMin);
                        float dstMax = EditorGUILayout.FloatField("Dest Max",   op.destMax);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(driver, "Change Remap Range");
                            driver.operations[i].sourceMin = srcMin;
                            driver.operations[i].sourceMax = srcMax;
                            driver.operations[i].destMin   = dstMin;
                            driver.operations[i].destMax   = dstMax;
                            EditorUtility.SetDirty(driver);
                        }
                        EditorGUI.indentLevel--;
                    }
                    break;
            }

            EditorGUI.indentLevel -= 2;
            GUILayout.Space(5);
        }
        else
        {
            GUILayout.Space(3);
        }

        EditorGUILayout.EndVertical();
        GUILayout.Space(3);
    }

}
