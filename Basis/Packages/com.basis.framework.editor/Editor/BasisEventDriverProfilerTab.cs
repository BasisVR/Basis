using Basis.Editor.Localization;
using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The hand-instrumented breakdown of BasisEventDriver's tick: per-callback cost, the job waits the
/// main thread actually paid for, and the counts (receivers, remotes, LOD skips) a marker name can't
/// carry. Reads <see cref="BasisEventDriverProfilerData"/>, which the driver only fills while this
/// tab has it enabled.
/// </summary>
public sealed class BasisEventDriverProfilerTab : BasisEditorTabPage
{
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
    private static readonly Color GraphBg = new Color(0.12f, 0.12f, 0.12f, 1f);

    private static readonly Color ColNetwork = new Color(1f, 0.4f, 0.4f, 0.9f);
    private static readonly Color ColRemoteAudio = new Color(0.4f, 1f, 0.4f, 0.9f);
    private static readonly Color ColRemoteFace = new Color(0.4f, 0.4f, 1f, 0.9f);
    private static readonly Color ColLocal = new Color(1f, 1f, 0.3f, 0.9f);
    private static readonly Color ColJiggle = new Color(1f, 0.5f, 1f, 0.9f);
    private static readonly Color ColTotal = new Color(1f, 1f, 1f, 0.5f);

    // Wide enough for "CompleteScheduledRemoteLerp" — the longest label in the breakdown.
    private const float k_LabelWidth = 210f;

    private float _budgetMs = 11.1f;

    public override string Title => "EventDriver";

    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.eventDriverProfiler.subtitle");

    public override void OnEnable() => BasisEventDriverProfilerData.Enabled = true;

    public override void OnDisable() => BasisEventDriverProfilerData.Enabled = false;

    public override void Draw()
    {
        if (!Application.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.eventDriverProfiler.enterPlayMode"), MessageType.Info);
            return;
        }

        using (BasisEditorUI.Card())
        {
            BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.frame"), BasisEventDriverProfilerData.FrameCount.ToString());
            _budgetMs = EditorGUILayout.Slider(BasisEditorLocalization.Get("editor.eventDriverProfiler.budgetMs"), _budgetMs, 2f, 33.3f);
        }

        Section(ref _showLateUpdate, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionLateUpdate"), DrawLateUpdate);
        Section(ref _showNetworkDeep, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionNetworkApply"), DrawNetworkDeep);
        Section(ref _showTransmitSim, "TransmissionResults.Simulate()", DrawTransmitSim);
        Section(ref _showPoseLod, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionPoseLod"), DrawPoseLod);
        Section(ref _showRemoteAudio, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionRemoteAudio"), DrawRemoteAudio);
        Section(ref _showRemoteFace, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionRemoteFace"), DrawRemoteFace);
        Section(ref _showLocal, BasisEditorLocalization.Get("editor.eventDriverProfiler.localPlayer"), DrawLocal);
        Section(ref _showPhysics, "JigglePhysics", DrawPhysics);
        Section(ref _showMisc, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionMisc"), DrawMisc);
        Section(ref _showThreading, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionJobStatus"), DrawThreading);
        Section(ref _showGraph, BasisEditorLocalization.Get("editor.eventDriverProfiler.sectionFrameHistory"), DrawGraph);
    }

    // ── sections ────────────────────────────────────────────────────────────

    private void DrawLateUpdate()
    {
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.lateUpdateTotal"), BasisEventDriverProfilerData.LateUpdateTotalMs, _budgetMs);
        TimingRow("OnBeforeRender", BasisEventDriverProfilerData.OnBeforeRenderMs, _budgetMs * 0.2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.networkApplyGroup"), BasisEventDriverProfilerData.NetworkApplyMs, 3f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.networkTransmit"), BasisEventDriverProfilerData.NetworkTransmitMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.remoteAudioSimApply"), BasisEventDriverProfilerData.RemoteAudioSimulateMs + BasisEventDriverProfilerData.RemoteAudioApplyMs, 2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.remoteFaceSimApply"), BasisEventDriverProfilerData.RemoteFaceSimulateMs + BasisEventDriverProfilerData.RemoteFaceApplyMs, 2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.localPlayer"), BasisEventDriverProfilerData.LocalPlayerMs, 2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.jiggleAll"), BasisEventDriverProfilerData.JiggleScheduleMs + BasisEventDriverProfilerData.JigglePoseMs + BasisEventDriverProfilerData.JiggleCompletePoseMs, 3f);
    }

    private void DrawNetworkDeep()
    {
        TimingRow("TransmitOwnedPickups", BasisEventDriverProfilerData.Net_TransmitPickupsMs, 0.5f);
        TimingRow("FireJustBeforeNetworkApply", BasisEventDriverProfilerData.Net_FireBeforeApplyMs, 0.5f);
        TimingRow("SimulateNetworkApply", BasisEventDriverProfilerData.Net_SimulateNetworkApplyMs, 3f);
        TimingRow("CompleteScheduledRemoteLerp", BasisEventDriverProfilerData.Net_CompleteRemoteLerpMs, 1f);

        EditorGUILayout.Space(4);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.eventDriverProfiler.insideSimulateNetworkApply"));

        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.interpolationComplete"), BasisEventDriverProfilerData.Net_RemoteDriverApplyMs, 1f);
        JobStatusRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.interpolationJobFromUpdate"), BasisEventDriverProfilerData.Net_InterpolationJobWasIncomplete);

        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.receiverCount"), BasisEventDriverProfilerData.Net_ReceiverCount.ToString());
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.receiverApplyLoop"), BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs, 2f);
        if (BasisEventDriverProfilerData.Net_ReceiverCount > 0)
        {
            double perReceiver = BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs / BasisEventDriverProfilerData.Net_ReceiverCount;
            BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.perReceiverAvg"), $"{perReceiver:F4} ms");
        }

        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.boneJobSchedule"), BasisEventDriverProfilerData.Net_BoneJobScheduleMs, 0.5f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.boneJobComplete"), BasisEventDriverProfilerData.Net_BoneJobCompleteMs, 1f);
        JobStatusRow("BoneJob", BasisEventDriverProfilerData.Net_BoneJobWasIncomplete);

        // How much of ApplySkeletonRotationsJob the write-skip is actually eliding. A low skip
        // share on a crowded instance means the bones genuinely moved; a high one means the
        // transform writes (and their subtree dirtying) are being avoided.
        int boneTotal = BasisEventDriverProfilerData.BoneWrite_Total;
        if (boneTotal > 0)
        {
            int boneSkipped = boneTotal - BasisEventDriverProfilerData.BoneWrite_Written;
            float skipPct = 100f * boneSkipped / boneTotal;
            BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.boneWritesSkipped"), $"{boneSkipped} / {boneTotal}  ({skipPct:F0}%)");
        }

        EditorGUILayout.Space(2);
        double totalStall = BasisEventDriverProfilerData.Net_RemoteDriverApplyMs + BasisEventDriverProfilerData.Net_BoneJobCompleteMs;
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.totalJobStallTime"), $"{totalStall:F3} ms");
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.totalMainThreadWork"), $"{BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs:F3} ms");
    }

    private void DrawTransmitSim()
    {
        BasisEditorUI.PillRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.ranThisTick"),
            BasisEventDriverProfilerData.Net_TransmitSimRanThisTick ? BasisEditorLocalization.Get("editor.eventDriverProfiler.yes") : BasisEditorLocalization.Get("editor.eventDriverProfiler.no"),
            BasisEditorUI.OnOff(BasisEventDriverProfilerData.Net_TransmitSimRanThisTick));
        if (!BasisEventDriverProfilerData.Net_TransmitSimRanThisTick) return;

        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.fillPositions"), BasisEventDriverProfilerData.Net_TransmitSim_FillPositionsMs, 0.5f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.jobSchedule"), BasisEventDriverProfilerData.Net_TransmitSim_JobScheduleMs, 0.2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.avatarCompress"), BasisEventDriverProfilerData.Net_TransmitSim_CompressMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.jobCompleteStall"), BasisEventDriverProfilerData.Net_TransmitSim_JobCompleteMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.postProcessLoop"), BasisEventDriverProfilerData.Net_TransmitSim_PostProcessMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.talkingPoints"), BasisEventDriverProfilerData.Net_TransmitSim_TalkingPointsMs, 0.2f);
    }

    private void DrawPoseLod()
    {
        float bias = BasisEventDriverProfilerData.PoseLod_Bias;
        int applied = BasisEventDriverProfilerData.PoseLod_Applied;
        int skipped = BasisEventDriverProfilerData.PoseLod_Skipped;
        int total = applied + skipped;

        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.biasSetting"), $"{bias:F1}");
        BasisEditorUI.PillRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.active"), bias > 0f ? BasisEditorLocalization.Get("editor.eventDriverProfiler.yes") : BasisEditorLocalization.Get("editor.eventDriverProfiler.no"), BasisEditorUI.OnOff(bias > 0f));

        var skipByLod = SMModuleDistanceBasedReductions.PoseSkipByLod;
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.skipRates"), $"[{skipByLod[0]}, {skipByLod[1]}, {skipByLod[2]}, {skipByLod[3]}]");

        EditorGUILayout.Space(4);
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.lod0"), BasisEventDriverProfilerData.PoseLod_Lod0.ToString());
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.lod1"), BasisEventDriverProfilerData.PoseLod_Lod1.ToString());
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.lod2"), BasisEventDriverProfilerData.PoseLod_Lod2.ToString());
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.lod3"), BasisEventDriverProfilerData.PoseLod_Lod3.ToString());

        EditorGUILayout.Space(4);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.eventDriverProfiler.thisFrame"));
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.setHumanPoseApplied"), applied.ToString());
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.setHumanPoseSkipped"), skipped.ToString());
        if (total > 0)
        {
            float pct = (skipped / (float)total) * 100f;
            BasisEditorUI.Bar(null, skipped / (float)total, BasisEditorLocalization.Get("editor.eventDriverProfiler.percentSkipped", pct),
                pct > 50 ? BasisEditorUI.Good : (pct > 10 ? BasisEditorUI.Warn : BarColor));
        }

        if (bias > 0f && skipped == 0 && total > 0)
        {
            BasisEditorUI.Help(
                BasisEditorLocalization.Get("editor.eventDriverProfiler.biasNotSkipping"),
                MessageType.Warning);
        }
    }

    private void DrawRemoteAudio()
    {
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.driverCount"), BasisEventDriverProfilerData.RemoteAudioDriverCount.ToString());
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.simulateViseme"), BasisEventDriverProfilerData.RemoteAudioSimulateMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.applyViseme"), BasisEventDriverProfilerData.RemoteAudioApplyMs, 1f);
        if (BasisEventDriverProfilerData.RemoteAudioDriverCount > 0)
        {
            double perDriver = (BasisEventDriverProfilerData.RemoteAudioSimulateMs + BasisEventDriverProfilerData.RemoteAudioApplyMs) / BasisEventDriverProfilerData.RemoteAudioDriverCount;
            BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.perDriverAvg"), $"{perDriver:F4} ms");
        }
    }

    private void DrawRemoteFace()
    {
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.remoteCount"), BasisEventDriverProfilerData.RemoteFace_Count.ToString());
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.simulateJobSchedule"), BasisEventDriverProfilerData.RemoteFaceSimulateMs, 0.5f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.applyTotal"), BasisEventDriverProfilerData.RemoteFaceApplyMs, 2f);

        EditorGUILayout.Space(2);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.eventDriverProfiler.insideApply"));
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.jobCompleteStall"), BasisEventDriverProfilerData.RemoteFace_JobCompleteMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.eyeBlinkWriteLoop"), BasisEventDriverProfilerData.RemoteFace_EyeWriteMs, 1f);
        BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.blinkMeshWrites"), BasisEventDriverProfilerData.RemoteFace_BlinkWriteCount.ToString());
        if (BasisEventDriverProfilerData.RemoteFace_Count > 0)
        {
            double perRemote = BasisEventDriverProfilerData.RemoteFace_EyeWriteMs / BasisEventDriverProfilerData.RemoteFace_Count;
            BasisEditorUI.Row(BasisEditorLocalization.Get("editor.eventDriverProfiler.perRemoteAvg"), $"{perRemote:F4} ms");
        }
        JobStatusRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.faceJob"), BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete);
    }

    private void DrawLocal()
    {
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.localPlayerTotal"), BasisEventDriverProfilerData.LocalPlayerMs, 2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.microphone"), BasisEventDriverProfilerData.MicrophoneMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.deviceManagement"), BasisEventDriverProfilerData.DeviceManagementMs, 1f);
    }

    private void DrawPhysics()
    {
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.schedule"), BasisEventDriverProfilerData.JiggleScheduleMs, 2f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.pose"), BasisEventDriverProfilerData.JigglePoseMs, 1f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.completePoseStall"), BasisEventDriverProfilerData.JiggleCompletePoseMs, 2f);
    }

    private void DrawMisc()
    {
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.namePlateSchedule"), BasisEventDriverProfilerData.NamePlateScheduleMs, 0.5f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.namePlateComplete"), BasisEventDriverProfilerData.NamePlateCompleteMs, 0.5f);
        TimingRow("BTween", BasisEventDriverProfilerData.BTweenMs, 0.5f);
        TimingRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.shadowCloneBs"), BasisEventDriverProfilerData.ShadowCloneMs, 0.5f);
    }

    private void DrawThreading()
    {
        JobStatusRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.interpolationJob"), BasisEventDriverProfilerData.Net_InterpolationJobWasIncomplete);
        JobStatusRow("BoneJob", BasisEventDriverProfilerData.Net_BoneJobWasIncomplete);
        JobStatusRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.remoteFaceJob"), BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete);
        JobStatusRow(BasisEditorLocalization.Get("editor.eventDriverProfiler.namePlateJob"), BasisEventDriverProfilerData.NamePlateJobWasIncomplete);
        EditorGUILayout.Space(2);
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eventDriverProfiler.stalledNote"));
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eventDriverProfiler.idealNote"));
    }

    // ── widgets ─────────────────────────────────────────────────────────────

    private void Section(ref bool expanded, string title, Action drawContent)
    {
        if (BasisEditorUI.BeginFoldout(ref expanded, title))
        {
            drawContent();
        }
        BasisEditorUI.EndFoldout();
    }

    private void TimingRow(string label, double ms, float warnThreshold)
    {
        Color barCol = ms > warnThreshold
            ? (ms > warnThreshold * 2 ? BasisEditorUI.Bad : BasisEditorUI.Warn)
            : BarColor;
        BasisEditorUI.Bar(label, (float)(ms / _budgetMs), $"{ms:F3} ms", barCol, k_LabelWidth);
    }

    private void JobStatusRow(string label, bool wasIncomplete) =>
        BasisEditorUI.PillRow(label, wasIncomplete ? BasisEditorLocalization.Get("editor.eventDriverProfiler.stalled") : BasisEditorLocalization.Get("editor.common.ok"),
            wasIncomplete ? BasisEditorUI.State.Warn : BasisEditorUI.State.Good);

    private void DrawGraph()
    {
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        DrawLegendSwatch(ColTotal, BasisEditorLocalization.Get("editor.eventDriverProfiler.legendTotal"));
        DrawLegendSwatch(ColNetwork, BasisEditorLocalization.Get("editor.eventDriverProfiler.legendNetwork"));
        DrawLegendSwatch(ColLocal, BasisEditorLocalization.Get("editor.eventDriverProfiler.legendLocal"));
        DrawLegendSwatch(ColRemoteAudio, BasisEditorLocalization.Get("editor.eventDriverProfiler.legendAudio"));
        DrawLegendSwatch(ColRemoteFace, BasisEditorLocalization.Get("editor.eventDriverProfiler.legendFace"));
        DrawLegendSwatch(ColJiggle, BasisEditorLocalization.Get("editor.eventDriverProfiler.legendJiggle"));
        EditorGUILayout.EndHorizontal();

        Rect graphRect = GUILayoutUtility.GetRect(0, 140, GUILayout.ExpandWidth(true));
        BasisEditorUI.Fill(graphRect, GraphBg, 4f);

        int histLen = BasisEventDriverProfilerData.HistorySize;
        int current = BasisEventDriverProfilerData.HistoryIndex;
        if (current == 0) return;

        int drawCount = Mathf.Min(current, (int)graphRect.width);
        float maxMs = _budgetMs * 2f;
        float budgetY = graphRect.yMax - ((_budgetMs / maxMs) * graphRect.height);

        Handles.BeginGUI();

        Handles.color = new Color(1f, 0f, 0f, 0.5f);
        Handles.DrawLine(new Vector3(graphRect.x, budgetY), new Vector3(graphRect.xMax, budgetY));

        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.LateUpdateHistory, histLen, current, drawCount, maxMs, ColTotal);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.NetworkApplyHistory, histLen, current, drawCount, maxMs, ColNetwork);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.LocalPlayerHistory, histLen, current, drawCount, maxMs, ColLocal);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.RemoteAudioHistory, histLen, current, drawCount, maxMs, ColRemoteAudio);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.RemoteFaceHistory, histLen, current, drawCount, maxMs, ColRemoteFace);
        DrawGraphLayer(graphRect, BasisEventDriverProfilerData.JiggleHistory, histLen, current, drawCount, maxMs, ColJiggle);

        Handles.EndGUI();

        EditorGUI.LabelField(new Rect(graphRect.x + 2, graphRect.y, 100, 16), $"{maxMs:F0} ms", EditorStyles.miniLabel);
        EditorGUI.LabelField(new Rect(graphRect.x + 2, budgetY - 14, 100, 16), BasisEditorLocalization.Get("editor.eventDriverProfiler.budgetLabel", _budgetMs), EditorStyles.miniLabel);
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
        BasisEditorUI.Fill(r, color, 2f);
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(65));
    }
}
