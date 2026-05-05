// Editor/BasisDocInspector_UI.cs
// Universal, DB-aware inspector with drill-down navigation.
// Shows API Reference for any MonoBehaviour with docs in BasisDocDB; falls back
// to default inspector otherwise. Filters out Unity/engine members.
// Drill-down: any field/property whose declared type is "ours" can be inspected
// in-place; navigation is tracked via a breadcrumb stack so snippets and live
// values follow the chain (e.g. BasisLocalPlayer.Instance.LocalAvatarDriver.X).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CanEditMultipleObjects]
[CustomEditor(typeof(MonoBehaviour), true, isFallback = true)]
public class BasisDocInspector_UI : Editor
{
    private const string DbAssetPath = "Packages/com.basis.framework.editor/Editor/Documentation Engine/BasisDocDB.asset";

    // ---------- Data ----------
    private BasisDocDB _db;
    private List<MemberRow> _all = new();
    private List<MemberRow> _view = new();
    private readonly List<NavFrame> _nav = new();

    // ---------- UI ----------
    private VisualElement _breadcrumbs;
    private ToolbarSearchField _search;
    private ToolbarToggle _fltFields, _fltProps, _fltMethods, _fltEvents, _fltInherited, _fltGroup;
    private ListView _list;
    private ScrollView _detail;

    private bool _useApiPanel;

    // ---------- Theme ----------
    private static readonly Color ColBorder = new(0, 0, 0, 0.12f);
    private static readonly Color ColMuted = new(1f, 1f, 1f, 0.75f);
    private static readonly Color ColCard = new(0.1f, 0.1f, 0.1f, 0.06f);
    private static readonly Color ColChipBg = new(0.2f, 0.2f, 0.2f, 0.25f);
    private static readonly Color ColChipOn = new(0.18f, 0.5f, 0.9f, 0.25f);
    private static readonly Color ColCrumb = new(0.3f, 0.6f, 0.95f, 0.6f);

    // ---------- Navigation frame ----------
    // One frame per level of drill-down. Live() evaluates the chain at runtime
    // (returns null if any link is null) so live values work without re-walking.
    private sealed class NavFrame
    {
        public Type Type;             // type whose API we're showing
        public string AccessExpr;     // code-snippet expression: "Type.Instance.Foo"
        public string CrumbLabel;     // short display label
        public bool MayBeNull;        // chain may be null at runtime
        public Func<object> Live;     // returns the live object, or null
        public string AccessorHint;   // "Singleton" | "Provider.Get" | "Field" | etc.
    }

    private NavFrame Current => _nav.Count > 0 ? _nav[^1] : null;

    // ---------- Row model ----------
    private class MemberRow
    {
        public MemberInfo Info;
        public string Kind;       // "Fields" | "Properties" | "Methods" | "Events"
        public string Name;
        public string TypeName;
        public string Signature;
        public string Display;
        public bool IsDrillable;  // type of this member (or its element type) is "ours"
        public Type DrillType;    // type to navigate into (== member type or collection element type)
        public string DrillSuffix; // "" | "[0]" | ".FirstOrDefault()" | ".Values.FirstOrDefault()"
        public string ConstValue;  // literal value for const fields (rendered in subtitle)

        // docs from DB
        public string Summary;
        public string Remarks;
        public string Returns;
        public string ValueDoc;
        public string Since;
        public string ObsoleteMsg;
        public string[] Platforms = Array.Empty<string>();

        public string[] ParamNames = Array.Empty<string>();
        public string[] ParamDocs = Array.Empty<string>();

        public string[] TypeParamNames = Array.Empty<string>();
        public string[] TypeParamDocs = Array.Empty<string>();

        public (string cref, string doc)[] Exceptions = Array.Empty<(string, string)>();
        public string[] See = Array.Empty<string>();
        public string[] SeeAlso = Array.Empty<string>();

        public List<string> Examples = new();

        public bool IsInherited(Type host) => Info?.DeclaringType != host;
    }

    // ---------- Inspector entry ----------
    public override VisualElement CreateInspectorGUI()
    {
        _db = AssetDatabase.LoadAssetAtPath<BasisDocDB>(DbAssetPath);
        _db?.BuildIndex();

        var hostType = target.GetType();
        _useApiPanel = ShouldHandleType(hostType);
        if (!_useApiPanel) return base.CreateInspectorGUI();

        var root = new VisualElement();
        InspectorElement.FillDefaultInspector(root, serializedObject, this);

        root.Add(Spacer(10));
        root.Add(Divider());
        root.Add(Spacer(6));

        var apiFoldout = new Foldout
        {
            text = "Basis API Reference",
            value = false
        };

        // Seed root frame
        _nav.Clear();
        _nav.Add(BuildRootFrame(hostType));

        var api = BuildApiSplitView();
        apiFoldout.Add(api);
        root.Add(apiFoldout);
        return root;
    }

    private NavFrame BuildRootFrame(Type hostType)
    {
        var pat = DiscoverHostAccessor(hostType, (Component)target);
        return new NavFrame
        {
            Type = hostType,
            AccessExpr = pat.Expr,
            CrumbLabel = hostType.Name,
            MayBeNull = pat.MayBeNull,
            Live = () => target,
            AccessorHint = pat.Hint,
        };
    }

    private bool ShouldHandleType(Type t)
    {
        if (_db == null) return false;
        if (!typeof(MonoBehaviour).IsAssignableFrom(t)) return false;
        if (!IsOurs(t, t)) return false;

        foreach (var mi in ReflectMembersForProbe(t))
            if (DbHasDocsFor(mi)) return true;
        return false;
    }

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
        return _db.FindFor(typeFull, mi.Name, kind, paramCount) != null;
    }

    // ---------- UI ----------
    private VisualElement BuildApiSplitView()
    {
        var container = new VisualElement { style = { flexDirection = FlexDirection.Column } };

        // Breadcrumb bar across the top
        _breadcrumbs = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                flexWrap = Wrap.Wrap,
                paddingLeft = 6, paddingRight = 6,
                paddingTop = 4, paddingBottom = 4,
                backgroundColor = ColCard,
                borderTopLeftRadius = 6, borderTopRightRadius = 6,
                borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                marginBottom = 4
            }
        };
        container.Add(_breadcrumbs);

        var outer = new TwoPaneSplitView(0, 90, TwoPaneSplitViewOrientation.Horizontal)
        {
            style = { minHeight = 420, height = 500 }
        };

        // LEFT: filters
        var left = new VisualElement { style = { flexDirection = FlexDirection.Column } };
        left.style.overflow = Overflow.Hidden;

        var filtersHeader = new Toolbar();
        filtersHeader.style.position = Position.Relative;
        filtersHeader.Add(new Label("Filter")
        {
            style = { unityFontStyleAndWeight = FontStyle.Bold, marginLeft = 6, marginRight = 6 }
        });
        left.Add(filtersHeader);

        var chips = new Toolbar();
        chips.style.flexDirection = FlexDirection.Column;
        chips.style.position = Position.Relative;
        _fltFields = Chip("Fields", true);
        _fltProps = Chip("Properties", true);
        _fltMethods = Chip("Methods", true);
        _fltEvents = Chip("Events", true);
        _fltInherited = Chip("Inherited", true);
        _fltGroup = Chip("Group by Type", false);
        chips.Add(_fltFields);
        chips.Add(_fltProps);
        chips.Add(_fltMethods);
        chips.Add(_fltEvents);
        chips.Add(new ToolbarSpacer());
        chips.Add(_fltInherited);
        chips.Add(_fltGroup);
        left.Add(chips);

        outer.Add(left);

        var inner = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal);
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
                borderTopColor = ColBorder, borderBottomColor = ColBorder, borderLeftColor = ColBorder, borderRightColor = ColBorder
            }
        };
        _list.makeItem = () =>
        {
            var row = new VisualElement
            {
                style = { paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6, flexDirection = FlexDirection.Row }
            };
            var icon = new Label { name = "drillIcon", style = { width = 12, color = ColCrumb, marginRight = 4, unityFontStyleAndWeight = FontStyle.Bold } };
            var col = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column } };
            var title = new Label { name = "title", style = { unityFontStyleAndWeight = FontStyle.Bold } };
            var sub = new Label { name = "sub", style = { color = ColMuted, fontSize = 11, whiteSpace = WhiteSpace.Normal } };
            col.Add(title);
            col.Add(sub);
            row.Add(icon);
            row.Add(col);
            return row;
        };
        _list.bindItem = (ve, i) =>
        {
            var row = _view[i];
            ve.Q<Label>("title").text = row.Display;
            string sub = row.Summary;
            if (!string.IsNullOrEmpty(row.ConstValue))
            {
                var cv = $"= {row.ConstValue}";
                sub = string.IsNullOrEmpty(sub) ? cv : $"{cv}  —  {sub}";
            }
            ve.Q<Label>("sub").text = sub;
            ve.Q<Label>("drillIcon").text = row.IsDrillable ? "▶" : "";
        };
        _list.selectionChanged += _ => ShowDetails(_list.selectedIndex);
        middle.Add(_list);

        _list.RegisterCallback<AttachToPanelEvent>(_ =>
        {
            var viewport = _list.Q<VisualElement>("unity-content-viewport");
            if (viewport != null)
                viewport.style.overflow = Overflow.Hidden;
        });

        inner.Add(middle);

        _detail = new ScrollView { style = { paddingLeft = 8, paddingRight = 8 } };
        _detail.style.overflow = Overflow.Hidden;
        _detail.style.position = Position.Relative;
        inner.Add(_detail);

        container.Add(outer);

        BuildData();
        ApplyFilter();
        UpdateBreadcrumbs();

        return container;
    }

    // ---------- Navigation ----------
    private void NavigatePush(MemberRow row)
    {
        if (row.DrillType == null) return;

        var parent = Current;
        bool isStatic = MemberIsStatic(row.Info);
        var suffix = row.DrillSuffix ?? "";
        var localInfo = row.Info;

        string baseExpr = isStatic
            ? $"{row.Info.DeclaringType?.Name ?? row.DrillType.Name}.{row.Name}"
            : $"{parent.AccessExpr}.{row.Name}";

        string newExpr = baseExpr + suffix;

        Func<object> newLive;
        if (isStatic)
        {
            newLive = () => ApplyDrillSuffix(GetMemberValue(localInfo, null), suffix);
        }
        else
        {
            var parentLive = parent.Live;
            newLive = () =>
            {
                var p = parentLive?.Invoke();
                if (p == null) return null;
                var raw = GetMemberValue(localInfo, p);
                return ApplyDrillSuffix(raw, suffix);
            };
        }

        var crumbLabel = string.IsNullOrEmpty(suffix) ? row.Name : $"{row.Name}{suffix}";
        var frame = new NavFrame
        {
            Type = row.DrillType,
            AccessExpr = newExpr,
            CrumbLabel = crumbLabel,
            MayBeNull = true,
            Live = newLive,
            AccessorHint = isStatic
                ? (string.IsNullOrEmpty(suffix) ? "StaticField" : "StaticCollectionElement")
                : (string.IsNullOrEmpty(suffix) ? "MemberAccess" : "CollectionElement"),
        };
        _nav.Add(frame);
        OnFrameChanged();
    }

    private void NavigateTo(int index)
    {
        if (index < 0 || index >= _nav.Count - 1) return;
        _nav.RemoveRange(index + 1, _nav.Count - index - 1);
        OnFrameChanged();
    }

    private void OnFrameChanged()
    {
        BuildData();
        ApplyFilter();
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        _breadcrumbs.Clear();

        for (int i = 0; i < _nav.Count; i++)
        {
            var frame = _nav[i];
            var idx = i;
            var crumb = new Button(() => NavigateTo(idx))
            {
                text = frame.CrumbLabel,
                style =
                {
                    backgroundColor = i == _nav.Count - 1 ? ColChipOn : ColChipBg,
                    paddingLeft = 6, paddingRight = 6, paddingTop = 1, paddingBottom = 1,
                    marginLeft = 0, marginRight = 0,
                    borderTopWidth = 0, borderBottomWidth = 0, borderLeftWidth = 0, borderRightWidth = 0,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    fontSize = 11
                }
            };
            crumb.tooltip = frame.AccessExpr;
            _breadcrumbs.Add(crumb);

            if (i < _nav.Count - 1)
            {
                _breadcrumbs.Add(new Label(" › ")
                {
                    style = { color = ColMuted, marginLeft = 2, marginRight = 2, unityTextAlign = TextAnchor.MiddleCenter }
                });
            }
        }
    }

    // ---------- Data build & filter ----------
    private void BuildData()
    {
        _all.Clear();
        var t = Current.Type;

        foreach (var mi in ReflectMembers(t))
            _all.Add(ToRow(mi, t));

        _view = _all;
        _list.itemsSource = _view;
    }

    private IEnumerable<MemberInfo> ReflectMembers(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        // Stop walking once we hit a Unity engine type (or object). For drilled types
        // (non-MonoBehaviour) we walk all the way up.
        for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(object); cur = cur.BaseType)
        {
            // For non-MonoBehaviour drill targets, stop once we leave "our" namespaces
            if (cur != t && !IsOurs(t, cur)) break;

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
            ResolveDrillTarget(fi.FieldType, out row.DrillType, out row.DrillSuffix);
            row.IsDrillable = row.DrillType != null;

            if (fi.IsLiteral && !fi.IsInitOnly)
            {
                try { row.ConstValue = FormatLiteral(fi.GetRawConstantValue()); }
                catch { row.ConstValue = null; }
            }
        }
        else if (mi is PropertyInfo pi)
        {
            row.TypeName = NiceType(pi.PropertyType);
            row.Display = $"Property • {row.TypeName}  {row.Name}";
            if (pi.CanRead)
            {
                ResolveDrillTarget(pi.PropertyType, out row.DrillType, out row.DrillSuffix);
                row.IsDrillable = row.DrillType != null;
            }
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

        if (_db != null)
        {
            var kindSingle = row.Kind.TrimEnd('s');
            var typeFull = mi.DeclaringType?.FullName;
            var paramCount = (mi as MethodInfo)?.GetParameters().Length ?? 0;

            var hit = _db.FindFor(typeFull, mi.Name, kindSingle, paramCount);
            if (hit != null)
            {
                row.Summary = NullIfEmpty(hit.Summary);
                row.Remarks = NullIfEmpty(hit.Remarks);
                row.Returns = NullIfEmpty(hit.Returns);
                row.ValueDoc = NullIfEmpty(hit.Value);
                row.Since = NullIfEmpty(hit.Since);
                row.ObsoleteMsg = NullIfEmpty(hit.ObsoleteMsg);
                row.Platforms = hit.Platforms?.ToArray() ?? Array.Empty<string>();

                row.ParamNames = hit.ParamNames?.ToArray() ?? Array.Empty<string>();
                row.ParamDocs = hit.ParamDocs?.ToArray() ?? Array.Empty<string>();

                row.TypeParamNames = hit.TypeParamNames?.ToArray() ?? Array.Empty<string>();
                row.TypeParamDocs = hit.TypeParamDocs?.ToArray() ?? Array.Empty<string>();

                if (hit.ExceptionCrefs != null && hit.ExceptionDocs != null)
                {
                    var n = Math.Min(hit.ExceptionCrefs.Count, hit.ExceptionDocs.Count);
                    var list = new List<(string, string)>();
                    for (int i = 0; i < n; i++)
                        list.Add((hit.ExceptionCrefs[i], hit.ExceptionDocs[i]));
                    row.Exceptions = list.ToArray();
                }

                row.See = hit.SeeCrefs?.ToArray() ?? Array.Empty<string>();
                row.SeeAlso = hit.SeeAlsoCrefs?.ToArray() ?? Array.Empty<string>();

                if (hit.Examples != null && hit.Examples.Count > 0)
                    row.Examples = new List<string>(hit.Examples);
            }
        }

        if (string.IsNullOrEmpty(row.Summary) && mi is FieldInfo f2)
        {
            var tt = f2.GetCustomAttribute<TooltipAttribute>();
            if (tt != null) row.Summary = tt.tooltip;
        }

        if (row.IsInherited(hostType))
            row.Display += $"    (from {mi.DeclaringType?.Name})";

        return row;
    }

    private void ApplyFilter()
    {
        var host = Current.Type;
        var q = _search?.value ?? "";

        bool showFields = _fltFields?.value ?? true;
        bool showProps = _fltProps?.value ?? true;
        bool showMethods = _fltMethods?.value ?? true;
        bool showEvents = _fltEvents?.value ?? true;
        bool showInherited = _fltInherited?.value ?? true;
        bool group = _fltGroup?.value ?? false;

        var filtered = _all.Where(r =>
        {
            if (!showInherited && r.IsInherited(host)) return false;
            if (!showFields && r.Kind == "Fields") return false;
            if (!showProps && r.Kind == "Properties") return false;
            if (!showMethods && r.Kind == "Methods") return false;
            if (!showEvents && r.Kind == "Events") return false;

            if (string.IsNullOrWhiteSpace(q)) return true;
            return (r.Display?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (r.Summary?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        });

        IOrderedEnumerable<MemberRow> ordered;
        if (group)
        {
            // Group by declaring type, with current host at top, then up the chain.
            // We approximate "depth" by walking the host's BaseType chain.
            var depth = new Dictionary<Type, int>();
            int d = 0;
            for (var cur = host; cur != null; cur = cur.BaseType) depth[cur] = d++;
            ordered = filtered
                .OrderBy(r => depth.TryGetValue(r.Info?.DeclaringType, out var dv) ? dv : 999)
                .ThenBy(r => r.Kind)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ordered = filtered
                .OrderBy(r => r.Kind)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }

        _view = ordered.ToList();
        _list.itemsSource = _view;
        _list.Rebuild();

        if (_view.Count > 0) _list.selectedIndex = 0;
        else _detail?.Clear();
    }

    // ---------- Detail panel ----------
    private void ShowDetails(int index)
    {
        _detail.Clear();

        AppendTypeInfoHeader();

        if (index < 0 || index >= _view.Count) return;
        var d = _view[index];

        var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap } };
        titleRow.Add(Title(d.Name));
        titleRow.Add(Spacer(6));
        titleRow.Add(ChipTag(d.Kind.TrimEnd('s')));

        // Modifier chips
        if (d.Info != null)
        {
            if (MemberIsStatic(d.Info)) titleRow.Add(ChipTag("static"));
            if (d.Info is FieldInfo fiMod)
            {
                if (fiMod.IsLiteral) titleRow.Add(ChipTag("const"));
                else if (fiMod.IsInitOnly) titleRow.Add(ChipTag("readonly"));
            }
            else if (d.Info is MethodInfo miMod)
            {
                if (miMod.IsAbstract) titleRow.Add(ChipTag("abstract"));
                else if (miMod.IsVirtual && !miMod.IsFinal) titleRow.Add(ChipTag("virtual"));
                if (miMod.GetBaseDefinition() != miMod) titleRow.Add(ChipTag("override"));
            }
        }

        if (d.IsInherited(Current.Type) && d.Info?.DeclaringType != null)
            titleRow.Add(ChipTag($"from {d.Info.DeclaringType.Name}", new Color(0.5f, 0.5f, 0.5f, 0.35f)));

        if (!string.IsNullOrEmpty(d.ObsoleteMsg))
            titleRow.Add(ChipTag("Obsolete", new Color(0.9f, 0.4f, 0.3f, 0.4f)));

        if (!string.IsNullOrEmpty(d.Since))
            titleRow.Add(ChipTag($"Since {d.Since}", new Color(0.3f, 0.8f, 0.5f, 0.35f)));

        if (d.Platforms is { Length: > 0 })
            foreach (var p in d.Platforms) titleRow.Add(ChipTag(p));

        _detail.Add(titleRow);

        if (!string.IsNullOrEmpty(d.ConstValue))
            _detail.Add(Subtle($"Const value: {d.ConstValue}"));

        if (!string.IsNullOrEmpty(d.Signature))
            _detail.Add(Subtle($"Signature: {d.Signature}"));
        else if (!string.IsNullOrEmpty(d.TypeName))
            _detail.Add(Subtle($"Type: {d.TypeName}"));

        if (!string.IsNullOrEmpty(d.Summary))
            _detail.Add(CardBlock("Summary", d.Summary));

        if (!string.IsNullOrEmpty(d.Remarks))
            _detail.Add(CardBlock("Remarks", d.Remarks));

        if (d.Info is MethodInfo)
        {
            if (d.TypeParamNames.Length > 0)
                _detail.Add(ListBlock("Type Parameters", d.TypeParamNames, d.TypeParamDocs));

            if (d.ParamNames.Length > 0)
                _detail.Add(ListBlock("Parameters", d.ParamNames, d.ParamDocs));

            if (!string.IsNullOrEmpty(d.Returns) && d.TypeName != "void")
                _detail.Add(CardBlock("Returns", d.Returns));
        }
        else if (d.Info is PropertyInfo && !string.IsNullOrEmpty(d.ValueDoc))
        {
            _detail.Add(CardBlock("Value", d.ValueDoc));
        }

        if (d.Exceptions.Length > 0)
            _detail.Add(ExceptionBlock("Exceptions", d.Exceptions.Select(e => (e.cref, e.doc)).ToArray()));

        if (d.See.Length > 0 || d.SeeAlso.Length > 0)
        {
            var links = new List<string>();
            if (d.See.Length > 0) links.AddRange(d.See);
            if (d.SeeAlso.Length > 0) links.AddRange(d.SeeAlso.Select(s => s + " (see also)"));
            _detail.Add(BulletBlock("Related", links));
        }

        if (d.Examples.Count > 0)
        {
            for (int i = 0; i < d.Examples.Count; i++)
            {
                var label = d.Examples.Count == 1 ? "Example" : $"Example {i + 1}";
                _detail.Add(ColorizedCodeBlock(label, d.Examples[i]));
            }
        }

        var snippet = GenerateSnippet(d);
        if (!string.IsNullOrEmpty(snippet))
            _detail.Add(ColorizedCodeBlock("How to call", snippet, showCopyButton: true));

        // Inspect button — drill into this member's type (or collection element type)
        if (d.IsDrillable && d.DrillType != null)
        {
            var label = string.IsNullOrEmpty(d.DrillSuffix)
                ? $"▶  Inspect {NiceType(d.DrillType)}"
                : $"▶  Inspect element ({NiceType(d.DrillType)}) via {d.Name}{d.DrillSuffix}";
            var btn = new Button(() => NavigatePush(d)) { text = label };
            btn.style.marginTop = 4;
            _detail.Add(btn);
        }

        // Live values: evaluate from the navigation chain
        if (d.Info is FieldInfo fi)
        {
            if (TryLive(() => GetMemberValue(fi, ResolveInstance(fi)), out var val))
                _detail.Add(CardBlock("Current Value", val));
        }
        else if (d.Info is PropertyInfo pi && pi.CanRead)
        {
            if (TryLive(() => GetMemberValue(pi, ResolveInstance(pi)), out var val))
                _detail.Add(CardBlock("Current Value", val));
        }

        // Invoke button (parameterless instance/static methods only). Requires a live instance for non-static.
        if (d.Info is MethodInfo m && m.GetParameters().Length == 0)
        {
            var inst = m.IsStatic ? null : ResolveInstance(m);
            var canInvoke = m.IsStatic || inst != null;
            var btn = new Button(() =>
            {
                try { m.Invoke(inst, null); }
                catch (Exception ex) { Debug.LogException(ex); }
            })
            {
                text = canInvoke
                    ? (Application.isPlaying ? "Invoke" : "Invoke (enter Play Mode)")
                    : "Invoke (instance unavailable)"
            };
            btn.SetEnabled(canInvoke && Application.isPlaying);
            _detail.Add(btn);
        }
    }

    // Returns the live "this" for the member, walking the nav chain. Null for static.
    private object ResolveInstance(MemberInfo mi)
    {
        if (MemberIsStatic(mi)) return null;
        return Current?.Live?.Invoke();
    }

    // Type-info header — shown above member details to give context about the
    // currently inspected type (especially when drilled in past the host).
    private void AppendTypeInfoHeader()
    {
        var t = Current?.Type;
        if (t == null) return;

        var inner = new VisualElement();
        var line1 = new Label($"Now viewing: {NiceType(t)}")
        {
            style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12 }
        };
        inner.Add(line1);

        // Source path — what got us here
        if (!string.IsNullOrEmpty(Current.AccessExpr))
        {
            inner.Add(new Label($"Path: {Current.AccessExpr}")
            {
                style = { color = ColMuted, fontSize = 10, whiteSpace = WhiteSpace.Normal }
            });
        }

        // Alternate singleton accessor — when a non-host frame's type also has its own
        // static Instance, the user can use it directly instead of the chain.
        if (_nav.Count > 1 && TryFindInstanceMember(t, t, out var altName, out _))
        {
            var altExpr = $"{t.Name}.{altName}";
            if (!string.Equals(altExpr, Current.AccessExpr, StringComparison.Ordinal))
            {
                inner.Add(new Label($"Equivalent: {altExpr}")
                {
                    style = { color = ColMuted, fontSize = 10, whiteSpace = WhiteSpace.Normal }
                });
            }
        }

        // Type docs from DB
        var typeDoc = _db?.FindType(t.FullName);
        if (typeDoc != null && !string.IsNullOrEmpty(typeDoc.Summary))
        {
            inner.Add(new Label(typeDoc.Summary)
            {
                style = { whiteSpace = WhiteSpace.Normal, marginTop = 3 }
            });
        }

        // Base + interfaces. Skip "framework noise" bases that don't help the reader.
        var bits = new List<string>();
        if (t.BaseType != null && !IsTrivialBaseType(t.BaseType))
            bits.Add($"extends {NiceType(t.BaseType)}");
        var ifaces = t.GetInterfaces().Where(i => IsOursType(i)).Select(NiceType).ToArray();
        if (ifaces.Length > 0) bits.Add($"implements {string.Join(", ", ifaces)}");
        if (bits.Count > 0)
            inner.Add(Subtle(string.Join("  •  ", bits)));

        Card(inner);
    }

    // ---------- Filtering helpers: keep our code, drop Unity/engine stuff ----------
    private static readonly HashSet<string> NameBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren",
        "GetComponentInParent", "GetComponentsInParent",
        "transform", "gameObject", "tag", "name", "hideFlags",
        "renderer", "particleSystem", "rigidbody", "rigidbody2D",
        "camera", "light", "animation", "constantForce", "collider",
        "collider2D", "HingeJoint", "networkView"
    };

    private static bool IsUnityFramework(MemberInfo mi)
    {
        var dt = mi.DeclaringType;
        if (dt == null) return false;
        var ns = dt.Namespace ?? "";
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
        if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return true;
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
        var hostAsm = hostType.Assembly;
        var declType = miOrType is MemberInfo m ? m.DeclaringType : (Type)miOrType;
        declType ??= hostType;
        var declAsm = declType.Assembly;
        if (declAsm != null && declAsm == hostAsm) return true;

        var ns = declType.Namespace ?? "";
        if (ns.StartsWith("Basis", StringComparison.Ordinal)) return true;
        return false;
    }

    // Is this type "ours" — declared in a Basis assembly/namespace and not a primitive/Unity
    // engine type. Used directly for member types and for collection element types.
    private static bool IsOursType(Type t)
    {
        if (t == null) return false;
        if (t.IsPrimitive || t.IsEnum) return false;
        if (t == typeof(string) || t == typeof(decimal)) return false;

        var ns = t.Namespace ?? "";
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("System", StringComparison.Ordinal)) return false;

        var asmName = t.Assembly.GetName().Name ?? "";
        if (asmName.StartsWith("Basis", StringComparison.Ordinal)) return true;
        if (ns.StartsWith("Basis", StringComparison.Ordinal)) return true;
        return false;
    }

    // Resolve the drill-into target for a member's declared type. For plain "ours"
    // types, the target is the type itself with empty suffix. For collections whose
    // element type is "ours", the target is the element type and the suffix
    // indexes/extracts a representative element.
    private static void ResolveDrillTarget(Type memberType, out Type drillType, out string suffix)
    {
        drillType = null; suffix = "";
        if (memberType == null) return;

        // Direct drill — type is itself "ours" (works even if the type happens to
        // implement IEnumerable, since we're after that type's own API).
        if (IsOursType(memberType))
        {
            drillType = memberType;
            return;
        }

        // T[] — drill into element if "ours"
        if (memberType.IsArray)
        {
            var el = memberType.GetElementType();
            if (IsOursType(el)) { drillType = el; suffix = "[0]"; }
            return;
        }

        // Dictionary<K, V> → V (most useful: a registry keyed by Id)
        if (memberType.IsGenericType)
        {
            var def = memberType.GetGenericTypeDefinition();
            var args = memberType.GetGenericArguments();
            if (def == typeof(Dictionary<,>) && args.Length == 2 && IsOursType(args[1]))
            {
                drillType = args[1];
                suffix = ".Values.FirstOrDefault()";
                return;
            }
        }

        // IEnumerable<T> family — pull the first element
        var elementType = GetEnumerableElementType(memberType);
        if (elementType != null && IsOursType(elementType))
        {
            drillType = elementType;
            suffix = ".FirstOrDefault()";
        }
    }

    private static Type GetEnumerableElementType(Type t)
    {
        if (t == null || t == typeof(string)) return null;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return t.GetGenericArguments()[0];
        foreach (var it in t.GetInterfaces())
            if (it.IsGenericType && it.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return it.GetGenericArguments()[0];
        return null;
    }

    // Apply the suffix to a live value: [0] indexes an array, .FirstOrDefault() takes
    // the first item of an IEnumerable, .Values.FirstOrDefault() works on dictionaries.
    private static object ApplyDrillSuffix(object value, string suffix)
    {
        if (value == null || string.IsNullOrEmpty(suffix)) return value;

        if (suffix == "[0]" && value is Array arr)
            return arr.Length > 0 ? arr.GetValue(0) : null;

        if (suffix == ".FirstOrDefault()" && value is IEnumerable seq)
        {
            foreach (var item in seq) return item;
            return null;
        }

        if (suffix == ".Values.FirstOrDefault()" && value is IDictionary dict)
        {
            foreach (var v in dict.Values) return v;
            return null;
        }

        return null;
    }

    private static string FormatLiteral(object v)
    {
        return v switch
        {
            null => "null",
            string s => $"\"{s}\"",
            char c => $"'{c}'",
            bool b => b ? "true" : "false",
            _ => v.ToString()
        };
    }

    private static bool IsTrivialBaseType(Type t) =>
        t == typeof(object) || t == typeof(ValueType) || t == typeof(Enum) ||
        t == typeof(MonoBehaviour) || t == typeof(Behaviour) ||
        t == typeof(Component) || t == typeof(UnityEngine.Object) ||
        t == typeof(MulticastDelegate) || t == typeof(Delegate);

    private static bool ShouldIncludeMember(Type hostType, MemberInfo mi)
    {
        if (IsUnityFramework(mi)) return false;
        if (IsBlockedByName(mi)) return false;
        if (!IsOurs(hostType, mi)) return false;
        return true;
    }

    // ---------- Small UI helpers ----------
    private static VisualElement Divider() => new VisualElement
    {
        style = { height = 1, backgroundColor = new Color(0,0,0,0.2f) }
    };

    private static VisualElement Spacer(float px) => new VisualElement { style = { height = px } };

    private static Label Title(string text) => new Label(text)
    {
        style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 13, marginTop = 6, marginBottom = 2 }
    };

    private static Label Subtle(string text) => new Label(text)
    {
        style = { color = ColMuted, fontSize = 11, marginBottom = 4, whiteSpace = WhiteSpace.Normal }
    };

    private static Label BlockHeader(string text) => new Label(text)
    {
        style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 2 }
    };

    private VisualElement CardBlock(string title, string body)
    {
        var inner = new VisualElement();
        inner.Add(BlockHeader(title));
        var lbl = new Label(body) { style = { whiteSpace = WhiteSpace.Normal } };
        inner.Add(lbl);
        Card(inner);
        return inner;
    }

    private VisualElement BulletBlock(string title, IEnumerable<string> items)
    {
        var inner = new VisualElement();
        inner.Add(BlockHeader(title));
        foreach (var it in items)
            inner.Add(new Label("• " + it) { style = { whiteSpace = WhiteSpace.Normal } });
        Card(inner);
        return inner;
    }

    private VisualElement ListBlock(string title, string[] names, string[] docs)
    {
        var inner = new VisualElement();
        inner.Add(BlockHeader(title));
        for (int i = 0; i < names.Length; i++)
        {
            var doc = (i < docs.Length) ? docs[i] : "";
            inner.Add(new Label($"• {names[i]} — {doc}") { style = { whiteSpace = WhiteSpace.Normal } });
        }
        Card(inner);
        return inner;
    }

    private VisualElement ExceptionBlock(string title, (string cref, string doc)[] items)
    {
        var inner = new VisualElement();
        inner.Add(BlockHeader(title));
        foreach (var (cref, doc) in items)
        {
            var line = string.IsNullOrEmpty(cref) ? $"• {doc}" : $"• {cref} — {doc}";
            inner.Add(new Label(line) { style = { whiteSpace = WhiteSpace.Normal } });
        }
        Card(inner);
        return inner;
    }

    private VisualElement ColorizedCodeBlock(string title, string code, bool showCopyButton = true)
    {
        var wrap = new VisualElement();
        wrap.Add(BlockHeader(title));
        var tf = new TextField { multiline = true, value = code };
        tf.isReadOnly = true;
        tf.style.whiteSpace = WhiteSpace.Normal;
        tf.style.unityTextAlign = TextAnchor.UpperLeft;
        tf.style.marginTop = 4;
        tf.style.height = Mathf.Clamp(40 + code.Length / 2, 60, 260);
        wrap.Add(tf);
        if (showCopyButton)
            wrap.Add(new Button(() => EditorGUIUtility.systemCopyBuffer = code) { text = "Copy code" });
        Card(wrap);
        return wrap;
    }

    private ToolbarToggle Chip(string text, bool value)
    {
        var t = new ToolbarToggle { text = text, value = value };
        t.style.height = 20;
        t.RegisterValueChangedCallback(_ => ApplyFilter());
        t.style.unityTextAlign = TextAnchor.MiddleLeft;
        t.style.backgroundColor = value ? ColChipOn : ColChipBg;
        t.RegisterCallback<ChangeEvent<bool>>(e => { t.style.backgroundColor = e.newValue ? ColChipOn : ColChipBg; });
        return t;
    }

    private VisualElement ChipTag(string text, Color? c = null)
    {
        var tag = new Label(text)
        {
            style =
            {
                backgroundColor = c ?? ColChipBg,
                unityTextAlign = TextAnchor.MiddleCenter,
                paddingLeft = 6, paddingRight = 6, paddingTop = 2, paddingBottom = 2,
                marginLeft = 4, marginRight = 0,
                borderTopLeftRadius = 999, borderTopRightRadius = 999,
                borderBottomLeftRadius = 999, borderBottomRightRadius = 999,
                fontSize = 10
            }
        };
        return tag;
    }

    private void Card(VisualElement content)
    {
        var card = new VisualElement
        {
            style =
            {
                marginTop = 6, marginBottom = 8,
                paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6,
                backgroundColor = ColCard,
                borderTopLeftRadius = 8, borderTopRightRadius = 8,
                borderBottomLeftRadius = 8, borderBottomRightRadius = 8
            }
        };
        card.Add(content);
        _detail.Add(card);
    }

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string NiceType(Type t)
    {
        if (t == null) return "void";
        if (t == typeof(void)) return "void";
        if (t == typeof(int)) return "int";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t.IsArray) return NiceType(t.GetElementType()) + "[]";
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
            var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0 ? "params " : "";
            var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
            return $"{mod}{NiceType(t)} {p.Name}";
        }));
        return $"{NiceType(m.ReturnType)} {m.Name}({parms})";
    }

    // ---------- Member shape helpers ----------
    private static Type MemberValueType(MemberInfo mi) => mi switch
    {
        FieldInfo f => f.FieldType,
        PropertyInfo p => p.PropertyType,
        EventInfo e => e.EventHandlerType,
        MethodInfo m => m.ReturnType,
        _ => null
    };

    private static bool MemberIsStatic(MemberInfo mi) => mi switch
    {
        FieldInfo f => f.IsStatic,
        PropertyInfo p => (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false,
        MethodInfo m => m.IsStatic,
        EventInfo e => (e.AddMethod ?? e.RemoveMethod)?.IsStatic ?? false,
        _ => false
    };

    private static object GetMemberValue(MemberInfo mi, object instance) => mi switch
    {
        FieldInfo f => f.GetValue(instance),
        PropertyInfo p => p.CanRead ? p.GetValue(instance, null) : null,
        _ => null
    };

    // ---------- Accessor discovery ----------
    private sealed class AccessorPattern
    {
        public string Expr;
        public bool MayBeNull;
        public string Hint;
        public override string ToString() => Expr;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    private sealed class AccessorTemplateAttribute : Attribute
    {
        public string Template { get; }
        public bool MayBeNull { get; }
        public string Hint { get; }
        public AccessorTemplateAttribute(string template, bool mayBeNull = true, string hint = "Attribute")
        {
            Template = template; MayBeNull = mayBeNull; Hint = hint;
        }
    }

    private static readonly Dictionary<(Type decl, Type host), AccessorPattern> _accessorCache = new();
    private static readonly Dictionary<Type, List<Type>> _descendantCache = new();

    // Common static field/property names that frameworks use for "the one true instance"
    private static readonly string[] InstanceMemberNames =
    {
        "Instance", "LocalInstance", "Singleton", "Current", "Active", "Main", "Default"
    };

    private static List<Type> Descendants(Type t)
    {
        if (_descendantCache.TryGetValue(t, out var cached)) return cached;
        var list = new List<Type>();
        foreach (var asm in AssembliesFor(t))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var u in types)
            {
                if (u == t) continue;
                if (!u.IsClass) continue;
                if (t.IsAssignableFrom(u)) list.Add(u);
            }
        }
        _descendantCache[t] = list;
        return list;
    }

    private static IEnumerable<Assembly> AssembliesFor(Type t)
    {
        var a = t.Assembly;
        yield return a;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == a) continue;
            var n = asm.GetName().Name ?? "";
            if (n.StartsWith("Basis", StringComparison.Ordinal)) yield return asm;
        }
    }

    // Try to find a static member named Instance/etc. on `searchOn` whose value type
    // is assignable to `requiredCompatibleWith`. If `requireExactType` is set, the
    // member's type must equal that type (used when looking at the type itself).
    private static bool TryFindInstanceMember(Type searchOn, Type requiredCompatibleWith,
                                              out string memberName, out Type memberType)
    {
        const BindingFlags FB = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                | BindingFlags.FlattenHierarchy;

        foreach (var name in InstanceMemberNames)
        {
            var f = searchOn.GetField(name, FB);
            if (f != null && requiredCompatibleWith.IsAssignableFrom(f.FieldType))
            {
                memberName = name; memberType = f.FieldType; return true;
            }
            var p = searchOn.GetProperty(name, FB);
            if (p != null && p.GetMethod != null && requiredCompatibleWith.IsAssignableFrom(p.PropertyType))
            {
                memberName = name; memberType = p.PropertyType; return true;
            }
        }
        memberName = null; memberType = null;
        return false;
    }

    // Best singleton accessor for `decl`, given the runtime `host` (may be null).
    // Strategy:
    //   1. host has its own Instance returning `host` (or descendant) → host.Instance
    //   2. decl itself has Instance returning decl → decl.Instance
    //   3. Some descendant D of decl has Instance returning D → D.Instance (most-derived
    //      assignable to host preferred when host is provided)
    //   4. Any ancestor A of decl has Instance returning A → ((decl)A.Instance)
    private static bool TryGetSingletonAccessor(Type decl, Type host, out AccessorPattern pat)
    {
        // 1. host.Instance
        if (host != null && host != decl && decl.IsAssignableFrom(host))
        {
            if (TryFindInstanceMember(host, host, out var name, out _))
            {
                pat = new AccessorPattern
                {
                    Expr = $"{host.Name}.{name}",
                    MayBeNull = true,
                    Hint = $"Singleton (via {host.Name})"
                };
                return true;
            }
        }

        // 2. decl.Instance (direct, including inherited static thanks to FlattenHierarchy)
        if (TryFindInstanceMember(decl, decl, out var n2, out _))
        {
            pat = new AccessorPattern
            {
                Expr = $"{decl.Name}.{n2}",
                MayBeNull = true,
                Hint = "Singleton"
            };
            return true;
        }

        // 3. Descendant with its own Instance — prefer host or its ancestors when applicable
        var descendants = Descendants(decl);
        Type bestDesc = null;
        string bestName = null;
        foreach (var d in descendants)
        {
            if (TryFindInstanceMember(d, d, out var dn, out _))
            {
                // Prefer the one closest to host
                if (host != null && d == host) { bestDesc = d; bestName = dn; break; }
                if (bestDesc == null) { bestDesc = d; bestName = dn; }
            }
        }
        if (bestDesc != null)
        {
            // The expression yields a `bestDesc`; if decl == bestDesc no cast needed.
            // Otherwise the value is assignable to decl, so we can use it directly when
            // accessing members declared on decl/its base.
            pat = new AccessorPattern
            {
                Expr = $"{bestDesc.Name}.{bestName}",
                MayBeNull = true,
                Hint = bestDesc == decl ? "Singleton" : $"Singleton (via {bestDesc.Name})"
            };
            return true;
        }

        // 4. Ancestor chain — if a base type has Instance returning the base, cast
        for (var cur = decl.BaseType; cur != null && cur != typeof(object) && cur != typeof(MonoBehaviour); cur = cur.BaseType)
        {
            if (TryFindInstanceMember(cur, cur, out var an, out _))
            {
                pat = new AccessorPattern
                {
                    Expr = $"(({decl.Name}){cur.Name}.{an})",
                    MayBeNull = true,
                    Hint = $"Singleton (cast from {cur.Name})"
                };
                return true;
            }
        }

        pat = null; return false;
    }

    private static bool IsSeqOf(Type seqType, Type t)
    {
        if (seqType == t.MakeArrayType()) return true;
        if (!typeof(IEnumerable).IsAssignableFrom(seqType)) return false;
        if (seqType.IsGenericType && seqType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return seqType.GetGenericArguments()[0] == t;
        foreach (var it in seqType.GetInterfaces())
            if (it.IsGenericType && it.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && it.GetGenericArguments()[0] == t) return true;
        return false;
    }

    private static bool TryAttributeAccessor(Type t, out AccessorPattern pat)
    {
        var attr = t.GetCustomAttributes(typeof(AccessorTemplateAttribute), false)
                    .Cast<AccessorTemplateAttribute>().FirstOrDefault();
        if (attr != null)
        {
            var expr = attr.Template.Replace("{T}", t.Name).Replace("{var}", SafeVarName(t.Name));
            pat = new AccessorPattern { Expr = expr, MayBeNull = attr.MayBeNull, Hint = attr.Hint ?? "Attribute" };
            return true;
        }
        pat = null; return false;
    }

    private static bool TryProviderTryGet(Type target, out AccessorPattern pat)
    {
        foreach (var asm in AssembliesFor(target))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var type in types)
            {
                if (!type.IsClass) continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static); }
                catch { continue; }
                foreach (var m in methods)
                {
                    var name = m.Name;
                    if (!(name.StartsWith("TryGet", StringComparison.Ordinal) ||
                          name.StartsWith("TryResolve", StringComparison.Ordinal))) continue;
                    if (m.ReturnType != typeof(bool)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0) continue;
                    var last = ps[^1];
                    if (!last.IsOut) continue;
                    var outType = last.ParameterType.GetElementType();
                    if (outType != target) continue;

                    var args = string.Join(", ", ps.Take(ps.Length - 1).Select(p =>
                        $"/* {p.Name}: {NiceType(p.ParameterType)} */"));
                    var expr = $"{type.FullName}.{m.Name}({args}, out var {SafeVarName(target.Name)}) ? {SafeVarName(target.Name)} : null";
                    pat = new AccessorPattern { Expr = expr, MayBeNull = true, Hint = "Provider.TryGet" };
                    return true;
                }
            }
        }
        pat = null; return false;
    }

    private static bool TryProviderGet(Type target, out AccessorPattern pat)
    {
        foreach (var asm in AssembliesFor(target))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var type in types)
            {
                if (!type.IsClass) continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static); }
                catch { continue; }
                foreach (var m in methods)
                {
                    if (m.ReturnType != target) continue;
                    if (m.IsSpecialName) continue;
                    var args = string.Join(", ", m.GetParameters().Select(p =>
                        $"/* {p.Name}: {NiceType(p.ParameterType)} */"));
                    var expr = $"{type.FullName}.{m.Name}({args})";
                    pat = new AccessorPattern { Expr = expr, MayBeNull = true, Hint = "Provider.Get" };
                    return true;
                }
            }
        }
        pat = null; return false;
    }

    private static bool TryProviderEnumerable(Type target, out AccessorPattern pat)
    {
        foreach (var asm in AssembliesFor(target))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var type in types)
            {
                if (!type.IsClass) continue;
                PropertyInfo[] props;
                MethodInfo[] methods;
                try
                {
                    props = type.GetProperties(BindingFlags.Public | BindingFlags.Static);
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                }
                catch { continue; }
                foreach (var p in props)
                {
                    if (p.GetMethod == null) continue;
                    if (!IsSeqOf(p.PropertyType, target)) continue;
                    var expr = $"{type.FullName}.{p.Name}.FirstOrDefault()";
                    pat = new AccessorPattern { Expr = expr, MayBeNull = true, Hint = "Provider.Enumerable" };
                    return true;
                }
                foreach (var m in methods)
                {
                    if (!IsSeqOf(m.ReturnType, target)) continue;
                    var args = string.Join(", ", m.GetParameters().Select(p =>
                        $"/* {p.Name}: {NiceType(p.ParameterType)} */"));
                    var expr = $"{type.FullName}.{m.Name}({args}).FirstOrDefault()";
                    pat = new AccessorPattern { Expr = expr, MayBeNull = true, Hint = "Provider.Enumerable" };
                    return true;
                }
            }
        }
        pat = null; return false;
    }

    private static AccessorPattern DiscoverAccessor(Type declaringType, Type hostType)
    {
        var key = (declaringType, hostType);
        if (_accessorCache.TryGetValue(key, out var cached)) return cached;

        if (TryAttributeAccessor(declaringType, out var patAttr))
            return _accessorCache[key] = patAttr;

        if (TryGetSingletonAccessor(declaringType, hostType, out var patSingleton))
            return _accessorCache[key] = patSingleton;

        if (TryProviderTryGet(declaringType, out var patTryGet))
            return _accessorCache[key] = patTryGet;

        if (TryProviderGet(declaringType, out var patGet))
            return _accessorCache[key] = patGet;

        if (TryProviderEnumerable(declaringType, out var patSeq))
            return _accessorCache[key] = patSeq;

        var fallback = new AccessorPattern { Expr = $"GetComponent<{declaringType.Name}>()", MayBeNull = true, Hint = "GetComponent" };
        return _accessorCache[key] = fallback;
    }

    // For the root frame (the host MonoBehaviour itself): prefer using its own
    // Instance accessor, otherwise fall through to the standard discovery.
    private AccessorPattern DiscoverHostAccessor(Type host, Component target)
    {
        // If host has any of the InstanceMemberNames returning host, use it.
        if (TryFindInstanceMember(host, host, out var name, out _))
        {
            return new AccessorPattern
            {
                Expr = $"{host.Name}.{name}",
                MayBeNull = true,
                Hint = "Singleton"
            };
        }
        // Fallback to provider discovery / GetComponent
        return DiscoverAccessor(host, host);
    }

    private static string SafeVarName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "obj";
        var v = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
        if (v is "var" or "int" or "string" or "float" or "bool") v = "_" + v;
        return v;
    }

    private string GenerateSnippet(MemberRow d)
    {
        var sb = new StringBuilder();
        var declType = d.Info?.DeclaringType ?? Current.Type;
        var declName = declType.Name;

        bool isStatic = MemberIsStatic(d.Info);

        if (isStatic)
        {
            switch (d.Kind)
            {
                case "Fields":
                    sb.AppendLine("// read");
                    sb.AppendLine($"var value = {declName}.{d.Name};");
                    if (d.Info is FieldInfo fStat && !fStat.IsLiteral && !fStat.IsInitOnly)
                    {
                        sb.AppendLine();
                        sb.AppendLine("// write");
                        sb.AppendLine($"{declName}.{d.Name} = /* new {d.TypeName} */;");
                    }
                    return sb.ToString();

                case "Properties":
                    sb.AppendLine("// read");
                    sb.AppendLine($"var value = {declName}.{d.Name};");
                    if ((d.Info as PropertyInfo)?.SetMethod != null)
                        sb.AppendLine($"{declName}.{d.Name} = /* new {d.TypeName} */;");
                    return sb.ToString();

                case "Methods":
                {
                    var mm = (MethodInfo)d.Info;
                    var ps = mm.GetParameters();
                    sb.Append($"{declName}.{mm.Name}(");
                    sb.Append(string.Join(", ", ps.Select(p =>
                    {
                        var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                        var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " :
                                  p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0 ? "params " : "";
                        return $"/* {mod}{NiceType(t)} {p.Name} */";
                    })));
                    sb.AppendLine(");");
                    return sb.ToString();
                }

                case "Events":
                    sb.AppendLine($"{declName}.{d.Name} += MyHandler;");
                    sb.AppendLine("// ... later");
                    sb.AppendLine($"{declName}.{d.Name} -= MyHandler;");
                    sb.AppendLine();
                    sb.AppendLine("void MyHandler() { /* ... */ }");
                    return sb.ToString();
            }
        }

        // Instance members — build off the current frame's chain. For inherited
        // members, point out the declaring type so users know where the member lives.
        var chain = Current.AccessExpr;
        var hint = Current.AccessorHint;
        sb.AppendLine($"// Source: {hint}  →  {chain}");

        // If member is declared on a base of the current type, surface that to the reader
        if (declType != Current.Type && declType.IsAssignableFrom(Current.Type))
            sb.AppendLine($"// (member declared on base {declName})");

        // Reference-types can be null; skip null-guard for struct chains
        if (Current.MayBeNull && !Current.Type.IsValueType)
            sb.AppendLine($"if ({chain} == null) return; // not available yet");

        switch (d.Kind)
        {
            case "Fields":
                sb.AppendLine($"var value = {chain}.{d.Name};");
                if (d.Info is FieldInfo fInst && !fInst.IsLiteral && !fInst.IsInitOnly)
                    sb.AppendLine($"{chain}.{d.Name} = /* new {d.TypeName} */;");
                break;

            case "Properties":
            {
                var canSet = (d.Info as PropertyInfo)?.SetMethod != null;
                sb.AppendLine($"var value = {chain}.{d.Name};");
                if (canSet) sb.AppendLine($"{chain}.{d.Name} = /* new {d.TypeName} */;");
                break;
            }

            case "Methods":
            {
                var mm = (MethodInfo)d.Info;
                var ps = mm.GetParameters();
                sb.Append($"{chain}.{mm.Name}(");
                sb.Append(string.Join(", ", ps.Select(p =>
                {
                    var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                    var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " :
                              p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0 ? "params " : "";
                    return $"/* {mod}{NiceType(t)} {p.Name} */";
                })));
                sb.AppendLine(");");
                break;
            }

            case "Events":
                sb.AppendLine($"{chain}.{d.Name} += MyHandler;");
                sb.AppendLine("// ... later");
                sb.AppendLine($"{chain}.{d.Name} -= MyHandler;");
                sb.AppendLine();
                sb.AppendLine("void MyHandler() { /* ... */ }");
                break;
        }

        return sb.ToString();
    }

    // Try to evaluate a getter and pretty-print the value. Returns false on exceptions
    // OR when the chain is null (drilled into a member of a not-yet-initialized parent).
    private bool TryLive(Func<object> getter, out string text)
    {
        try
        {
            var v = getter();
            if (v == null)
            {
                text = "null";
                return true;
            }
            text = v switch
            {
                string s => $"\"{s}\"",
                UnityEngine.Object uo => $"{uo.name} ({uo.GetType().Name})",
                _ => v.ToString()
            };
            return true;
        }
        catch (TargetInvocationException tex) when (tex.InnerException != null)
        {
            text = $"<error: {tex.InnerException.GetType().Name}>";
            return true;
        }
        catch
        {
            text = null;
            return false;
        }
    }
}
