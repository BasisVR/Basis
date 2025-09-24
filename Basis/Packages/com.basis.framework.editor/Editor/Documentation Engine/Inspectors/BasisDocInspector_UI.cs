// Editor/BasisDocInspector_UI.cs
// Universal, DB-aware inspector: shows API Reference for any MonoBehaviour that
// has docs in BasisDocDB; otherwise falls back to default inspector.
// Also filters out Unity/engine members so you only see your API.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(MonoBehaviour), true, isFallback = true)]
public class BasisDocInspector_UI : Editor
{
    // Path your generator writes to
    private const string DbAssetPath = "Packages/com.basis.framework.editor/Editor/Documentation Engine/BasisDocDB.asset";

    // Data
    private BasisDocDB _db;
    private List<MemberRow> _all = new();
    private List<MemberRow> _view = new();

    // UI
    private ToolbarSearchField _search;
    private ToolbarToggle _fltFields, _fltProps, _fltMethods, _fltEvents, _fltInherited;
    private ListView _list;
    private ScrollView _detail;

    // If we decide this type shouldn't use the custom panel, we fall back to default
    private bool _useApiPanel;

    // ---------- Row model ----------
    private class MemberRow
    {
        public MemberInfo Info;
        public string Kind;       // "Fields" | "Properties" | "Methods" | "Events"
        public string Name;
        public string TypeName;   // field/property type or method return type
        public string Signature;  // pretty method signature
        public string Display;    // one-line label for list

        // docs from DB
        public string Summary;
        public string Remarks;
        public string Returns;
        public string Example;
        public string[] ParamNames;
        public string[] ParamDocs;

        public bool IsInherited(Type host) => Info?.DeclaringType != host;
    }

    // ---------- Inspector entry ----------
    public override VisualElement CreateInspectorGUI()
    {
        // Load DB once here
        _db = AssetDatabase.LoadAssetAtPath<BasisDocDB>(DbAssetPath);

        var hostType = target.GetType();
        _useApiPanel = ShouldHandleType(hostType);

        if (!_useApiPanel)
        {
            return new IMGUIContainer(() => base.OnInspectorGUI());
        }

        var root = new VisualElement
        {
            style =
        {
            marginLeft = 6, marginRight = 6, marginTop = 6, marginBottom = 6
        }
        };

        var defaultIMGUI = new IMGUIContainer(() => base.OnInspectorGUI());
        root.Add(defaultIMGUI);

        // Spacing + divider
        root.Add(Spacer(10));
        root.Add(Divider());
        root.Add(Spacer(6));

        // ====== API Reference (Foldout) ======
        var apiFoldout = new Foldout
        {
            text = "Basis API Reference",
            value = false // closed by default
        };

        // Add your API content inside the foldout
        var api = BuildApiSplitView();
        apiFoldout.Add(api);

        root.Add(apiFoldout);

        return root;
    }

    private bool ShouldHandleType(Type t)
    {
        if (_db == null) return false;
        if (!typeof(MonoBehaviour).IsAssignableFrom(t)) return false;

        // Only if it's "ours" (same assembly OR allowed namespaces)
        if (!IsOurs(t, t)) return false;

        // Quick probe: does DB contain any entries for this type?
        // If your DB has a faster "HasType" API, use that. Otherwise, cheaply sample members.
        foreach (var mi in ReflectMembersForProbe(t))
        {
            if (DbHasDocsFor(mi))
                return true;
        }
        return false;
    }

    // Lighter pass used only to decide if we have any docs at all
    private IEnumerable<MemberInfo> ReflectMembersForProbe(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
        {
            foreach (var f in cur.GetFields(flags)) yield return f;
            foreach (var p in cur.GetProperties(flags)) yield return p;
            foreach (var e in cur.GetEvents(flags)) yield return e;
            foreach (var m in cur.GetMethods(flags)) if (!m.IsSpecialName) yield return m;
        }
    }

    private bool DbHasDocsFor(MemberInfo mi)
    {
        // Match logic mirrors ToRow() usage
        var kind = mi switch
        {
            FieldInfo => "Field",
            PropertyInfo => "Property",
            MethodInfo => "Method",
            EventInfo => "Event",
            _ => null
        };
        if (kind == null) return false;

        var typeFull = mi.DeclaringType?.FullName;
        var paramCount = (mi as MethodInfo)?.GetParameters().Length ?? 0;
        var hit = _db.FindFor(typeFull, mi.Name, kind, paramCount);
        return hit != null;
    }

    // ---------- Build API UI (three panes) ----------
    private VisualElement BuildApiSplitView()
    {
        // OUTER: [ Left(Filters) | Right(InnerSplit) ]
        var outer = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Horizontal)
        {
            style = { minHeight = 380, height = 420 }
        };

        // LEFT: filters only
        var left = new VisualElement { style = { flexDirection = FlexDirection.Column } };
        left.style.overflow = Overflow.Hidden;

        var filtersHeader = new Toolbar();
        filtersHeader.style.position = Position.Relative; // stacking context
        filtersHeader.Add(new Label("Filter")
        {
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold,
                marginLeft = 6, marginRight = 6
            }
        });
        left.Add(filtersHeader);

        // Filter chips
        var chips = new Toolbar();
        chips.style.position = Position.Relative;
        _fltFields = Chip("Fields", true);
        _fltProps = Chip("Properties", true);
        _fltMethods = Chip("Methods", true);
        _fltEvents = Chip("Events", true);
        _fltInherited = Chip("Inherited", true);
        chips.Add(_fltFields);
        chips.Add(_fltProps);
        chips.Add(_fltMethods);
        chips.Add(_fltEvents);
        chips.Add(new ToolbarSpacer());
        chips.Add(_fltInherited);
        left.Add(chips);

        outer.Add(left);

        // RIGHT of OUTER: an inner split that holds [ Middle(List) | Right(Details) ]
        var inner = new TwoPaneSplitView(0, 320, TwoPaneSplitViewOrientation.Horizontal);
        outer.Add(inner);

        // MIDDLE: search + list
        var middle = new VisualElement { style = { flexDirection = FlexDirection.Column, flexGrow = 1 } };
        middle.style.overflow = Overflow.Hidden;

        var searchBar = new Toolbar();
        searchBar.style.position = Position.Relative;
        _search = new ToolbarSearchField { style = { flexGrow = 1 } };
        _search.RegisterValueChangedCallback(_ => ApplyFilter());
        searchBar.Add(_search);
        middle.Add(searchBar);

        _list = new ListView
        {
            virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
            selectionType = SelectionType.Single,
            style =
            {
                flexGrow = 1,
                overflow = Overflow.Hidden,
                borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                borderTopColor = new Color(0,0,0,0.1f),
                borderBottomColor = new Color(0,0,0,0.1f),
                borderLeftColor = new Color(0,0,0,0.1f),
                borderRightColor = new Color(0,0,0,0.1f)
            }
        };
        _list.makeItem = () => new Label
        {
            style =
            {
                position = Position.Relative,
                unityTextAlign = TextAnchor.UpperLeft,
                whiteSpace = WhiteSpace.Normal,
                paddingLeft = 8, paddingRight = 8, paddingTop = 4, paddingBottom = 4
            }
        };
        _list.bindItem = (ve, i) =>
        {
            var label = (Label)ve;
            var row = _view[i];
            label.text = row.Display;
            label.tooltip = row.Summary;
        };
        _list.onSelectionChange += _ => ShowDetails(_list.selectedIndex);
        middle.Add(_list);

        // Clip the ListView internal viewport once it’s mounted
        _list.RegisterCallback<AttachToPanelEvent>(_ =>
        {
            var viewport = _list.Q<VisualElement>("unity-content-viewport");
            if (viewport != null)
                viewport.style.overflow = Overflow.Hidden;
        });

        inner.Add(middle);

        // RIGHT: details
        _detail = new ScrollView { style = { paddingLeft = 8, paddingRight = 8 } };
        _detail.style.overflow = Overflow.Hidden;
        _detail.style.position = Position.Relative;
        inner.Add(_detail);

        // Build data now
        BuildData();
        ApplyFilter();

        return outer;
    }

    private ToolbarToggle Chip(string text, bool value)
    {
        var t = new ToolbarToggle { text = text, value = value };
        t.RegisterValueChangedCallback(_ => ApplyFilter());
        return t;
    }

    // ---------- Data build & filter ----------
    private void BuildData()
    {
        _all.Clear();
        var host = target.GetType();

        foreach (var mi in ReflectMembers(host))
            _all.Add(ToRow(mi, host));

        _view = _all;
        _list.itemsSource = _view;
    }

    private IEnumerable<MemberInfo> ReflectMembers(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
        {
            foreach (var f in cur.GetFields(flags))
            {
                if (f.IsSpecialName) continue;
                if (Attribute.IsDefined(f, typeof(HideInInspector))) continue;
                if (!ShouldIncludeMember(t, f)) continue;
                yield return f;
            }
            foreach (var p in cur.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.GetMethod == null && p.SetMethod == null) continue;
                if (!ShouldIncludeMember(t, p)) continue;
                yield return p;
            }
            foreach (var e in cur.GetEvents(flags))
            {
                if (!ShouldIncludeMember(t, e)) continue;
                yield return e;
            }

            foreach (var m in cur.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                if (!ShouldIncludeMember(t, m)) continue;
                yield return m;
            }
        }
    }

    private MemberRow ToRow(MemberInfo mi, Type hostType)
    {
        var row = new MemberRow
        {
            Info = mi,
            Kind = mi switch
            {
                FieldInfo => "Fields",
                PropertyInfo => "Properties",
                MethodInfo => "Methods",
                EventInfo => "Events",
                _ => "Other"
            },
            Name = mi.Name
        };

        if (mi is FieldInfo fi)
        {
            row.TypeName = NiceType(fi.FieldType);
            row.Display = $"Field • {row.TypeName}  {row.Name}";
        }
        else if (mi is PropertyInfo pi)
        {
            row.TypeName = NiceType(pi.PropertyType);
            row.Display = $"Property • {row.TypeName}  {row.Name}";
        }
        else if (mi is MethodInfo mm)
        {
            row.TypeName = NiceType(mm.ReturnType);
            row.Signature = BuildSignature(mm);
            row.Display = $"Method • {row.Signature}";
        }
        else if (mi is EventInfo ei)
        {
            row.Display = $"Event • {ei.EventHandlerType?.Name}  {row.Name}";
        }

        // Docs from DB (match by type, member, kind, param count)
        if (_db != null)
        {
            var kindSingle = row.Kind.TrimEnd('s'); // Fields -> Field
            var typeFull = mi.DeclaringType?.FullName;
            var paramCount = (mi as MethodInfo)?.GetParameters().Length ?? 0;

            var hit = _db.FindFor(typeFull, mi.Name, kindSingle, paramCount);
            if (hit != null)
            {
                row.Summary = NullIfEmpty(hit.Summary);
                row.Remarks = NullIfEmpty(hit.Remarks);
                row.Returns = NullIfEmpty(hit.Returns);
                row.Example = NullIfEmpty(hit.Example);
                row.ParamNames = hit.ParamNames?.ToArray();
                row.ParamDocs = hit.ParamDocs?.ToArray();
            }
        }

        // Fallback: Tooltip for fields
        if (string.IsNullOrEmpty(row.Summary) && mi is FieldInfo f2)
        {
            var tt = f2.GetCustomAttribute<TooltipAttribute>();
            if (tt != null) row.Summary = tt.tooltip;
        }

        if (row.IsInherited(hostType))
            row.Display += "    (inherited)";

        return row;
    }

    private void ApplyFilter()
    {
        var host = target.GetType();
        var q = _search?.value ?? "";

        bool showFields = _fltFields?.value ?? true;
        bool showProps = _fltProps?.value ?? true;
        bool showMethods = _fltMethods?.value ?? true;
        bool showEvents = _fltEvents?.value ?? true;
        bool showInherited = _fltInherited?.value ?? true;

        _view = _all.Where(r =>
        {
            if (!showInherited && r.IsInherited(host)) return false;
            if (!showFields && r.Kind == "Fields") return false;
            if (!showProps && r.Kind == "Properties") return false;
            if (!showMethods && r.Kind == "Methods") return false;
            if (!showEvents && r.Kind == "Events") return false;

            if (string.IsNullOrWhiteSpace(q)) return true;
            return (r.Display?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (r.Summary?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        })
        .OrderBy(r => r.Kind)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        _list.itemsSource = _view;
        _list.Rebuild();

        if (_view.Count > 0)
            _list.selectedIndex = 0;
        else
            _detail?.Clear();
    }

    // ---------- Detail panel ----------
    private void ShowDetails(int index)
    {
        _detail.Clear();
        if (index < 0 || index >= _view.Count) return;
        var d = _view[index];

        _detail.Add(Title(d.Name));

        if (!string.IsNullOrEmpty(d.Signature))
            _detail.Add(Subtle($"Signature: {d.Signature}"));
        else if (!string.IsNullOrEmpty(d.TypeName))
            _detail.Add(Subtle($"Type: {d.TypeName}"));

        if (!string.IsNullOrEmpty(d.Summary))
            _detail.Add(CardBlock("Summary", d.Summary));
        if (!string.IsNullOrEmpty(d.Remarks))
            _detail.Add(CardBlock("Remarks", d.Remarks));

        if (d.Info is MethodInfo mm)
        {
            if (d.ParamNames != null && d.ParamNames.Length > 0)
            {
                var box = new VisualElement();
                box.Add(BlockHeader("Parameters"));
                for (int i = 0; i < d.ParamNames.Length; i++)
                {
                    var line = $"• {d.ParamNames[i]} — {(d.ParamDocs != null && i < d.ParamDocs.Length ? d.ParamDocs[i] : "")}";
                    box.Add(new Label(line) { style = { whiteSpace = WhiteSpace.Normal } });
                }
                Card(box);
            }
            if (!string.IsNullOrEmpty(d.Returns) && d.TypeName != "void")
                _detail.Add(CardBlock("Returns", d.Returns));
        }

        if (!string.IsNullOrEmpty(d.Example))
        {
            _detail.Add(BlockHeader("Example"));
            var tf = new TextField { multiline = true, value = d.Example };
            tf.style.height = Math.Min(240, 40 + d.Example.Length / 2);
            _detail.Add(tf);
            _detail.Add(new Button(() => EditorGUIUtility.systemCopyBuffer = d.Example) { text = "Copy example" });
            _detail.Add(Spacer(6));
        }

        // auto usage snippet
        var snippet = GenerateSnippet(d, (Component)target);
        if (!string.IsNullOrEmpty(snippet))
        {
            _detail.Add(BlockHeader("How to call"));
            var tf = new TextField { multiline = true, value = snippet };
            tf.style.height = Math.Min(240, 40 + snippet.Length / 2);
            _detail.Add(tf);
            _detail.Add(new Button(() => EditorGUIUtility.systemCopyBuffer = snippet) { text = "Copy snippet" });
        }

        // live value for fields/props
        if (d.Info is FieldInfo fi)
        {
            if (TryValue(() => fi.GetValue(target), out var val))
                _detail.Add(CardBlock("Current Value", val));
        }
        else if (d.Info is PropertyInfo pi && pi.CanRead)
        {
            if (TryValue(() => pi.GetValue(target, null), out var val))
                _detail.Add(CardBlock("Current Value", val));
        }

        // invoke button for parameterless methods
        if (d.Info is MethodInfo m && m.GetParameters().Length == 0)
        {
            var btn = new Button(() =>
            {
                try { m.Invoke(target, null); }
                catch (Exception ex) { Debug.LogException(ex); }
            })
            { text = Application.isPlaying ? "Invoke" : "Invoke (enter Play Mode)" };
            btn.SetEnabled(Application.isPlaying);
            _detail.Add(btn);
        }
    }

    // ---------- Filtering helpers: keep our code, drop Unity/enginey stuff ----------
    private static readonly HashSet<string> NameBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Methods
        "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren",
        "GetComponentInParent", "GetComponentsInParent",

        // Properties/fields commonly inherited from Unity types (legacy shorthands included)
        "transform", "gameObject", "tag", "name", "hideFlags",
        "renderer", "particleSystem", "rigidbody", "rigidbody2D",
        "camera", "light", "animation", "constantForce", "collider",
        "collider2D", "HingeJoint", "networkView"
    };

    private static bool IsUnityFramework(MemberInfo mi)
    {
        var dt = mi.DeclaringType;
        if (dt == null) return false;

        // Namespace gate
        var ns = dt.Namespace ?? "";
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
        if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return true;

        // Core Unity base classes
        return dt == typeof(MonoBehaviour)
            || dt == typeof(Component)
            || dt == typeof(Behaviour)
            || dt == typeof(GameObject)
            || dt == typeof(UnityEngine.Object);
    }

    private static bool IsBlockedByName(MemberInfo mi)
    {
        var n = mi.Name;
        if (NameBlocklist.Contains(n)) return true;
        if (n.StartsWith("GetComponent", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsOurs(Type hostType, MemberInfo miOrType)
    {
        // Accept same assembly as the inspected host
        var hostAsm = hostType.Assembly;
        var declType = miOrType as MemberInfo != null ? ((MemberInfo)miOrType).DeclaringType : (Type)miOrType;
        declType ??= hostType;
        var declAsm = declType.Assembly;
        if (declAsm != null && declAsm == hostAsm) return true;

        // Accept friendly namespaces you own
        var ns = declType.Namespace ?? "";
        if (ns.StartsWith("Basis", StringComparison.Ordinal)) return true;

        return false;
    }

    private static bool ShouldIncludeMember(Type hostType, MemberInfo mi)
    {
        if (IsUnityFramework(mi)) return false;
        if (IsBlockedByName(mi)) return false;

        // Only include "our" code (same assembly or our namespaces)
        if (!IsOurs(hostType, mi)) return false;

        return true;
    }

    // ---------- Small helpers ----------
    private static VisualElement Divider() => new VisualElement
    {
        style =
        {
            height = 1,
            backgroundColor = new Color(0,0,0,0.2f)
        }
    };

    private static VisualElement Spacer(float px) => new VisualElement { style = { height = px } };

    private static Label SectionHeader(string text) => new Label(text)
    {
        style =
        {
            unityFontStyleAndWeight = FontStyle.Bold,
            fontSize = 13,
            marginBottom = 6
        }
    };

    private static Label Title(string text) => new Label(text)
    {
        style =
        {
            unityFontStyleAndWeight = FontStyle.Bold,
            fontSize = 13,
            marginTop = 6, marginBottom = 2
        }
    };

    private static Label Subtle(string text) => new Label(text)
    {
        style =
        {
            color = new Color(1f,1f,1f,0.75f),
            fontSize = 11,
            marginBottom = 4
        }
    };

    private static Label BlockHeader(string text) => new Label(text)
    {
        style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 2 }
    };

    private VisualElement CardBlock(string title, string body)
    {
        var inner = new VisualElement();
        inner.Add(BlockHeader(title));
        inner.Add(new Label(body) { style = { whiteSpace = WhiteSpace.Normal } });
        Card(inner);
        return inner;
    }

    private void Card(VisualElement content)
    {
        var card = new VisualElement
        {
            style =
            {
                marginTop = 4, marginBottom = 6,
                paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6,
                backgroundColor = new Color(0.1f,0.1f,0.1f,0.06f),
                borderTopLeftRadius = 6, borderTopRightRadius = 6,
                borderBottomLeftRadius = 6, borderBottomRightRadius = 6
            }
        };
        card.Add(content);
        _detail.Add(card);
    }

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private string NiceType(Type t)
    {
        if (t == null) return "void";
        if (t == typeof(void)) return "void";
        if (!t.IsGenericType) return t.Name;
        var root = t.Name.Split('`')[0];
        var args = string.Join(", ", t.GetGenericArguments().Select(NiceType));
        return $"{root}<{args}>";
    }

    private string BuildSignature(MethodInfo m)
    {
        var ps = m.GetParameters();
        var parms = string.Join(", ", ps.Select(p =>
        {
            var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
            return $"{mod}{NiceType(p.ParameterType)} {p.Name}";
        }));
        return $"{NiceType(m.ReturnType)} {m.Name}({parms})";
    }

    private string GenerateSnippet(MemberRow d, Component comp)
    {
        var compType = comp.GetType().Name;
        var varName = char.ToLowerInvariant(compType[0]) + compType.Substring(1);
        var sb = new StringBuilder();

        switch (d.Kind)
        {
            case "Fields":
                {
                    var isStatic = (d.Info as FieldInfo)?.IsStatic ?? false;
                    if (isStatic)
                    {
                        sb.AppendLine($"// read");
                        sb.AppendLine($"var value = {d.Info.DeclaringType.Name}.{d.Name};");
                        sb.AppendLine();
                        sb.AppendLine($"// write");
                        sb.AppendLine($"{d.Info.DeclaringType.Name}.{d.Name} = /* new {d.TypeName} */;");
                    }
                    else
                    {
                        sb.AppendLine($"{compType} {varName} = GetComponent<{compType}>();");
                        sb.AppendLine($"var value = {varName}.{d.Name};");
                        sb.AppendLine($"{varName}.{d.Name} = /* new {d.TypeName} */;");
                    }
                    break;
                }
            case "Properties":
                {
                    var canSet = (d.Info as PropertyInfo)?.SetMethod != null;
                    sb.AppendLine($"{compType} {varName} = GetComponent<{compType}>();");
                    sb.AppendLine($"var value = {varName}.{d.Name};");
                    if (canSet) sb.AppendLine($"{varName}.{d.Name} = /* new {d.TypeName} */;");
                    break;
                }
            case "Methods":
                {
                    var mm = (MethodInfo)d.Info;
                    var ps = mm.GetParameters();
                    sb.AppendLine($"{compType} {varName} = GetComponent<{compType}>();");
                    sb.Append($"{varName}.{mm.Name}(");
                    sb.Append(string.Join(", ", ps.Select(p => $"/* {p.Name}: {NiceType(p.ParameterType)} */")));
                    sb.AppendLine(");");
                    break;
                }
            case "Events":
                {
                    sb.AppendLine($"{compType} {varName} = GetComponent<{compType}>();");
                    sb.AppendLine($"{varName}.{d.Name} += MyHandler;");
                    sb.AppendLine("// ... later");
                    sb.AppendLine($"{varName}.{d.Name} -= MyHandler;");
                    sb.AppendLine();
                    sb.AppendLine("void MyHandler() { /* ... */ }");
                    break;
                }
        }
        return sb.ToString();
    }

    private bool TryValue(Func<object> getter, out string text)
    {
        try
        {
            var v = getter();
            text = v switch
            {
                null => "null",
                string s => $"\"{s}\"",
                UnityEngine.Object uo => $"{uo.name} ({uo.GetType().Name})",
                _ => v.ToString()
            };
            return true;
        }
        catch
        {
            text = null;
            return false;
        }
    }
}
