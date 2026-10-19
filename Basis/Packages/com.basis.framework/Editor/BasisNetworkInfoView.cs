using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Basis.Editor.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Live, read-only reflection view of every metadata field on a synced component — config,
/// runtime sync state, ownership, the jitter-buffer receiver and the value schema. Play-mode only.
/// </summary>
public static class BasisNetworkInfoView
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SyncNamespace = "Basis.Scripts.Networking.Sync";
    private const int MaxDepth = 4;
    private const int RefreshMs = 250;
    private const int MaxElements = 24;

    private static Color Accent => BasisSyncInspectorUI.Accent;
    private static Color Muted => BasisEditorUI.Light ? new Color(0.34f, 0.34f, 0.34f, 1f) : new Color(0.78f, 0.78f, 0.78f, 1f);
    private static Color KeyCol => BasisEditorUI.Light ? new Color(0.12f, 0.34f, 0.66f, 1f) : new Color(0.62f, 0.78f, 1f, 1f);
    private static Color ValueCol => BasisEditorUI.Light ? new Color(0.10f, 0.10f, 0.10f, 1f) : Color.white;

    public static VisualElement Build(UnityEngine.Object target)
    {
        var foldout = new Foldout { text = BasisEditorLocalization.Get("editor.networkInfo.title"), value = false };
        foldout.style.marginTop = 6;
        foldout.style.marginBottom = 8;
        Label title = foldout.Q<Label>();
        if (title != null)
        {
            title.style.color = new StyleColor(Accent);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 12;
        }

        var content = new VisualElement();
        content.style.marginTop = 2;
        foldout.Add(content);

        var refreshers = new List<Action>();
        bool builtForPlaying = !Application.isPlaying;

        void Rebuild()
        {
            content.Clear();
            refreshers.Clear();
            builtForPlaying = Application.isPlaying;

            if (!Application.isPlaying)
            {
                content.Add(Note(BasisEditorLocalization.Get("editor.networkInfo.playModeNote")));
                return;
            }
            if (target == null)
            {
                content.Add(Note(BasisEditorLocalization.Get("editor.networkInfo.targetGone")));
                return;
            }
            BuildTypeGroups(content, () => target, target.GetType(), refreshers);
            if (refreshers.Count == 0) content.Add(Note(BasisEditorLocalization.Get("editor.networkInfo.noMetadata")));
        }

        Rebuild();

        foldout.schedule.Execute(() =>
        {
            if (builtForPlaying != Application.isPlaying || (Application.isPlaying && target == null))
            {
                Rebuild();
                return;
            }
            if (!Application.isPlaying || !foldout.value) return;
            for (int i = 0; i < refreshers.Count; i++) refreshers[i]();
        }).Every(RefreshMs);

        return foldout;
    }

    private static void BuildTypeGroups(VisualElement parent, Func<object> getInstance, Type mostDerived, List<Action> refreshers)
    {
        for (Type t = mostDerived; IsOwnType(t); t = t.BaseType)
        {
            FieldInfo[] fields = t.GetFields(InstanceFlags | BindingFlags.DeclaredOnly);
            if (fields.Length == 0) continue;

            Foldout group = SubFoldout(t.Name, true);
            parent.Add(group);
            foreach (FieldInfo f in fields) AddField(group, getInstance, f, 1, refreshers);
        }
    }

    private static void AddFieldsFlat(VisualElement parent, Func<object> getInstance, Type type, int depth, List<Action> refreshers)
    {
        var seen = new HashSet<string>();
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(InstanceFlags | BindingFlags.DeclaredOnly))
                if (seen.Add(f.Name)) AddField(parent, getInstance, f, depth, refreshers);
        }
    }

    private static void AddField(VisualElement parent, Func<object> getOwner, FieldInfo field, int depth, List<Action> refreshers)
    {
        string name = PrettyName(field.Name);
        Func<object> getValue = () =>
        {
            object owner = getOwner();
            if (owner == null) return null;
            try { return field.GetValue(owner); } catch { return null; }
        };

        if (depth < MaxDepth && IsExpandable(field.FieldType))
        {
            Foldout sub = SubFoldout(name, false);
            parent.Add(sub);
            Label hint = sub.Q<Label>();
            AddFieldsFlat(sub, getValue, field.FieldType, depth + 1, refreshers);
            refreshers.Add(() =>
            {
                bool isNull = getValue() == null;
                sub.SetEnabled(!isNull);
                if (hint != null) hint.text = isNull ? BasisEditorLocalization.Get("editor.networkInfo.nullName", name) : name;
            });
            return;
        }

        VisualElement row = Row();
        Label key = new Label(name);
        key.style.color = new StyleColor(KeyCol);
        key.style.flexBasis = new StyleLength(Length.Percent(42));
        key.style.flexGrow = 0;
        key.style.flexShrink = 0;
        key.style.whiteSpace = WhiteSpace.NoWrap;
        key.style.overflow = Overflow.Hidden;
        key.style.textOverflow = TextOverflow.Ellipsis;

        Label val = new Label("—");
        val.style.color = new StyleColor(ValueCol);
        val.style.flexGrow = 1;
        val.style.whiteSpace = WhiteSpace.Normal;

        row.Add(key);
        row.Add(val);
        parent.Add(row);

        refreshers.Add(() => val.text = Format(getValue(), 0));
    }

    private static bool IsOwnType(Type t) =>
        t != null
        && t != typeof(MonoBehaviour)
        && t != typeof(Behaviour)
        && t != typeof(Component)
        && t != typeof(UnityEngine.Object)
        && t != typeof(object);

    private static bool IsExpandable(Type t)
    {
        if (t == null || !t.IsClass || t == typeof(string)) return false;
        if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
        if (t.IsArray || typeof(IEnumerable).IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t)) return false;
        return (t.Namespace ?? "") == SyncNamespace;
    }

    private static string Format(object v, int depth)
    {
        if (v == null) return "—";
        if (v is bool bo) return bo ? "true" : "false";

        Type t = v.GetType();
        if (t.IsPrimitive)
        {
            if (v is float fl) return fl.ToString("0.######", CultureInfo.InvariantCulture);
            if (v is double db) return db.ToString("0.######", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        if (t.IsEnum) return v.ToString();
        if (v is string s) return s.Length == 0 ? "\"\"" : s;
        if (v is Delegate) return BasisEditorLocalization.Get("editor.networkInfo.callback");
        if (v is UnityEngine.Object uo) return uo != null ? $"{uo.name} ({t.Name})" : BasisEditorLocalization.Get("editor.common.none");

        if (v is float3 f3) return $"({Num(f3.x)}, {Num(f3.y)}, {Num(f3.z)})";
        if (v is quaternion qm) return $"({Num(qm.value.x)}, {Num(qm.value.y)}, {Num(qm.value.z)}, {Num(qm.value.w)})";
        if (v is Vector3 v3) return $"({Num(v3.x)}, {Num(v3.y)}, {Num(v3.z)})";
        if (v is Vector2 v2) return $"({Num(v2.x)}, {Num(v2.y)})";
        if (v is Vector4 v4) return $"({Num(v4.x)}, {Num(v4.y)}, {Num(v4.z)}, {Num(v4.w)})";
        if (v is Quaternion qq) return $"({Num(qq.x)}, {Num(qq.y)}, {Num(qq.z)}, {Num(qq.w)})";
        if (v is Color c) return $"RGBA({Num(c.r)}, {Num(c.g)}, {Num(c.b)}, {Num(c.a)})";

        if (v is Array arr) return FormatList(arr, arr.Length);
        if (v is ICollection col) return FormatList(col, col.Count);
        if (v is IEnumerable en) return FormatList(en, -1);

        if (t.IsValueType && (t.Namespace ?? "") == SyncNamespace && depth < 2) return FormatStruct(v, t, depth);

        try { return v.ToString(); } catch { return t.Name; }
    }

    private static string FormatStruct(object v, Type t, int depth)
    {
        var sb = new StringBuilder();
        foreach (FieldInfo f in t.GetFields(InstanceFlags))
        {
            if (sb.Length > 0) sb.Append(", ");
            object fv;
            try { fv = f.GetValue(v); } catch { fv = null; }
            sb.Append(PrettyName(f.Name)).Append('=').Append(Format(fv, depth + 1));
        }
        return sb.Length == 0 ? t.Name : sb.ToString();
    }

    private static string FormatList(IEnumerable en, int count)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(count >= 0 ? count.ToString() : "?").Append(']');

        var inner = new StringBuilder();
        bool simpleOnly = true;
        int i = 0;
        foreach (object item in en)
        {
            if (!IsSimple(item)) { simpleOnly = false; break; }
            if (i >= MaxElements) { inner.Append(", …"); break; }
            if (i > 0) inner.Append(", ");
            inner.Append(Format(item, 2));
            i++;
        }
        if (simpleOnly && inner.Length > 0) sb.Append(" {").Append(inner).Append('}');
        return sb.ToString();
    }

    private static bool IsSimple(object v)
    {
        if (v == null) return true;
        Type t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is string) return true;
        string ns = t.Namespace ?? "";
        if (ns.StartsWith("Unity.Mathematics") || ns.StartsWith("UnityEngine")) return true;
        return t.IsValueType && ns == SyncNamespace;
    }

    private static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

    private static string PrettyName(string raw)
    {
        if (raw.Length > 0 && raw[0] == '<')
        {
            int end = raw.IndexOf('>');
            if (end > 1) raw = raw.Substring(1, end - 1);
        }
        return ObjectNames.NicifyVariableName(raw);
    }

    private static Foldout SubFoldout(string text, bool open)
    {
        var f = new Foldout { text = text, value = open };
        f.style.marginLeft = 2;
        f.style.marginTop = 1;
        Label lbl = f.Q<Label>();
        if (lbl != null)
        {
            lbl.style.color = new StyleColor(Muted);
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.fontSize = 11;
        }
        return f;
    }

    private static VisualElement Row()
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.justifyContent = Justify.SpaceBetween;
        row.style.paddingTop = 1;
        row.style.paddingBottom = 1;
        row.style.paddingLeft = 4;
        row.style.paddingRight = 4;
        return row;
    }

    private static Label Note(string text)
    {
        var l = new Label(text);
        l.style.color = new StyleColor(Muted);
        l.style.whiteSpace = WhiteSpace.Normal;
        l.style.unityFontStyleAndWeight = FontStyle.Italic;
        l.style.marginLeft = 4;
        l.style.marginTop = 2;
        l.style.marginBottom = 2;
        return l;
    }
}
