using Basis.Editor.Localization;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

/// <summary>
/// Inspector for <see cref="BasisMediaAudioChannel"/>: the channel selection,
/// and the analysis-feed detail shown only once the feed is on.
/// </summary>
[CustomEditor(typeof(BasisMediaAudioChannel))]
public class BasisMediaAudioChannelInspector : Editor
{
    private VisualElement _root;

    private static string PackagePath => UnityEditor.PackageManager.PackageInfo
        .FindForAssembly(typeof(BasisMediaAudioChannelInspector).Assembly)?.assetPath;

    public override VisualElement CreateInspectorGUI()
    {
        _root = new VisualElement();

        string package = PackagePath;
        if (string.IsNullOrEmpty(package))
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.packagePathMissing"), HelpBoxMessageType.Error));
            return _root;
        }

        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerChannelSDK.uxml").Replace('\\', '/'));
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uss").Replace('\\', '/'));
        if (tree == null)
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "MediaPlayerChannelSDK.uxml"), HelpBoxMessageType.Error));
            return _root;
        }
        tree.CloneTree(_root);
        BasisEditorLocalization.LocalizeTree(_root, "editor.mediaPlayer.uxml.channel.");
        if (sheet != null) _root.styleSheets.Add(sheet);

        BindByName("ChannelField", "Channel");
        BindByName("AnalysisFeedField", "AnalysisFeed");
        BindByName("AnalysisFeedLatencyField", "AnalysisFeedLatency");
        _root.Bind(serializedObject);

        // The buffer length and the note only mean anything once the feed is on.
        var toggle = _root.Q<PropertyField>("AnalysisFeedField");
        toggle?.RegisterValueChangeCallback(evt => ShowAnalysisFeedDetail(evt.changedProperty.boolValue));
        ShowAnalysisFeedDetail(serializedObject.FindProperty("AnalysisFeed").boolValue);

        return _root;
    }

    private void ShowAnalysisFeedDetail(bool on)
    {
        DisplayStyle style = on ? DisplayStyle.Flex : DisplayStyle.None;
        if (_root.Q<VisualElement>("AnalysisFeedLatencyField") is VisualElement latency) latency.style.display = style;
        if (_root.Q<VisualElement>("AnalysisFeedHelp") is VisualElement help) help.style.display = style;
    }

    private void BindByName(string name, string property)
    {
        if (_root.Q<VisualElement>(name) is IBindable bindable) bindable.bindingPath = property;
    }
}
