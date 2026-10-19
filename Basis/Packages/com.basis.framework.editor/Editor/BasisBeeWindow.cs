using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The BEE bundle tools, in one window.
///
/// Explorer answers "what is in this bundle" and Avatar Preview answers "what does the client get
/// when it loads one" — the same bundle, two questions, previously two windows you had to keep open
/// side by side.
/// </summary>
public sealed class BasisBeeWindow : BasisTabbedEditorWindow
{
    protected override string HeaderTitle => BasisEditorLocalization.Get("editor.beeWindow.title");

    protected override string HeaderSubtitle =>
        BasisEditorLocalization.Get("editor.beeWindow.subtitle");

    [MenuItem("Basis/Build/BEE Bundles", false, 302)]
    public static void ShowWindow()
    {
        BasisBeeWindow w = GetWindow<BasisBeeWindow>();
        w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.beeWindow.title"));
        w.minSize = new Vector2(720, 560);
        w.Show();
    }

    protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
    {
        new BasisBeeExplorerTab(),
        new BasisBeeAvatarPreviewTab(),
    };
}
