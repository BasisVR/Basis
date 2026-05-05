using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using OpenLipSync.Inference.OVRCompat;
using UnityEditor;
using UnityEngine;

public class BasisOpenLipSyncDebugWindow : EditorWindow
{
    private Vector2 _scrollPos;
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

    [MenuItem("Basis/Debug/OpenLipSync Debug")]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisOpenLipSyncDebugWindow>("OpenLipSync Debug");
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

        DrawHeader();
        EditorGUILayout.Space(4);

        _showBackend = DrawSection("1. OpenLipSync Backend", _showBackend, DrawBackendSection);
        _showMicrophone = DrawSection("2. Microphone Input", _showMicrophone, DrawMicrophoneSection);
        _showDriver = DrawSection("3. Audio & Viseme Driver", _showDriver, DrawDriverSection);
        _showBuffers = DrawSection("4. Inference Pipeline", _showBuffers, DrawBufferSection);
        _showVisemes = DrawSection("5. Viseme Output", _showVisemes, DrawVisemeSection);

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("OpenLipSync Debug Pipeline", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Mic -> Buffer -> Task.Run -> ONNX -> Visemes -> Blendshapes",
            EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to see live data.", MessageType.Info);
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
        StatusLabel("Initialized", init);
        EditorGUILayout.LabelField("Active Slots",
            $"{BasisOpenLipSyncDriver.ActiveSlotCount} / {BasisOpenLipSyncDriver.MaxSlots}");

        float fill = BasisOpenLipSyncDriver.MaxSlots > 0
            ? (float)BasisOpenLipSyncDriver.ActiveSlotCount / BasisOpenLipSyncDriver.MaxSlots
            : 0f;
        Rect barRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        DrawBar(barRect, fill, "Slot Usage");

        if (!init)
        {
            EditorGUILayout.HelpBox(
                "Backend not initialized. Check:\n" +
                "- model.onnx.bytes and config.json are Addressable\n" +
                "- Microsoft.ML.OnnxRuntime.dll is in Plugins/Managed/\n" +
                "- onnxruntime.dll native is in Plugins/win-x64/native/\n" +
                "- Check Console for [OpenLipSync] errors",
                MessageType.Warning);
        }

        if (Application.isPlaying)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Deep Pipeline Status", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Mel Frames Total", BasisOpenLipSyncDriver.DebugMelFramesProduced.ToString());
            EditorGUILayout.LabelField("Inference Runs Total", BasisOpenLipSyncDriver.DebugInferenceRuns.ToString());
            EditorGUILayout.LabelField("Last Inference Max", $"{BasisOpenLipSyncDriver.DebugLastInferenceMax:F6}");
            EditorGUILayout.LabelField("Pipeline", BasisOpenLipSyncDriver.DebugPipelineStatus ?? "-");

            var inferDetail = BasisOpenLipSyncDriver.DebugInferenceDetail;
            if (!string.IsNullOrEmpty(inferDetail))
            {
                EditorGUILayout.LabelField("Inference", inferDetail);
            }

            if (BasisOpenLipSyncDriver.DebugMelFramesProduced == 0 && BasisOpenLipSyncDriver.DebugInferenceRuns == 0)
            {
                EditorGUILayout.HelpBox(
                    "Zero mel frames = audio never reaches mel spectrogram.\n" +
                    "Likely causes:\n" +
                    "- Resampler not producing output (48kHz->16kHz)\n" +
                    "- Ring buffer not accumulating enough for hop (160 samples at 16kHz)\n" +
                    "- Audio chunks too small after resampling",
                    MessageType.Error);
            }
            else if (BasisOpenLipSyncDriver.DebugLastInferenceMax < 0.001f)
            {
                EditorGUILayout.HelpBox(
                    "Inference runs but output is ~0. Model may predict silence.\n" +
                    "The dev-clean trained model needs real speech input.",
                    MessageType.Warning);
            }

            var err = BasisOpenLipSyncDriver.DebugBackendError;
            if (!string.IsNullOrEmpty(err))
            {
                EditorGUILayout.HelpBox($"Backend error: {err}", MessageType.Error);
            }
        }
    }

    private void DrawMicrophoneSection()
    {
        if (!Application.isPlaying)
        {
            EditorGUILayout.LabelField("(Play Mode only)");
            return;
        }

#if !BASIS_DISABLE_MICROPHONE
        StatusLabel("Initialized", BasisLocalMicrophoneDriver.IsInitialize);
        EditorGUILayout.LabelField("Paused", BasisLocalMicrophoneDriver.isPaused.ToString());
        EditorGUILayout.LabelField("Device", BasisLocalMicrophoneDriver.MicrophoneDevice ?? "(none)");
        EditorGUILayout.LabelField("Sample Rate", $"{LocalOpusSettings.MicrophoneSampleRate} Hz");
        EditorGUILayout.LabelField("Frame Length", $"{BasisLocalMicrophoneDriver.ProcessFrameLength} samples");
        EditorGUILayout.LabelField("Volume", $"{BasisLocalMicrophoneDriver.Volume:F3}");

        float rms = BasisLocalMicrophoneDriver.averageRms;
        EditorGUILayout.LabelField("Average RMS", $"{rms:F6}");
        Rect rmsRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        float rmsNorm = Mathf.Clamp01(rms * 50f);
        DrawBar(rmsRect, rmsNorm, "Audio Level");

        var buf = BasisLocalMicrophoneDriver.processBufferArray;
        if (buf != null)
        {
            EditorGUILayout.LabelField("Process Buffer", $"{buf.Length} samples");

            Rect waveRect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            DrawMiniWaveform(waveRect, buf);
        }
#else
        EditorGUILayout.LabelField("Microphone disabled (BASIS_DISABLE_MICROPHONE)");
#endif
    }

    private void DrawDriverSection()
    {
        if (!Application.isPlaying || !BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.LabelField("(Waiting for local player...)");
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalVisemeDriver;
        StatusLabel("Successful Init", driver.WasSuccessful);
        StatusLabel("Use OpenLipSync", driver.UseOpenLipSync);
        StatusLabel("Enabled (face visible)", driver.FaceVisible);

        if (driver.Avatar != null)
        {
            EditorGUILayout.LabelField("Avatar", driver.Avatar.name);
            EditorGUILayout.LabelField("Blendshape Count", driver.BlendShapeCount.ToString());
        }

        if (driver.UseOpenLipSync && driver.openLipSyncContext != null)
        {
            EditorGUILayout.LabelField("Backend", "OpenLipSync (ONNX Neural, Threaded)");
            StatusLabel("Context Initialized", driver.openLipSyncContext.IsInitialized);
            EditorGUILayout.LabelField("Context Handle",
                $"#{driver.openLipSyncContext.DebugContextHandle}");
            StatusLabel("Face Visible", driver.openLipSyncContext.DebugFaceVisible);
            StatusLabel("Background Task Running", driver.openLipSyncContext.DebugTaskRunning);
        }
        else
        {
            EditorGUILayout.LabelField("Backend", "(no slot)");

            string reason = !BasisOpenLipSyncDriver.IsInitialized
                ? "Manager not initialized (model missing?)"
                : BasisOpenLipSyncDriver.ActiveSlotCount >= BasisOpenLipSyncDriver.MaxSlots
                    ? "All slots occupied"
                    : "Slot acquisition failed";
            EditorGUILayout.HelpBox($"No context: {reason}", MessageType.Info);
        }
    }

    private void DrawBufferSection()
    {
        if (!Application.isPlaying || !BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.LabelField("(Waiting for local player...)");
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalVisemeDriver;
        if (!driver.UseOpenLipSync || driver.openLipSyncContext == null)
        {
            EditorGUILayout.LabelField("(OpenLipSync not active - no buffer data)");
            return;
        }

        var ctx = driver.openLipSyncContext;

        EditorGUILayout.LabelField("Audio Buffers", EditorStyles.boldLabel);
        int activeBuffer = ctx.DebugActiveBuffer;
        int writeA = ctx.DebugWriteIndexA;
        int writeB = ctx.DebugWriteIndexB;

        EditorGUILayout.LabelField("Active Write Buffer", activeBuffer == 0 ? "A" : "B");

        Rect barA = GUILayoutUtility.GetRect(0, 12, GUILayout.ExpandWidth(true));
        DrawBar(barA, writeA / 48000f, $"Buf A: {writeA} {(activeBuffer == 0 ? "(WRITING)" : "(FROZEN)")}");

        Rect barB = GUILayoutUtility.GetRect(0, 12, GUILayout.ExpandWidth(true));
        DrawBar(barB, writeB / 48000f, $"Buf B: {writeB} {(activeBuffer == 1 ? "(WRITING)" : "(FROZEN)")}");

        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField("Threading", EditorStyles.boldLabel);
        StatusLabel("Background Task Active", ctx.DebugTaskRunning);

        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField("Viseme -> Blendshape Map", EditorStyles.boldLabel);
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
                    EditorGUILayout.LabelField($"  [{i}] {label}", "(unmapped)");
                    GUI.color = p2;
                }
            }
            EditorGUILayout.LabelField($"  {mapped}/15 visemes mapped");
        }
    }

    private void DrawVisemeSection()
    {
        if (!Application.isPlaying || !BasisLocalPlayer.PlayerReady)
        {
            EditorGUILayout.LabelField("(Waiting for local player...)");
            return;
        }

        var driver = BasisLocalPlayer.Instance.LocalVisemeDriver;
        if (!driver.UseOpenLipSync || driver.openLipSyncContext == null)
        {
            EditorGUILayout.LabelField("(OpenLipSync not active)");
            return;
        }

        var ctx = driver.openLipSyncContext;
        var cached = ctx.DebugVisemeWeights;
        var applied = ctx.DebugLastApplied;

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

        EditorGUILayout.LabelField("Cached Weights", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"  Sum: {cachedSum:F4}  Max: {cachedMax:F4} ({SafeLabel(cachedPeak)})  All zero: {cachedSum < 0.0001f}");

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("", GUILayout.Width(30));
        EditorGUILayout.LabelField("Bar", EditorStyles.miniLabel);
        EditorGUILayout.LabelField("Weight", EditorStyles.miniLabel, GUILayout.Width(50));
        EditorGUILayout.LabelField("Applied", EditorStyles.miniLabel, GUILayout.Width(50));
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
        EditorGUILayout.LabelField(value ? "YES" : "NO", EditorStyles.boldLabel);
        GUI.color = prev;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBar(Rect rect, float fill, string label, Color? color = null)
    {
        fill = Mathf.Clamp01(fill);
        EditorGUI.DrawRect(rect, BarBgColor);
        Rect filled = new Rect(rect.x, rect.y, rect.width * fill, rect.height);
        EditorGUI.DrawRect(filled, color ?? BarColor);
        EditorGUI.LabelField(rect, $"  {label}", EditorStyles.miniLabel);
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
