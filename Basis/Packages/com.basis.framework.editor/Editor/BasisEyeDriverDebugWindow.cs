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
        _showStatus = DrawSection("1. Status", _showStatus, () => DrawStatusSection());
        _showState = DrawSection("2. Eye State (Live)", _showState, DrawStateSection);
        _showVisual = DrawSection("3. Eye Position Visual", _showVisual, () => DrawVisualSection(driver));
        _showConfig = DrawSection("4. Configuration", _showConfig, () => DrawConfigSection(driver));
        _showCalibration = DrawSection("5. Calibration Data", _showCalibration, DrawCalibrationSection);

        EditorGUILayout.EndScrollView();
    }

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

    private void DrawStateSection()
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField("(Eye driver not enabled)");
            return;
        }
        BasisEyeState state =  BasisLocalEyeDriver.LastKnownState;
        // Phase
        string phaseName = state.phase == 0 ? "HOLD" : "SACCADE";
        Color phaseColor = state.phase == 0 ? HoldColor : SaccadeColor;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Phase", GUILayout.Width(EditorGUIUtility.labelWidth));
        var prev = GUI.color;
        GUI.color = phaseColor;
        EditorGUILayout.LabelField(phaseName, EditorStyles.boldLabel);
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();

        // Phase timer
        float progress = state.phaseDur > 0 ? state.phaseT / state.phaseDur : 0f;
        EditorGUILayout.LabelField("Phase Timer", $"{state.phaseT:F3}s / {state.phaseDur:F3}s");
        Rect barRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(barRect, progress, $"{progress * 100f:F0}%", phaseColor);

        EditorGUILayout.Space(4);

        // Yaw/Pitch in degrees
        float2 currentDeg = math.degrees(state.currentYawPitch);
        float2 targetDeg = math.degrees(state.targetYawPitch);
        float2 startDeg = math.degrees(state.startYawPitch);

        EditorGUILayout.LabelField("Current Yaw/Pitch", $"{currentDeg.x:F2} / {currentDeg.y:F2} deg");
        EditorGUILayout.LabelField("Target Yaw/Pitch", $"{targetDeg.x:F2} / {targetDeg.y:F2} deg");
        EditorGUILayout.LabelField("Start Yaw/Pitch", $"{startDeg.x:F2} / {startDeg.y:F2} deg");

        EditorGUILayout.Space(4);

        // Offset quaternions
        EditorGUILayout.LabelField("Left Offset", QuatToString(state.leftOffset));
        EditorGUILayout.LabelField("Right Offset", QuatToString(state.rightOffset));

        // Offset angles
        float leftAngle = math.degrees(2f * math.acos(math.clamp(math.abs(state.leftOffset.value.w), 0f, 1f)));
        float rightAngle = math.degrees(2f * math.acos(math.clamp(math.abs(state.rightOffset.value.w), 0f, 1f)));
        EditorGUILayout.LabelField("Left Offset Angle", $"{leftAngle:F2} deg");
        EditorGUILayout.LabelField("Right Offset Angle", $"{rightAngle:F2} deg");
    }

    private void DrawVisualSection(BasisLocalEyeDriver driver)
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField("(Eye driver not enabled)");
            return;
        }
        BasisEyeState state = BasisLocalEyeDriver.LastKnownState;

        float maxDeg = driver.maxAngleDeg;
        float size = 180f;

        // Reserve space for the visual
        Rect area = GUILayoutUtility.GetRect(size + 40, size + 40, GUILayout.ExpandWidth(true));
        float cx = area.x + area.width * 0.5f;
        float cy = area.y + area.height * 0.5f;
        float radius = size * 0.5f;

        // Background
        EditorGUI.DrawRect(area, new Color(0.12f, 0.12f, 0.12f, 1f));

        Handles.BeginGUI();

        // Max angle boundary circle
        Handles.color = new Color(0.4f, 0.4f, 0.4f, 0.6f);
        Handles.DrawWireDisc(new Vector3(cx, cy, 0), Vector3.forward, radius);

        // Cross-hairs
        Handles.color = new Color(0.3f, 0.3f, 0.3f, 0.4f);
        Handles.DrawLine(new Vector3(cx - radius, cy, 0), new Vector3(cx + radius, cy, 0));
        Handles.DrawLine(new Vector3(cx, cy - radius, 0), new Vector3(cx, cy + radius, 0));

        // Half-angle ring
        Handles.color = new Color(0.3f, 0.3f, 0.3f, 0.3f);
        Handles.DrawWireDisc(new Vector3(cx, cy, 0), Vector3.forward, radius * 0.5f);

        float2 currentDeg = math.degrees(state.currentYawPitch);
        float2 targetDeg = math.degrees(state.targetYawPitch);

        // Target position (translucent)
        float txNorm = targetDeg.x / maxDeg;
        float tyNorm = -targetDeg.y / maxDeg; // flip Y for screen space
        float tx = cx + txNorm * radius;
        float ty = cy + tyNorm * radius;
        Handles.color = new Color(1f, 1f, 0.3f, 0.4f);
        Handles.DrawSolidDisc(new Vector3(tx, ty, 0), Vector3.forward, 4f);

        // Current eye position (bright)
        float exNorm = currentDeg.x / maxDeg;
        float eyNorm = -currentDeg.y / maxDeg;
        float ex = cx + exNorm * radius;
        float ey = cy + eyNorm * radius;

        // Line from center to current
        Color eyeColor = state.phase == 0 ? HoldColor : SaccadeColor;
        Handles.color = new Color(eyeColor.r, eyeColor.g, eyeColor.b, 0.3f);
        Handles.DrawLine(new Vector3(cx, cy, 0), new Vector3(ex, ey, 0));

        // Current dot
        Handles.color = eyeColor;
        Handles.DrawSolidDisc(new Vector3(ex, ey, 0), Vector3.forward, 6f);

        Handles.EndGUI();

        // Legend
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        var prevColor = GUI.color;
        GUI.color = HoldColor;
        GUILayout.Label("Hold", EditorStyles.miniLabel);
        GUI.color = SaccadeColor;
        GUILayout.Label("Saccade", EditorStyles.miniLabel);
        GUI.color = new Color(1f, 1f, 0.3f);
        GUILayout.Label("Target", EditorStyles.miniLabel);
        GUI.color = prevColor;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField($"Range: +/- {maxDeg:F0} deg", EditorStyles.centeredGreyMiniLabel);
    }

    private void DrawConfigSection(BasisLocalEyeDriver driver)
    {
        EditorGUILayout.LabelField("Max Angle", $"{driver.maxAngleDeg:F1} deg");
        EditorGUILayout.LabelField("Hold Time Range", $"{driver.holdTimeRange.x:F2}s - {driver.holdTimeRange.y:F2}s");
        EditorGUILayout.LabelField("Saccade Time Range", $"{driver.saccadeTimeRange.x:F2}s - {driver.saccadeTimeRange.y:F2}s");
        EditorGUILayout.LabelField("Center Bias", $"{driver.centerBias:F2}");
        EditorGUILayout.LabelField("Per-Eye Variance", $"{driver.perEyeVarianceDeg:F2} deg");
        StatusLabel("Occasional Center Return", driver.occasionalCenterReturn);
    }

    private void DrawCalibrationSection()
    {
        if (!BasisLocalEyeDriver.IsEnabled)
        {
            EditorGUILayout.LabelField("(Eye driver not enabled - no calibration data)");
            return;
        }

        EditorGUILayout.LabelField("Left Eye Calibration", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("  Basis", QuatToString(BasisLocalEyeDriver.calLeft.basis));
        EditorGUILayout.LabelField("  InvBasis", QuatToString(BasisLocalEyeDriver.calLeft.invBasis));
        EditorGUILayout.LabelField("  InitialRot", QuatToString(BasisLocalEyeDriver.calLeft.initialRotation));

        EditorGUILayout.Space(2);

        EditorGUILayout.LabelField("Right Eye Calibration", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("  Basis", QuatToString(BasisLocalEyeDriver.calRight.basis));
        EditorGUILayout.LabelField("  InvBasis", QuatToString(BasisLocalEyeDriver.calRight.invBasis));
        EditorGUILayout.LabelField("  InitialRot", QuatToString(BasisLocalEyeDriver.calRight.initialRotation));

        // Show what axes were detected
        if (BasisLocalEyeDriver.leftEyeTransform != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Detected Axes (world-space, from calibration basis)", EditorStyles.miniLabel);

            float3 leftFwd = math.mul(BasisLocalEyeDriver.calLeft.basis, new float3(0, 0, 1));
            float3 leftUp = math.mul(BasisLocalEyeDriver.calLeft.basis, new float3(0, 1, 0));
            EditorGUILayout.LabelField("  Left Forward", $"({leftFwd.x:F3}, {leftFwd.y:F3}, {leftFwd.z:F3})");
            EditorGUILayout.LabelField("  Left Up", $"({leftUp.x:F3}, {leftUp.y:F3}, {leftUp.z:F3})");

            float3 rightFwd = math.mul(BasisLocalEyeDriver.calRight.basis, new float3(0, 0, 1));
            float3 rightUp = math.mul(BasisLocalEyeDriver.calRight.basis, new float3(0, 1, 0));
            EditorGUILayout.LabelField("  Right Forward", $"({rightFwd.x:F3}, {rightFwd.y:F3}, {rightFwd.z:F3})");
            EditorGUILayout.LabelField("  Right Up", $"({rightUp.x:F3}, {rightUp.y:F3}, {rightUp.z:F3})");
        }
    }

    private void StatusLabel(string label, bool value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
        var prev = GUI.color;
        GUI.color = value ? GoodColor : ErrorColor;
        EditorGUILayout.LabelField(value ? "YES" : "NO", EditorStyles.boldLabel);
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBar(Rect rect, float fill, string label, Color? color = null)
    {
        fill = Mathf.Clamp01(fill);
        EditorGUI.DrawRect(rect, BarBg);
        Rect filled = new Rect(rect.x, rect.y, rect.width * fill, rect.height);
        EditorGUI.DrawRect(filled, color ?? BarColor);
        EditorGUI.LabelField(rect, $"  {label}", EditorStyles.miniLabel);
    }

    private static string QuatToString(quaternion q)
    {
        return $"({q.value.x:F4}, {q.value.y:F4}, {q.value.z:F4}, {q.value.w:F4})";
    }
}
#endif
