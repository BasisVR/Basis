#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Basis.Shims.Editor
{
    /// <summary>
    /// Takes a script and reports what the sandbox will refuse, with the fix one click away.
    ///
    /// <para>The other tabs answer "what is allowed?"; this one answers "will this work?", which is
    /// the question people actually arrive with. Everything shown comes from the compiled IL of the
    /// script's method bodies, so it reflects what the code calls rather than what it declares.</para>
    /// </summary>
    internal sealed class BasisCilboxScriptPage : BasisEditorTabPage
    {
        private MonoScript _asset;
        private Type _target;
        private CilboxScriptScan _scan;

        private CilboxBoxKind _box = CilboxBoxKind.Scene;
        private bool _onlyBlocked = true;
        private string _error;

        /// <summary>Staged edits per box, so switching what you check against does not lose work.</summary>
        private readonly Dictionary<CilboxBoxKind, CilboxListEditor[]> _editors =
            new Dictionary<CilboxBoxKind, CilboxListEditor[]>();

        public override string Title => BasisCilboxLoc.Get("sdk.cilbox.page.scan");

        public override string Subtitle => BasisCilboxLoc.Get("sdk.cilbox.page.scan.subtitle");

        public override void Draw()
        {
            DrawPicker();

            if (!string.IsNullOrEmpty(_error))
            {
                BasisEditorUI.Help(_error, MessageType.Error);
            }

            if (_scan == null || _scan.ScriptType == null)
            {
                BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.scan.empty"), MessageType.Info);
                return;
            }

            DrawIssues();
            DrawTargetSelector();
            DrawPendingBar();
            DrawResults();
        }

        // ------------------------------------------------------------------ picking a script

        private void DrawPicker()
        {
            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.scan.card.script")))
            {
                BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.scan.note"));

                EditorGUILayout.BeginHorizontal();
                var picked = (MonoScript)EditorGUILayout.ObjectField(_asset, typeof(MonoScript), false);
                if (picked != _asset)
                {
                    _asset = picked;
                    SetTarget(_asset != null ? _asset.GetClass() : null,
                        _asset != null && _asset.GetClass() == null
                            ? BasisCilboxLoc.Get("sdk.cilbox.scan.noClass")
                            : null);
                }

                if (BasisEditorUI.SecondaryButton(BasisCilboxLoc.Get("sdk.cilbox.scan.fromSelection"),
                        18f, GUILayout.Width(110f)))
                {
                    UseSelection();
                }
                EditorGUILayout.EndHorizontal();

                List<Type> dropped = BasisCilboxDropTarget.Draw(
                    BasisCilboxLoc.Get("sdk.cilbox.scan.drop"), out Rect area);

                if (dropped != null && dropped.Count > 0)
                {
                    if (dropped.Count == 1)
                    {
                        SetTarget(dropped[0], null);
                    }
                    else
                    {
                        BasisCilboxPickerWindow.PickType(area, name =>
                        {
                            SetTarget(BasisCilboxPermissionModel.ResolveType(name), null);
                            Host?.Repaint();
                        }, null, dropped);
                    }
                }

                if (_target != null)
                {
                    BasisEditorUI.Row(_target.Name, _target.Namespace ?? string.Empty);
                }
            }
        }

        private void UseSelection()
        {
            foreach (UnityEngine.Object item in Selection.objects)
            {
                if (item is MonoScript script)
                {
                    _asset = script;
                    SetTarget(script.GetClass(),
                        script.GetClass() == null ? BasisCilboxLoc.Get("sdk.cilbox.scan.noClass") : null);
                    return;
                }
                if (item is MonoBehaviour behaviour)
                {
                    _asset = MonoScript.FromMonoBehaviour(behaviour);
                    SetTarget(behaviour.GetType(), null);
                    return;
                }
            }
            _error = BasisCilboxLoc.Get("sdk.cilbox.scan.noClass");
        }

        private void SetTarget(Type type, string error)
        {
            _error = error;
            _target = type;
            _scan = type != null ? BasisCilboxScriptScanner.Scan(type) : null;
        }

        // ------------------------------------------------------------------ issues

        private void DrawIssues()
        {
            if (_scan.Issues.Count == 0 && _scan.UnreadableMethods == 0) return;

            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.scan.card.issues")))
            {
                foreach (CilboxScriptIssue issue in _scan.Issues)
                {
                    string text = issue.Detail == null
                        ? BasisCilboxLoc.Get(issue.TitleKey)
                        : BasisCilboxLoc.Get(issue.TitleKey, issue.Detail);
                    BasisEditorUI.Help(text, issue.IsError ? MessageType.Warning : MessageType.Info);
                }

                if (_scan.UnreadableMethods > 0)
                {
                    BasisEditorUI.Help(
                        BasisCilboxLoc.Get("sdk.cilbox.scan.incomplete", _scan.UnreadableMethods),
                        MessageType.Warning);
                }
            }
        }

        // ------------------------------------------------------------------ target box

        private void DrawTargetSelector()
        {
            using (BasisEditorUI.Card())
            {
                var labels = new string[BasisCilboxPermissionModel.Boxes.Count];
                for (int i = 0; i < labels.Length; i++)
                {
                    labels[i] = BasisCilboxLoc.Get(BasisCilboxPermissionModel.Boxes[i].LocalizationKey);
                }

                EditorGUILayout.LabelField(BasisCilboxLoc.Get("sdk.cilbox.scan.target"));
                int current = (int)_box;
                int next = BasisEditorUI.Tabs(current, labels);
                if (next != current) _box = (CilboxBoxKind)next;

                _onlyBlocked = EditorGUILayout.ToggleLeft(
                    BasisCilboxLoc.Get("sdk.cilbox.scan.onlyBlocked"), _onlyBlocked);

                BasisEditorUI.Note(BasisCilboxLoc.Get("sdk.cilbox.scan.summary",
                    _scan.Types.Count, _scan.Fields.Count, _scan.Methods.Count));
            }
        }

        /// <summary>Loads (once) the two editable lists for a box, so fixes can be staged.</summary>
        private CilboxListEditor[] EditorsFor(CilboxBoxKind kind)
        {
            if (_editors.TryGetValue(kind, out CilboxListEditor[] cached)) return cached;

            CilboxBoxInfo info = BasisCilboxPermissionModel.Box(kind);
            var types = new CilboxListEditor();
            var fields = new CilboxListEditor();

            if (info != null)
            {
                types.Load(info.SourcePath, info.AssetPath, BasisCilboxPermissionModel.FieldExtraTypes,
                    "sdk.cilbox.list.boxTypes", false, CilboxPickKind.Type);
                fields.Load(info.SourcePath, info.AssetPath, BasisCilboxPermissionModel.FieldExtraFields,
                    "sdk.cilbox.list.boxFields", false, CilboxPickKind.Field);
            }

            cached = new[] { types, fields };
            _editors[kind] = cached;
            return cached;
        }

        private void DrawPendingBar()
        {
            int changed = 0;
            foreach (KeyValuePair<CilboxBoxKind, CilboxListEditor[]> pair in _editors)
            {
                foreach (CilboxListEditor list in pair.Value)
                {
                    if (list.Dirty) changed++;
                }
            }
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
                    foreach (KeyValuePair<CilboxBoxKind, CilboxListEditor[]> pair in _editors)
                    {
                        foreach (CilboxListEditor list in pair.Value) list.Revert();
                    }
                    _error = null;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void ApplyAll()
        {
            _error = null;
            var failures = new List<string>();

            foreach (KeyValuePair<CilboxBoxKind, CilboxListEditor[]> pair in _editors)
            {
                foreach (CilboxListEditor list in pair.Value)
                {
                    if (!list.Apply(out string error))
                    {
                        failures.Add(BasisCilboxLoc.Get(list.TitleKey) + ": " + error);
                    }
                }
            }

            if (failures.Count > 0)
            {
                _error = string.Join("\n", failures);
                return;
            }

            AssetDatabase.Refresh();
            _editors.Clear();
            BasisCilboxPermissionModel.Reload();
            if (_target != null) _scan = BasisCilboxScriptScanner.Scan(_target);
        }

        // ------------------------------------------------------------------ results

        private void DrawResults()
        {
            CilboxBoxInfo info = BasisCilboxPermissionModel.Box(_box);
            if (info == null) return;

            CilboxListEditor[] editors = EditorsFor(_box);
            CilboxListEditor typeList = editors[0];
            CilboxListEditor fieldList = editors[1];

            int blocked = 0;

            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.scan.section.types")))
            {
                foreach (CilboxReference reference in _scan.Types)
                {
                    var type = (Type)reference.Member;
                    bool allowed = BasisCilboxPermissionModel.IsTypeAllowed(info, type.FullName);
                    if (!allowed) blocked++;
                    if (_onlyBlocked && allowed) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(reference.Display, GUILayout.MinWidth(120f));
                    BasisEditorUI.Pill(
                        BasisCilboxLoc.Get(allowed ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"),
                        allowed ? BasisEditorUI.State.Good : BasisEditorUI.State.Bad);

                    if (!allowed && typeList.Editable)
                    {
                        bool staged = typeList.Contains(type.FullName);
                        using (new EditorGUI.DisabledScope(staged))
                        {
                            if (BasisEditorUI.SecondaryButton(
                                    BasisCilboxLoc.Get(staged ? "sdk.cilbox.scan.staged" : "sdk.cilbox.scan.addType"),
                                    18f, GUILayout.Width(90f)))
                            {
                                typeList.TryAdd(type.FullName);
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.scan.section.fields")))
            {
                foreach (CilboxReference reference in _scan.Fields)
                {
                    var field = (System.Reflection.FieldInfo)reference.Member;
                    CilboxVerdict verdict = BasisCilboxPermissionModel.EvaluateField(info, reference.DeclaringType, field);
                    bool allowed = verdict.IsAllowed;
                    if (!allowed) blocked++;
                    if (_onlyBlocked && allowed) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(reference.Display, GUILayout.MinWidth(120f));
                    BasisEditorUI.Pill(
                        BasisCilboxLoc.Get(allowed ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"),
                        allowed ? BasisEditorUI.State.Good : BasisEditorUI.State.Bad);

                    if (!allowed && fieldList.Editable)
                    {
                        // A field entry only means anything once its declaring type is allowed, so
                        // say that instead of offering an addition that would change nothing.
                        bool typeAllowed = BasisCilboxPermissionModel.IsTypeAllowed(info, reference.DeclaringType.FullName)
                                           || typeList.Contains(reference.DeclaringType.FullName);
                        if (!typeAllowed)
                        {
                            EditorGUILayout.LabelField(BasisCilboxLoc.Get("sdk.cilbox.scan.needsType"),
                                EditorStyles.miniLabel);
                        }
                        else
                        {
                            bool staged = fieldList.Contains(reference.Display);
                            using (new EditorGUI.DisabledScope(staged))
                            {
                                if (BasisEditorUI.SecondaryButton(
                                        BasisCilboxLoc.Get(staged ? "sdk.cilbox.scan.staged" : "sdk.cilbox.scan.addField"),
                                        18f, GUILayout.Width(90f)))
                                {
                                    fieldList.TryAdd(reference.Display);
                                }
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    if (!allowed && !string.IsNullOrEmpty(verdict.Reason))
                    {
                        BasisEditorUI.Note("    " + verdict.Reason);
                    }
                }
            }

            using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.scan.section.methods")))
            {
                foreach (CilboxReference reference in _scan.Methods)
                {
                    var method = (System.Reflection.MethodBase)reference.Member;
                    CilboxVerdict verdict = BasisCilboxPermissionModel.EvaluateMethod(info, reference.DeclaringType, method);
                    bool allowed = verdict.IsAllowed;
                    if (!allowed) blocked++;
                    if (_onlyBlocked && allowed) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(reference.Display, GUILayout.MinWidth(120f));
                    BasisEditorUI.Pill(
                        BasisCilboxLoc.Get(allowed ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"),
                        allowed ? BasisEditorUI.State.Good : BasisEditorUI.State.Bad);
                    EditorGUILayout.EndHorizontal();

                    if (!allowed)
                    {
                        // A blocked method is normally a symptom: either a parameter or return type
                        // is missing from the type list (fix it in the Types card above), or the
                        // type is method-pinned, which is edited on the box's own tab.
                        BasisEditorUI.Note("    " + verdict.Reason);
                        if (BasisCilboxPermissionModel.TryGetMethodPins(info, reference.DeclaringType, out _, out _))
                        {
                            BasisEditorUI.Note("    " + BasisCilboxLoc.Get("sdk.cilbox.scan.methodPinned",
                                BasisCilboxLoc.Get(info.LocalizationKey)));
                        }
                    }
                }
            }

            if (_scan.SameAssembly.Count > 0)
            {
                using (BasisEditorUI.Card(BasisCilboxLoc.Get("sdk.cilbox.scan.section.sameAssembly")))
                {
                    BasisEditorUI.Help(BasisCilboxLoc.Get("sdk.cilbox.scan.sameAssembly.note"), MessageType.Info);
                    foreach (Type type in _scan.SameAssembly)
                    {
                        BasisEditorUI.Readout("    " + type.FullName);
                    }
                }
            }

            if (blocked == 0)
            {
                BasisEditorUI.Help(
                    BasisCilboxLoc.Get("sdk.cilbox.scan.allOk", BasisCilboxLoc.Get(info.LocalizationKey)),
                    MessageType.Info);
            }
        }
    }
}
#endif
