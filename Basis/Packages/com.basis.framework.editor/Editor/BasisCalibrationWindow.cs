using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The calibration tools, in one window.
///
/// Four windows used to sit under Basis ▸ Calibration answering successive questions about the same
/// run: which tracker did the classifier bind where, what did the whole run look like frame by
/// frame, what height did it settle on, and is the jitter filter tuned for it. Tabs keep them in
/// the order you actually use them.
/// </summary>
public sealed class BasisCalibrationWindow : BasisTabbedEditorWindow
{
    protected override string HeaderTitle => BasisEditorLocalization.Get("editor.calibrationWindow.title");

    protected override string HeaderSubtitle =>
        BasisEditorLocalization.Get("editor.calibrationWindow.subtitle");

    [MenuItem("Basis/Calibration/Calibration Tools", false, 180)]
    public static void ShowWindow()
    {
        BasisCalibrationWindow w = GetWindow<BasisCalibrationWindow>();
        w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.calibrationWindow.title"));
        w.minSize = new Vector2(640, 560);
        w.Show();
    }

    protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
    {
        new BasisConstellationTab(),
        new BasisCalibrationCsvTab(),
        new BasisHeightDriverTab(),
        new BasisOneEuroFilterTab(),
    };
}
