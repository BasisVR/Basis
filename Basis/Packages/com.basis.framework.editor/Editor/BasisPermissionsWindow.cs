#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Basis.Editor.Localization;

/// <summary>
/// Permissions, in one window.
///
/// The authored server rules and the live grant list used to be two separate windows that answered
/// two halves of the same question ("what should this user be allowed to do" / "what is this client
/// actually allowed to do"). They are tabs now, so you can author a node and immediately check
/// whether the connected client received it.
/// </summary>
public sealed class BasisPermissionsWindow : BasisTabbedEditorWindow
{
    protected override string HeaderTitle => BasisEditorLocalization.Get("editor.permissionsWindow.title");

    protected override string HeaderSubtitle =>
        BasisEditorLocalization.Get("editor.permissionsWindow.subtitle");

    [MenuItem("Basis/Settings/Permissions", false, 400)]
    public static void Open()
    {
        BasisPermissionsWindow w = GetWindow<BasisPermissionsWindow>();
        w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.permissionsWindow.title"));
        w.minSize = new Vector2(920, 520);
        w.Show();
    }

    protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
    {
        new BasisServerPermissionsTab(),
        new BasisLocalPermissionsTab(),
    };
}
#endif
