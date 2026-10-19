using Basis.Editor.Localization;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

/// <summary>
/// Inspector for <see cref="BasisVideoMaterialOutput"/>: the layout lives in
/// UXML and the look in the shared stylesheet, so this is binding glue.
/// </summary>
[CustomEditor(typeof(BasisVideoMaterialOutput))]
public class BasisVideoMaterialOutputInspector : Editor
{
    private VisualElement _root;

    /// <summary>
    /// The package's own asset root, asked of the package manager rather than
    /// spelled out, so the paths survive the folder being renamed.
    /// </summary>
    private static string PackagePath => UnityEditor.PackageManager.PackageInfo
        .FindForAssembly(typeof(BasisVideoMaterialOutputInspector).Assembly)?.assetPath;

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
            Path.Combine(package, "Editor/StyleSheets/VideoMaterialOutputSDK.uxml").Replace('\\', '/'));
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uss").Replace('\\', '/'));
        if (tree == null)
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "VideoMaterialOutputSDK.uxml"), HelpBoxMessageType.Error));
            return _root;
        }
        tree.CloneTree(_root);
        BasisEditorLocalization.LocalizeTree(_root, "editor.mediaPlayer.uxml.videoOutput.");
        if (sheet != null) _root.styleSheets.Add(sheet);

        var playerField = _root.Q<ObjectField>("PlayerField");
        if (playerField != null) playerField.objectType = typeof(BasisMediaPlayer);

        BindByName("PlayerField", "Player");
        BindByName("TargetRendererField", "TargetRenderer");
        BindByName("MaterialIndexField", "MaterialIndex");
        BindByName("TexturePropertyField", "TexturePropertyName");
        BindByName("UseSharedMaterialField", "UseSharedMaterial");
        BindByName("AdditionalTargetsField", "AdditionalTargets");
        BindByName("PlaceholderField", "PlaceholderTexture");
        BindByName("RestoreOnEndedField", "RestorePlaceholderOnEnded");
        BindByName("FlipVerticallyField", "FlipVertically");
        BindByName("FlipHorizontallyField", "FlipHorizontally");
        BindByName("ProjectionModeField", "ProjectionMode");
        BindByName("StereoEyeField", "StereoEye");
        BindByName("AspectModeField", "AspectMode");
        BindByName("DisplayAspectOverrideField", "DisplayAspectOverride");
        BindByName("PictureField", "Picture");
        _root.Bind(serializedObject);

        return _root;
    }

    private void BindByName(string name, string property)
    {
        if (_root.Q<VisualElement>(name) is IBindable bindable) bindable.bindingPath = property;
    }
}
