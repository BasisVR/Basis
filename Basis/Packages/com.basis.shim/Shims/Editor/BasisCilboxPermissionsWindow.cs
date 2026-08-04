#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Basis.Shims.Editor
{
    /// <summary>
    /// Answers "will this work in cilbox?" for a type the author has in mind, across all three
    /// boxes at once. The per-box tabs show what a whitelist contains; this one starts from the
    /// call site instead, which is the direction the question actually gets asked in.
    /// </summary>
    internal sealed class BasisCilboxLookupPage : BasisEditorTabPage
    {
        private string _query = "UnityEngine.Transform";
        private string _memberFilter = string.Empty;
        private Type _resolved;
        private string _lastResolved;
        private bool _showMembers = true;
        private bool _hideBlocked;

        public override string Title => BasisCilboxLoc.Get("sdk.cilbox.page.lookup");

        public override string Subtitle => BasisCilboxLoc.Get("sdk.cilbox.page.lookup.subtitle");

        public override void Draw()
        {
            DrawQuery();

            if (string.IsNullOrWhiteSpace(_query)) return;

            DrawTypeVerdicts();

            if (_resolved == null)
            {
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.lookup.unresolved"), MessageType.Warning);
                return;
            }

            DrawMembers();
        }

        private void DrawQuery()
        {
            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.lookup.card")))
            {
                BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.lookup.note"));

                EditorGUILayout.BeginHorizontal();
                _query = EditorGUILayout.TextField(BasisCilboxLoc.Get("sdk.cilbox.lookup.type"), _query);

                Rect browseRect = GUILayoutUtility.GetRect(
                    new GUIContent(BasisCilboxLoc.Get("sdk.cilbox.button.browse")),
                    EditorStyles.miniButton, GUILayout.Width(80f), GUILayout.Height(18f));
                BasisEditorUI.Fill(browseRect, new Color(0.31f, 0.31f, 0.31f), 6f);
                if (GUI.Button(browseRect, BasisCilboxLoc.Get("sdk.cilbox.button.browse"), EditorStyles.miniButton))
                {
                    BasisCilboxPickerWindow.PickType(browseRect, picked =>
                    {
                        Select(picked);
                        Host?.Repaint();
                    });
                }
                EditorGUILayout.EndHorizontal();

                List<Type> dropped = BasisCilboxDropTarget.Draw(
                    BasisCilboxLoc.Get("sdk.cilbox.picker.dropHint"), out Rect area);
                if (dropped != null && dropped.Count > 0)
                {
                    if (dropped.Count == 1)
                    {
                        Select(dropped[0].FullName);
                    }
                    else
                    {
                        BasisCilboxPickerWindow.PickType(area, picked =>
                        {
                            Select(picked);
                            Host?.Repaint();
                        }, null, dropped);
                    }
                }

                if (!string.Equals(_query, _lastResolved, StringComparison.Ordinal))
                {
                    _lastResolved = _query;
                    _resolved = BasisCilboxPermissionModel.ResolveType(_query?.Trim());
                }
            }
        }

        /// <summary>Points the tab at a type, keeping the text field and the resolved type in step.</summary>
        private void Select(string fullName)
        {
            _query = fullName ?? string.Empty;
            _lastResolved = _query;
            _resolved = BasisCilboxPermissionModel.ResolveType(_query.Trim());
            GUI.FocusControl(null);
        }

        private void DrawTypeVerdicts()
        {
            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.lookup.verdict")))
            {
                string typeName = _query.Trim();
                foreach (CilboxBoxInfo box in BasisCilboxPermissionModel.Boxes)
                {
                    bool allowed = BasisCilboxPermissionModel.IsTypeAllowed(box, typeName);
                    BasisEditorUI.PillRow(
                        BasisCilboxLoc.Get(box.LocalizationKey),
                        BasisCilboxLoc.Get(allowed ? "sdk.cilbox.state.allowed" : "sdk.cilbox.state.blocked"),
                        allowed ? BasisEditorUI.State.Good : BasisEditorUI.State.Bad);

                    if (!allowed) continue;

                    string pattern = BasisCilboxPermissionModel.MatchingTypePattern(box, typeName, out bool shared);
                    if (!string.IsNullOrEmpty(pattern))
                    {
                        string source = BasisCilboxLoc.Get(shared ? "sdk.cilbox.pill.shared" : "sdk.cilbox.pill.boxOnly");
                        string via = string.Equals(pattern, typeName, StringComparison.Ordinal)
                            ? BasisCilboxLoc.Get("sdk.cilbox.lookup.viaExact", source)
                            : BasisCilboxLoc.Get("sdk.cilbox.lookup.viaWildcard", pattern, source);
                        BasisEditorUI.Note("    " + via);
                    }

                    if (_resolved != null &&
                        BasisCilboxPermissionModel.TryGetMethodPins(box, _resolved, out HashSet<string> pinned, out _))
                    {
                        BasisEditorUI.Note("    " + (pinned.Count == 0
                            ? BasisCilboxLoc.Get("sdk.cilbox.lookup.pinnedNone")
                            : BasisCilboxLoc.Get("sdk.cilbox.lookup.pinnedCount", pinned.Count)));
                    }
                    else if (_resolved != null)
                    {
                        BasisEditorUI.Note("    " + BasisCilboxLoc.Get("sdk.cilbox.lookup.notPinned"));
                    }
                }
            }
        }

        private void DrawMembers()
        {
            bool expanded = _showMembers;
            if (BasisEditorUI.BeginFoldout(ref expanded, BasisCilboxLoc.Get("sdk.cilbox.lookup.members")))
            {
                _showMembers = expanded;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(BasisCilboxLoc.Get("sdk.cilbox.label.search"), GUILayout.Width(60f));
                _memberFilter = EditorGUILayout.TextField(_memberFilter);
                _hideBlocked = EditorGUILayout.ToggleLeft(
                    BasisCilboxLoc.Get("sdk.cilbox.lookup.hideBlocked"), _hideBlocked, GUILayout.Width(150f));
                EditorGUILayout.EndHorizontal();

                DrawHeaderRow();

                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                           BindingFlags.Static | BindingFlags.DeclaredOnly;

                foreach (FieldInfo field in _resolved.GetFields(flags))
                {
                    DrawMemberRow(field.Name, box => BasisCilboxPermissionModel.EvaluateField(box, _resolved, field));
                }

                foreach (MethodInfo method in _resolved.GetMethods(flags))
                {
                    if (method.IsSpecialName && method.Name.StartsWith("op_", StringComparison.Ordinal)) continue;
                    DrawMemberRow(Signature(method), box => BasisCilboxPermissionModel.EvaluateMethod(box, _resolved, method));
                }
            }
            else
            {
                _showMembers = expanded;
            }
            BasisEditorUI.EndFoldout();
        }

        private static string Signature(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var parts = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parts[i] = BasisCilboxPermissionModel.Pretty(parameters[i].ParameterType);
            }
            return method.Name + "(" + string.Join(", ", parts) + ")";
        }

        private static void DrawHeaderRow()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(string.Empty, GUILayout.MinWidth(80f));
            foreach (CilboxBoxInfo box in BasisCilboxPermissionModel.Boxes)
            {
                EditorGUILayout.LabelField(BasisCilboxLoc.Get(box.LocalizationKey), EditorStyles.miniBoldLabel, GUILayout.Width(64f));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawMemberRow(string label, Func<CilboxBoxInfo, CilboxVerdict> evaluate)
        {
            if (!string.IsNullOrEmpty(_memberFilter) &&
                label.IndexOf(_memberFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            var verdicts = new List<CilboxVerdict>();
            bool anyAllowed = false;
            foreach (CilboxBoxInfo box in BasisCilboxPermissionModel.Boxes)
            {
                CilboxVerdict verdict = evaluate(box);
                verdicts.Add(verdict);
                if (verdict.IsAllowed) anyAllowed = true;
            }

            if (_hideBlocked && !anyAllowed) return;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.MinWidth(80f));
            foreach (CilboxVerdict verdict in verdicts)
            {
                string text;
                BasisEditorUI.State state;
                switch (verdict.Access)
                {
                    case CilboxAccess.Allowed:
                        text = BasisCilboxLoc.Get("sdk.cilbox.state.yes");
                        state = BasisEditorUI.State.Good;
                        break;
                    case CilboxAccess.DefaultAllowed:
                        text = BasisCilboxLoc.Get("sdk.cilbox.state.open");
                        state = BasisEditorUI.State.Warn;
                        break;
                    case CilboxAccess.Blocked:
                        text = BasisCilboxLoc.Get("sdk.cilbox.state.no");
                        state = BasisEditorUI.State.Bad;
                        break;
                    default:
                        text = "?";
                        state = BasisEditorUI.State.Neutral;
                        break;
                }

                EditorGUILayout.BeginHorizontal(GUILayout.Width(64f));
                BasisEditorUI.Pill(text, state);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    /// <summary>
    /// The written reference: what a cilboxed script can call, grouped by area, with an example per
    /// entry and availability answered by the sandboxes rather than asserted in prose.
    /// </summary>
    internal sealed class BasisCilboxApiPage : BasisEditorTabPage
    {
        private string _search = string.Empty;
        private readonly Dictionary<string, bool> _groupExpanded = new Dictionary<string, bool>();
        private readonly HashSet<string> _exampleShown = new HashSet<string>();
        private bool _notesExpanded = true;

        public override string Title => BasisCilboxLoc.Get("sdk.cilbox.page.api");

        public override string Subtitle => BasisCilboxLoc.Get("sdk.cilbox.page.api.subtitle");

        public override void Draw()
        {
            DrawSearch();
            DrawNotes();

            foreach (string group in BasisCilboxApiCatalog.GroupOrder)
            {
                var entries = new List<CilboxApiEntry>();
                foreach (CilboxApiEntry entry in BasisCilboxApiCatalog.Entries)
                {
                    if (entry.GroupKey != group) continue;
                    if (!MatchesSearch(entry)) continue;
                    entries.Add(entry);
                }
                if (entries.Count == 0) continue;

                if (!_groupExpanded.TryGetValue(group, out bool expanded))
                {
                    expanded = !string.IsNullOrEmpty(_search);
                }

                bool draw = BasisEditorUI.BeginFoldout(ref expanded, BasisCilboxLoc.Get(group) + "  (" + entries.Count + ")");
                _groupExpanded[group] = expanded;

                if (draw)
                {
                    foreach (CilboxApiEntry entry in entries)
                    {
                        DrawEntry(entry);
                    }
                }
                BasisEditorUI.EndFoldout();
            }
        }

        private void DrawSearch()
        {
            using (BasisEditorUI.Card())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(BasisCilboxLoc.Get("sdk.cilbox.label.search"), GUILayout.Width(60f));
                _search = EditorGUILayout.TextField(_search);
                if (!string.IsNullOrEmpty(_search) && BasisEditorUI.SecondaryButton(
                        BasisCilboxLoc.Get("sdk.cilbox.button.clear"), 18f, GUILayout.Width(60f)))
                {
                    _search = string.Empty;
                    GUI.FocusControl(null);
                }
                EditorGUILayout.EndHorizontal();
                BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.page.api.legend"));
            }
        }

        private bool MatchesSearch(CilboxApiEntry entry)
        {
            if (string.IsNullOrEmpty(_search)) return true;

            if (Contains(BasisCilboxLoc.Get(entry.TitleKey))) return true;
            if (Contains(BasisCilboxLoc.Get(entry.SummaryKey))) return true;
            if (Contains(entry.Example)) return true;
            foreach (CilboxApiRequirement requirement in entry.Requires)
            {
                if (Contains(requirement.TypeName)) return true;
                if (Contains(requirement.MemberName)) return true;
            }
            return false;
        }

        private bool Contains(string value) =>
            value != null && value.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawEntry(CilboxApiEntry entry)
        {
            BasisEditorUI.Divider();

            EditorGUILayout.BeginHorizontal();
            BasisEditorUI.SectionTitle(BasisCilboxLoc.Get(entry.TitleKey));
            GUILayout.FlexibleSpace();
            foreach (CilboxBoxInfo box in BasisCilboxPermissionModel.Boxes)
            {
                bool available = entry.AvailableIn(box);
                BasisEditorUI.Pill(BasisCilboxLoc.Get(box.LocalizationKey),
                    available ? BasisEditorUI.State.Good : BasisEditorUI.State.Bad);
            }
            EditorGUILayout.EndHorizontal();

            BasisEditorUI.Note(BasisCilboxLoc.Get(entry.SummaryKey));

            if (string.IsNullOrEmpty(entry.Example)) return;

            string key = entry.TitleKey;
            bool shown = _exampleShown.Contains(key);

            EditorGUILayout.BeginHorizontal();
            if (BasisEditorUI.SecondaryButton(
                    BasisCilboxLoc.Get(shown ? "sdk.cilbox.button.hideExample" : "sdk.cilbox.button.showExample"),
                    18f, GUILayout.Width(120f)))
            {
                if (shown) _exampleShown.Remove(key); else _exampleShown.Add(key);
            }
            if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.copy"), 18f, GUILayout.Width(70f)))
            {
                EditorGUIUtility.systemCopyBuffer = entry.Example;
            }
            EditorGUILayout.EndHorizontal();

            if (shown)
            {
                DrawCode(entry.Example);
            }
        }

        private void DrawNotes()
        {
            bool expanded = _notesExpanded;
            if (BasisEditorUI.BeginFoldout(ref expanded, BasisCilboxLoc.Get("sdk.cilbox.card.notes")))
            {
                _notesExpanded = expanded;
                foreach (CilboxApiNote note in BasisCilboxApiCatalog.Notes)
                {
                    string title = BasisCilboxLoc.Get(note.TitleKey);
                    string body = BasisCilboxLoc.Get(note.BodyKey);
                    if (!string.IsNullOrEmpty(_search) && !Contains(title) && !Contains(body) && !Contains(note.Example))
                    {
                        continue;
                    }

                    BasisEditorUI.Divider();
                    BasisEditorUI.SectionTitle(title);
                    BasisEditorUI.Note(body);
                    if (!string.IsNullOrEmpty(note.Example))
                    {
                        DrawCode(note.Example);
                    }
                }
            }
            else
            {
                _notesExpanded = expanded;
            }
            BasisEditorUI.EndFoldout();
        }

        private static void DrawCode(string code)
        {
            // SelectableLabel so the snippet can be dragged out without the copy button.
            var style = new GUIStyle(EditorStyles.textArea)
            {
                font = EditorStyles.miniFont,
                wordWrap = false,
                richText = false,
            };
            float height = style.CalcHeight(new GUIContent(code), EditorGUIUtility.currentViewWidth - 60f);
            EditorGUILayout.SelectableLabel(code, style, GUILayout.Height(height + 8f));
        }
    }

    /// <summary>
    /// What content is allowed to do inside the Cilbox sandbox, in one window: the avatar, prop and
    /// scene whitelists as editable lists, a cross-box lookup for a single type, and a reference for
    /// the API a cilboxed script is actually written against.
    /// </summary>
    internal sealed class BasisCilboxPermissionsWindow : BasisTabbedEditorWindow
    {
        [MenuItem("Basis/Tools/Cilbox Permissions", false, 504)]
        private static void Open()
        {
            var window = GetWindow<BasisCilboxPermissionsWindow>();
            window.titleContent = new GUIContent(BasisCilboxLoc.Get("sdk.cilbox.window.title"));
            window.minSize = new Vector2(560f, 420f);
            window.Show();
        }

        protected override string HeaderTitle => BasisCilboxLoc.Get("sdk.cilbox.window.title");

        protected override string HeaderSubtitle => BasisCilboxLoc.Get("sdk.cilbox.window.subtitle");

        protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
        {
            new BasisCilboxBoxPage(CilboxBoxKind.Avatar),
            new BasisCilboxBoxPage(CilboxBoxKind.Prop),
            new BasisCilboxBoxPage(CilboxBoxKind.Scene),
            new BasisCilboxScriptPage(),
            new BasisCilboxLookupPage(),
            new BasisCilboxApiPage(),
        };
    }
}
#endif
