using UnityEditor;
using UnityEngine;

public class BasisEventDriverProfilerWindow : EditorWindow
{
    private Vector2 _scrollPos;
    private bool _showLateUpdate = true;
    private bool _showNetworkDeep = true;
    private bool _showTransmitSim = true;
    private bool _showRemoteAudio = true;
    private bool _showRemoteFace = true;
    private bool _showLocal = true;
    private bool _showPhysics = true;
    private bool _showMisc = true;
    private bool _showPoseLod = true;
    private bool _showGraph = true;
    private bool _showThreading = true;

    private static readonly Color BarColor = new Color(0.2f, 0.7f, 1f, 0.8f);
    private static readonly Color BarBgColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    private static readonly Color GoodColor = new Color(0.3f, 0.9f, 0.3f);
    private static readonly Color WarnColor = new Color(1f, 0.8f, 0.2f);
    private static readonly Color ErrorColor = new Color(1f, 0.3f, 0.3f);
    private static readonly Color GraphBg = new Color(0.12f, 0.12f, 0.12f, 1f);

    private static readonly Color ColNetwork = new Color(1f, 0.4f, 0.4f, 0.9f);
    private static readonly Color ColRemoteAudio = new Color(0.4f, 1f, 0.4f, 0.9f);
    private static readonly Color ColRemoteFace = new Color(0.4f, 0.4f, 1f, 0.9f);
    private static readonly Color ColLocal = new Color(1f, 1f, 0.3f, 0.9f);
    private static readonly Color ColJiggle = new Color(1f, 0.5f, 1f, 0.9f);
    private static readonly Color ColBlendShape = new Color(0.3f, 1f, 1f, 0.9f);
    private static readonly Color ColTotal = new Color(1f, 1f, 1f, 0.5f);

    private float _budgetMs = 11.1f;

    [MenuItem("Basis/Debug/EventDriver Profiler")]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisEventDriverProfilerWindow>("EventDriver Profiler");
        w.minSize = new Vector2(450, 600);
    }

    private void OnEnable()
    {
        EditorApplication.update += Repaint;
        BasisEventDriverProfilerData.Enabled = true;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Repaint;
        BasisEventDriverProfilerData.Enabled = false;
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        EditorGUILayout.LabelField("BasisEventDriver Profiler", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Per-frame timing breakdown — deeper into Network Apply + Remote Face", EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to see live data.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.LabelField($"Frame: {BasisEventDriverProfilerData.FrameCount}");
        _budgetMs = EditorGUILayout.Slider("Budget (ms)", _budgetMs, 2f, 33.3f);
        EditorGUILayout.Space(4);

        // ── LateUpdate total ──
        _showLateUpdate = DrawSection("LateUpdate Overview", _showLateUpdate, () =>
        {
            TimingRow("LateUpdate Total", BasisEventDriverProfilerData.LateUpdateTotalMs, _budgetMs);
            TimingRow("OnBeforeRender", BasisEventDriverProfilerData.OnBeforeRenderMs, _budgetMs * 0.2f);
            TimingRow("Network Apply (group)", BasisEventDriverProfilerData.NetworkApplyMs, 3f);
            TimingRow("Network Transmit", BasisEventDriverProfilerData.NetworkTransmitMs, 1f);
            TimingRow("Remote Audio (sim+apply)", BasisEventDriverProfilerData.RemoteAudioSimulateMs + BasisEventDriverProfilerData.RemoteAudioApplyMs, 2f);
            TimingRow("Remote Face (sim+apply)", BasisEventDriverProfilerData.RemoteFaceSimulateMs + BasisEventDriverProfilerData.RemoteFaceApplyMs, 2f);
            TimingRow("Local Player", BasisEventDriverProfilerData.LocalPlayerMs, 2f);
            TimingRow("JigglePhysics (all)", BasisEventDriverProfilerData.JiggleScheduleMs + BasisEventDriverProfilerData.JigglePoseMs + BasisEventDriverProfilerData.JiggleCompletePoseMs, 3f);
            TimingRow("BlendShapes (sim+apply)", BasisEventDriverProfilerData.BlendShapeSimulateMs + BasisEventDriverProfilerData.BlendShapeApplyMs, 1f);
        });

        // ── Network Apply Deep ──
        _showNetworkDeep = DrawSection("Network Apply Breakdown", _showNetworkDeep, () =>
        {
            TimingRow("TransmitOwnedPickups", BasisEventDriverProfilerData.Net_TransmitPickupsMs, 0.5f);
            TimingRow("FireJustBeforeNetworkApply", BasisEventDriverProfilerData.Net_FireBeforeApplyMs, 0.5f);
            TimingRow("SimulateNetworkApply", BasisEventDriverProfilerData.Net_SimulateNetworkApplyMs, 3f);
            TimingRow("CompleteScheduledRemoteLerp", BasisEventDriverProfilerData.Net_CompleteRemoteLerpMs, 1f);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Inside SimulateNetworkApply:", EditorStyles.boldLabel);

            TimingRow("  Interpolation Complete (stall)", BasisEventDriverProfilerData.Net_RemoteDriverApplyMs, 1f);
            JobStatusRow("  Interpolation Job (from Update)", BasisEventDriverProfilerData.Net_InterpolationJobWasIncomplete);

            InfoRow("  Receiver Count", BasisEventDriverProfilerData.Net_ReceiverCount.ToString());
            TimingRow("  Receiver Apply Loop", BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs, 2f);
            if (BasisEventDriverProfilerData.Net_ReceiverCount > 0)
            {
                double perReceiver = BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs / BasisEventDriverProfilerData.Net_ReceiverCount;
                InfoRow("  Per-Receiver Avg", $"{perReceiver:F4} ms");
            }

            TimingRow("  BoneJob Schedule", BasisEventDriverProfilerData.Net_BoneJobScheduleMs, 0.5f);
            TimingRow("  BoneJob Complete (stall)", BasisEventDriverProfilerData.Net_BoneJobCompleteMs, 1f);
            JobStatusRow("  BoneJob", BasisEventDriverProfilerData.Net_BoneJobWasIncomplete);

            EditorGUILayout.Space(2);
            double totalStall = BasisEventDriverProfilerData.Net_RemoteDriverApplyMs + BasisEventDriverProfilerData.Net_BoneJobCompleteMs;
            double totalWork = BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs;
            InfoRow("  Total Job Stall Time", $"{totalStall:F3} ms");
            InfoRow("  Total Main Thread Work", $"{totalWork:F3} ms");
        });

        // ── TransmissionResults.Simulate ──
        _showTransmitSim = DrawSection("TransmissionResults.Simulate()", _showTransmitSim, () =>
        {
            StatusLabel("Ran This Tick", BasisEventDriverProfilerData.Net_TransmitSimRanThisTick);
            if (BasisEventDriverProfilerData.Net_TransmitSimRanThisTick)
            {
                TimingRow("Fill Positions", BasisEventDriverProfilerData.Net_TransmitSim_FillPositionsMs, 0.5f);
                TimingRow("Job Schedule", BasisEventDriverProfilerData.Net_TransmitSim_JobScheduleMs, 0.2f);
                TimingRow("Avatar Compress", BasisEventDriverProfilerData.Net_TransmitSim_CompressMs, 1f);
                TimingRow("Job Complete (stall)", BasisEventDriverProfilerData.Net_TransmitSim_JobCompleteMs, 1f);
                TimingRow("Post-Process Loop", BasisEventDriverProfilerData.Net_TransmitSim_PostProcessMs, 1f);
                TimingRow("Talking Points", BasisEventDriverProfilerData.Net_TransmitSim_TalkingPointsMs, 0.2f);
            }
        });

        // ── Pose LOD ──
        _showPoseLod = DrawSection("Pose LOD Diagnostics", _showPoseLod, () =>
        {
            float bias = BasisEventDriverProfilerData.PoseLod_Bias;
            int applied = BasisEventDriverProfilerData.PoseLod_Applied;
            int skipped = BasisEventDriverProfilerData.PoseLod_Skipped;
            int total = applied + skipped;

            InfoRow("Bias (setting)", $"{bias:F1}");
            StatusLabel("Active", bias > 0f);

            var skipByLod = SMModuleDistanceBasedReductions.PoseSkipByLod;
            InfoRow("Skip Rates [L0,L1,L2,L3]", $"[{skipByLod[0]}, {skipByLod[1]}, {skipByLod[2]}, {skipByLod[3]}]");

            EditorGUILayout.Space(4);
            InfoRow("LOD 0 (closest)", BasisEventDriverProfilerData.PoseLod_Lod0.ToString());
            InfoRow("LOD 1", BasisEventDriverProfilerData.PoseLod_Lod1.ToString());
            InfoRow("LOD 2", BasisEventDriverProfilerData.PoseLod_Lod2.ToString());
            InfoRow("LOD 3 (furthest)", BasisEventDriverProfilerData.PoseLod_Lod3.ToString());

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("This Frame:", EditorStyles.boldLabel);
            InfoRow("SetHumanPose Applied", applied.ToString());
            InfoRow("SetHumanPose Skipped", skipped.ToString());
            if (total > 0)
            {
                float pct = (skipped / (float)total) * 100f;
                Rect barRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
                DrawBar(barRect, skipped / (float)total, $"{pct:F0}% skipped ({skipped}/{total})",
                    pct > 50 ? GoodColor : (pct > 10 ? WarnColor : BarColor));
            }

            if (bias > 0f && skipped == 0 && total > 0)
            {
                EditorGUILayout.HelpBox(
                    "Bias is set but nothing is being skipped.\n" +
                    "Check that CurrentLodLevel is being set on remote players.\n" +
                    "If all players are LOD 0, nothing will be skipped.",
                    MessageType.Warning);
            }
        });

        // ── Remote Audio ──
        _showRemoteAudio = DrawSection("Remote Audio", _showRemoteAudio, () =>
        {
            InfoRow("Driver Count", BasisEventDriverProfilerData.RemoteAudioDriverCount.ToString());
            TimingRow("Simulate (viseme decode)", BasisEventDriverProfilerData.RemoteAudioSimulateMs, 1f);
            TimingRow("Apply (viseme write)", BasisEventDriverProfilerData.RemoteAudioApplyMs, 1f);
            if (BasisEventDriverProfilerData.RemoteAudioDriverCount > 0)
            {
                double perDriver = (BasisEventDriverProfilerData.RemoteAudioSimulateMs + BasisEventDriverProfilerData.RemoteAudioApplyMs) / BasisEventDriverProfilerData.RemoteAudioDriverCount;
                InfoRow("Per-Driver Avg", $"{perDriver:F4} ms");
            }
        });

        // ── Remote Face ──
        _showRemoteFace = DrawSection("Remote Face", _showRemoteFace, () =>
        {
            InfoRow("Remote Count", BasisEventDriverProfilerData.RemoteFace_Count.ToString());
            TimingRow("Simulate (job schedule)", BasisEventDriverProfilerData.RemoteFaceSimulateMs, 0.5f);
            TimingRow("Apply Total", BasisEventDriverProfilerData.RemoteFaceApplyMs, 2f);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Inside Apply:", EditorStyles.boldLabel);
            TimingRow("  Job Complete (stall)", BasisEventDriverProfilerData.RemoteFace_JobCompleteMs, 1f);
            TimingRow("  Eye+Blink Write Loop", BasisEventDriverProfilerData.RemoteFace_EyeWriteMs, 1f);
            InfoRow("  Blink Mesh Writes", BasisEventDriverProfilerData.RemoteFace_BlinkWriteCount.ToString());
            if (BasisEventDriverProfilerData.RemoteFace_Count > 0)
            {
                double perRemote = BasisEventDriverProfilerData.RemoteFace_EyeWriteMs / BasisEventDriverProfilerData.RemoteFace_Count;
                InfoRow("  Per-Remote Avg", $"{perRemote:F4} ms");
            }
            JobStatusRow("  Face Job", BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete);
        });

        // ── Local ──
        _showLocal = DrawSection("Local Player", _showLocal, () =>
        {
            TimingRow("Local Player Total", BasisEventDriverProfilerData.LocalPlayerMs, 2f);
            TimingRow("Microphone", BasisEventDriverProfilerData.MicrophoneMs, 1f);
            TimingRow("Device Management", BasisEventDriverProfilerData.DeviceManagementMs, 1f);
        });

        // ── Physics ──
        _showPhysics = DrawSection("JigglePhysics", _showPhysics, () =>
        {
            TimingRow("Schedule", BasisEventDriverProfilerData.JiggleScheduleMs, 2f);
            TimingRow("Pose", BasisEventDriverProfilerData.JigglePoseMs, 1f);
            TimingRow("Complete Pose (stall)", BasisEventDriverProfilerData.JiggleCompletePoseMs, 2f);
        });

        // ── Misc ──
        _showMisc = DrawSection("Misc", _showMisc, () =>
        {
            TimingRow("NamePlate Schedule", BasisEventDriverProfilerData.NamePlateScheduleMs, 0.5f);
            TimingRow("NamePlate Complete", BasisEventDriverProfilerData.NamePlateCompleteMs, 0.5f);
            TimingRow("BTween", BasisEventDriverProfilerData.BTweenMs, 0.5f);
            TimingRow("BlendShape Simulate", BasisEventDriverProfilerData.BlendShapeSimulateMs, 0.5f);
            TimingRow("BlendShape Apply", BasisEventDriverProfilerData.BlendShapeApplyMs, 0.5f);
            TimingRow("Shadow Clone BS", BasisEventDriverProfilerData.ShadowCloneMs, 0.5f);
        });

        // ── Threading ──
        _showThreading = DrawSection("Job Completion Status", _showThreading, () =>
        {
            JobStatusRow("Interpolation Job", BasisEventDriverProfilerData.Net_InterpolationJobWasIncomplete);
            JobStatusRow("BoneJob", BasisEventDriverProfilerData.Net_BoneJobWasIncomplete);
            JobStatusRow("Remote Face Job", BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete);
            JobStatusRow("NamePlate Job", BasisEventDriverProfilerData.NamePlateJobWasIncomplete);
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("STALLED = main thread waited for job to finish.", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Ideally all jobs complete before their Apply call.", EditorStyles.miniLabel);
        });

        // ── Graph ──
        _showGraph = DrawSection("Frame History", _showGraph, DrawGraph);

        EditorGUILayout.EndScrollView();
    }

    // ────────────────────────────────────────────────────────────────────

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

    private void TimingRow(string label, double ms, float warnThreshold)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(220));

        float fill = Mathf.Clamp01((float)(ms / _budgetMs));
        Rect barRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        Color barCol = ms > warnThreshold ? (ms > warnThreshold * 2 ? ErrorColor : WarnColor) : BarColor;
        DrawBar(barRect, fill, $"{ms:F3} ms", barCol);

        EditorGUILayout.EndHorizontal();
    }

    private void InfoRow(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(220));
        EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();
    }

    private void StatusLabel(string label, bool value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(220));
        var prev = GUI.color;
        GUI.color = value ? GoodColor : new Color(0.5f, 0.5f, 0.5f);
        EditorGUILayout.LabelField(value ? "YES" : "NO", EditorStyles.boldLabel);
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();
    }

    private void JobStatusRow(string label, bool wasIncomplete)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(220));
        var prev = GUI.color;
        if (wasIncomplete)
        {
            GUI.color = WarnColor;
            EditorGUILayout.LabelField("STALLED", EditorStyles.boldLabel);
        }
        else
        {
            GUI.color = GoodColor;
            EditorGUILayout.LabelField("OK", EditorStyles.boldLabel);
        }
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBar(Rect rect, float fill, string label, Color color)
    {
        fill = Mathf.Clamp01(fill);
        EditorGUI.DrawRect(rect, BarBgColor);
        Rect filled = new Rect(rect.x, rect.y, rect.width * fill, rect.height);
        EditorGUI.DrawRect(filled, color);
        EditorGUI.LabelField(rect, $"  {label}", EditorStyles.miniLabel);
    }

    private void DrawGraph()
    {
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        DrawLegendSwatch(ColTotal, "Total");
        DrawLegendSwatch(ColNetwork, "Network");
        DrawLegendSwatch(ColLocal, "Local");
        DrawLegendSwatch(ColRemoteAudio, "Audio");
        DrawLegendSwatch(ColRemoteFace, "Face");
        DrawLegendSwatch(ColJiggle, "Jiggle");
        DrawLegendSwatch(ColBlendShape, "BlendShape");
        EditorGUILayout.EndHorizontal();

        Rect graphRect = GUILayoutUtility.GetRect(0, 140, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(graphRect, GraphBg);

        int histLen = BasisEventDriverProfilerData.HistorySize;
        int current = BasisEventDriverProfilerData.HistoryIndex;
        if (current == 0) return;

        int drawCount = Mathf.Min(current, (int)graphRect.width);
        float maxMs = _budgetMs * 2f;

        float budgetY = graphRect.yMax - ((_budgetMs / maxMs) * graphRect.height);

        Handles.BeginGUI();

        // Budget line
        Handles.color = new Color(1f, 0f, 0f, 0.5f);
        Handles.DrawLine(new Vector3(graphRect.x, budgetY), new Vector3(graphRect.xMax, budgetY));

        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.LateUpdateHistory, histLen, current, drawCount, maxMs, ColTotal);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.NetworkApplyHistory, histLen, current, drawCount, maxMs, ColNetwork);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.LocalPlayerHistory, histLen, current, drawCount, maxMs, ColLocal);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.RemoteAudioHistory, histLen, current, drawCount, maxMs, ColRemoteAudio);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.RemoteFaceHistory, histLen, current, drawCount, maxMs, ColRemoteFace);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.JiggleHistory, histLen, current, drawCount, maxMs, ColJiggle);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.BlendShapeHistory, histLen, current, drawCount, maxMs, ColBlendShape);

        Handles.EndGUI();

        EditorGUI.LabelField(new Rect(graphRect.x + 2, graphRect.y, 100, 16), $"{maxMs:F0} ms", EditorStyles.miniLabel);
        EditorGUI.LabelField(new Rect(graphRect.x + 2, budgetY - 14, 100, 16), $"budget {_budgetMs:F1} ms", EditorStyles.miniLabel);
    }

    private void DrawGraphLayer(Rect rect, double[] history, int histLen, int current, int drawCount, float maxMs, Color color)
    {
        Handles.color = color;
        for (int i = 1; i < drawCount; i++)
        {
            int idx0 = (current - drawCount + i - 1) % histLen;
            int idx1 = (current - drawCount + i) % histLen;
            if (idx0 < 0) idx0 += histLen;
            if (idx1 < 0) idx1 += histLen;

            float x0 = rect.x + (i - 1);
            float x1 = rect.x + i;
            float y0 = rect.yMax - ((float)(history[idx0] / maxMs) * rect.height);
            float y1 = rect.yMax - ((float)(history[idx1] / maxMs) * rect.height);

            y0 = Mathf.Clamp(y0, rect.y, rect.yMax);
            y1 = Mathf.Clamp(y1, rect.y, rect.yMax);

            Handles.DrawLine(new Vector3(x0, y0), new Vector3(x1, y1));
        }
    }

    private void DrawLegendSwatch(Color color, string label)
    {
        Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12));
        EditorGUI.DrawRect(r, color);
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(65));
    }
}
