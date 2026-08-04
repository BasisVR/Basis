#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Basis.Shims.Editor
{
    /// <summary>
    /// One editable whitelist: the parse of the collection in the file, the entries as they were
    /// read, and the entries as the user has them now. Edits are staged here rather than written on
    /// each keystroke, because every write to a <c>.cs</c> costs a domain reload and would throw the
    /// window away mid-edit.
    /// </summary>
    internal sealed class CilboxListEditor
    {
        public string FilePath;
        public string AssetPath;
        public string FieldName;
        public string TitleKey;

        /// <summary>Whether entries are type names or <c>Type.field</c> pairs — picks the browse flow.</summary>
        public CilboxPickKind Kind = CilboxPickKind.Type;

        /// <summary>True when this list lives in CilboxBasisCommon and every box inherits it.</summary>
        public bool Shared;

        public CilboxSourceList Source;
        public List<CilboxSourceEntry> Original = new List<CilboxSourceEntry>();
        public List<CilboxSourceEntry> Working = new List<CilboxSourceEntry>();
        public bool Expanded;
        public string PendingAdd = string.Empty;

        /// <summary>Why the list cannot be edited, or null when it can.</summary>
        public string ReadOnlyReasonKey;

        public bool Editable => Source != null && Source.RoundTrips && ReadOnlyReasonKey == null;

        public bool Dirty
        {
            get
            {
                if (Original.Count != Working.Count) return true;
                for (int i = 0; i < Working.Count; i++)
                {
                    if (!string.Equals(Original[i].Expression, Working[i].Expression, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }

        public void Load(string filePath, string assetPath, string fieldName, string titleKey, bool shared,
            CilboxPickKind kind)
        {
            FilePath = filePath;
            AssetPath = assetPath;
            FieldName = fieldName;
            TitleKey = titleKey;
            Shared = shared;
            Kind = kind;
            Reload();
        }

        /// <summary>Whether a value is already an entry, so browsing cannot add it twice.</summary>
        public bool Contains(string value)
        {
            foreach (CilboxSourceEntry entry in Working)
            {
                if (string.Equals(entry.Resolved, value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Appends a value unless it is already listed. Returns false when it was a duplicate.</summary>
        public bool TryAdd(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            if (Contains(value)) return false;
            Working.Add(BasisCilboxPermissionSource.NewEntry(Source, value));
            return true;
        }

        public void Reload()
        {
            Source = null;
            Original.Clear();
            Working.Clear();
            ReadOnlyReasonKey = null;

            if (string.IsNullOrEmpty(FilePath))
            {
                ReadOnlyReasonKey = "sdk.cilbox.readonly.noSource";
                return;
            }

            Source = BasisCilboxPermissionSource.ReadList(FilePath, FieldName);
            if (Source == null)
            {
                ReadOnlyReasonKey = "sdk.cilbox.readonly.unparsed";
                return;
            }
            if (!Source.RoundTrips)
            {
                ReadOnlyReasonKey = "sdk.cilbox.readonly.formatting";
            }

            Original = BasisCilboxPermissionSource.Snapshot(Source);
            Working = BasisCilboxPermissionSource.Snapshot(Source);
        }

        public void Revert()
        {
            Working = new List<CilboxSourceEntry>(Original.Count);
            foreach (CilboxSourceEntry entry in Original)
            {
                Working.Add(new CilboxSourceEntry
                {
                    LeadingLines = new List<string>(entry.LeadingLines),
                    Indent = entry.Indent,
                    Expression = entry.Expression,
                    TrailingComment = entry.TrailingComment,
                    Resolved = entry.Resolved,
                    IsPlainLiteral = entry.IsPlainLiteral,
                });
            }
        }

        /// <summary>
        /// Re-reads the list from disk, confirms it still matches what the edits were made against,
        /// then writes the working entries over it. Re-reading is what makes it safe to apply
        /// several lists from the same file in one pass — the earlier write has already moved the
        /// offsets the later list was parsed at.
        /// </summary>
        public bool Apply(out string error)
        {
            error = null;
            if (!Dirty) return true;

            if (!Editable)
            {
                error = BasisCilboxLoc.Get("sdk.cilbox.error.notEditable");
                return false;
            }

            CilboxSourceList fresh = BasisCilboxPermissionSource.ReadList(FilePath, FieldName);
            if (fresh == null || !fresh.RoundTrips)
            {
                error = BasisCilboxLoc.Get("sdk.cilbox.error.rereadFailed");
                return false;
            }

            if (fresh.Entries.Count != Original.Count)
            {
                error = BasisCilboxLoc.Get("sdk.cilbox.error.changedOnDisk");
                return false;
            }
            for (int i = 0; i < fresh.Entries.Count; i++)
            {
                if (!string.Equals(fresh.Entries[i].Expression, Original[i].Expression, StringComparison.Ordinal))
                {
                    error = BasisCilboxLoc.Get("sdk.cilbox.error.changedOnDisk");
                    return false;
                }
            }

            List<CilboxSourceEntry> snapshot = BasisCilboxPermissionSource.Snapshot(fresh);
            fresh.Entries = Working;

            if (!BasisCilboxPermissionSource.TryWrite(FilePath, fresh, snapshot, out string writeError))
            {
                error = writeError;
                return false;
            }
            return true;
        }
    }

    /// <summary>A method whitelist entry: one type, and the methods pinned on it.</summary>
    internal sealed class CilboxMethodEditor
    {
        public string TypeExpression;
        public CilboxSourceMethodEntry Entry;
        public CilboxListEditor List;
        public bool Shared;

        /// <summary>The compiled type this entry restricts, for browsing its methods.</summary>
        public Type ResolvedType;

        /// <summary>Resolved from the compiled dictionary, so it is right even when the source is not editable.</summary>
        public List<string> LiveMethods = new List<string>();

        public bool BlocksEverything => LiveMethods.Count == 0;
    }

    /// <summary>Localization shorthand for the Cilbox window.</summary>
    internal static class BasisCilboxLoc
    {
        public static string Get(string key) => Basis.Editor.Localization.BasisEditorLocalization.Get(key);

        public static string Get(string key, params object[] args) =>
            Basis.Editor.Localization.BasisEditorLocalization.Get(key, args);
    }

    /// <summary>
    /// The editing tab for one sandbox. Shows what the box inherits from the shared list and what
    /// it adds on its own, and lets both be edited — an entry added to the shared list changes all
    /// three boxes, which the page says out loud rather than leaving to be discovered.
    /// </summary>
    internal sealed class BasisCilboxBoxPage : BasisEditorTabPage
    {
        private readonly CilboxBoxKind _kind;

        private CilboxBoxInfo _box;
        private readonly List<CilboxListEditor> _lists = new List<CilboxListEditor>();
        private readonly List<CilboxMethodEditor> _methods = new List<CilboxMethodEditor>();

        private string _search = string.Empty;
        private string _error;
        private bool _methodsExpanded;
        private bool _denyExpanded;
        private bool _overridesExpanded;

        public BasisCilboxBoxPage(CilboxBoxKind kind)
        {
            _kind = kind;
        }

        public override string Title => BasisCilboxLoc.Get(TitleKeyFor(_kind));

        public override string Subtitle => BasisCilboxLoc.Get("sdk.cilbox.page.box.subtitle");

        private static string TitleKeyFor(CilboxBoxKind kind) =>
            kind == CilboxBoxKind.Avatar ? "sdk.cilbox.box.avatar" :
            kind == CilboxBoxKind.Prop ? "sdk.cilbox.box.prop" :
            "sdk.cilbox.box.scene";

        public override void OnEnable() => Reload();

        private bool AnyDirty
        {
            get
            {
                foreach (CilboxListEditor list in _lists) if (list.Dirty) return true;
                foreach (CilboxMethodEditor method in _methods) if (method.List != null && method.List.Dirty) return true;
                return false;
            }
        }

        public void Reload()
        {
            _error = null;
            _lists.Clear();
            _methods.Clear();

            BasisCilboxPermissionModel.Reload();
            _box = BasisCilboxPermissionModel.Box(_kind);
            if (_box == null) return;

            string commonPath = BasisCilboxPermissionModel.CommonSourcePath;
            string commonAsset = BasisCilboxPermissionModel.CommonAssetPath;

            AddList(commonPath, commonAsset, BasisCilboxPermissionModel.FieldCommonTypes, "sdk.cilbox.list.sharedTypes", true, CilboxPickKind.Type);
            AddList(_box.SourcePath, _box.AssetPath, BasisCilboxPermissionModel.FieldExtraTypes, "sdk.cilbox.list.boxTypes", false, CilboxPickKind.Type);
            AddList(commonPath, commonAsset, BasisCilboxPermissionModel.FieldCommonFields, "sdk.cilbox.list.sharedFields", true, CilboxPickKind.Field);
            AddList(_box.SourcePath, _box.AssetPath, BasisCilboxPermissionModel.FieldExtraFields, "sdk.cilbox.list.boxFields", false, CilboxPickKind.Field);

            LoadMethods(commonPath, commonAsset, BasisCilboxPermissionModel.FieldCommonMethods, BasisCilboxPermissionModel.CommonMethods, true);
            LoadMethods(_box.SourcePath, _box.AssetPath, BasisCilboxPermissionModel.FieldExtraMethods, _box.ExtraMethods, false);
        }

        private void AddList(string path, string assetPath, string field, string titleKey, bool shared,
            CilboxPickKind kind)
        {
            var editor = new CilboxListEditor();
            editor.Load(path, assetPath, field, titleKey, shared, kind);
            _lists.Add(editor);
        }

        private void LoadMethods(string path, string assetPath, string field, Dictionary<Type, HashSet<string>> live, bool shared)
        {
            List<CilboxSourceMethodEntry> parsed = string.IsNullOrEmpty(path)
                ? null
                : BasisCilboxPermissionSource.ReadMethodDictionary(path, field);

            // The compiled dictionary is the source of truth for what is shown; the parse only
            // decides whether a given entry can also be edited.
            foreach (KeyValuePair<Type, HashSet<string>> pair in live)
            {
                var editor = new CilboxMethodEditor
                {
                    TypeExpression = pair.Key.FullName ?? pair.Key.Name,
                    ResolvedType = pair.Key,
                    Shared = shared,
                };
                editor.LiveMethods = new List<string>(pair.Value);
                editor.LiveMethods.Sort(StringComparer.Ordinal);

                if (parsed != null)
                {
                    foreach (CilboxSourceMethodEntry candidate in parsed)
                    {
                        if (!MatchesType(candidate.TypeExpression, pair.Key)) continue;
                        editor.Entry = candidate;
                        if (candidate.Methods != null && candidate.Methods.RoundTrips)
                        {
                            editor.List = new CilboxListEditor
                            {
                                FilePath = path,
                                AssetPath = assetPath,
                                FieldName = field,
                                Source = candidate.Methods,
                                Original = BasisCilboxPermissionSource.Snapshot(candidate.Methods),
                                Working = BasisCilboxPermissionSource.Snapshot(candidate.Methods),
                                Shared = shared,
                            };
                        }
                        break;
                    }
                }

                _methods.Add(editor);
            }

            _methods.Sort((a, b) => string.Compare(a.TypeExpression, b.TypeExpression, StringComparison.Ordinal));
        }

        /// <summary>
        /// Matches a source <c>typeof(...)</c> argument against a resolved type. The files spell
        /// these several ways — fully qualified, <c>global::</c> prefixed, or bare where a using
        /// directive covers it — so compare on the trailing segments.
        /// </summary>
        private static bool MatchesType(string expression, Type type)
        {
            if (string.IsNullOrEmpty(expression) || type == null) return false;

            string written = expression.Replace("global::", string.Empty).Trim();
            string full = type.FullName ?? type.Name;

            if (string.Equals(written, full, StringComparison.Ordinal)) return true;
            if (string.Equals(written, type.Name, StringComparison.Ordinal)) return true;
            return full.EndsWith("." + written, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ drawing

        public override void Draw()
        {
            if (_box == null)
            {
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.error.noBox"), MessageType.Error);
                return;
            }

            DrawSummary();
            DrawPendingBar();

            if (!string.IsNullOrEmpty(_error))
            {
                BasisEditorUI.Help(_error, MessageType.Error);
            }

            DrawSearch();

            foreach (CilboxListEditor list in _lists)
            {
                DrawList(list);
            }

            DrawMethods();
            DrawHardDenies();
            DrawOverrides();
        }

        private void DrawSummary()
        {
            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.card.summary")))
            {
                BasisEditorUI.Note(BasisCilboxLoc.Get(SummaryKeyFor(_kind)));
                BasisEditorUI.Divider();

                BasisEditorUI.Row(BasisCilboxLoc.Get("sdk.cilbox.row.sharedTypes"),
                    BasisCilboxPermissionModel.CommonTypes.Count.ToString());
                BasisEditorUI.Row(BasisCilboxLoc.Get("sdk.cilbox.row.boxTypes"),
                    _box.ExtraTypes.Count.ToString());
                BasisEditorUI.Row(BasisCilboxLoc.Get("sdk.cilbox.row.pinnedTypes"),
                    _methods.Count.ToString());

                if (!string.IsNullOrEmpty(BasisCilboxPermissionModel.LoadError))
                {
                    BasisEditorUI.Help(BasisCilboxPermissionModel.LoadError, MessageType.Warning);
                }

                EditorGUILayout.BeginHorizontal();
                if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.openBoxFile")))
                {
                    Ping(_box.AssetPath);
                }
                if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.openSharedFile")))
                {
                    Ping(BasisCilboxPermissionModel.CommonAssetPath);
                }
                if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.reload")))
                {
                    if (!AnyDirty || EditorUtility.DisplayDialog(
                            BasisCilboxLoc.Get("sdk.cilbox.dialog.discard.title"),
                            BasisCilboxLoc.Get("sdk.cilbox.dialog.discard.body"),
                            BasisCilboxLoc.Get("sdk.common.dialog.yes"),
                            BasisCilboxLoc.Get("sdk.common.dialog.cancel")))
                    {
                        Reload();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private static string SummaryKeyFor(CilboxBoxKind kind) =>
            kind == CilboxBoxKind.Avatar ? "sdk.cilbox.box.avatar.summary" :
            kind == CilboxBoxKind.Prop ? "sdk.cilbox.box.prop.summary" :
            "sdk.cilbox.box.scene.summary";

        private void DrawPendingBar()
        {
            int changed = 0;
            foreach (CilboxListEditor list in _lists) if (list.Dirty) changed++;
            foreach (CilboxMethodEditor method in _methods) if (method.List != null && method.List.Dirty) changed++;
            if (changed == 0) return;

            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.card.pending")))
            {
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.pending.body", changed), MessageType.Warning);

                EditorGUILayout.BeginHorizontal();
                if (BasisEditorUI.PrimaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.apply")))
                {
                    ApplyAll();
                }
                if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.revert"), 28f))
                {
                    foreach (CilboxListEditor list in _lists) list.Revert();
                    foreach (CilboxMethodEditor method in _methods) method.List?.Revert();
                    _error = null;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void ApplyAll()
        {
            _error = null;
            var failures = new List<string>();

            foreach (CilboxListEditor list in _lists)
            {
                if (!list.Apply(out string error)) failures.Add(BasisCilboxLoc.Get(list.TitleKey) + ": " + error);
            }
            foreach (CilboxMethodEditor method in _methods)
            {
                if (method.List == null) continue;
                if (!method.List.Apply(out string error)) failures.Add(method.TypeExpression + ": " + error);
            }

            if (failures.Count > 0)
            {
                _error = string.Join("\n", failures);
                return;
            }

            AssetDatabase.Refresh();
            Reload();
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
                BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.label.search.note"));

                DrawDropTarget();
            }
        }

        /// <summary>
        /// Drop a prefab, scene object or script to work with the types it is actually made of.
        /// That is usually the real question — "will this thing run as a prop?" — and it starts
        /// from an object, not from a type name someone has to already know.
        /// </summary>
        private void DrawDropTarget()
        {
            CilboxListEditor target = null;
            foreach (CilboxListEditor list in _lists)
            {
                if (list.Kind == CilboxPickKind.Type && !list.Shared) { target = list; break; }
            }
            if (target == null || !target.Editable) return;

            List<Type> dropped = BasisCilboxDropTarget.Draw(
                BasisCilboxLoc.Get("sdk.cilbox.picker.dropHint"), out Rect area);
            if (dropped == null) return;

            if (dropped.Count == 0)
            {
                _error = BasisCilboxLoc.Get("sdk.cilbox.picker.dropNothing");
                return;
            }

            _error = null;
            Browse(target, area, dropped);
        }

        private bool Matches(string value) =>
            string.IsNullOrEmpty(_search) ||
            (value != null && value.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);

        private void DrawList(CilboxListEditor list)
        {
            int shown = 0;
            foreach (CilboxSourceEntry entry in list.Working)
            {
                if (Matches(entry.Resolved ?? entry.Expression)) shown++;
            }

            string header = BasisCilboxLoc.Get(list.TitleKey) + "  (" + list.Working.Count + ")";
            if (!string.IsNullOrEmpty(_search)) header += "  " + BasisCilboxLoc.Get("sdk.cilbox.label.matching", shown);

            bool expanded = list.Expanded;
            if (BasisEditorUI.BeginFoldout(ref expanded, header))
            {
                list.Expanded = expanded;

                if (list.Shared)
                {
                    BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.warn.shared"), MessageType.Warning);
                }
                if (!list.Editable)
                {
                    BasisEditorUI.Help(BasisCilboxLoc.Get(list.ReadOnlyReasonKey ?? "sdk.cilbox.readonly.formatting"), MessageType.Info);
                }

                int removeAt = -1;
                for (int i = 0; i < list.Working.Count; i++)
                {
                    CilboxSourceEntry entry = list.Working[i];
                    string display = entry.Resolved ?? entry.Expression;
                    if (!Matches(display)) continue;

                    EditorGUILayout.BeginHorizontal();

                    if (list.Editable && entry.IsPlainLiteral)
                    {
                        string edited = EditorGUILayout.DelayedTextField(display);
                        if (!string.Equals(edited, display, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(edited))
                        {
                            BasisCilboxPermissionSource.SetValue(entry, edited.Trim());
                        }
                    }
                    else
                    {
                        EditorGUILayout.SelectableLabel(display, GUILayout.Height(18f));
                    }

                    using (new EditorGUI.DisabledScope(!list.Editable))
                    {
                        if (BasisEditorUI.SecondaryButton("×", 18f, GUILayout.Width(24f)))
                        {
                            removeAt = i;
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    string comment = entry.TrailingCommentText;
                    if (!string.IsNullOrEmpty(comment))
                    {
                        BasisEditorUI.Note("    " + comment);
                    }
                }

                if (removeAt >= 0)
                {
                    list.Working.RemoveAt(removeAt);
                    GUI.FocusControl(null);
                }

                if (list.Editable)
                {
                    BasisEditorUI.Divider();
                    EditorGUILayout.BeginHorizontal();
                    list.PendingAdd = EditorGUILayout.TextField(list.PendingAdd);

                    Rect browseRect = GUILayoutUtility.GetRect(
                        new GUIContent(BasisCilboxLoc.Get("sdk.cilbox.button.browse")),
                        EditorStyles.miniButton, GUILayout.Width(80f), GUILayout.Height(18f));
                    BasisEditorUI.Fill(browseRect, new Color(0.31f, 0.31f, 0.31f), 6f);
                    if (GUI.Button(browseRect, BasisCilboxLoc.Get("sdk.cilbox.button.browse"), EditorStyles.miniButton))
                    {
                        Browse(list, browseRect);
                    }

                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(list.PendingAdd)))
                    {
                        if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.add"), 18f, GUILayout.Width(70f)))
                        {
                            // Typing a wildcard such as "Basis.Shims.*" is still the way to add one,
                            // so the text field stays even though browsing is the usual route.
                            list.TryAdd(list.PendingAdd);
                            list.PendingAdd = string.Empty;
                            GUI.FocusControl(null);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            else
            {
                list.Expanded = expanded;
            }
            BasisEditorUI.EndFoldout();
        }

        /// <summary>
        /// Opens the right picker for a list. A field list needs two steps — the type, then one of
        /// its fields — because an entry is a <c>Type.field</c> pair and guessing the type from a
        /// bare field name would be wrong as often as not.
        /// </summary>
        private void Browse(CilboxListEditor list, Rect anchor, List<Type> candidates = null)
        {
            if (list.Kind == CilboxPickKind.Type)
            {
                BasisCilboxPickerWindow.PickType(anchor, picked =>
                {
                    list.TryAdd(picked);
                    Host?.Repaint();
                }, list.Contains, candidates);
                return;
            }

            BasisCilboxPickerWindow.PickType(anchor, pickedType =>
            {
                Type owner = BasisCilboxPermissionModel.ResolveType(pickedType);
                if (owner == null) return;

                BasisCilboxPickerWindow.PickMember(anchor, CilboxPickKind.Field, owner, picked =>
                {
                    list.TryAdd(picked);
                    Host?.Repaint();
                }, list.Contains);
            }, null, candidates);
        }

        private void DrawMethods()
        {
            bool expanded = _methodsExpanded;
            string header = BasisCilboxLoc.Get("sdk.cilbox.list.methods") + "  (" + _methods.Count + ")";

            if (BasisEditorUI.BeginFoldout(ref expanded, header))
            {
                _methodsExpanded = expanded;
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.help.methods"), MessageType.Info);

                foreach (CilboxMethodEditor method in _methods)
                {
                    bool typeMatches = Matches(method.TypeExpression);
                    bool methodMatches = false;
                    foreach (string name in method.LiveMethods)
                    {
                        if (Matches(name)) { methodMatches = true; break; }
                    }
                    if (!typeMatches && !methodMatches) continue;

                    BasisEditorUI.Divider();

                    EditorGUILayout.BeginHorizontal();
                    BasisEditorUI.SectionTitle(method.TypeExpression);
                    if (method.Shared)
                    {
                        BasisEditorUI.Pill(BasisCilboxLoc.Get("sdk.cilbox.pill.shared"), BasisEditorUI.State.Neutral);
                    }
                    EditorGUILayout.EndHorizontal();

                    if (method.BlocksEverything)
                    {
                        BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.help.blocksAll"), MessageType.Warning);
                    }

                    if (method.List != null)
                    {
                        DrawMethodEntries(method);
                    }
                    else
                    {
                        foreach (string name in method.LiveMethods)
                        {
                            if (!Matches(name) && !typeMatches) continue;
                            BasisEditorUI.Readout("    " + name);
                        }
                        if (method.LiveMethods.Count > 0)
                        {
                            BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.readonly.formatting"));
                        }
                    }
                }
            }
            else
            {
                _methodsExpanded = expanded;
            }
            BasisEditorUI.EndFoldout();
        }

        private void DrawMethodEntries(CilboxMethodEditor method)
        {
            CilboxListEditor list = method.List;
            int removeAt = -1;

            for (int i = 0; i < list.Working.Count; i++)
            {
                CilboxSourceEntry entry = list.Working[i];
                string display = entry.Resolved ?? entry.Expression;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(12f);

                if (entry.IsPlainLiteral)
                {
                    string edited = EditorGUILayout.DelayedTextField(display);
                    if (!string.Equals(edited, display, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(edited))
                    {
                        BasisCilboxPermissionSource.SetValue(entry, edited.Trim());
                    }
                }
                else
                {
                    // nameof(...) and the typeof(...).GetProperty(...) spelling stay as written.
                    EditorGUILayout.SelectableLabel(display, GUILayout.Height(18f));
                }

                if (BasisEditorUI.SecondaryButton("×", 18f, GUILayout.Width(24f)))
                {
                    removeAt = i;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (removeAt >= 0)
            {
                list.Working.RemoveAt(removeAt);
                GUI.FocusControl(null);
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12f);
            list.PendingAdd = EditorGUILayout.TextField(list.PendingAdd);

            using (new EditorGUI.DisabledScope(method.ResolvedType == null))
            {
                Rect browseRect = GUILayoutUtility.GetRect(
                    new GUIContent(BasisCilboxLoc.Get("sdk.cilbox.button.browse")),
                    EditorStyles.miniButton, GUILayout.Width(80f), GUILayout.Height(18f));
                BasisEditorUI.Fill(browseRect, new Color(0.31f, 0.31f, 0.31f), 6f);
                if (GUI.Button(browseRect, BasisCilboxLoc.Get("sdk.cilbox.button.browse"), EditorStyles.miniButton))
                {
                    BasisCilboxPickerWindow.PickMember(browseRect, CilboxPickKind.Method, method.ResolvedType,
                        picked =>
                        {
                            list.TryAdd(picked);
                            Host?.Repaint();
                        }, list.Contains);
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(list.PendingAdd)))
            {
                if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.button.add"), 18f, GUILayout.Width(70f)))
                {
                    list.TryAdd(list.PendingAdd);
                    list.PendingAdd = string.Empty;
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHardDenies()
        {
            bool expanded = _denyExpanded;
            if (BasisEditorUI.BeginFoldout(ref expanded, BasisCilboxLoc.Get("sdk.cilbox.card.hardDenies")))
            {
                _denyExpanded = expanded;
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.help.hardDenies"), MessageType.Info);

                foreach ((string type, string members, string reasonKey) in BasisCilboxPermissionModel.HardDenies)
                {
                    if (!Matches(type) && !Matches(members)) continue;
                    BasisEditorUI.Divider();
                    BasisEditorUI.SectionTitle(type);
                    BasisEditorUI.Readout("    " + members);
                    BasisEditorUI.Note("    " + BasisCilboxLoc.Get(reasonKey));
                }
            }
            else
            {
                _denyExpanded = expanded;
            }
            BasisEditorUI.EndFoldout();
        }

        private void DrawOverrides()
        {
            bool expanded = _overridesExpanded;
            if (BasisEditorUI.BeginFoldout(ref expanded, BasisCilboxLoc.Get("sdk.cilbox.card.overrides")))
            {
                _overridesExpanded = expanded;
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.help.overrides"), MessageType.Info);

                foreach ((string written, string actual) in BasisCilboxPermissionModel.TypeOverrides)
                {
                    if (!Matches(written) && !Matches(actual)) continue;
                    BasisEditorUI.Row(written, actual);
                }
            }
            else
            {
                _overridesExpanded = expanded;
            }
            BasisEditorUI.EndFoldout();
        }

        private static void Ping(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null) return;
            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }
    }
}
#endif
