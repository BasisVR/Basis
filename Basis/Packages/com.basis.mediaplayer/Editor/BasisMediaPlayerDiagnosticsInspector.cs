using Basis.Editor.Localization;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Inspector for <see cref="BasisMediaPlayerDiagnostics"/>: the serialized
/// settings, a live readout while playing, and the start/stop/flush/reveal
/// controls.
/// </summary>
[CustomEditor(typeof(BasisMediaPlayerDiagnostics))]
public class BasisMediaPlayerDiagnosticsInspector : Editor
{
    BasisMediaPlayerDiagnostics _target;
    VisualElement _root;
    Label _isLogging, _rows, _resolvedPath, _error, _editHint;
    Button _start, _stop, _flush, _reveal;

    static string PackagePath => UnityEditor.PackageManager.PackageInfo
        .FindForAssembly(typeof(BasisMediaPlayerDiagnosticsInspector).Assembly)?.assetPath;

    public override VisualElement CreateInspectorGUI()
    {
        _target = (BasisMediaPlayerDiagnostics)target;
        _root = new VisualElement();

        string package = PackagePath;
        if (string.IsNullOrEmpty(package))
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.packagePathMissing"), HelpBoxMessageType.Error));
            return _root;
        }

        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerDiagnosticsSDK.uxml").Replace('\\', '/'));
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uss").Replace('\\', '/'));
        if (tree == null)
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "MediaPlayerDiagnosticsSDK.uxml"), HelpBoxMessageType.Error));
            return _root;
        }
        tree.CloneTree(_root);
        BasisEditorLocalization.LocalizeTree(_root, "editor.mediaPlayer.uxml.diagnostics.");
        if (sheet != null) _root.styleSheets.Add(sheet);

        BindByName("AutoStartField", "AutoStart");
        BindByName("FlushEveryField", "FlushEveryNRows");
        BindByName("AppendField", "AppendBetweenSessions");
        BindByName("LogPathOverrideField", "LogPathOverride");
        _root.Bind(serializedObject);

        _isLogging = _root.Q<Label>("StatusIsLogging");
        _rows = _root.Q<Label>("StatusRows");
        _resolvedPath = _root.Q<Label>("StatusResolvedPath");
        _error = _root.Q<Label>("StatusError");
        _editHint = _root.Q<Label>("StatusEditModeHint");

        _start = _root.Q<Button>("ActStartButton");
        _stop = _root.Q<Button>("ActStopButton");
        _flush = _root.Q<Button>("ActFlushButton");
        _reveal = _root.Q<Button>("ActRevealButton");

        _start.clicked += () => { if (PlayModeOnly()) _target.StartLogging(); };
        _stop.clicked += () => { if (PlayModeOnly()) _target.StopLogging(); };
        _flush.clicked += () => { if (PlayModeOnly()) _target.Flush(); };
        _reveal.clicked += () =>
        {
            if (_target == null) return;
            string path = _target.ResolvedLogPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) EditorUtility.RevealInFinder(path);
            else EditorUtility.RevealInFinder(Path.GetDirectoryName(path) ?? Application.persistentDataPath);
        };

        _root.schedule.Execute(RefreshStatus).Every(250);
        return _root;
    }

    bool PlayModeOnly()
    {
        if (Application.isPlaying && _target != null) return true;
        Debug.LogWarning("[BasisMedia] diagnostics start/stop/flush only run in play mode.");
        return false;
    }

    void RefreshStatus()
    {
        if (_target == null) _target = (BasisMediaPlayerDiagnostics)target;
        if (_target == null) return;

        bool live = Application.isPlaying;
        if (_editHint != null) _editHint.style.display = live ? DisplayStyle.None : DisplayStyle.Flex;

        SetPill(_isLogging, _target.IsLogging, live);
        SetText(_rows, live ? _target.RowsWritten.ToString() : "—");
        SetText(_resolvedPath, string.IsNullOrEmpty(_target.ResolvedLogPath) ? BasisEditorLocalization.Get("editor.mediaPlayer.diagnostics.unset") : _target.ResolvedLogPath);

        bool failed = !string.IsNullOrEmpty(_target.LastError);
        if (_error != null)
        {
            _error.style.display = failed ? DisplayStyle.Flex : DisplayStyle.None;
            if (failed) _error.text = _target.LastError;
        }

        Show(_start, live && !_target.IsLogging);
        Show(_stop, live && _target.IsLogging);
        Show(_flush, live && _target.IsLogging);
    }

    static void Show(VisualElement element, bool show)
    {
        if (element != null) element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
    }

    static void SetText(Label label, string value)
    {
        if (label != null) label.text = value;
    }

    static void SetPill(Label label, bool value, bool live)
    {
        if (label == null) return;
        label.RemoveFromClassList("bvp-pill-neutral");
        label.RemoveFromClassList("bvp-pill-good");
        label.RemoveFromClassList("bvp-pill-bad");
        if (!live) { label.text = "—"; label.AddToClassList("bvp-pill-neutral"); return; }
        label.text = BasisEditorLocalization.Get(value ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no");
        label.AddToClassList(value ? "bvp-pill-good" : "bvp-pill-bad");
    }

    void BindByName(string name, string property)
    {
        if (_root.Q<VisualElement>(name) is IBindable bindable) bindable.bindingPath = property;
    }
}
