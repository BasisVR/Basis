using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Where the frame went, in one window.
///
/// Markers is the raw profiler-marker table — anything the engine or a Basis subsystem instruments,
/// discovered automatically. EventDriver is the hand-instrumented breakdown of the central tick,
/// which knows about the stalls and the counts a marker name can't tell you. They answer the same
/// question at two altitudes, so they share a header and a recording session instead of being two
/// windows kept open side by side.
/// </summary>
public sealed class BasisFrameTimingWindow : BasisTabbedEditorWindow
{
    protected override string HeaderTitle => BasisEditorLocalization.Get("editor.frameTiming.title");

    protected override string HeaderSubtitle =>
        BasisEditorLocalization.Get("editor.frameTiming.subtitle");

    [MenuItem("Basis/Debug/Frame Timing", false, 620)]
    public static void ShowWindow()
    {
        BasisFrameTimingWindow w = GetWindow<BasisFrameTimingWindow>();
        w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.frameTiming.title"));
        w.minSize = new Vector2(560, 620);
        w.Show();
    }

    protected override BasisEditorTabPage[] BuildPages() => new BasisEditorTabPage[]
    {
        new BasisFrameMarkersTab(),
        new BasisEventDriverProfilerTab(),
    };

    // Both tabs are live readouts, so the window drives its own repaint rather than each tab
    // subscribing and repainting the host twice per editor tick. Rate-limited because repainting is
    // the expensive half: the tabs sample every frame regardless, and everything on screen is a
    // rolling average over hundreds of frames, so redrawing at the editor's tick rate spends real
    // main-thread time in play mode to animate digits nobody can read that fast. Input events
    // repaint on their own, so the controls stay immediate.
    const double k_RepaintInterval = 1.0 / 12.0;
    double _nextRepaint;

    protected override void OnEnable()
    {
        base.OnEnable();
        EditorApplication.update += TickRepaint;
    }

    protected override void OnDisable()
    {
        EditorApplication.update -= TickRepaint;
        base.OnDisable();
    }

    void TickRepaint()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now < _nextRepaint)
        {
            return;
        }
        _nextRepaint = now + k_RepaintInterval;
        Repaint();
    }
}
