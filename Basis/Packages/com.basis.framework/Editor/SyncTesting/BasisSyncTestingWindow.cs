using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync.EditorTools
{
    /// <summary>
    /// The generic value-sync harnesses, in one window.
    ///
    /// Correctness and latency were two windows driving the same codec and receiver — one asking
    /// "does it converge", the other "how late does it feel". Both are offline simulations with no
    /// play mode and no server, so they belong side by side: change a setting, check it still
    /// converges, then check what it cost in latency.
    /// </summary>
    public sealed class BasisSyncTestingWindow : BasisTabbedEditorWindow
    {
        protected override string HeaderTitle => BasisEditorLocalization.Get("editor.syncTesting.window.title");

        protected override string HeaderSubtitle =>
            BasisEditorLocalization.Get("editor.syncTesting.window.subtitle");

        [MenuItem("Basis/Debug/Sync Testing", false, 601)]
        public static void ShowWindow()
        {
            BasisSyncTestingWindow w = GetWindow<BasisSyncTestingWindow>();
            w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.window.title"));
            w.minSize = new Vector2(720, 520);
            w.Show();
        }

        protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
        {
            new BasisSyncMatrixTab(),
            new BasisSyncLatencyTab(),
        };
    }
}
