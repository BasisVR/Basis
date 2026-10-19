#if UNITY_EDITOR
using Basis.Editor.Localization;
using System.Collections.Generic;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Visualises the most recent FBIK constellation calibration pass — front and side
/// projections of every tracker sample alongside the role priors used to classify
/// them. Drives nothing at runtime; reads BasisAvatarIKStageCalibration.ConstellationDebug.
/// </summary>
public sealed class BasisConstellationTab : BasisEditorTabPage
{
        public override string Title => BasisEditorLocalization.Get("editor.constellation.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.constellation.subtitle");

    private const float RepaintInterval = 0.2f;
    private const float PanelMinX = -0.9f;
    private const float PanelMaxX = 0.9f;
    private const float PanelMinY = -0.05f;
    private const float PanelMaxY = 1.25f;
    private const float PanelHeight = 520f;

    private static readonly Color GoodColor = new Color(0.30f, 0.92f, 0.45f);
    private static readonly Color OkayColor = new Color(0.95f, 0.85f, 0.20f);
    private static readonly Color MarginalColor = new Color(0.98f, 0.55f, 0.15f);
    private static readonly Color BadColor = new Color(0.95f, 0.30f, 0.30f);
    private static readonly Color StaleColor = new Color(0.85f, 0.30f, 0.95f);   // near-origin / not-polled trackers
    private static readonly Color DisabledColor = new Color(0.45f, 0.45f, 0.45f);
    private static readonly Color PriorIdleColor = new Color(0.85f, 0.85f, 0.90f);
    private static readonly Color SilhouetteColor = new Color(1f, 1f, 1f, 0.10f);
    private static readonly Color GridColor = new Color(1f, 1f, 1f, 0.08f);
    private static readonly Color PanelBgColor = new Color(0.12f, 0.12f, 0.13f, 1f);

    private bool _autoRefresh = true;
    private bool _mirrorFront = true;
    private bool _showSilhouette = true;
    private bool _showGrid = true;
    private bool _showAltLines = true;
    private double _nextRepaint;
    private Vector2 _scroll;
    private int _selectedSample = -1;

    public override void OnEnable() { EditorApplication.update += Tick; }
    public override void OnDisable() { EditorApplication.update -= Tick; }

    private void Tick()
    {
        if (!_autoRefresh || !EditorApplication.isPlaying) return;
        if (EditorApplication.timeSinceStartup >= _nextRepaint)
        {
            _nextRepaint = EditorApplication.timeSinceStartup + RepaintInterval;
            Host.Repaint();
        }
    }

    public override void Draw()
    {
        DrawToolbar();
        DrawHeader();
        DrawStaleBanner();
        EditorGUILayout.Space(4);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawPanels();
        EditorGUILayout.Space(8);
        DrawLegend();
        EditorGUILayout.Space(8);
        DrawDiagnostics();
        EditorGUILayout.Space(8);
        DrawTrackerTable();
        EditorGUILayout.Space(8);
        DrawRoleStatus();

        EditorGUILayout.EndScrollView();
    }

    // ─────────── Toolbar / Header ───────────

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.refresh"), EditorStyles.toolbarButton, GUILayout.Width(70))) Host.Repaint();

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || BasisLocalPlayer.Instance == null))
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.constellation.runCalibration"), EditorStyles.toolbarButton, GUILayout.Width(120)))
                {
                    BasisAvatarIKStageCalibration.FullBodyCalibration();
                    Host.Repaint();
                }
            }

            GUILayout.Space(8);
            _autoRefresh = GUILayout.Toggle(_autoRefresh, BasisEditorLocalization.Get("editor.common.auto"), EditorStyles.toolbarButton, GUILayout.Width(50));
            _mirrorFront = GUILayout.Toggle(_mirrorFront, BasisEditorLocalization.Get("editor.constellation.mirrorFront"), EditorStyles.toolbarButton, GUILayout.Width(110));
            _showSilhouette = GUILayout.Toggle(_showSilhouette, BasisEditorLocalization.Get("editor.constellation.silhouette"), EditorStyles.toolbarButton, GUILayout.Width(80));
            _showGrid = GUILayout.Toggle(_showGrid, BasisEditorLocalization.Get("editor.constellation.grid"), EditorStyles.toolbarButton, GUILayout.Width(50));
            _showAltLines = GUILayout.Toggle(_showAltLines, BasisEditorLocalization.Get("editor.constellation.altLinks"), EditorStyles.toolbarButton, GUILayout.Width(80));

            GUILayout.FlexibleSpace();
            GUILayout.Label(BasisAvatarIKStageCalibration.ConstellationDebug.Status, EditorStyles.miniLabel);
        }
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            int total = BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count;
            int assigned = 0;
            for (int i = 0; i < total; i++) if (BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i].Assigned) assigned++;

            int rolesEnabled = 0;
            int rolesBound = 0;
            for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Priors.Count; i++)
            {
                if (BasisAvatarIKStageCalibration.ConstellationDebug.Priors[i].Enabled) rolesEnabled++;
                if (BasisAvatarIKStageCalibration.ConstellationDebug.Priors[i].AssignedSampleIndex >= 0) rolesBound++;
            }

            GUILayout.Label(BasisAvatarIKStageCalibration.ConstellationDebug.HasSnapshot ? BasisEditorLocalization.Get("editor.constellation.snapshot") : BasisEditorLocalization.Get("editor.constellation.noSnapshot"), EditorStyles.boldLabel, GUILayout.Width(110));
            GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.headerEye", BasisAvatarIKStageCalibration.ConstellationDebug.EyeHeight), GUILayout.Width(80));
            GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.headerArmReach", BasisAvatarIKStageCalibration.ConstellationDebug.ArmReach), GUILayout.Width(110));
            GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.headerTrackers", assigned, total), GUILayout.Width(110));
            GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.headerRoles", rolesBound, rolesEnabled), GUILayout.Width(110));
            GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.headerThreshold", Mathf.Sqrt(-BasisAvatarIKStageCalibration.ConstellationDebug.AcceptThreshold)), GUILayout.Width(100));
            GUILayout.FlexibleSpace();
            if (!EditorApplication.isPlaying)
            {
                GUI.color = new Color(1f, 0.8f, 0.3f);
                GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.enterPlayMode"), EditorStyles.miniBoldLabel);
                GUI.color = Color.white;
            }
        }
    }

    private void DrawStaleBanner()
    {
        int stale = 0;
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
            if (BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i].NearOrigin) stale++;
        if (stale == 0) return;
        EditorGUILayout.HelpBox(
            BasisEditorLocalization.Get("editor.constellation.staleBanner", stale),
            MessageType.Error);
    }

    // ─────────── Panels ───────────

    private struct PanelMap
    {
        public Rect Rect;
        public float MinX, MaxX, MinY, MaxY;
        public bool MirrorX;
        public Vector2 ToPanel(float x, float y)
        {
            if (MirrorX) x = -x;
            float u = Mathf.InverseLerp(MinX, MaxX, x);
            float v = Mathf.InverseLerp(MinY, MaxY, y);
            return new Vector2(Rect.x + u * Rect.width, Rect.yMax - v * Rect.height);
        }
        public float HalfSpanX(float sigma) => sigma / (MaxX - MinX) * Rect.width;
        public float HalfSpanY(float sigma) => sigma / (MaxY - MinY) * Rect.height;
    }

    private void DrawPanels()
    {
        float wTotal = Host.position.width - 28f;
        float panelW = Mathf.Max(280f, (wTotal - 12f) * 0.5f);

        using (new EditorGUILayout.HorizontalScope())
        {
            Rect frontRect = GUILayoutUtility.GetRect(panelW, PanelHeight, GUILayout.Width(panelW), GUILayout.Height(PanelHeight));
            GUILayout.Space(8);
            Rect sideRect = GUILayoutUtility.GetRect(panelW, PanelHeight, GUILayout.Width(panelW), GUILayout.Height(PanelHeight));

            DrawFrontPanel(frontRect);
            DrawSidePanel(sideRect);
        }
    }

    private void DrawFrontPanel(Rect rect)
    {
        PanelMap map = new PanelMap
        {
            Rect = Inset(rect, 14f, 28f, 14f, 22f),
            MinX = PanelMinX,
            MaxX = PanelMaxX,
            MinY = PanelMinY,
            MaxY = PanelMaxY,
            MirrorX = _mirrorFront,
        };

        EditorGUI.DrawRect(rect, PanelBgColor);
        DrawPanelTitle(rect, BasisEditorLocalization.Get("editor.constellation.frontViewTitle"));

        Handles.BeginGUI();
        try
        {
            DrawAxes(map, _mirrorFront ? BasisEditorLocalization.Get("editor.constellation.axisUserLeft") : BasisEditorLocalization.Get("editor.constellation.axisBodyLeft"), _mirrorFront ? BasisEditorLocalization.Get("editor.constellation.axisUserRight") : BasisEditorLocalization.Get("editor.constellation.axisBodyRight"));
            if (_showSilhouette) DrawFrontSilhouette(map);

            DrawPriorsFront(map);
            DrawSamplesFront(map);
        }
        finally
        {
            Handles.EndGUI();
        }

        DrawPanelFooter(rect, map, BasisEditorLocalization.Get("editor.constellation.axisLateral"));
    }

    private void DrawSidePanel(Rect rect)
    {
        PanelMap map = new PanelMap
        {
            Rect = Inset(rect, 14f, 28f, 14f, 22f),
            MinX = -0.7f,
            MaxX = 0.7f,
            MinY = PanelMinY,
            MaxY = PanelMaxY,
            MirrorX = false,
        };

        EditorGUI.DrawRect(rect, PanelBgColor);
        DrawPanelTitle(rect, BasisEditorLocalization.Get("editor.constellation.sideViewTitle"));

        Handles.BeginGUI();
        try
        {
            DrawAxes(map, BasisEditorLocalization.Get("editor.constellation.axisBehind"), BasisEditorLocalization.Get("editor.constellation.axisForward"));
            if (_showSilhouette) DrawSideSilhouette(map);

            DrawPriorsSide(map);
            DrawSamplesSide(map);
        }
        finally
        {
            Handles.EndGUI();
        }

        DrawPanelFooter(rect, map, BasisEditorLocalization.Get("editor.constellation.axisDepth"));
    }

    // ─────────── Panel chrome ───────────

    private static Rect Inset(Rect r, float l, float t, float right, float b) =>
        new Rect(r.x + l, r.y + t, r.width - l - right, r.height - t - b);

    private static void DrawPanelTitle(Rect rect, string text)
    {
        Rect titleRect = new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 20f);
        var s = new GUIStyle(EditorStyles.boldLabel);
        s.normal.textColor = new Color(0.85f, 0.92f, 1f);
        GUI.Label(titleRect, text, s);
    }

    private static void DrawPanelFooter(Rect outer, PanelMap map, string xLabel)
    {
        Rect r = new Rect(outer.x + 12f, outer.yMax - 18f, outer.width - 24f, 14f);
        var s = new GUIStyle(EditorStyles.miniLabel);
        s.alignment = TextAnchor.MiddleCenter;
        s.normal.textColor = new Color(0.7f, 0.7f, 0.75f);
        GUI.Label(r, xLabel, s);
    }

    private void DrawAxes(PanelMap map, string leftLabel, string rightLabel)
    {
        if (_showGrid)
        {
            Handles.color = GridColor;
            for (float h = 0f; h <= 1.2f + 0.001f; h += 0.1f)
            {
                Vector2 a = map.ToPanel(map.MinX, h);
                Vector2 b = map.ToPanel(map.MaxX, h);
                Handles.DrawLine(new Vector3(a.x, a.y), new Vector3(b.x, b.y));
            }
            for (float x = -0.8f; x <= 0.8f + 0.001f; x += 0.2f)
            {
                Vector2 a = map.ToPanel(x, map.MinY);
                Vector2 b = map.ToPanel(x, map.MaxY);
                Handles.DrawLine(new Vector3(a.x, a.y), new Vector3(b.x, b.y));
            }
        }

        // Floor line (height = 0)
        Handles.color = new Color(0.5f, 0.6f, 0.7f, 0.55f);
        Vector2 fL = map.ToPanel(map.MinX, 0f);
        Vector2 fR = map.ToPanel(map.MaxX, 0f);
        Handles.DrawAAPolyLine(2f, new Vector3[] { new Vector3(fL.x, fL.y), new Vector3(fR.x, fR.y) });

        // Centerline
        Handles.color = new Color(0.5f, 0.6f, 0.7f, 0.30f);
        Vector2 cT = map.ToPanel(0f, map.MaxY);
        Vector2 cB = map.ToPanel(0f, map.MinY);
        Handles.DrawLine(new Vector3(cT.x, cT.y), new Vector3(cB.x, cB.y));

        // Eye height line (1.0)
        Handles.color = new Color(0.4f, 0.7f, 0.9f, 0.45f);
        Vector2 eL = map.ToPanel(map.MinX, 1.0f);
        Vector2 eR = map.ToPanel(map.MaxX, 1.0f);
        Handles.DrawDottedLine(new Vector3(eL.x, eL.y), new Vector3(eR.x, eR.y), 4f);

        // Y labels
        var ys = new GUIStyle(EditorStyles.miniLabel);
        ys.normal.textColor = new Color(0.65f, 0.7f, 0.8f);
        Vector2 floorPos = map.ToPanel(map.MinX, 0f);
        GUI.Label(new Rect(floorPos.x - 36, floorPos.y - 7, 36, 14), BasisEditorLocalization.Get("editor.constellation.labelFloor"), ys);
        Vector2 hipsPos = map.ToPanel(map.MinX, 0.55f);
        GUI.Label(new Rect(hipsPos.x - 36, hipsPos.y - 7, 36, 14), BasisEditorLocalization.Get("editor.constellation.labelHips"), ys);
        Vector2 chestPos = map.ToPanel(map.MinX, 0.78f);
        GUI.Label(new Rect(chestPos.x - 36, chestPos.y - 7, 36, 14), BasisEditorLocalization.Get("editor.constellation.labelChest"), ys);
        Vector2 eyePos = map.ToPanel(map.MinX, 1.0f);
        GUI.Label(new Rect(eyePos.x - 36, eyePos.y - 7, 36, 14), BasisEditorLocalization.Get("editor.constellation.labelEye"), ys);

        // X edge labels
        var xs = new GUIStyle(EditorStyles.miniLabel);
        xs.normal.textColor = new Color(0.65f, 0.7f, 0.8f);
        Vector2 left = map.ToPanel(map.MinX, 0f);
        GUI.Label(new Rect(left.x + 2, left.y + 4, 80, 14), leftLabel, xs);
        var xr = new GUIStyle(xs); xr.alignment = TextAnchor.UpperRight;
        Vector2 right = map.ToPanel(map.MaxX, 0f);
        GUI.Label(new Rect(right.x - 82, right.y + 4, 80, 14), rightLabel, xr);
    }

    private static void DrawFrontSilhouette(PanelMap map)
    {
        Handles.color = SilhouetteColor;
        // head circle
        Vector2 headC = map.ToPanel(0f, 1.05f);
        DrawCircle(new Vector2(headC.x, headC.y), 12f, SilhouetteColor, 24, 1.5f);
        // neck
        Vector2 nA = map.ToPanel(0f, 0.95f);
        Vector2 nB = map.ToPanel(0f, 0.92f);
        Handles.DrawLine(new Vector3(nA.x, nA.y), new Vector3(nB.x, nB.y));
        // torso outline
        Vector2 tlT = map.ToPanel(-0.13f, 0.92f);
        Vector2 trT = map.ToPanel(+0.13f, 0.92f);
        Vector2 tlB = map.ToPanel(-0.10f, 0.50f);
        Vector2 trB = map.ToPanel(+0.10f, 0.50f);
        Handles.DrawAAPolyLine(1.5f, new Vector3[] {
            new Vector3(tlT.x, tlT.y), new Vector3(trT.x, trT.y),
            new Vector3(trB.x, trB.y), new Vector3(tlB.x, tlB.y), new Vector3(tlT.x, tlT.y) });
        // arms (T-pose)
        Vector2 lsh = map.ToPanel(-0.15f, 0.88f);
        Vector2 lwr = map.ToPanel(-0.65f, 0.88f);
        Vector2 rsh = map.ToPanel(+0.15f, 0.88f);
        Vector2 rwr = map.ToPanel(+0.65f, 0.88f);
        Handles.DrawLine(new Vector3(lsh.x, lsh.y), new Vector3(lwr.x, lwr.y));
        Handles.DrawLine(new Vector3(rsh.x, rsh.y), new Vector3(rwr.x, rwr.y));
        // legs
        Vector2 lhip = map.ToPanel(-0.10f, 0.50f);
        Vector2 lft = map.ToPanel(-0.10f, 0.04f);
        Vector2 rhip = map.ToPanel(+0.10f, 0.50f);
        Vector2 rft = map.ToPanel(+0.10f, 0.04f);
        Handles.DrawLine(new Vector3(lhip.x, lhip.y), new Vector3(lft.x, lft.y));
        Handles.DrawLine(new Vector3(rhip.x, rhip.y), new Vector3(rft.x, rft.y));
    }

    private static void DrawSideSilhouette(PanelMap map)
    {
        Handles.color = SilhouetteColor;
        // head
        Vector2 headC = map.ToPanel(0f, 1.05f);
        DrawEllipse(headC, 10f, 12f, SilhouetteColor, 24, 1.5f);
        // torso
        Vector2 tlT = map.ToPanel(-0.07f, 0.92f);
        Vector2 trT = map.ToPanel(+0.07f, 0.92f);
        Vector2 tlB = map.ToPanel(-0.05f, 0.50f);
        Vector2 trB = map.ToPanel(+0.05f, 0.50f);
        Handles.DrawAAPolyLine(1.5f, new Vector3[] {
            new Vector3(tlT.x, tlT.y), new Vector3(trT.x, trT.y),
            new Vector3(trB.x, trB.y), new Vector3(tlB.x, tlB.y), new Vector3(tlT.x, tlT.y) });
        // legs
        Vector2 lhip = map.ToPanel(-0.02f, 0.50f);
        Vector2 lft = map.ToPanel(0.10f, 0.04f);
        Handles.DrawLine(new Vector3(lhip.x, lhip.y), new Vector3(lft.x, lft.y));
    }

    // ─────────── Priors ───────────

    private static Color PriorColor(BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior p)
    {
        if (!p.Enabled) return DisabledColor;
        if (p.AssignedSampleIndex < 0) return PriorIdleColor;
        BasisAvatarIKStageCalibration.ConstellationDebug.DebugSample s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[p.AssignedSampleIndex];
        return ScoreToColor(s.AssignedScore);
    }

    private void DrawPriorsFront(PanelMap map)
    {
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Priors.Count; i++)
        {
            var p = BasisAvatarIKStageCalibration.ConstellationDebug.Priors[i];
            Color c = PriorColor(p);
            Vector2 ctr = map.ToPanel(p.ExpectedLateral, p.ExpectedHeight);

            float rx1 = map.HalfSpanX(p.LateralSigma);
            float ry1 = map.HalfSpanY(p.HeightSigma);
            DrawEllipse(ctr, rx1, ry1, FadeColor(c, 0.22f), 28, 1.2f);
            DrawEllipse(ctr, rx1 * 3f, ry1 * 3f, FadeColor(c, 0.55f), 32, 1.2f);
            DrawCross(ctr, 5f, c, p.Enabled ? 2.2f : 1.2f);

            string label = ShortRoleName(p.Role) + (p.Enabled ? "" : BasisEditorLocalization.Get("editor.constellation.priorOff"));
            DrawTextWithShadow(new Vector2(ctr.x + 7f, ctr.y - 9f), label, c);
        }
    }

    private void DrawPriorsSide(PanelMap map)
    {
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Priors.Count; i++)
        {
            var p = BasisAvatarIKStageCalibration.ConstellationDebug.Priors[i];
            Color c = PriorColor(p);
            Vector2 ctr = map.ToPanel(0f, p.ExpectedHeight);
            float ry1 = map.HalfSpanY(p.HeightSigma);
            DrawEllipse(ctr, 4f, ry1, FadeColor(c, 0.22f), 24, 1f);
            DrawEllipse(ctr, 12f, ry1 * 3f, FadeColor(c, 0.45f), 28, 1f);
            DrawCross(ctr, 4f, c, p.Enabled ? 2f : 1f);
            string label = ShortRoleName(p.Role);
            DrawTextWithShadow(new Vector2(ctr.x + 7f, ctr.y - 9f), label, c);
        }
    }

    // ─────────── Samples ───────────

    private static Color ScoreToColor(float score)
    {
        // sigma-distance² = -score; sd in [0,1]=green, [1,2]=yellow, [2,3]=orange, [3,∞)=red
        float sd = score >= 0f ? 0f : Mathf.Sqrt(-score);
        if (sd < 1f) return GoodColor;
        if (sd < 2f) return OkayColor;
        if (sd < 3f) return MarginalColor;
        return BadColor;
    }

    private void DrawSamplesFront(PanelMap map)
    {
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
        {
            var s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i];
            Vector2 sp = map.ToPanel(s.LateralRatio, s.HeightRatio);
            Color c = SampleColor(s);

            // Connection line to assigned prior (or alt prior if showing).
            BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior linkedPrior =
                FindPrior(s.Assigned ? s.AssignedRole : s.BestAnyRole);
            if (linkedPrior != null && (s.Assigned || _showAltLines))
            {
                Vector2 pp = map.ToPanel(linkedPrior.ExpectedLateral, linkedPrior.ExpectedHeight);
                Color lineCol = FadeColor(c, s.Assigned ? 0.55f : 0.22f);
                if (s.Assigned)
                {
                    Handles.color = lineCol;
                    Handles.DrawAAPolyLine(1.5f, new Vector3[] { new Vector3(sp.x, sp.y), new Vector3(pp.x, pp.y) });
                }
                else
                {
                    Handles.color = lineCol;
                    Handles.DrawDottedLine(new Vector3(sp.x, sp.y), new Vector3(pp.x, pp.y), 3f);
                }
            }

            // Sample dot
            float dotR = (i == _selectedSample) ? 7.5f : 5.5f;
            Handles.color = c;
            Handles.DrawSolidDisc(new Vector3(sp.x, sp.y, 0f), Vector3.forward, dotR);
            DrawCircle(sp, dotR + 0.5f, new Color(0f, 0f, 0f, 0.8f), 16, 1.5f);

            // Click region
            Rect hit = new Rect(sp.x - dotR - 2, sp.y - dotR - 2, (dotR + 2) * 2, (dotR + 2) * 2);
            if (Event.current.type == EventType.MouseDown && hit.Contains(Event.current.mousePosition))
            {
                _selectedSample = i;
                Event.current.Use();
                Host.Repaint();
            }

            // Short label
            string lbl = s.NearOrigin ? BasisEditorLocalization.Get("editor.constellation.staleDevice", ShortDeviceId(s.DeviceId)) : ShortDeviceId(s.DeviceId);
            DrawTextWithShadow(new Vector2(sp.x + dotR + 2, sp.y + 2), lbl, FadeColor(c, 0.9f));
        }
    }

    private void DrawSamplesSide(PanelMap map)
    {
        float eye = Mathf.Max(BasisAvatarIKStageCalibration.ConstellationDebug.EyeHeight, 0.5f);
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
        {
            var s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i];
            float zRatio = s.BodyLocal.z / eye;
            Vector2 sp = map.ToPanel(zRatio, s.HeightRatio);

            Color c = SampleColor(s);

            // Line from sample horizontally to z=0 (the prior plane) — emphasises forward/back drift
            if (s.Assigned || _showAltLines)
            {
                Vector2 pp = map.ToPanel(0f, s.HeightRatio);
                Handles.color = FadeColor(c, s.Assigned ? 0.45f : 0.20f);
                Handles.DrawDottedLine(new Vector3(sp.x, sp.y), new Vector3(pp.x, pp.y), 3f);
            }

            float dotR = (i == _selectedSample) ? 7.5f : 5.5f;
            Handles.color = c;
            Handles.DrawSolidDisc(new Vector3(sp.x, sp.y, 0f), Vector3.forward, dotR);
            DrawCircle(sp, dotR + 0.5f, new Color(0f, 0f, 0f, 0.8f), 16, 1.5f);

            Rect hit = new Rect(sp.x - dotR - 2, sp.y - dotR - 2, (dotR + 2) * 2, (dotR + 2) * 2);
            if (Event.current.type == EventType.MouseDown && hit.Contains(Event.current.mousePosition))
            {
                _selectedSample = i;
                Event.current.Use();
                Host.Repaint();
            }

            string lbl = s.NearOrigin
                ? BasisEditorLocalization.Get("editor.constellation.stalePoll")
                : $"z={s.BodyLocal.z:+0.00;-0.00;0.00}m";
            DrawTextWithShadow(new Vector2(sp.x + dotR + 2, sp.y + 2), lbl, FadeColor(c, 0.85f));
        }
    }

    private static Color SampleColor(BasisAvatarIKStageCalibration.ConstellationDebug.DebugSample s)
    {
        if (s.NearOrigin) return StaleColor;
        return s.Assigned ? ScoreToColor(s.AssignedScore) : BadColor;
    }

    // ─────────── Legend / Diagnostics / Tables ───────────

    private void DrawLegend()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            DrawLegendDot(GoodColor, BasisEditorLocalization.Get("editor.constellation.legendGood"));
            DrawLegendDot(OkayColor, BasisEditorLocalization.Get("editor.constellation.legendOkay"));
            DrawLegendDot(MarginalColor, BasisEditorLocalization.Get("editor.constellation.legendMarginal"));
            DrawLegendDot(BadColor, BasisEditorLocalization.Get("editor.constellation.legendRejected"));
            DrawLegendDot(StaleColor, BasisEditorLocalization.Get("editor.constellation.legendStale"));
            DrawLegendDot(PriorIdleColor, BasisEditorLocalization.Get("editor.constellation.legendPriorIdle"));
            DrawLegendDot(DisabledColor, BasisEditorLocalization.Get("editor.constellation.legendPriorDisabled"));
            GUILayout.FlexibleSpace();
            GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.legendLines"), EditorStyles.miniLabel);
        }
    }

    private static void DrawLegendDot(Color c, string text)
    {
        Rect r = GUILayoutUtility.GetRect(12f, 12f, GUILayout.Width(12), GUILayout.Height(12));
        EditorGUI.DrawRect(new Rect(r.x, r.y + 3, 10, 10), c);
        GUILayout.Label(text, GUILayout.Width(140));
    }

    private void DrawDiagnostics()
    {
        if (!BasisAvatarIKStageCalibration.ConstellationDebug.HasSnapshot) return;
        List<string> issues = CollectDiagnostics();

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.constellation.diagnostics"));
            if (issues.Count == 0)
            {
                BasisEditorUI.Note("  " + BasisEditorLocalization.Get("editor.constellation.noIssues"));
            }
            else
            {
                for (int i = 0; i < issues.Count; i++)
                    EditorGUILayout.LabelField("• " + issues[i], EditorStyles.wordWrappedMiniLabel);
            }
        }
    }

    private static List<string> CollectDiagnostics()
    {
        List<string> issues = new List<string>();

        // Near-origin trackers come first — they're almost certainly a polling bug, not a calibration mistake.
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
        {
            var s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i];
            if (s.NearOrigin)
            {
                issues.Add(BasisEditorLocalization.Get("editor.constellation.issuePolledAtOrigin", ShortDeviceId(s.DeviceId), s.RawUnscaled.x, s.RawUnscaled.y, s.RawUnscaled.z));
            }
        }

        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
        {
            var s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i];
            if (s.NearOrigin) continue; // already reported above
            if (!s.Assigned)
            {
                float sd = s.BestAnyScore <= 0f ? Mathf.Sqrt(-s.BestAnyScore) : 0f;
                issues.Add(BasisEditorLocalization.Get("editor.constellation.issueNotAssigned", ShortDeviceId(s.DeviceId), s.BestAnyRole, sd));
            }
            else if (Mathf.Sqrt(Mathf.Max(0f, -s.AssignedScore)) > 2f)
            {
                float sd = Mathf.Sqrt(-s.AssignedScore);
                issues.Add(BasisEditorLocalization.Get("editor.constellation.issueBorderline", ShortDeviceId(s.DeviceId), s.AssignedRole, sd));
            }
        }

        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Priors.Count; i++)
        {
            var p = BasisAvatarIKStageCalibration.ConstellationDebug.Priors[i];
            if (p.Enabled && p.AssignedSampleIndex < 0)
                issues.Add(BasisEditorLocalization.Get("editor.constellation.issueRoleUnbound", p.Role, p.ExpectedHeight, p.ExpectedLateral));
        }

        SideAsymmetry(BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.RightFoot, BasisEditorLocalization.Get("editor.constellation.sideFeet"), issues);
        SideAsymmetry(BasisBoneTrackedRole.LeftLowerLeg, BasisBoneTrackedRole.RightLowerLeg, BasisEditorLocalization.Get("editor.constellation.sideKnees"), issues);
        SideAsymmetry(BasisBoneTrackedRole.LeftLowerArm, BasisBoneTrackedRole.RightLowerArm, BasisEditorLocalization.Get("editor.constellation.sideElbows"), issues);
        SideAsymmetry(BasisBoneTrackedRole.LeftShoulder, BasisBoneTrackedRole.RightShoulder, BasisEditorLocalization.Get("editor.constellation.sideShoulders"), issues);

        float eye = Mathf.Max(BasisAvatarIKStageCalibration.ConstellationDebug.EyeHeight, 0.5f);
        for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
        {
            var s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i];
            if (!s.Assigned) continue;
            float zRatio = Mathf.Abs(s.BodyLocal.z / eye);
            if (zRatio > 0.25f)
                issues.Add(BasisEditorLocalization.Get(s.BodyLocal.z >= 0 ? "editor.constellation.issueDriftFront" : "editor.constellation.issueDriftBehind", ShortDeviceId(s.DeviceId), s.AssignedRole, Mathf.Abs(s.BodyLocal.z) * 100f));
        }

        return issues;
    }

    private static void SideAsymmetry(BasisBoneTrackedRole left, BasisBoneTrackedRole right, string label, List<string> issues)
    {
        var l = FindAssignedSampleForRole(left);
        var r = FindAssignedSampleForRole(right);
        if (l == null || r == null) return;
        float dh = Mathf.Abs(l.HeightRatio - r.HeightRatio);
        float dl = Mathf.Abs(Mathf.Abs(l.LateralRatio) - Mathf.Abs(r.LateralRatio));
        if (dh > 0.05f)
            issues.Add(BasisEditorLocalization.Get("editor.constellation.issueAsymHeight", label, dh, 0.05f));
        if (dl > 0.07f)
            issues.Add(BasisEditorLocalization.Get("editor.constellation.issueAsymLateral", label, dl));
    }

    private void DrawTrackerTable()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.constellation.trackers"));
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                Hdr(BasisEditorLocalization.Get("editor.constellation.colDevice"), 220);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colAssigned"), 150);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colScore"), 100);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colHeightRatio"), 70);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colLateralRatio"), 70);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colZ"), 70);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colBestAlt"), 150);
            }

            for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count; i++)
            {
                var s = BasisAvatarIKStageCalibration.ConstellationDebug.Samples[i];
                Rect row = EditorGUILayout.BeginHorizontal();
                if (i == _selectedSample) EditorGUI.DrawRect(row, new Color(0.27f, 0.36f, 0.52f, 0.5f));

                if (GUILayout.Button(s.DeviceId, EditorStyles.label, GUILayout.Width(220)))
                {
                    _selectedSample = i;
                    Host.Repaint();
                }

                var prevCol = GUI.contentColor;
                GUI.contentColor = SampleColor(s);
                if (s.NearOrigin)
                {
                    GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.staleCell"), GUILayout.Width(150));
                    GUILayout.Label("—", GUILayout.Width(100));
                }
                else if (s.Assigned)
                {
                    GUILayout.Label(s.AssignedRole.ToString(), GUILayout.Width(150));
                    float sd = Mathf.Sqrt(Mathf.Max(0f, -s.AssignedScore));
                    GUILayout.Label($"{s.AssignedScore:F2} / {sd:F2}σ", GUILayout.Width(100));
                }
                else
                {
                    GUILayout.Label(BasisEditorLocalization.Get("editor.constellation.unassigned"), GUILayout.Width(150));
                    GUILayout.Label("—", GUILayout.Width(100));
                }
                GUI.contentColor = prevCol;

                GUILayout.Label(s.HeightRatio.ToString("F2"), GUILayout.Width(70));
                GUILayout.Label(s.LateralRatio.ToString("F2"), GUILayout.Width(70));
                GUILayout.Label(s.BodyLocal.z.ToString("F2"), GUILayout.Width(70));

                float bsd = Mathf.Sqrt(Mathf.Max(0f, -s.BestAnyScore));
                GUILayout.Label($"{s.BestAnyRole} ({bsd:F1}σ)", GUILayout.Width(150));

                EditorGUILayout.EndHorizontal();
            }

            if (BasisAvatarIKStageCalibration.ConstellationDebug.Samples.Count == 0)
                BasisEditorUI.Note("  " + BasisEditorLocalization.Get("editor.constellation.noSamples"));
        }
    }

    private void DrawRoleStatus()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.constellation.roleBindings"));
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                Hdr(BasisEditorLocalization.Get("editor.constellation.colRole"), 160);
                Hdr(BasisEditorLocalization.Get("editor.common.enabled"), 70);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colBound"), 220);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colExpectHeight"), 80);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colExpectLateral"), 80);
                Hdr(BasisEditorLocalization.Get("editor.constellation.colSigma"), 90);
            }
            for (int i = 0; i < BasisAvatarIKStageCalibration.ConstellationDebug.Priors.Count; i++)
            {
                var p = BasisAvatarIKStageCalibration.ConstellationDebug.Priors[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(p.Role.ToString(), GUILayout.Width(160));
                    var prev = GUI.contentColor;
                    GUI.contentColor = p.Enabled ? GoodColor : DisabledColor;
                    GUILayout.Label(p.Enabled ? BasisEditorLocalization.Get("editor.constellation.yes") : BasisEditorLocalization.Get("editor.constellation.no"), GUILayout.Width(70));
                    GUI.contentColor = prev;
                    if (p.AssignedSampleIndex >= 0)
                    {
                        GUI.contentColor = ScoreToColor(BasisAvatarIKStageCalibration.ConstellationDebug.Samples[p.AssignedSampleIndex].AssignedScore);
                        GUILayout.Label(ShortDeviceId(BasisAvatarIKStageCalibration.ConstellationDebug.Samples[p.AssignedSampleIndex].DeviceId), GUILayout.Width(220));
                        GUI.contentColor = prev;
                    }
                    else
                    {
                        GUI.contentColor = p.Enabled ? BadColor : DisabledColor;
                        GUILayout.Label(p.Enabled ? "—" : BasisEditorLocalization.Get("editor.constellation.skipped"), GUILayout.Width(220));
                        GUI.contentColor = prev;
                    }
                    GUILayout.Label(p.ExpectedHeight.ToString("F2"), GUILayout.Width(80));
                    GUILayout.Label(p.ExpectedLateral.ToString("+0.00;-0.00;0.00"), GUILayout.Width(80));
                    GUILayout.Label($"{p.HeightSigma:F2} / {p.LateralSigma:F2}", GUILayout.Width(90));
                }
            }
            if (BasisAvatarIKStageCalibration.ConstellationDebug.Priors.Count == 0)
                BasisEditorUI.Note("  " + BasisEditorLocalization.Get("editor.constellation.noPriors"));
        }
    }

    private static void Hdr(string text, float width)
    {
        GUILayout.Label(text, EditorStyles.toolbarButton, GUILayout.Width(width));
    }

    // ─────────── helpers ───────────

    private static BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior FindPrior(BasisBoneTrackedRole role)
    {
        var priors = BasisAvatarIKStageCalibration.ConstellationDebug.Priors;
        for (int i = 0; i < priors.Count; i++)
            if (priors[i].Role == role) return priors[i];
        return null;
    }

    private static BasisAvatarIKStageCalibration.ConstellationDebug.DebugSample FindAssignedSampleForRole(BasisBoneTrackedRole role)
    {
        var p = FindPrior(role);
        if (p == null || p.AssignedSampleIndex < 0) return null;
        return BasisAvatarIKStageCalibration.ConstellationDebug.Samples[p.AssignedSampleIndex];
    }

    private static string ShortRoleName(BasisBoneTrackedRole role)
    {
        switch (role)
        {
            case BasisBoneTrackedRole.LeftLowerArm: return BasisEditorLocalization.Get("editor.constellation.roleLeftElbow");
            case BasisBoneTrackedRole.RightLowerArm: return BasisEditorLocalization.Get("editor.constellation.roleRightElbow");
            case BasisBoneTrackedRole.LeftLowerLeg: return BasisEditorLocalization.Get("editor.constellation.roleLeftKnee");
            case BasisBoneTrackedRole.RightLowerLeg: return BasisEditorLocalization.Get("editor.constellation.roleRightKnee");
            case BasisBoneTrackedRole.LeftShoulder: return BasisEditorLocalization.Get("editor.constellation.roleLeftShoulder");
            case BasisBoneTrackedRole.RightShoulder: return BasisEditorLocalization.Get("editor.constellation.roleRightShoulder");
            case BasisBoneTrackedRole.LeftFoot: return BasisEditorLocalization.Get("editor.constellation.roleLeftFoot");
            case BasisBoneTrackedRole.RightFoot: return BasisEditorLocalization.Get("editor.constellation.roleRightFoot");
            default: return role.ToString();
        }
    }

    private static string ShortDeviceId(string id)
    {
        if (string.IsNullOrEmpty(id)) return BasisEditorLocalization.Get("editor.constellation.unknownDevice");
        if (id.Length <= 14) return id;
        return "…" + id.Substring(id.Length - 13);
    }

    private static Color FadeColor(Color c, float a) => new Color(c.r, c.g, c.b, a);

    private static void DrawCircle(Vector2 center, float radius, Color color, int segments = 24, float thickness = 1.5f)
    {
        Vector3[] pts = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments * Mathf.PI * 2f;
            pts[i] = new Vector3(center.x + Mathf.Cos(t) * radius, center.y + Mathf.Sin(t) * radius, 0f);
        }
        Handles.color = color;
        Handles.DrawAAPolyLine(thickness, pts);
    }

    private static void DrawEllipse(Vector2 center, float rx, float ry, Color color, int segments = 24, float thickness = 1.5f)
    {
        Vector3[] pts = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments * Mathf.PI * 2f;
            pts[i] = new Vector3(center.x + Mathf.Cos(t) * rx, center.y + Mathf.Sin(t) * ry, 0f);
        }
        Handles.color = color;
        Handles.DrawAAPolyLine(thickness, pts);
    }

    private static void DrawCross(Vector2 center, float size, Color color, float thickness = 2f)
    {
        Handles.color = color;
        Handles.DrawAAPolyLine(thickness,
            new Vector3[] { new Vector3(center.x - size, center.y, 0), new Vector3(center.x + size, center.y, 0) });
        Handles.DrawAAPolyLine(thickness,
            new Vector3[] { new Vector3(center.x, center.y - size, 0), new Vector3(center.x, center.y + size, 0) });
    }

    private static void DrawTextWithShadow(Vector2 pos, string text, Color color)
    {
        var shadow = new GUIStyle(EditorStyles.miniBoldLabel);
        shadow.normal.textColor = new Color(0, 0, 0, 0.85f);
        var fg = new GUIStyle(EditorStyles.miniBoldLabel);
        fg.normal.textColor = color;
        Rect r = new Rect(pos.x, pos.y, 220, 14);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, shadow);
        GUI.Label(r, text, fg);
    }
}
#endif
