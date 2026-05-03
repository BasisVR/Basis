using System;
using System.Collections.Generic;
using Basis.Editor.Localization;
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
            new GUIContent(
                BasisEditorLocalization.Get("sdk.parameterDriver.localOnly.label"),
                BasisEditorLocalization.Get("sdk.parameterDriver.localOnly.tooltip")),
            driver.localOnly);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(driver, "Change Local Only");
            driver.localOnly = localOnly;
            EditorUtility.SetDirty(driver);
        }

        EditorGUI.BeginChangeCheck();
        string debugString = EditorGUILayout.TextField(
            new GUIContent(
                BasisEditorLocalization.Get("sdk.parameterDriver.debugLabel.label"),
                BasisEditorLocalization.Get("sdk.parameterDriver.debugLabel.tooltip")),
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
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.parameterDriver.operations.header", driver.operations.Length), EditorStyles.boldLabel);

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
            GUILayout.Label(BasisEditorLocalization.Get("sdk.parameterDriver.operations.empty"), emptyStyle);
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
        if (GUILayout.Button(BasisEditorLocalization.Get("sdk.parameterDriver.operations.add"), GUILayout.Height(30)))
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

        string destLabel = string.IsNullOrEmpty(op.destination) ? BasisEditorLocalization.Get("sdk.parameterDriver.destination.placeholder") : op.destination;
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
            var newType = (OperationType)EditorGUILayout.EnumPopup(BasisEditorLocalization.Get("sdk.parameterDriver.field.type"), op.type);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(driver, "Change Operation Type");
                driver.operations[i].type = newType;
                EditorUtility.SetDirty(driver);
            }

            // Destination
            EditorGUI.BeginChangeCheck();
            string newDest = EditorGUILayout.TextField(BasisEditorLocalization.Get("sdk.parameterDriver.field.destination"), op.destination ?? "");
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
                    float val = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.value"), op.value);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(driver, "Change Value");
                        driver.operations[i].value = val;
                        EditorUtility.SetDirty(driver);
                    }
                    break;

                case OperationType.Random:
                    EditorGUI.BeginChangeCheck();
                    float minV    = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.minValue"), op.minValue);
                    float maxV    = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.maxValue"), op.maxValue);
                    float chance  = EditorGUILayout.Slider(
                        new GUIContent(
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.chance.label"),
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.chance.tooltip")),
                        op.chance, 0f, 1f);
                    bool noRepeat = EditorGUILayout.Toggle(
                        new GUIContent(
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.preventRepeats.label"),
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.preventRepeats.tooltip")),
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
                        new GUIContent(
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.source.label"),
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.source.tooltip")),
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
                        new GUIContent(
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.remap.label"),
                            BasisEditorLocalization.Get("sdk.parameterDriver.field.remap.tooltip")),
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
                        float srcMin = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.sourceMin"), op.sourceMin);
                        float srcMax = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.sourceMax"), op.sourceMax);
                        float dstMin = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.destMin"),   op.destMin);
                        float dstMax = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.parameterDriver.field.destMax"),   op.destMax);
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
