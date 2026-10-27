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

        /// <summary>Points the tab at a type from outside the window — see the API Reference's Cilbox section.</summary>
        public void Focus(string fullName) => Select(fullName);

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
    /// What content is allowed to do inside the Cilbox sandbox, in one window: the avatar, prop and
    /// scene whitelists as editable lists, a script check and a cross-box lookup for a single type.
    /// The written API reference lives in the Creator Guide window.
    /// </summary>
    internal sealed class BasisCilboxPermissionsWindow : BasisTabbedEditorWindow
    {
        [MenuItem("Basis/Tools/Cilbox Permissions", false, 504)]
        private static void OpenMenu() => Open();

        private static BasisCilboxPermissionsWindow Open()
        {
            var window = GetWindow<BasisCilboxPermissionsWindow>();
            window.titleContent = new GUIContent(BasisCilboxLoc.Get("sdk.cilbox.window.title"));
            window.minSize = new Vector2(560f, 420f);
            window.Show();
            return window;
        }

        /// <summary>Opens the window on the lookup tab, already pointed at a type.</summary>
        internal static void OpenForType(string fullName)
        {
            BasisCilboxPermissionsWindow window = Open();

            BasisCilboxLookupPage page = window.Page<BasisCilboxLookupPage>();
            if (page == null) return;

            page.Focus(fullName);
            window.SelectPage(page);
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
        };
    }
}
#endif
