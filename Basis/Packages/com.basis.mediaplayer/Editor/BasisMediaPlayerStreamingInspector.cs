using Basis.Editor.Localization;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Inspector for <see cref="BasisMediaPlayerStreaming"/>: the stream URL, and
/// the per-platform pair shown only once automatic selection is on.
/// </summary>
[CustomEditor(typeof(BasisMediaPlayerStreaming))]
public class BasisMediaPlayerStreamingInspector : Editor
{
    private BasisMediaPlayerStreaming _target;
    private VisualElement _root;
    private TextField _pcUrl, _questUrl;
    private Toggle _autoSelect;
    private Button _configureBtn;

    private static string PackagePath => UnityEditor.PackageManager.PackageInfo
        .FindForAssembly(typeof(BasisMediaPlayerStreamingInspector).Assembly)?.assetPath;

    public override VisualElement CreateInspectorGUI()
    {
        _target = (BasisMediaPlayerStreaming)target;
        _root = new VisualElement();

        string package = PackagePath;
        if (string.IsNullOrEmpty(package))
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.packagePathMissing"), HelpBoxMessageType.Error));
            return _root;
        }

        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerStreamingSDK.uxml").Replace('\\', '/'));
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uss").Replace('\\', '/'));
        if (tree == null)
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "MediaPlayerStreamingSDK.uxml"), HelpBoxMessageType.Error));
            return _root;
        }
        tree.CloneTree(_root);
        BasisEditorLocalization.LocalizeTree(_root, "editor.mediaPlayer.uxml.streaming.");
        if (sheet != null) _root.styleSheets.Add(sheet);

        BindByName("StreamUrlField", "StreamUrl");
        BindByName("AutoSelectField", "AutoSelectPerPlatform");
        BindByName("PcUrlField", "PcUrl");
        BindByName("QuestUrlField", "QuestUrl");
        BindByName("ConfigureOnStartField", "ConfigureOnStart");
        _root.Bind(serializedObject);

        _pcUrl = _root.Q<TextField>("PcUrlField");
        _questUrl = _root.Q<TextField>("QuestUrlField");
        _autoSelect = _root.Q<Toggle>("AutoSelectField");

        if (_autoSelect != null) _autoSelect.RegisterValueChangedCallback(_ => RefreshAutoSelectVisibility());
        RefreshAutoSelectVisibility();

        _configureBtn = _root.Q<Button>("ConfigureButton");
        if (_configureBtn != null) _configureBtn.clicked += () =>
        {
            if (_target != null) _target.Configure();
        };

        var debugBtn = _root.Q<Button>("OpenDebugButton");
        if (debugBtn != null) debugBtn.clicked += BasisMediaPlayerDebugWindow.ShowWindow;

        _root.schedule.Execute(RefreshPlayButton).Every(250);
        return _root;
    }

    // The per-platform pair only means anything once selection is automatic.
    private void RefreshAutoSelectVisibility()
    {
        bool show = _autoSelect != null && _autoSelect.value;
        DisplayStyle style = show ? DisplayStyle.Flex : DisplayStyle.None;
        if (_pcUrl != null) _pcUrl.style.display = style;
        if (_questUrl != null) _questUrl.style.display = style;
    }

    // Configure opens a session, so it is only offered in play mode.
    private void RefreshPlayButton()
    {
        if (_configureBtn != null)
            _configureBtn.style.display = Application.isPlaying ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void BindByName(string name, string property)
    {
        if (_root.Q<VisualElement>(name) is IBindable bindable) bindable.bindingPath = property;
    }
}
