using System.Collections.Generic;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Basis.Editor.Identity
{
    public class BasisIdentityWindow : EditorWindow
    {
        private readonly Dictionary<BasisIdentityEntry, string> edits = new Dictionary<BasisIdentityEntry, string>();
        private readonly Dictionary<BasisIdentityEntry, string> current = new Dictionary<BasisIdentityEntry, string>();
        private readonly Dictionary<BasisIdentityEntry, List<BasisIdentityRead>> reads = new Dictionary<BasisIdentityEntry, List<BasisIdentityRead>>();
        private readonly HashSet<BasisIdentityEntry> expanded = new HashSet<BasisIdentityEntry>();
        private Vector2 scroll;
        private string status;

        [MenuItem("Basis/Settings/Identity and Branding", false, 403)]
        public static void Open()
        {
            BasisIdentityWindow window = GetWindow<BasisIdentityWindow>(BasisEditorLocalization.Get("editor.identity.windowTitle"));
            window.minSize = new Vector2(520f, 400f);
        }

        private void OnEnable() => Reload();
        private void OnFocus() { if (edits.Count == 0) Reload(); }

        private void Reload()
        {
            current.Clear();
            reads.Clear();
            foreach (BasisIdentityEntry entry in BasisIdentitySettings.Entries)
            {
                current[entry] = BasisIdentitySettings.Current(entry);
                if (!entry.IsSetting) reads[entry] = BasisIdentitySettings.ReadAll(entry);
            }
        }

        private string Value(BasisIdentityEntry entry) => edits.TryGetValue(entry, out string value) ? value : current[entry] ?? string.Empty;

        private void OnGUI()
        {
            BasisEditorUI.Header(BasisEditorLocalization.Get("editor.identity.header"), BasisEditorLocalization.Get("editor.identity.subtitle"));
            scroll = BasisEditorUI.BeginPage(scroll);
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.identity.intro"));
            foreach (string group in BasisIdentitySettings.Groups)
            {
                using (BasisEditorUI.Card(BasisEditorLocalization.Get("editor.identity.group." + group + ".title")))
                {
                    BasisEditorUI.Note(BasisEditorLocalization.Get("editor.identity.group." + group + ".note"));
                    foreach (BasisIdentityEntry entry in BasisIdentitySettings.Entries)
                    {
                        if (entry.Group == group) DrawEntry(entry);
                    }
                }
            }
            BasisEditorUI.EndPage();
            DrawFooter();
        }

        private void DrawEntry(BasisIdentityEntry entry)
        {
            GUILayout.Space(6f);
            string before = Value(entry);
            string after = EditorGUILayout.DelayedTextField(BasisEditorLocalization.Get(entry.LabelKey), before);
            if (after != before)
            {
                if (after == (current[entry] ?? string.Empty)) edits.Remove(entry);
                else edits[entry] = after;
            }
            BasisEditorUI.Note(BasisEditorLocalization.Get(entry.NoteKey));
            string invalid = BasisIdentitySettings.Validate(entry, after);
            if (invalid != null) BasisEditorUI.Help(BasisEditorLocalization.Get(invalid), MessageType.Error);
            if (entry.LinkedTo != null)
            {
                BasisIdentityEntry linked = BasisIdentitySettings.Find(entry.LinkedTo);
                string linkedValue = Value(linked);
                if (linkedValue != after)
                {
                    BasisEditorUI.Help(BasisEditorLocalization.Get("editor.identity.linkMismatch", BasisEditorLocalization.Get(linked.LabelKey), linkedValue), MessageType.Warning);
                    if (BasisEditorUI.SecondaryButton(BasisEditorLocalization.Get("editor.identity.useLinked", linkedValue))) edits[entry] = linkedValue;
                }
            }
            if (entry.IsSetting) return;
            List<BasisIdentityRead> list = reads[entry];
            bool problem = false;
            foreach (BasisIdentityRead read in list)
            {
                if (!read.Missing && (read.Matches != 1 || read.Value != current[entry])) problem = true;
                if (read.Missing && !read.Target.Optional) problem = true;
            }
            if (problem) BasisEditorUI.Help(BasisEditorLocalization.Get("editor.identity.outOfSync"), MessageType.Warning);
            bool open = expanded.Contains(entry) || problem;
            bool show = EditorGUILayout.Foldout(open, BasisEditorLocalization.Get("editor.identity.locations", list.Count), true);
            if (show != open) { if (show) expanded.Add(entry); else expanded.Remove(entry); }
            if (!show) return;
            EditorGUI.indentLevel++;
            foreach (BasisIdentityRead read in list)
            {
                string state = read.Missing
                    ? BasisEditorLocalization.Get(read.Target.Optional ? "editor.identity.location.optionalMissing" : "editor.identity.location.missing")
                    : read.Matches == 1 ? read.Value
                    : BasisEditorLocalization.Get("editor.identity.location.matches", read.Matches);
                BasisEditorUI.Row(read.Target.Display, state, read.Missing && read.Target.Optional ? BasisEditorUI.State.Neutral : read.Matches == 1 && read.Value == current[entry] ? BasisEditorUI.State.Good : BasisEditorUI.State.Bad);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawFooter()
        {
            using (BasisEditorUI.Card())
            {
                if (!string.IsNullOrEmpty(status)) BasisEditorUI.Note(status);
                bool valid = true;
                foreach (KeyValuePair<BasisIdentityEntry, string> edit in edits) valid &= BasisIdentitySettings.Validate(edit.Key, edit.Value) == null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(edits.Count == 0))
                    {
                        if (BasisEditorUI.SecondaryButton(BasisEditorLocalization.Get("editor.identity.revert"), 28f)) { edits.Clear(); status = null; GUI.FocusControl(null); }
                    }
                    using (new EditorGUI.DisabledScope(edits.Count == 0 || !valid))
                    {
                        if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.identity.apply", edits.Count))) ApplyEdits();
                    }
                }
            }
        }

        private void ApplyEdits()
        {
            if (!EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.identity.confirmTitle"), BasisEditorLocalization.Get("editor.identity.confirmBody", edits.Count), BasisEditorLocalization.Get("editor.identity.apply", edits.Count), BasisEditorLocalization.Get("sdk.common.dialog.cancel"))) return;
            List<string> written = BasisIdentitySettings.Apply(edits);
            status = BasisEditorLocalization.Get("editor.identity.applied", edits.Count, written.Count);
            edits.Clear();
            GUI.FocusControl(null);
            Reload();
        }
    }
}
