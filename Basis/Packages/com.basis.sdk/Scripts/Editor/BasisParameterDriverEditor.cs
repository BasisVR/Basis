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

    // Type accent colours
    private static readonly Color ColSet    = new Color(0.22f, 0.46f, 0.76f);
    private static readonly Color ColAdd    = new Color(0.18f, 0.58f, 0.33f);
    private static readonly Color ColRandom = new Color(0.76f, 0.46f, 0.10f);
    private static readonly Color ColCopy   = new Color(0.54f, 0.22f, 0.74f);

    // SDK-sourced button colours
    private static readonly Color ColButtonAdd  = new Color(0.18f, 0.58f, 0.33f, 1f);
    private static readonly Color ColButtonGray = new Color(0.5f,  0.5f,  0.5f,  1f);
    private static readonly Color ColErrorRed   = new Color(0.96f, 0.26f, 0.21f, 1f);

    public override void OnInspectorGUI()
    {
        var driver = (BasisParameterDriver)target;
        serializedObject.Update();

        // ── Settings panel ────────────────────────────────────────────────
        DrawPanel(new Color(0.25f, 0.25f, 0.30f, 0.5f), new Color(0.5f, 0.5f, 0.7f), () =>
        {
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
        });

        // ── Operations header ─────────────────────────────────────────────
        if (driver.operations == null) driver.operations = Array.Empty<Operation>();
        DrawSectionLabel($"Operations  ({driver.operations.Length})");

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
        DrawSdkButton("+ Add Operation", ColButtonAdd, 30, () =>
        {
            Undo.RecordObject(driver, "Add Operation");
            var list = new List<Operation>(driver.operations) { new Operation() };
            driver.operations = list.ToArray();
            _foldouts.Add(true);
            EditorUtility.SetDirty(driver);
        });

        serializedObject.ApplyModifiedProperties();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Operation card
    // ─────────────────────────────────────────────────────────────────────

    private void DrawOperationCard(BasisParameterDriver driver, int i,
        ref int removeIndex, ref int swapA, ref int swapB)
    {
        var   op     = driver.operations[i];
        Color accent = TypeAccent(op.type);
        Color bg     = new Color(accent.r * 0.15f, accent.g * 0.15f, accent.b * 0.15f, 0.5f);
        Color dimBorder = new Color(accent.r * 0.5f, accent.g * 0.5f, accent.b * 0.5f);

        // Card background — mimics SDK panel style (tinted bg, 2px border, strong accent bottom)
        Rect cardRect = EditorGUILayout.BeginVertical();
        if (Event.current.type == EventType.Repaint)
        {
            EditorGUI.DrawRect(cardRect, dimBorder);                                                               // outer border
            EditorGUI.DrawRect(new Rect(cardRect.x + 2, cardRect.y + 2, cardRect.width - 4, cardRect.height - 4), bg); // inner bg
            EditorGUI.DrawRect(new Rect(cardRect.x, cardRect.y, 2, cardRect.height), accent);                     // left bar
            EditorGUI.DrawRect(new Rect(cardRect.x, cardRect.yMax - 3, cardRect.width, 3), accent);               // bottom accent
        }

        GUILayout.Space(5);

        // ── Header row ────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(8);

        // Foldout
        GUIStyle foldStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, fontSize = 12 };
        string destLabel = string.IsNullOrEmpty(op.destination) ? "<destination>" : op.destination;
        _foldouts[i] = EditorGUILayout.Foldout(_foldouts[i], $" [{i}]  {destLabel}", true, foldStyle);

        // Type pill — SDK button style: bold white text, coloured bg, radius via miniButton skin
        GUIStyle pillStyle = new GUIStyle(EditorStyles.miniButton)
        {
            fontStyle = FontStyle.Bold,
            fontSize  = 10,
            alignment = TextAnchor.MiddleCenter
        };
        pillStyle.normal.textColor = Color.white;
        pillStyle.hover.textColor  = Color.white;
        Color prevBg = GUI.backgroundColor;
        GUI.backgroundColor = accent;
        GUILayout.Button(op.type.ToString().ToUpper(), pillStyle, GUILayout.Width(58), GUILayout.Height(16));
        GUI.backgroundColor = prevBg;

        // ▲ ▼ ✕ — SDK icon button style: bold white text, coloured bg
        GUIStyle iconStyle = MakeIconButtonStyle();

        GUI.enabled = i > 0;
        GUI.backgroundColor = ColButtonGray;
        if (GUILayout.Button("▲", iconStyle, GUILayout.Width(22), GUILayout.Height(18))) { swapA = i - 1; swapB = i; }

        GUI.enabled = i < driver.operations.Length - 1;
        GUI.backgroundColor = ColButtonGray;
        if (GUILayout.Button("▼", iconStyle, GUILayout.Width(22), GUILayout.Height(18))) { swapA = i; swapB = i + 1; }

        GUI.enabled = true;
        GUI.backgroundColor = ColErrorRed;
        if (GUILayout.Button("✕", iconStyle, GUILayout.Width(22), GUILayout.Height(18))) removeIndex = i;
        GUI.backgroundColor = prevBg;

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

                    DrawThinSeparator(new Color(accent.r * 0.4f, accent.g * 0.4f, accent.b * 0.4f));

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

    // ─────────────────────────────────────────────────────────────────────
    // SDK-style IMGUI helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full-width button matching SDK DocumentationButton / AutoFixButton:
    /// bold white text 14pt, coloured bg, centered.
    /// </summary>
    private static void DrawSdkButton(string text, Color bgColor, float height, Action onClick)
    {
        GUIStyle style = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
            fontSize  = 14,
            alignment = TextAnchor.MiddleCenter
        };
        style.normal.textColor  = Color.white;
        style.hover.textColor   = Color.white;
        style.active.textColor  = Color.white;
        style.focused.textColor = Color.white;

        Color prev = GUI.backgroundColor;
        GUI.backgroundColor = bgColor;
        if (GUILayout.Button(text, style, GUILayout.Height(height)))
            onClick?.Invoke();
        GUI.backgroundColor = prev;
        GUILayout.Space(4);
    }

    /// <summary>
    /// Draws content inside a styled panel matching SDK CreateErrorPanel / CreatePassedPanel:
    /// tinted bg, 2px dim border all sides, 3px strong accent on bottom.
    /// </summary>
    private static void DrawPanel(Color bgColor, Color borderAccent, Action content)
    {
        Color dimBorder = new Color(borderAccent.r * 0.5f, borderAccent.g * 0.5f, borderAccent.b * 0.5f);

        Rect panelRect = EditorGUILayout.BeginVertical();
        if (Event.current.type == EventType.Repaint)
        {
            EditorGUI.DrawRect(panelRect, dimBorder);
            EditorGUI.DrawRect(new Rect(panelRect.x + 2, panelRect.y + 2, panelRect.width - 4, panelRect.height - 4), bgColor);
            EditorGUI.DrawRect(new Rect(panelRect.x, panelRect.yMax - 3, panelRect.width, 3), borderAccent);
        }

        GUILayout.Space(5);
        EditorGUI.indentLevel++;
        content?.Invoke();
        EditorGUI.indentLevel--;
        GUILayout.Space(8);
        EditorGUILayout.EndVertical();
        GUILayout.Space(6);
    }

    private static void DrawSectionLabel(string text)
    {
        GUIStyle style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        style.normal.textColor = Color.white;
        GUILayout.Space(2);
        GUILayout.Label(text, style);
        GUILayout.Space(2);
    }

    private static void DrawThinSeparator(Color color)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(r, color);
        GUILayout.Space(2);
    }

    private static GUIStyle MakeIconButtonStyle()
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
            fontSize  = 11,
            alignment = TextAnchor.MiddleCenter
        };
        style.normal.textColor  = Color.white;
        style.hover.textColor   = Color.white;
        style.active.textColor  = Color.white;
        style.focused.textColor = Color.white;
        return style;
    }

    private static Color TypeAccent(OperationType t) => t switch
    {
        OperationType.Set    => ColSet,
        OperationType.Add    => ColAdd,
        OperationType.Random => ColRandom,
        OperationType.Copy   => ColCopy,
        _                    => new Color(0.45f, 0.45f, 0.45f),
    };
}
