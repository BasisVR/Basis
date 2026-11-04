using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Device_Management.EyeTracking.Editor
{
    /// <summary>
    /// Eye tracking, in one window.
    ///
    /// The pipeline view (which device is feeding gaze, how arbitration merges it, who consumes it)
    /// and the driver view (what the local eye driver does with that gaze) were separate windows,
    /// which meant diagnosing "my eyes aren't moving" involved opening both and guessing which half
    /// was at fault. Pipeline first: if no data arrives, the driver tab cannot help.
    /// </summary>
    public sealed class BasisEyeDebugWindow : BasisTabbedEditorWindow
    {
        protected override string HeaderTitle => BasisEditorLocalization.Get("editor.eyeDebug.title");

        protected override string HeaderSubtitle =>
            BasisEditorLocalization.Get("editor.eyeDebug.subtitle");

        [MenuItem("Basis/Debug/Eye Tracking", false, 603)]
        public static void ShowWindow()
        {
            BasisEyeDebugWindow w = GetWindow<BasisEyeDebugWindow>();
            w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.eyeDebug.title"));
            w.minSize = new Vector2(420, 520);
            w.Show();
        }

        protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
        {
            new BasisEyePipelineTab(),
            new BasisEyeDriverTab(),
        };
    }
}
