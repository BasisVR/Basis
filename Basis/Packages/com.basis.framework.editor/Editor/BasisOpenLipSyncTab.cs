using System;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using OpenLipSync.Inference.OVRCompat;
using UnityEditor;
using UnityEngine;

public sealed class BasisOpenLipSyncTab : BasisEditorTabPage
{
    public override string Title => "OpenLipSync";
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.openLipSync.subtitle");

    private bool _showBackend = true;
    private bool _showMicrophone = true;
    private bool _showDriver = true;
    private bool _showBuffers = true;
    private bool _showVisemes = true;

    private static readonly string[] VisemeLabels = {
        "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS",
        "nn", "RR", "aa", "E", "ih", "oh", "ou"
    };

    private static readonly Color BarColor = new Color(0.2f, 0.7f, 1f, 0.8f);
    private static readonly Color BarBgColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    private static readonly Color GoodColor = new Color(0.3f, 0.9f, 0.3f);
    private static readonly Color WarnColor = new Color(1f, 0.8f, 0.2f);
    private static readonly Color ErrorColor = new Color(1f, 0.3f, 0.3f);

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
        DrawHeader();
        EditorGUILayout.Space(4);

        _showBackend = DrawSection(BasisEditorLocalization.Get("editor.openLipSync.section.backend"), _showBackend, DrawBackendSection);
        _showMicrophone = DrawSection(BasisEditorLocalization.Get("editor.openLipSync.section.microphone"), _showMicrophone, DrawMicrophoneSection);
        _showDriver = DrawSection(BasisEditorLocalization.Get("editor.openLipSync.section.driver"), _showDriver, DrawDriverSection);
        _showBuffers = DrawSection(BasisEditorLocalization.Get("editor.openLipSync.section.pipeline"), _showBuffers, DrawBufferSection);
        _showVisemes = DrawSection(BasisEditorLocalization.Get("editor.openLipSync.section.visemes"), _showVisemes, DrawVisemeSection);

    }

    private void DrawHeader()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.openLipSync.header"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.flow"),
            EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.openLipSync.playModeOnly"), MessageType.Info);
        }
    }

    private bool DrawSection(string title, bool expanded, Action drawContent)
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

    private void DrawBackendSection()
    {
        bool init = BasisOpenLipSyncDriver.IsInitialized;
        StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.initialized"), init);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.activeSlots"),
            $"{BasisOpenLipSyncDriver.ActiveSlotCount} / {BasisOpenLipSyncDriver.MaxSlots}");

        float fill = BasisOpenLipSyncDriver.MaxSlots > 0
            ? (float)BasisOpenLipSyncDriver.ActiveSlotCount / BasisOpenLipSyncDriver.MaxSlots
            : 0f;
        Rect barRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        DrawBar(barRect, fill, BasisEditorLocalization.Get("editor.openLipSync.slotUsage"));

        if (!init)
        {
            EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.openLipSync.backendNotInitializedHelp"), MessageType.Warning);
        }

        if (Application.isPlaying)
        {
            EditorGUILayout.Space(4);
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.openLipSync.deepStatus"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.melFramesTotal"), BasisOpenLipSyncDriver.DebugMelFramesProduced.ToString());
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.inferenceRunsTotal"), BasisOpenLipSyncDriver.DebugInferenceRuns.ToString());
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.lastInferenceMax"), $"{BasisOpenLipSyncDriver.DebugLastInferenceMax:F6}");
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.pipeline"), BasisOpenLipSyncDriver.DebugPipelineStatus ?? "-");

            var inferDetail = BasisOpenLipSyncDriver.DebugInferenceDetail;
            if (!string.IsNullOrEmpty(inferDetail))
            {
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.inference"), inferDetail);
            }

            if (BasisOpenLipSyncDriver.DebugMelFramesProduced == 0 && BasisOpenLipSyncDriver.DebugInferenceRuns == 0)
            {
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.openLipSync.zeroMelHelp"), MessageType.Error);
            }
            else if (BasisOpenLipSyncDriver.DebugLastInferenceMax < 0.001f)
            {
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.openLipSync.inferenceSilentHelp"), MessageType.Warning);
            }

            var err = BasisOpenLipSyncDriver.DebugBackendError;
            if (!string.IsNullOrEmpty(err))
            {
                BasisEditorUI.Help(BasisEditorLocalization.Get("editor.openLipSync.backendError", err), MessageType.Error);
            }
        }
    }

    private void DrawMicrophoneSection()
    {
        if (!Application.isPlaying)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.playModeOnlyShort"));
            return;
        }

#if !BASIS_DISABLE_MICROPHONE
        StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.initialized"), BasisLocalMicrophoneDriver.IsInitialize);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.paused"), BasisLocalMicrophoneDriver.isPaused.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.device"), BasisLocalMicrophoneDriver.MicrophoneDevice ?? BasisEditorLocalization.Get("editor.openLipSync.deviceNone"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.sampleRate"), $"{LocalOpusSettings.MicrophoneSampleRate} Hz");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.frameLength"), BasisEditorLocalization.Get("editor.openLipSync.samplesValue", BasisLocalMicrophoneDriver.ProcessFrameLength));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.volume"), $"{BasisLocalMicrophoneDriver.Volume:F3}");

        float rms = BasisLocalMicrophoneDriver.averageRms;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.averageRms"), $"{rms:F6}");
        Rect rmsRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        float rmsNorm = Mathf.Clamp01(rms * 50f);
        DrawBar(rmsRect, rmsNorm, BasisEditorLocalization.Get("editor.openLipSync.audioLevel"));

        var buf = BasisLocalMicrophoneDriver.processBufferArray;
        if (buf != null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.processBuffer"), BasisEditorLocalization.Get("editor.openLipSync.samplesValue", buf.Length));

            Rect waveRect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            DrawMiniWaveform(waveRect, buf);
        }
#else
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.microphoneDisabled"));
#endif
    }

    private void DrawDriverSection()
    {
        if (!Application.isPlaying || !BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.waitingForPlayer"));
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalVisemeDriver;
        StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.successfulInit"), driver.WasSuccessful);
        StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.useOpenLipSync"), driver.UseOpenLipSync);
        StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.faceVisibleEnabled"), driver.FaceVisible);

        if (driver.Avatar != null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.avatar"), driver.Avatar.name);
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.blendshapeCount"), driver.BlendShapeCount.ToString());
        }

        if (driver.UseOpenLipSync && driver.openLipSyncContext != null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.backend"), BasisEditorLocalization.Get("editor.openLipSync.backendValue"));
            StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.contextInitialized"), driver.openLipSyncContext.IsInitialized);
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.contextHandle"),
                $"#{driver.openLipSyncContext.DebugContextHandle}");
            StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.faceVisible"), driver.openLipSyncContext.DebugFaceVisible);
            StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.backgroundTaskRunning"), driver.openLipSyncContext.DebugTaskRunning);
        }
        else
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.backend"), BasisEditorLocalization.Get("editor.openLipSync.backendNoSlot"));

            string reason = !BasisOpenLipSyncDriver.IsInitialized
                ? BasisEditorLocalization.Get("editor.openLipSync.reason.notInitialized")
                : BasisOpenLipSyncDriver.ActiveSlotCount >= BasisOpenLipSyncDriver.MaxSlots
                    ? BasisEditorLocalization.Get("editor.openLipSync.reason.slotsOccupied")
                    : BasisEditorLocalization.Get("editor.openLipSync.reason.acquisitionFailed");
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.openLipSync.noContext", reason), MessageType.Info);
        }
    }

    private void DrawBufferSection()
    {
        if (!Application.isPlaying || !BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.waitingForPlayer"));
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalVisemeDriver;
        if (!driver.UseOpenLipSync || driver.openLipSyncContext == null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.noBufferData"));
            return;
        }

        var ctx = driver.openLipSyncContext;

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.openLipSync.audioBuffers"));
        int activeBuffer = ctx.DebugActiveBuffer;
        int writeA = ctx.DebugWriteIndexA;
        int writeB = ctx.DebugWriteIndexB;

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.activeWriteBuffer"), activeBuffer == 0 ? "A" : "B");

        Rect barA = GUILayoutUtility.GetRect(0, 12, GUILayout.ExpandWidth(true));
        DrawBar(barA, writeA / 48000f, BasisEditorLocalization.Get(activeBuffer == 0 ? "editor.openLipSync.bufAWriting" : "editor.openLipSync.bufAFrozen", writeA));

        Rect barB = GUILayoutUtility.GetRect(0, 12, GUILayout.ExpandWidth(true));
        DrawBar(barB, writeB / 48000f, BasisEditorLocalization.Get(activeBuffer == 1 ? "editor.openLipSync.bufBWriting" : "editor.openLipSync.bufBFrozen", writeB));

        EditorGUILayout.Space(4);

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.openLipSync.threading"));
        StatusLabel(BasisEditorLocalization.Get("editor.openLipSync.backgroundTaskActive"), ctx.DebugTaskRunning);

        EditorGUILayout.Space(4);

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.openLipSync.visemeMap"));
        var hasV = ctx.DebugHasViseme;
        var mapV = ctx.DebugVisemeToBlendShape;
        if (hasV != null && mapV != null)
        {
            int mapped = 0;
            for (int i = 0; i < BasisOpenLipSyncContext.VisemeCount; i++)
            {
                string label = i < VisemeLabels.Length ? VisemeLabels[i] : $"#{i}";
                if (hasV[i])
                {
                    EditorGUILayout.LabelField($"  [{i}] {label}", $"-> bs #{mapV[i]}");
                    mapped++;
                }
                else
                {
                    var p2 = GUI.color;
                    GUI.color = WarnColor;
                    EditorGUILayout.LabelField($"  [{i}] {label}", BasisEditorLocalization.Get("editor.openLipSync.unmapped"));
                    GUI.color = p2;
                }
            }
            EditorGUILayout.LabelField("  " + BasisEditorLocalization.Get("editor.openLipSync.visemesMapped", mapped));
        }
    }

    private void DrawVisemeSection()
    {
        if (!Application.isPlaying || !BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.waitingForPlayer"));
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalVisemeDriver;
        if (!driver.UseOpenLipSync || driver.openLipSyncContext == null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.notActive"));
            return;
        }

        var ctx = driver.openLipSyncContext;
        var cached = ctx.DebugVisemeWeights;
        var applied = ctx.LastApplied;

        float cachedSum = 0f, cachedMax = 0f;
        int cachedPeak = 0;
        if (cached != null)
        {
            for (int i = 0; i < cached.Length; i++)
            {
                cachedSum += cached[i];
                if (cached[i] > cachedMax) { cachedMax = cached[i]; cachedPeak = i; }
            }
        }

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.openLipSync.cachedWeights"));
        EditorGUILayout.LabelField("  " + BasisEditorLocalization.Get("editor.openLipSync.cachedSummary", cachedSum, cachedMax, SafeLabel(cachedPeak), cachedSum < 0.0001f));

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("", GUILayout.Width(30));
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.openLipSync.bar"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.weight"), EditorStyles.miniLabel, GUILayout.Width(50));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.openLipSync.applied"), EditorStyles.miniLabel, GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        for (int i = 0; i < 15; i++)
        {
            float cw = (cached != null && i < cached.Length) ? cached[i] : 0f;
            float app = (applied != null && i < applied.Length) ? applied[i] : 0f;

            bool isPeak = i == cachedPeak && cachedMax > 0.01f;

            EditorGUILayout.BeginHorizontal();

            var style = isPeak ? EditorStyles.boldLabel : EditorStyles.label;
            EditorGUILayout.LabelField(SafeLabel(i), style, GUILayout.Width(30));

            Rect barRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            Color barCol = isPeak ? new Color(1f, 0.5f, 0.1f, 0.9f) : BarColor;
            DrawBar(barRect, Mathf.Clamp01(cw), $"{cw:F3}", barCol);

            EditorGUILayout.LabelField($"{cw:F4}", GUILayout.Width(50));
            EditorGUILayout.LabelField($"{app:F1}", GUILayout.Width(50));

            EditorGUILayout.EndHorizontal();
        }
    }

    private string SafeLabel(int idx)
    {
        return idx < VisemeLabels.Length ? VisemeLabels[idx] : $"#{idx}";
    }

    private void StatusLabel(string label, bool value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
        var prev = GUI.color;
        GUI.color = value ? GoodColor : ErrorColor;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get(value ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"), EditorStyles.boldLabel);
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBar(Rect rect, float fill, string label, Color? color = null)
    {
        fill = Mathf.Clamp01(fill);
        EditorGUI.DrawRect(rect, BarBgColor);
        Rect filled = new Rect(rect.x, rect.y, rect.width * fill, rect.height);
        EditorGUI.DrawRect(filled, color ?? BarColor);
        EditorGUI.LabelField(rect, $"  {label}", BasisEditorUI.OnDarkMiniLabel);
    }

    private void DrawMiniWaveform(Rect rect, float[] samples)
    {
        EditorGUI.DrawRect(rect, BarBgColor);

        if (samples == null || samples.Length == 0) return;

        int width = (int)rect.width;
        float midY = rect.y + rect.height * 0.5f;
        float halfH = rect.height * 0.45f;
        int step = Mathf.Max(1, samples.Length / width);

        Handles.color = BarColor;
        Handles.BeginGUI();
        for (int x = 0; x < width && x * step < samples.Length; x++)
        {
            float s = samples[x * step];
            float y = midY - s * halfH;
            y = Mathf.Clamp(y, rect.y, rect.yMax);
            Handles.DrawLine(
                new Vector3(rect.x + x, midY, 0),
                new Vector3(rect.x + x, y, 0));
        }
        Handles.EndGUI();
    }
}
