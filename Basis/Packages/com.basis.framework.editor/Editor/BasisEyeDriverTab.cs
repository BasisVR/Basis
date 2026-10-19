#if UNITY_EDITOR
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Players;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

public sealed class BasisEyeDriverTab : BasisEditorTabPage
{
    public override string Title => BasisEditorLocalization.Get("editor.eyeDriver.title");
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.eyeDriver.subtitle");

    private bool _showStatus = true;
    private bool _showState = true;
    private bool _showCalibration = false;
    private bool _showConfig = true;
    private bool _showVisual = true;

    private static readonly Color GoodColor = new Color(0.3f, 0.9f, 0.3f);
    private static readonly Color WarnColor = new Color(1f, 0.8f, 0.2f);
    private static readonly Color ErrorColor = new Color(1f, 0.3f, 0.3f);
    private static readonly Color BarBg = new Color(0.15f, 0.15f, 0.15f, 1f);
    private static readonly Color BarColor = new Color(0.2f, 0.7f, 1f, 0.8f);
    private static readonly Color HoldColor = new Color(0.3f, 0.85f, 0.4f, 0.8f);
    private static readonly Color SaccadeColor = new Color(1f, 0.5f, 0.15f, 0.9f);
    private static readonly Color GazeColor = new Color(0.6f, 0.4f, 1f, 0.8f);
    private static readonly Color PursuitColor = new Color(0.2f, 0.8f, 0.6f, 0.8f);

    private static readonly Vector3[] _ellipsePoints = new Vector3[65];

    public override void OnEnable()
    {
        EditorApplication.update += Host.Repaint;
    }

    public override void OnDisable()
    {
        EditorApplication.update -= Host.Repaint;
    }

    public override void Draw()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.eyeDriver.header"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.flow"),
            EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.eyeDriver.playModeOnly"), MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        if (!BasisLocalPlayer.PlayerReady)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.eyeDriver.waitingForPlayer"), MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalEyeDriver;

        EditorGUILayout.Space(4);
        _showStatus = DrawSection(BasisEditorLocalization.Get("editor.common.status"), _showStatus, () => DrawStatusSection());
        _showState = DrawSection(BasisEditorLocalization.Get("editor.eyeDriver.section.state"), _showState, DrawStateSection);
        _showVisual = DrawSection(BasisEditorLocalization.Get("editor.eyeDriver.section.visual"), _showVisual, () => DrawVisualSection(driver));
        _showConfig = DrawSection(BasisEditorLocalization.Get("editor.eyeDriver.section.config"), _showConfig, () => DrawConfigSection(driver));
        _showCalibration = DrawSection(BasisEditorLocalization.Get("editor.eyeDriver.section.calibration"), _showCalibration, DrawCalibrationSection);

    }

    #region Section Template

    private bool DrawSection(string title, bool expanded, System.Action drawContent)
    {
        expanded = EditorGUILayout.BeginFoldoutHeaderGroup(expanded, title);
        if (expanded)
        {
            EditorGUI.indentLevel++;
            drawContent();
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
        EditorGUILayout.Space(2);
        return expanded;
    }

    #endregion

    #region Status

    private void DrawStatusSection()
    {
        StatusLabel("IsEnabled", BasisLocalEyeDriver.IsEnabled);
        StatusLabel(BasisEditorLocalization.Get("editor.eyeDriver.status.override"), BasisLocalEyeDriver.Override);
        StatusLabel("HasEyeSchedule", BasisLocalEyeDriver.HasEyeSchedule);

        EditorGUILayout.Space(2);

        bool leftOk = BasisLocalEyeDriver.leftEyeTransform != null;
        bool rightOk = BasisLocalEyeDriver.rightEyeTransform != null;
        StatusLabel(BasisEditorLocalization.Get("editor.eyeDriver.status.leftEyeTransform"), leftOk);
        if (leftOk)
            EditorGUILayout.LabelField("  ", BasisLocalEyeDriver.leftEyeTransform.name);
        StatusLabel(BasisEditorLocalization.Get("editor.eyeDriver.status.rightEyeTransform"), rightOk);
        if (rightOk)
            EditorGUILayout.LabelField("  ", BasisLocalEyeDriver.rightEyeTransform.name);

        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.eyeDriver.status.disabledHelp"), MessageType.Warning);
        }

        if (BasisLocalEyeDriver.Override)
        {
            EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.eyeDriver.status.overrideHelp"), MessageType.Info);
        }
    }

    #endregion

    #region Eye State

    private void DrawStateSection()
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.notEnabled"));
            return;
        }
        BasisEyeState state =  BasisLocalEyeDriver.LastKnownState;
        var snap = BasisLocalEyeDriver.DebugSnapshot;

        // Mode indicator
        string mode;
        Color modeColor;
        if (BasisLocalEyeDriver.Override)
            { mode = BasisEditorLocalization.Get("editor.eyeDriver.mode.override"); modeColor = WarnColor; }
        else if (state.gazeBlend > 0.5f)
            { mode = BasisEditorLocalization.Get("editor.eyeDriver.mode.gaze"); modeColor = GazeColor; }
        else
            { mode = BasisEditorLocalization.Get("editor.eyeDriver.mode.idle"); modeColor = HoldColor; }

        ColoredLabel(BasisEditorLocalization.Get("editor.common.mode"), mode, modeColor);
        EditorGUILayout.Space(2);

        // Phase
        string phaseName = BasisEditorLocalization.Get(state.phase == 0 ? "editor.eyeDriver.phase.hold" : "editor.eyeDriver.phase.saccade");
        Color phaseColor = state.phase == 0 ? HoldColor : SaccadeColor;
        ColoredLabel(BasisEditorLocalization.Get("editor.eyeDriver.phase"), phaseName, phaseColor);

        // Phase timer (text only, ticks every frame)
        float phaseProgress = state.phaseDur > 0 ? state.phaseT / state.phaseDur : 0f;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.phaseTimer"),
            $"{state.phaseT:F2}s / {state.phaseDur:F2}s  ({phaseProgress * 100f:F0}%)");

        EditorGUILayout.Space(4);

        // Yaw/Pitch in degrees
        float2 currentDeg = math.degrees(state.currentYawPitch);
        float2 targetDeg = math.degrees(state.targetYawPitch);
        float2 startDeg = math.degrees(state.startYawPitch);

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.currentYawPitch"), $"{currentDeg.x:F2} / {currentDeg.y:F2} deg");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.targetYawPitch"), $"{targetDeg.x:F2} / {targetDeg.y:F2} deg");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.startYawPitch"), $"{startDeg.x:F2} / {startDeg.y:F2} deg");

        EditorGUILayout.Space(4);

        // Gaze targeting
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.gazeBlend"), $"{state.gazeBlend:F2}");
        Rect gazeBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(gazeBar, state.gazeBlend, $"{state.gazeBlend * 100f:F0}%", GazeColor);

        // Target info
        if (snap.hasGazeTarget)
        {
            string targetName = snap.currentTargetId >= 0
                ? BasisEditorLocalization.Get("editor.eyeDriver.targetPlayer", snap.currentTargetId)
                : snap.currentGazeTarget != null
                    ? snap.currentGazeTarget.gameObject.name
                    : "?";
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.target"), BasisEditorLocalization.Get("editor.eyeDriver.targetInfo", targetName, snap.bestDist, snap.bestScore));
        }
        else
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.target"), BasisEditorLocalization.Get("editor.eyeDriver.targetNone"));
        }

        // Social triangle (N/A if no avatar targets)
        bool hasSocial = snap.hasGazeTarget && snap.currentTargetId >= 0;
        if (hasSocial)
        {
            string[] socialNames = { BasisEditorLocalization.Get("editor.eyeDriver.social.leftEye"), BasisEditorLocalization.Get("editor.eyeDriver.social.rightEye"), BasisEditorLocalization.Get("editor.eyeDriver.social.mouth") };
            string socialName = state.socialPhase < socialNames.Length ? socialNames[state.socialPhase] : "?";

            string subState;
            Color subColor;
            if (state.socialSaccadeT < 0f) { subState = BasisEditorLocalization.Get("editor.eyeDriver.reactionDelay"); subColor = WarnColor; }
            else if (state.socialSaccadeT < 1f) { subState = BasisEditorLocalization.Get("editor.eyeDriver.social.saccading"); subColor = SaccadeColor; }
            else { subState = BasisEditorLocalization.Get("editor.eyeDriver.social.pursuit"); subColor = PursuitColor; }

            ColoredLabel(BasisEditorLocalization.Get("editor.eyeDriver.social"), $"{socialName}  -  {subState}", subColor);

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.socialHoldTimer"),
                $"{state.socialTimer:F2}s / {state.socialDur:F2}s");

            if (state.socialSaccadeT < 0f)
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.saccadeProgress"),
                    BasisEditorLocalization.Get("editor.eyeDriver.saccadeReaction", -state.socialSaccadeT * state.socialSaccadeDur));
            else if (state.socialSaccadeT < 1f)
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.saccadeProgress"),
                    BasisEditorLocalization.Get("editor.eyeDriver.saccadeTotal", state.socialSaccadeT, state.socialSaccadeDur));
            else
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.saccadeProgress"), BasisEditorLocalization.Get("editor.eyeDriver.saccadeComplete"));

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.mouthGlanceChance"), $"{snap.gazeMouthScale:F2}");
        }
        else
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.social"), BasisEditorLocalization.Get("editor.eyeDriver.notApplicable"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.socialHoldTimer"), BasisEditorLocalization.Get("editor.eyeDriver.notApplicable"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.saccadeProgress"), BasisEditorLocalization.Get("editor.eyeDriver.notApplicable"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.mouthGlanceChance"), BasisEditorLocalization.Get("editor.eyeDriver.notApplicable"));
        }
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.jitterScale"), $"{state.jitterScale:F2}x");

        // Gaze disengagement break state
        if (state.isAverting == 1)
            ColoredLabel(BasisEditorLocalization.Get("editor.eyeDriver.gazeBreak"), BasisEditorLocalization.Get("editor.eyeDriver.gazeBreakAverting", state.avertedTimer), WarnColor);
        else if (state.gazeBreakTimer > 0f)
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.gazeBreak"), BasisEditorLocalization.Get("editor.eyeDriver.gazeBreakNext", state.gazeBreakTimer));
        else
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.gazeBreak"), BasisEditorLocalization.Get("editor.eyeDriver.gazeBreakIdle"));

        EditorGUILayout.Space(4);

        // Crowd
        if (snap.avatarsInRange > 0)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.avatarsInRange"), $"{snap.avatarsInRange}");
        }

        // Offset angles
        float leftAngle = math.degrees(2f * math.acos(math.clamp(math.abs(state.leftOffset.value.w), 0f, 1f)));
        float rightAngle = math.degrees(2f * math.acos(math.clamp(math.abs(state.rightOffset.value.w), 0f, 1f)));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.offsetAngles"), $"L {leftAngle:F2} / R {rightAngle:F2} deg");

        // Per-eye vergence offset
        float2 eyeVarDeg = math.degrees(state.eyeVar);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.vergenceOffset"),
            BasisEditorLocalization.Get("editor.eyeDriver.vergenceValue", eyeVarDeg.x, eyeVarDeg.y, BasisEyeState.VergenceScale));
    }

    #endregion

    #region Eye Pos Visual

    private void DrawVisualSection(BasisLocalEyeDriver driver)
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.notEnabled"));
            return;
        }
        BasisEyeState state = BasisLocalEyeDriver.LastKnownState;

        float maxDeg = BasisLocalEyeDriver.MaxAngleDeg;
        float size = 280f;
        float canvasMaxDeg = 52f;

        // Reserve space for the visual
        Rect area = GUILayoutUtility.GetRect(size + 40, size + 40, GUILayout.ExpandWidth(true));
        float cx = area.x + area.width * 0.5f;
        float cy = area.y + area.height * 0.5f;
        float radius = size * 0.5f;
        float pxPerDeg = radius / canvasMaxDeg;

        // Background
        EditorGUI.DrawRect(area, new Color(0.12f, 0.12f, 0.12f, 1f));

        Handles.BeginGUI();

        // Peripheral gaze boundary
        DrawEllipse(cx, cy, BasisEyeState.PeripheralHorizDeg * pxPerDeg, BasisEyeState.PeripheralVertDeg * pxPerDeg,
            new Color(GazeColor.r, GazeColor.g, GazeColor.b, 0.3f));

        // Fade zone (where gaze blend starts ramping down)
        float fadeH = BasisEyeState.PeripheralHorizDeg * BasisEyeState.PeripheralFadeStart;
        float fadeV = BasisEyeState.PeripheralVertDeg * BasisEyeState.PeripheralFadeStart;
        DrawEllipse(cx, cy, fadeH * pxPerDeg, fadeV * pxPerDeg,
            new Color(GazeColor.r, GazeColor.g, GazeColor.b, 0.15f));

        // Idle boundary (maxAngle horiz, dampened vert)
        DrawEllipse(cx, cy, maxDeg * pxPerDeg, maxDeg * BasisEyeState.VerticalDampening * pxPerDeg,
            new Color(0.4f, 0.4f, 0.4f, 0.6f));

        // Half-angle
        DrawEllipse(cx, cy, maxDeg * 0.5f * pxPerDeg, maxDeg * 0.5f * BasisEyeState.VerticalDampening * pxPerDeg,
            new Color(0.3f, 0.3f, 0.3f, 0.3f));

        // Cross-hairs
        Handles.color = new Color(0.3f, 0.3f, 0.3f, 0.4f);
        Handles.DrawLine(new Vector3(cx - radius, cy, 0), new Vector3(cx + radius, cy, 0));
        Handles.DrawLine(new Vector3(cx, cy - radius, 0), new Vector3(cx, cy + radius, 0));

        float2 socialDeg = math.degrees(state.socialCurrent);
        float2 targetDeg = math.degrees(state.targetYawPitch);
        float2 finalDeg = math.degrees(state.finalYawPitch);

        // Idle saccade target
        float idleAlpha = 1f - state.gazeBlend;
        if (idleAlpha > 0.05f)
        {
            Handles.color = new Color(1f, 1f, 0.3f, idleAlpha * 0.4f);
            Handles.DrawSolidDisc(new Vector3(cx + targetDeg.x * pxPerDeg, cy - targetDeg.y * pxPerDeg, 0), Vector3.forward, 3f);
        }

        // Social gaze position (visible when blending toward a target)
        if (state.gazeBlend > 0.01f)
        {
            Handles.color = new Color(GazeColor.r, GazeColor.g, GazeColor.b, state.gazeBlend * 0.5f);
            Handles.DrawSolidDisc(new Vector3(cx + socialDeg.x * pxPerDeg, cy - socialDeg.y * pxPerDeg, 0), Vector3.forward, 4f);
        }

        // Line from center to blended position
        float bx = cx + finalDeg.x * pxPerDeg;
        float by = cy - finalDeg.y * pxPerDeg;
        Color eyeColor = state.phase == 0 ? HoldColor : SaccadeColor;
        Handles.color = new Color(eyeColor.r, eyeColor.g, eyeColor.b, 0.3f);
        Handles.DrawLine(new Vector3(cx, cy, 0), new Vector3(bx, by, 0));

        // Current dot
        Handles.color = eyeColor;
        Handles.DrawSolidDisc(new Vector3(bx, by, 0), Vector3.forward, 6f);

        Handles.EndGUI();

        // Legend
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        LegendItem(BasisEditorLocalization.Get("editor.eyeDriver.legend.hold"), HoldColor);
        LegendItem(BasisEditorLocalization.Get("editor.eyeDriver.legend.saccade"), SaccadeColor);
        LegendItem(BasisEditorLocalization.Get("editor.common.target"), new Color(1f, 1f, 0.3f));
        LegendItem(BasisEditorLocalization.Get("editor.eyeDriver.legend.gaze"), GazeColor);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.range", maxDeg), EditorStyles.centeredGreyMiniLabel);
    }

    #endregion

    #region Config / Personality

    private void DrawConfigSection(BasisLocalEyeDriver driver)
    {
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.maxLookAngle"), $"{BasisLocalEyeDriver.MaxAngleDeg:F1} deg");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.saccadeTimeRange"), $"{driver.saccadeTimeRange.x:F2}s - {driver.saccadeTimeRange.y:F2}s");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.perEyeVariance"), $"{driver.perEyeVarianceDeg:F2} deg");

        EditorGUILayout.Space(4);

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.eyeDriver.personality"));
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.liveliness"), $"{BasisLocalEyeDriverData.Liveliness:F2}");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.attentiveness"), $"{BasisLocalEyeDriverData.Attentiveness:F2}");
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4);

        // Derived parameters from eye personality settings
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.eyeDriver.derived"));
        var p = BasisLocalEyeDriver.DebugSnapshot.personality;
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.holdRange"), $"{p.holdMin:F2}s - {p.holdMax:F2}s");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.centerBias"), $"{p.centerBias:F2}");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.centerReturn"), $"{p.centerReturnChance * 100f:F0}%");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.maxJitter"), $"{math.degrees(p.maxFocusedJitterRad):F2} deg");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.holdScaleGaze"), $"{p.holdScaleAtFullGaze:F2}x");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.blendInOut"), $"{p.gazeBlendInSpeed:F2} / {p.gazeBlendOutSpeed:F2} /s");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.socialHoldScale"), $"{p.socialHoldScale:F2}x");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.gazeBreakRange"), $"{p.gazeBreakMin:F1}s - {p.gazeBreakMax:F1}s");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.avertedRange"), $"{p.avertedMin:F2}s - {p.avertedMax:F2}s");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.reactionDelay"), $"{p.reactionMin:F2}s - {p.reactionMax:F2}s");
        EditorGUI.indentLevel--;
    }

    #endregion

    #region Calibration

    private void DrawCalibrationSection()
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyeDriver.noCalibration"));
            return;
        }

        DrawCalibrationEye(BasisEditorLocalization.Get("editor.eyeDriver.leftEyeCalibration"), BasisLocalEyeDriver.calLeft);
        EditorGUILayout.Space(2);
        DrawCalibrationEye(BasisEditorLocalization.Get("editor.eyeDriver.rightEyeCalibration"), BasisLocalEyeDriver.calRight);

        if (BasisLocalEyeDriver.leftEyeTransform != null)
        {
            EditorGUILayout.Space(4);
            BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eyeDriver.detectedAxes"));
            DrawDetectedAxes(BasisEditorLocalization.Get("editor.eyeDriver.leftForward"), BasisEditorLocalization.Get("editor.eyeDriver.leftUp"), BasisLocalEyeDriver.calLeft);
            DrawDetectedAxes(BasisEditorLocalization.Get("editor.eyeDriver.rightForward"), BasisEditorLocalization.Get("editor.eyeDriver.rightUp"), BasisLocalEyeDriver.calRight);
        }
    }

    private void DrawCalibrationEye(string title, BasisEyeCalibration cal)
    {
        BasisEditorUI.SectionTitle(title);
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField("Basis", QuatToString(cal.basis));
        EditorGUILayout.LabelField("InvBasis", QuatToString(cal.invBasis));
        EditorGUILayout.LabelField("InitialRot", QuatToString(cal.initialRotation));
        EditorGUI.indentLevel--;
    }

    private void DrawDetectedAxes(string forwardLabel, string upLabel, BasisEyeCalibration cal)
    {
        float3 fwd = math.mul(cal.basis, new float3(0, 0, 1));
        float3 up = math.mul(cal.basis, new float3(0, 1, 0));
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField(forwardLabel, $"({fwd.x:F3}, {fwd.y:F3}, {fwd.z:F3})");
        EditorGUILayout.LabelField(upLabel, $"({up.x:F3}, {up.y:F3}, {up.z:F3})");
        EditorGUI.indentLevel--;
    }

    #endregion

    #region Helpers

    private void WithColor(Color c, System.Action body)
    {
        var prev = GUI.color;
        GUI.color = c;
        body();
        GUI.color = prev;
    }

    private void ColoredLabel(string label, string value, Color color)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
        WithColor(color, () => EditorGUILayout.LabelField(value, EditorStyles.boldLabel));
        EditorGUILayout.EndHorizontal();
    }

    private void StatusLabel(string label, bool value)
    {
        ColoredLabel(label, BasisEditorLocalization.Get(value ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"), value ? GoodColor : ErrorColor);
    }

    private void LegendItem(string label, Color color)
    {
        WithColor(color, () => GUILayout.Label("\u25A0", EditorStyles.miniLabel, GUILayout.Width(12)));
        GUILayout.Label(label, EditorStyles.miniLabel);
        GUILayout.Space(6);
    }

    private void DrawBar(Rect rect, float fill, string label, Color? color = null)
    {
        fill = Mathf.Clamp01(fill);
        EditorGUI.DrawRect(rect, BarBg);
        Rect filled = new Rect(rect.x, rect.y, rect.width * fill, rect.height);
        EditorGUI.DrawRect(filled, color ?? BarColor);
        EditorGUI.LabelField(rect, $"  {label}", BasisEditorUI.OnDarkMiniLabel);
    }

    private void DrawEllipse(float cx, float cy, float rH, float rV, Color color)
    {
        Handles.color = color;
        for (int i = 0; i <= 64; i++)
        {
            float a = (i / 64f) * math.PI * 2f;
            _ellipsePoints[i] = new Vector3(cx + math.cos(a) * rH, cy + math.sin(a) * rV, 0);
        }
        Handles.DrawPolyLine(_ellipsePoints);
    }

    private static string QuatToString(quaternion q)
    {
        return $"({q.value.x:F4}, {q.value.y:F4}, {q.value.z:F4}, {q.value.w:F4})";
    }

    #endregion
}
#endif
