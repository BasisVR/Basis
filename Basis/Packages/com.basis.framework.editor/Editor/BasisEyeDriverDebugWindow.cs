#if UNITY_EDITOR
using Basis.Scripts.BasisSdk.Players;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

public class BasisEyeDriverDebugWindow : EditorWindow
{
    private Vector2 _scrollPos;
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

    [MenuItem("Basis/Debug/Eye Driver Debug")]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisEyeDriverDebugWindow>("Eye Driver Debug");
        w.minSize = new Vector2(400, 500);
    }

    private void OnEnable()
    {
        EditorApplication.update += Repaint;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Repaint;
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        EditorGUILayout.LabelField("Eye Driver Debug", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Calibrate -> Simulate (yaw/pitch) -> Apply offset onto animated pose",
            EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to see live data.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        if (!BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.HelpBox("Waiting for local player...", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalEyeDriver;

        EditorGUILayout.Space(4);
        _showStatus = DrawSection("Status", _showStatus, () => DrawStatusSection());
        _showState = DrawSection("Eye State", _showState, DrawStateSection);
        _showVisual = DrawSection("Eye Position Visual", _showVisual, () => DrawVisualSection(driver));
        _showConfig = DrawSection("Configuration / Personality", _showConfig, () => DrawConfigSection(driver));
        _showCalibration = DrawSection("Calibration Data", _showCalibration, DrawCalibrationSection);

        EditorGUILayout.EndScrollView();
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
        StatusLabel("Override (external control)", BasisLocalEyeDriver.Override);
        StatusLabel("HasEyeSchedule", BasisLocalEyeDriver.HasEyeSchedule);

        EditorGUILayout.Space(2);

        bool leftOk = BasisLocalEyeDriver.leftEyeTransform != null;
        bool rightOk = BasisLocalEyeDriver.rightEyeTransform != null;
        StatusLabel("Left Eye Transform", leftOk);
        if (leftOk)
            EditorGUILayout.LabelField("  ", BasisLocalEyeDriver.leftEyeTransform.name);
        StatusLabel("Right Eye Transform", rightOk);
        if (rightOk)
            EditorGUILayout.LabelField("  ", BasisLocalEyeDriver.rightEyeTransform.name);

        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.HelpBox(
                "Eye driver is DISABLED. Possible causes:\n" +
                "- Avatar has no Humanoid LeftEye/RightEye bone mapping\n" +
                "- BasisTransformMapping.HasLeftEye or HasRightEye is false\n" +
                "- Initalize() was never called (check BasisLocalAvatarDriver calibration)",
                MessageType.Warning);
        }

        if (BasisLocalEyeDriver.Override)
        {
            EditorGUILayout.HelpBox(
                "Override is TRUE - an external system (e.g. EyeTrackingBoneActuation) is controlling eyes.\n" +
                "The procedural eye simulation will not run while Override is active.",
                MessageType.Info);
        }
    }

    #endregion

    #region Eye State

    private void DrawStateSection()
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField("(Eye driver not enabled)");
            return;
        }
        BasisEyeState state =  BasisLocalEyeDriver.LastKnownState;
        var snap = BasisLocalEyeDriver.DebugSnapshot;

        // Mode indicator
        string mode;
        Color modeColor;
        if (BasisLocalEyeDriver.Override)
            { mode = "OVERRIDE (external)"; modeColor = WarnColor; }
        else if (state.gazeBlend > 0.5f)
            { mode = "GAZE TRACKING"; modeColor = GazeColor; }
        else
            { mode = "IDLE SACCADES"; modeColor = HoldColor; }

        ColoredLabel("Mode", mode, modeColor);
        EditorGUILayout.Space(2);

        // Phase
        string phaseName = state.phase == 0 ? "HOLD" : "SACCADE";
        Color phaseColor = state.phase == 0 ? HoldColor : SaccadeColor;
        ColoredLabel("Phase", phaseName, phaseColor);

        // Phase timer (text only, ticks every frame)
        float phaseProgress = state.phaseDur > 0 ? state.phaseT / state.phaseDur : 0f;
        EditorGUILayout.LabelField("Phase Timer",
            $"{state.phaseT:F2}s / {state.phaseDur:F2}s  ({phaseProgress * 100f:F0}%)");

        EditorGUILayout.Space(4);

        // Yaw/Pitch in degrees
        float2 currentDeg = math.degrees(state.currentYawPitch);
        float2 targetDeg = math.degrees(state.targetYawPitch);
        float2 startDeg = math.degrees(state.startYawPitch);

        EditorGUILayout.LabelField("Current Yaw/Pitch", $"{currentDeg.x:F2} / {currentDeg.y:F2} deg");
        EditorGUILayout.LabelField("Target Yaw/Pitch", $"{targetDeg.x:F2} / {targetDeg.y:F2} deg");
        EditorGUILayout.LabelField("Start Yaw/Pitch", $"{startDeg.x:F2} / {startDeg.y:F2} deg");

        EditorGUILayout.Space(4);

        // Gaze targeting
        EditorGUILayout.LabelField("Gaze Blend", $"{state.gazeBlend:F2}");
        Rect gazeBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(gazeBar, state.gazeBlend, $"{state.gazeBlend * 100f:F0}%", GazeColor);

        // Target info
        if (snap.hasGazeTarget)
        {
            string targetName = snap.currentTargetId >= 0
                ? $"Player {snap.currentTargetId}"
                : snap.currentGazeTarget != null
                    ? snap.currentGazeTarget.gameObject.name
                    : "?";
            EditorGUILayout.LabelField("Target", $"{targetName}  ({snap.bestDist:F2}m, score {snap.bestScore:F2})");
        }
        else
        {
            EditorGUILayout.LabelField("Target", "(none)");
        }

        // Social triangle (N/A if no avatar targets)
        bool hasSocial = snap.hasGazeTarget && snap.currentTargetId >= 0;
        if (hasSocial)
        {
            string[] socialNames = { "Left Eye", "Right Eye", "Mouth" };
            string socialName = state.socialPhase < socialNames.Length ? socialNames[state.socialPhase] : "?";

            string subState;
            Color subColor;
            if (state.socialSaccadeT < 0f) { subState = "Reaction Delay"; subColor = WarnColor; }
            else if (state.socialSaccadeT < 1f) { subState = "Saccading"; subColor = SaccadeColor; }
            else { subState = "Pursuit"; subColor = PursuitColor; }

            ColoredLabel("Social", $"{socialName}  -  {subState}", subColor);

            EditorGUILayout.LabelField("Social Hold Timer",
                $"{state.socialTimer:F2}s / {state.socialDur:F2}s");

            if (state.socialSaccadeT < 0f)
                EditorGUILayout.LabelField("Saccade Progress",
                    $"Reaction: {-state.socialSaccadeT * state.socialSaccadeDur:F2}s remaining");
            else if (state.socialSaccadeT < 1f)
                EditorGUILayout.LabelField("Saccade Progress",
                    $"{state.socialSaccadeT:F2} ({state.socialSaccadeDur:F2}s total)");
            else
                EditorGUILayout.LabelField("Saccade Progress", "Complete (pursuit)");

            EditorGUILayout.LabelField("Mouth Glance Chance", $"{snap.gazeMouthScale:F2}");
        }
        else
        {
            EditorGUILayout.LabelField("Social", "N/A");
            EditorGUILayout.LabelField("Social Hold Timer", "N/A");
            EditorGUILayout.LabelField("Saccade Progress", "N/A");
            EditorGUILayout.LabelField("Mouth Glance Chance", "N/A");
        }
        EditorGUILayout.LabelField("Jitter Scale", $"{state.jitterScale:F2}x");

        // Gaze disengagement break state
        if (state.isAverting == 1)
            ColoredLabel("Gaze Break", $"AVERTING ({state.avertedTimer:F2}s remaining)", WarnColor);
        else if (state.gazeBreakTimer > 0f)
            EditorGUILayout.LabelField("Gaze Break", $"Next break in {state.gazeBreakTimer:F1}s");
        else
            EditorGUILayout.LabelField("Gaze Break", "Idle");

        EditorGUILayout.Space(4);

        // Crowd
        if (snap.avatarsInRange > 0)
        {
            EditorGUILayout.LabelField("Avatars In Range", $"{snap.avatarsInRange}");
        }

        // Offset angles
        float leftAngle = math.degrees(2f * math.acos(math.clamp(math.abs(state.leftOffset.value.w), 0f, 1f)));
        float rightAngle = math.degrees(2f * math.acos(math.clamp(math.abs(state.rightOffset.value.w), 0f, 1f)));
        EditorGUILayout.LabelField("Offset Angles", $"L {leftAngle:F2} / R {rightAngle:F2} deg");

        // Per-eye vergence offset
        float2 eyeVarDeg = math.degrees(state.eyeVar);
        EditorGUILayout.LabelField("Vergence Offset",
            $"({eyeVarDeg.x:F2}, {eyeVarDeg.y:F2}) deg  (scaled: x{BasisEyeState.VergenceScale:F1})");
    }

    #endregion

    #region Eye Pos Visual

    private void DrawVisualSection(BasisLocalEyeDriver driver)
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField("(Eye driver not enabled)");
            return;
        }
        BasisEyeState state = BasisLocalEyeDriver.LastKnownState;

        float maxDeg = driver.maxAngleDeg;
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
        LegendItem("Hold", HoldColor);
        LegendItem("Saccade", SaccadeColor);
        LegendItem("Target", new Color(1f, 1f, 0.3f));
        LegendItem("Gaze", GazeColor);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField($"Range: +/- {maxDeg:F0} deg", EditorStyles.centeredGreyMiniLabel);
    }

    #endregion

    #region Config / Personality

    private void DrawConfigSection(BasisLocalEyeDriver driver)
    {
        EditorGUILayout.LabelField("Max Angle", $"{driver.maxAngleDeg:F1} deg");
        EditorGUILayout.LabelField("Saccade Time Range", $"{driver.saccadeTimeRange.x:F2}s - {driver.saccadeTimeRange.y:F2}s");
        EditorGUILayout.LabelField("Per-Eye Variance", $"{driver.perEyeVarianceDeg:F2} deg");

        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField("Personality", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField("Liveliness", $"{BasisLocalEyeDriver.Liveliness:F2}");
        EditorGUILayout.LabelField("Attentiveness", $"{BasisLocalEyeDriver.Attentiveness:F2}");
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4);

        // Derived parameters from eye personality settings
        EditorGUILayout.LabelField("Derived", EditorStyles.boldLabel);
        var p = BasisLocalEyeDriver.DebugSnapshot.personality;
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField("Hold Range", $"{p.holdMin:F2}s - {p.holdMax:F2}s");
        EditorGUILayout.LabelField("Center Bias", $"{p.centerBias:F2}");
        EditorGUILayout.LabelField("Center Return", $"{p.centerReturnChance * 100f:F0}%");
        EditorGUILayout.LabelField("Max Jitter", $"{math.degrees(p.maxFocusedJitterRad):F2} deg");
        EditorGUILayout.LabelField("Hold Scale (gaze)", $"{p.holdScaleAtFullGaze:F2}x");
        EditorGUILayout.LabelField("Blend In/Out", $"{p.gazeBlendInSpeed:F2} / {p.gazeBlendOutSpeed:F2} /s");
        EditorGUILayout.LabelField("Social Hold Scale", $"{p.socialHoldScale:F2}x");
        EditorGUILayout.LabelField("Gaze Break Range", $"{p.gazeBreakMin:F1}s - {p.gazeBreakMax:F1}s");
        EditorGUILayout.LabelField("Averted Range", $"{p.avertedMin:F2}s - {p.avertedMax:F2}s");
        EditorGUILayout.LabelField("Reaction Delay", $"{p.reactionMin:F2}s - {p.reactionMax:F2}s");
        EditorGUI.indentLevel--;
    }

    #endregion

    #region Calibration

    private void DrawCalibrationSection()
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField("(Eye driver not enabled - no calibration data)");
            return;
        }

        DrawCalibrationEye("Left", BasisLocalEyeDriver.calLeft);
        EditorGUILayout.Space(2);
        DrawCalibrationEye("Right", BasisLocalEyeDriver.calRight);

        if (BasisLocalEyeDriver.leftEyeTransform != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Detected Axes (world-space, from calibration basis)", EditorStyles.miniLabel);
            DrawDetectedAxes("Left", BasisLocalEyeDriver.calLeft);
            DrawDetectedAxes("Right", BasisLocalEyeDriver.calRight);
        }
    }

    private void DrawCalibrationEye(string label, BasisEyeCalibration cal)
    {
        EditorGUILayout.LabelField($"{label} Eye Calibration", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField("Basis", QuatToString(cal.basis));
        EditorGUILayout.LabelField("InvBasis", QuatToString(cal.invBasis));
        EditorGUILayout.LabelField("InitialRot", QuatToString(cal.initialRotation));
        EditorGUI.indentLevel--;
    }

    private void DrawDetectedAxes(string label, BasisEyeCalibration cal)
    {
        float3 fwd = math.mul(cal.basis, new float3(0, 0, 1));
        float3 up = math.mul(cal.basis, new float3(0, 1, 0));
        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField($"{label} Forward", $"({fwd.x:F3}, {fwd.y:F3}, {fwd.z:F3})");
        EditorGUILayout.LabelField($"{label} Up", $"({up.x:F3}, {up.y:F3}, {up.z:F3})");
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
        ColoredLabel(label, value ? "YES" : "NO", value ? GoodColor : ErrorColor);
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
        EditorGUI.LabelField(rect, $"  {label}", EditorStyles.miniLabel);
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
