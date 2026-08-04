#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Basis.Shims.Editor
{
    /// <summary>What a picker is being asked to choose.</summary>
    internal enum CilboxPickKind
    {
        Type,
        Field,
        Method,
    }

    /// <summary>
    /// The searchable type list behind the pickers. Built once from the loaded assemblies, because
    /// enumerating them costs a few hundred milliseconds and the whole point is that the field
    /// responds on every keystroke.
    /// </summary>
    internal static class BasisCilboxTypeIndex
    {
        private static Type[] _types;

        public static int Count
        {
            get
            {
                Build();
                return _types.Length;
            }
        }

        public static void Invalidate() => _types = null;

        private static void Build()
        {
            if (_types != null) return;

            var found = new List<Type>(1 << 14);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // A half-loaded assembly still yields the types that did resolve.
                    types = e.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null) continue;
                    if (!type.IsPublic && !type.IsNestedPublic) continue;
                    if (type.IsGenericParameter) continue;

                    string full = type.FullName;
                    if (string.IsNullOrEmpty(full)) continue;

                    // Compiler-generated closures, iterators and anonymous types.
                    if (full.IndexOf('<') >= 0) continue;

                    found.Add(type);
                }
            }

            found.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
            _types = found.ToArray();
        }

        /// <summary>
        /// Ranked substring search. A match on the short name beats a match on the namespace, and
        /// among equal ranks the shorter name wins, so "collider" offers Collider before
        /// MeshColliderCookingOptions.
        /// </summary>
        public static List<Type> Search(string query, int limit, IReadOnlyList<Type> candidates = null)
        {
            Build();
            IReadOnlyList<Type> source = candidates ?? _types;

            var scored = new List<(int Rank, int Length, Type Type)>();
            bool all = string.IsNullOrEmpty(query);

            foreach (Type type in source)
            {
                int rank;
                if (all)
                {
                    rank = 0;
                }
                else
                {
                    string name = type.Name;
                    string full = type.FullName;

                    if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) rank = 0;
                    else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) rank = 1;
                    else if (full.StartsWith(query, StringComparison.OrdinalIgnoreCase)) rank = 2;
                    else if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) rank = 3;
                    else if (full.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) rank = 4;
                    else continue;
                }

                scored.Add((rank, type.Name.Length, type));
            }

            scored.Sort((a, b) =>
            {
                int byRank = a.Rank.CompareTo(b.Rank);
                if (byRank != 0) return byRank;
                int byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0) return byLength;
                return string.Compare(a.Type.FullName, b.Type.FullName, StringComparison.Ordinal);
            });

            var result = new List<Type>(Mathf.Min(limit, scored.Count));
            for (int i = 0; i < scored.Count && result.Count < limit; i++)
            {
                result.Add(scored[i].Type);
            }
            return result;
        }

        /// <summary>
        /// Every type reachable from a dropped object: a component's own type, a GameObject's
        /// components, or the class a script asset declares.
        /// </summary>
        public static List<Type> FromObjects(UnityEngine.Object[] objects)
        {
            var types = new List<Type>();
            if (objects == null) return types;

            foreach (UnityEngine.Object item in objects)
            {
                if (item == null) continue;

                if (item is MonoScript script)
                {
                    Type declared = script.GetClass();
                    if (declared != null) Add(types, declared);
                    continue;
                }

                if (item is GameObject go)
                {
                    Add(types, typeof(GameObject));
                    foreach (Component component in go.GetComponents<Component>())
                    {
                        if (component != null) Add(types, component.GetType());
                    }
                    continue;
                }

                Add(types, item.GetType());
            }
            return types;
        }

        private static void Add(List<Type> list, Type type)
        {
            if (type != null && !list.Contains(type)) list.Add(type);
        }
    }

    /// <summary>
    /// A drop-down list with a search box, for choosing a type or one of its members instead of
    /// typing a fully qualified name by hand.
    /// </summary>
    internal sealed class BasisCilboxPickerWindow : EditorWindow
    {
        private CilboxPickKind _kind;
        private Type _owner;
        private Action<string> _onPicked;
        private Func<string, bool> _isListed;
        private List<Type> _candidates;

        private string _query = string.Empty;
        private Vector2 _scroll;
        private int _selected;
        private bool _focusQueued;

        private List<Type> _typeResults = new List<Type>();
        private List<MemberInfo> _memberResults = new List<MemberInfo>();

        /// <summary>
        /// Index chosen this frame, applied once the layout groups have been closed. Acting on the
        /// click where it happens would return out of a scroll view and close the window in the
        /// middle of its own OnGUI.
        /// </summary>
        private int _pendingAccept = -1;

        private const int Limit = 300;

        /// <summary>Opens the type list. <paramref name="onPicked"/> receives the full type name.</summary>
        public static void PickType(Rect anchor, Action<string> onPicked, Func<string, bool> isListed = null,
            List<Type> candidates = null)
        {
            Open(anchor, CilboxPickKind.Type, null, onPicked, isListed, candidates);
        }

        /// <summary>
        /// Opens the member list for a type. For fields the callback receives
        /// <c>Namespace.Type.field</c>; for methods it receives the bare method name, which is what
        /// a method restriction stores.
        /// </summary>
        public static void PickMember(Rect anchor, CilboxPickKind kind, Type owner, Action<string> onPicked,
            Func<string, bool> isListed = null)
        {
            if (owner == null) return;
            Open(anchor, kind, owner, onPicked, isListed, null);
        }

        private static void Open(Rect anchor, CilboxPickKind kind, Type owner, Action<string> onPicked,
            Func<string, bool> isListed, List<Type> candidates)
        {
            var window = CreateInstance<BasisCilboxPickerWindow>();
            window._kind = kind;
            window._owner = owner;
            window._onPicked = onPicked;
            window._isListed = isListed;
            window._candidates = candidates;
            window._focusQueued = true;
            window.Refresh();
            window.ShowAsDropDown(GUIUtility.GUIToScreenRect(anchor), new Vector2(Mathf.Max(anchor.width, 420f), 340f));
        }

        private void Refresh()
        {
            _selected = 0;
            _scroll = Vector2.zero;

            if (_kind == CilboxPickKind.Type)
            {
                _typeResults = BasisCilboxTypeIndex.Search(_query, Limit, _candidates);
                return;
            }

            _memberResults = new List<MemberInfo>();
            if (_owner == null) return;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.DeclaredOnly;

            if (_kind == CilboxPickKind.Field)
            {
                foreach (FieldInfo field in _owner.GetFields(flags))
                {
                    // A const never emits a field access, so listing one would do nothing.
                    if (field.IsLiteral) continue;
                    if (Matches(field.Name)) _memberResults.Add(field);
                }
            }
            else
            {
                foreach (MethodInfo method in _owner.GetMethods(flags))
                {
                    if (method.IsSpecialName && method.Name.StartsWith("op_", StringComparison.Ordinal)) continue;
                    if (Matches(method.Name)) _memberResults.Add(method);
                }
                foreach (ConstructorInfo constructor in _owner.GetConstructors(flags))
                {
                    if (Matches(".ctor")) _memberResults.Add(constructor);
                }
            }

            _memberResults.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        private bool Matches(string value) =>
            string.IsNullOrEmpty(_query) || value.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;

        private void OnGUI()
        {
            HandleKeys();

            using (BasisEditorUI.Card(TitleText()))
            {
                if (_kind != CilboxPickKind.Type && _owner != null)
                {
                    BasisEditorUI.Note(_owner.FullName);
                }

                GUI.SetNextControlName("cilboxPickerSearch");
                string next = EditorGUILayout.TextField(_query);
                if (!string.Equals(next, _query, StringComparison.Ordinal))
                {
                    _query = next;
                    Refresh();
                }

                if (_focusQueued && Event.current.type == EventType.Repaint)
                {
                    _focusQueued = false;
                    EditorGUI.FocusTextInControl("cilboxPickerSearch");
                }

                if (_kind == CilboxPickKind.Field)
                {
                    BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.picker.fieldNote"));
                }
            }

            if (_kind == CilboxPickKind.Type)
            {
                DrawTypes();
            }
            else
            {
                DrawMembers();
            }

            if (_pendingAccept >= 0)
            {
                int index = _pendingAccept;
                _pendingAccept = -1;
                Accept(index);
            }
        }

        private string TitleText() =>
            _kind == CilboxPickKind.Type ? BasisCilboxLoc.Get("sdk.cilbox.picker.title.type") :
            _kind == CilboxPickKind.Field ? BasisCilboxLoc.Get("sdk.cilbox.picker.title.field") :
            BasisCilboxLoc.Get("sdk.cilbox.picker.title.method");

        private void HandleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;

            int count = _kind == CilboxPickKind.Type ? _typeResults.Count : _memberResults.Count;

            switch (e.keyCode)
            {
                case KeyCode.DownArrow:
                    _selected = Mathf.Min(_selected + 1, Mathf.Max(0, count - 1));
                    e.Use();
                    Repaint();
                    break;
                case KeyCode.UpArrow:
                    _selected = Mathf.Max(_selected - 1, 0);
                    e.Use();
                    Repaint();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_selected < count)
                    {
                        _pendingAccept = _selected;
                        e.Use();
                    }
                    break;
                case KeyCode.Escape:
                    Close();
                    e.Use();
                    break;
            }
        }

        private void DrawTypes()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_typeResults.Count == 0)
            {
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.picker.noResults"), MessageType.Info);
            }

            for (int i = 0; i < _typeResults.Count; i++)
            {
                Type type = _typeResults[i];
                bool listed = _isListed != null && _isListed(type.FullName);
                if (Row(i, type.Name, type.Namespace ?? string.Empty, listed))
                {
                    _pendingAccept = i;
                }
            }

            EditorGUILayout.EndScrollView();

            BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.picker.results",
                _typeResults.Count, BasisCilboxTypeIndex.Count));
        }

        private void DrawMembers()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_memberResults.Count == 0)
            {
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.picker.noResults"), MessageType.Info);
            }

            for (int i = 0; i < _memberResults.Count; i++)
            {
                MemberInfo member = _memberResults[i];
                string detail = member is FieldInfo field
                    ? BasisCilboxPermissionModel.Pretty(field.FieldType)
                    : Signature(member);

                bool listed = _isListed != null && _isListed(ValueFor(member));
                if (Row(i, member.Name, detail, listed))
                {
                    _pendingAccept = i;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static string Signature(MemberInfo member)
        {
            var method = member as MethodBase;
            if (method == null) return string.Empty;

            ParameterInfo[] parameters = method.GetParameters();
            var parts = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parts[i] = BasisCilboxPermissionModel.Pretty(parameters[i].ParameterType);
            }

            string returns = method is MethodInfo info
                ? " : " + BasisCilboxPermissionModel.Pretty(info.ReturnType)
                : string.Empty;
            return "(" + string.Join(", ", parts) + ")" + returns;
        }

        /// <summary>Draws one result row and reports whether it was clicked.</summary>
        private bool Row(int index, string label, string detail, bool listed)
        {
            Rect rect = EditorGUILayout.BeginHorizontal(GUILayout.Height(18f));
            if (index == _selected)
            {
                BasisEditorUI.Fill(rect, new Color(BasisEditorUI.Accent.r, BasisEditorUI.Accent.g,
                    BasisEditorUI.Accent.b, 0.28f), 3f);
            }

            EditorGUILayout.LabelField(label, GUILayout.MinWidth(110f));
            EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            if (listed)
            {
                BasisEditorUI.Pill(BasisCilboxLoc.Get("sdk.cilbox.picker.alreadyListed"), BasisEditorUI.State.Good);
            }
            EditorGUILayout.EndHorizontal();

            Event e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                _selected = index;
                e.Use();
                return true;
            }
            return false;
        }

        private string ValueFor(MemberInfo member)
        {
            // A field restriction is keyed by the declaring type; a method restriction is keyed by
            // the bare name inside that type's entry.
            return _kind == CilboxPickKind.Field
                ? _owner.FullName + "." + member.Name
                : member.Name;
        }

        private void Accept(int index)
        {
            string value = _kind == CilboxPickKind.Type
                ? (index < _typeResults.Count ? _typeResults[index].FullName : null)
                : (index < _memberResults.Count ? ValueFor(_memberResults[index]) : null);

            Action<string> callback = _onPicked;
            Close();

            if (string.IsNullOrEmpty(value) || callback == null) return;

            // Deferred so this window is gone before the callback runs — a field pick opens a second
            // picker from here, and chaining one drop-down straight out of another's close is where
            // IMGUI drop-downs misbehave.
            EditorApplication.delayCall += () => callback(value);
        }
    }

    /// <summary>Drop targets and selection helpers shared by the tabs.</summary>
    internal static class BasisCilboxDropTarget
    {
        /// <summary>
        /// A dashed panel that accepts a GameObject, component or script and reports the types it
        /// yielded. Dropping a prefab is the fastest way to answer "will this thing work in a
        /// prop?" without knowing any of its type names.
        /// </summary>
        public static List<Type> Draw(string hint, out Rect rect, float height = 32f)
        {
            rect = GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));
            Event e = Event.current;
            bool hovering = rect.Contains(e.mousePosition) &&
                            (e.type == EventType.DragUpdated || e.type == EventType.DragPerform);

            BasisEditorUI.Fill(rect, hovering
                ? new Color(BasisEditorUI.Accent.r, BasisEditorUI.Accent.g, BasisEditorUI.Accent.b, 0.25f)
                : new Color(1f, 1f, 1f, 0.06f), 4f);

            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
            GUI.Label(rect, hint, style);

            if (!rect.Contains(e.mousePosition)) return null;

            if (e.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                e.Use();
                return null;
            }

            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                e.Use();
                return BasisCilboxTypeIndex.FromObjects(DragAndDrop.objectReferences);
            }

            return null;
        }
    }
}
#endif
