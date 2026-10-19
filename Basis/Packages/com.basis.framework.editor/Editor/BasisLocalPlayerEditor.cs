using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Players;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Restores inspector visibility of the player state that moved from public fields to
/// <see cref="IBasisPlayer"/> get/set properties. Unity no longer serializes those, so the
/// default inspector dropped them; this draws them live via reflection.
///
/// <para>Derives from <see cref="BasisDocInspector_UI"/> so declaring a CustomEditor for
/// BasisLocalPlayer does not cost it the Basis API Reference — a plain Editor here shadows the
/// fallback inspector and the reference silently disappears from the type people look it up on
/// most.</para>
/// </summary>
[CustomEditor(typeof(BasisLocalPlayer))]
[NoAutoStaticsCleanup]
public class BasisLocalPlayerEditor : BasisDocInspector_UI
{
    private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public;
    private static PropertyInfo[] _properties;
    private bool _show = true;

    private void OnEnable()
    {
        if (_properties != null) return;
        var list = new List<PropertyInfo>();
        foreach (PropertyInfo p in typeof(BasisLocalPlayer).GetProperties(MemberFlags))
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length != 0) continue;
            // Only Basis-declared properties; skips MonoBehaviour/Component/Object noise.
            if (!typeof(BasisPlayer).IsAssignableFrom(p.DeclaringType)) continue;
            list.Add(p);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _properties = list.ToArray();
    }

    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        // The property table is reflection-driven IMGUI; hosting it as-is keeps that code unchanged
        // while the API Reference underneath stays UI Toolkit like every other Basis inspector.
        root.Add(new IMGUIContainer(OnInspectorGUI));

        VisualElement api = CreateApiReferenceFoldout();
        if (api != null)
        {
            root.Add(new VisualElement { style = { height = 8 } });
            root.Add(api);
        }
        return root;
    }

    public override void OnInspectorGUI()
    {
        var player = (BasisLocalPlayer)target;

        _show = EditorGUILayout.Foldout(_show, BasisEditorLocalization.Get("editor.localPlayerEditor.propertiesFoldout"), true);
        if (_show)
        {
            EditorGUILayout.HelpBox(
                BasisEditorLocalization.Get("editor.localPlayerEditor.propertiesHelp"),
                MessageType.None);

            using (new EditorGUI.IndentLevelScope())
            {
                foreach (PropertyInfo p in _properties)
                {
                    DrawProperty(player, p);
                }
            }
        }

        EditorGUILayout.Space();
        DrawDefaultInspector();

        if (Application.isPlaying)
        {
            Repaint();
        }
    }

    private static void DrawProperty(object instance, PropertyInfo p)
    {
        object value;
        try { value = p.GetValue(instance); }
        catch (Exception e) { EditorGUILayout.LabelField(p.Name, $"<{e.GetType().Name}>"); return; }

        Type t = p.PropertyType;
        bool canSet = p.CanWrite;

        using (new EditorGUI.DisabledScope(!canSet))
        using (var check = new EditorGUI.ChangeCheckScope())
        {
            object newValue = value;
            bool handled = true;

            if (t == typeof(string)) newValue = EditorGUILayout.TextField(p.Name, (string)value);
            else if (t == typeof(bool)) newValue = EditorGUILayout.Toggle(p.Name, (bool)value);
            else if (t == typeof(int)) newValue = EditorGUILayout.IntField(p.Name, (int)value);
            else if (t == typeof(byte)) newValue = (byte)Mathf.Clamp(EditorGUILayout.IntField(p.Name, (byte)value), 0, 255);
            else if (t == typeof(short)) newValue = (short)EditorGUILayout.IntField(p.Name, (short)value);
            else if (t == typeof(float)) newValue = EditorGUILayout.FloatField(p.Name, (float)value);
            else if (t.IsEnum) newValue = EditorGUILayout.EnumPopup(p.Name, (Enum)value);
            else if (typeof(UnityEngine.Object).IsAssignableFrom(t)) newValue = EditorGUILayout.ObjectField(p.Name, (UnityEngine.Object)value, t, true);
            else { handled = false; EditorGUILayout.LabelField(p.Name, value == null ? "null" : value.ToString()); }

            if (handled && canSet && check.changed)
            {
                try { p.SetValue(instance, newValue); }
                catch { /* live-only set; ignore failures */ }
            }
        }
    }
}
