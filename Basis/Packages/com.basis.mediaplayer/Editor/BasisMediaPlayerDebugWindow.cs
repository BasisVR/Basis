using Basis.Editor.Localization;
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Live readout of one player, walking the pipeline in the order it runs:
/// source, decode, bank and present, clock, audio, captions, engine. It makes
/// a bad run legible while it is happening (which stage stopped, or which
/// rate is off) rather than after the fact from a capture.
/// </summary>
public class BasisMediaPlayerDebugWindow : EditorWindow
{
    BasisMediaPlayer _target;
    ObjectField _targetField;
    VisualElement _missingBanner;
    VisualElement _editModeHint;

    // Rates are differenced against the previous refresh rather than read
    // from a counter, because what matters is the rate right now.
    double _rateLastTime;
    ulong _rateLastDecoded;
    long _rateLastPresented;
    long _rateLastPulled;
    int _rateLastFrame;
    float _decodedRate, _presentedRate, _pullRate, _editorFrameRate;

    Label _diagPath, _diagRows;
    Button _diagAttach, _diagStart, _diagStop, _diagFlush, _diagReveal;

    static string PackagePath => UnityEditor.PackageManager.PackageInfo
        .FindForAssembly(typeof(BasisMediaPlayerDebugWindow).Assembly)?.assetPath;

    [MenuItem("Basis/Debug/Media Player", false, 606)]
    public static void ShowWindow()
    {
        var window = GetWindow<BasisMediaPlayerDebugWindow>(BasisEditorLocalization.Get("editor.mediaPlayer.debug.windowTitle"));
        window.minSize = new Vector2(440, 600);
    }

    public void CreateGUI()
    {
        string package = PackagePath;
        if (string.IsNullOrEmpty(package))
        {
            rootVisualElement.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.packagePathMissing"), HelpBoxMessageType.Error));
            return;
        }

        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerDebug.uxml").Replace('\\', '/'));
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uss").Replace('\\', '/'));
        if (tree == null)
        {
            rootVisualElement.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "MediaPlayerDebug.uxml"), HelpBoxMessageType.Error));
            return;
        }

        tree.CloneTree(rootVisualElement);
        BasisEditorLocalization.LocalizeTree(rootVisualElement, "editor.mediaPlayer.uxml.debug.");
        if (sheet != null) rootVisualElement.styleSheets.Add(sheet);

        _targetField = rootVisualElement.Q<ObjectField>("TargetPlayer");
        if (_targetField != null)
        {
            _targetField.objectType = typeof(BasisMediaPlayer);
            _targetField.RegisterValueChangedCallback(evt => _target = evt.newValue as BasisMediaPlayer);
        }

        _missingBanner = rootVisualElement.Q<VisualElement>("MissingTargetBanner");
        _editModeHint = rootVisualElement.Q<VisualElement>("EditModeHint");

        BindDiagnostics();
        rootVisualElement.schedule.Execute(Refresh).Every(250);
    }

    void BindDiagnostics()
    {
        _diagPath = rootVisualElement.Q<Label>("DiagPathLabel");
        _diagRows = rootVisualElement.Q<Label>("DiagRowsLabel");
        _diagAttach = rootVisualElement.Q<Button>("DiagAttachButton");
        _diagStart = rootVisualElement.Q<Button>("DiagStartButton");
        _diagStop = rootVisualElement.Q<Button>("DiagStopButton");
        _diagFlush = rootVisualElement.Q<Button>("DiagFlushButton");
        _diagReveal = rootVisualElement.Q<Button>("DiagRevealButton");

        if (_diagAttach != null)
            _diagAttach.clicked += () =>
            {
                if (_target == null) return;
                if (_target.GetComponent<BasisMediaPlayerDiagnostics>() != null) return;
                Undo.AddComponent<BasisMediaPlayerDiagnostics>(_target.gameObject);
            };
        if (_diagStart != null) _diagStart.clicked += () => Diagnostics()?.StartLogging();
        if (_diagStop != null) _diagStop.clicked += () => Diagnostics()?.StopLogging();
        if (_diagFlush != null) _diagFlush.clicked += () => Diagnostics()?.Flush();
        if (_diagReveal != null)
            _diagReveal.clicked += () =>
            {
                string path = Diagnostics()?.ResolvedLogPath;
                if (string.IsNullOrEmpty(path)) { EditorUtility.RevealInFinder(Application.persistentDataPath); return; }
                if (File.Exists(path)) EditorUtility.RevealInFinder(path);
                else EditorUtility.RevealInFinder(Path.GetDirectoryName(path) ?? Application.persistentDataPath);
            };
    }

    BasisMediaPlayerDiagnostics Diagnostics()
        => _target != null ? _target.GetComponent<BasisMediaPlayerDiagnostics>() : null;

    void Refresh()
    {
        // Fall back to whatever is in the scene, so the window is useful
        // the moment it opens during a play session.
        if (_target == null)
        {
            _target = BasisMediaPlayerRegistry.Count > 0
                ? BasisMediaPlayerRegistry.Players[0]
                : FindAnyObjectByType<BasisMediaPlayer>();
            if (_target != null && _targetField != null) _targetField.SetValueWithoutNotify(_target);
        }

        bool live = Application.isPlaying;
        Show(_editModeHint, !live);
        Show(_missingBanner, _target == null);
        if (_target == null) return;

        UpdateRates(live);

        // 1. Player
        SetPill("P_State", _target.State.ToString(), StateTone(_target.State));
        Set("P_Position", live ? $"{_target.PositionSeconds:F2} s" : "—");
        Set("P_Error", _target.ErrorCode != 0 ? _target.ErrorCode.ToString() : BasisEditorLocalization.Get("editor.mediaPlayer.debug.none"));
        Set("P_LoadGen", _target.LoadGeneration.ToString());

        // 2. Source / transport
        Set("S_Url", Redact(_target.url));
        Set("S_AudioUrl", string.IsNullOrEmpty(_target.ActiveAudioStreamUrl)
            ? BasisEditorLocalization.Get("editor.mediaPlayer.debug.muxed") : Redact(_target.ActiveAudioStreamUrl));
        Set("S_Liveness", _target.liveness.ToString());
        Set("S_Provider", _target.Media?.Provider ?? BasisEditorLocalization.Get("editor.mediaPlayer.debug.direct"));
        Set("S_Title", _target.Media?.Title ?? "—");

        // 3. Decode
        Set("VD_Decoded", _target.FramesDecoded.ToString());
        Set("VD_FpsDecoded", live ? $"{_decodedRate:F1} /s" : "—");
        Set("VD_VideoSize", _target.VideoSize == Vector2Int.zero
            ? "—"
            : $"{_target.VideoSize.x} × {_target.VideoSize.y}");
        SetPill("VD_OutputTex", BasisEditorLocalization.Get(_target.Texture != null ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"), _target.Texture != null ? Tone.Good : Tone.Neutral);
        Set("VD_Origin", BasisEditorLocalization.Get(_target.OutputFrameIsTopLeftOrigin ? "editor.mediaPlayer.debug.originTopLeft" : "editor.mediaPlayer.debug.originBottomLeft"));
        Set("VD_Preference", BasisMediaPlayer.DecodePreference.ToString());

        // 4. Bank / present
        long banked = _target.BankedMilliseconds;
        int depth = _target.EffectiveBufferDepthMs;
        Set("VQ_BufferValue", live ? $"{banked} ms" : "—");
        // Against the configured depth, or 3 s as a yardstick on Auto.
        Fill("VQ_BufferFill", depth > 0 ? banked / (float)depth : banked / 3000f, banked > 0 ? Tone.Good : Tone.Bad);
        Set("VQ_Depth", depth > 0 ? $"{depth} ms" : BasisEditorLocalization.Get("editor.common.auto"));
        Set("VQ_Presented", _target.PresentedFrameCount.ToString());
        Set("VQ_FpsDisplayed", live ? $"{_presentedRate:F1} /s" : "—");
        Set("VQ_FpsRender", live ? $"{_editorFrameRate:F1} /s" : "—");
        Set("VQ_InFlight", ((long)_target.FramesDecoded - _target.PresentedFrameCount).ToString());

        // 5. Clock and sync
        Set("C_Position", live ? $"{_target.PositionSeconds:F3} s" : "—");
        Set("C_Duration", _target.DurationSeconds > 0 ? $"{_target.DurationSeconds:F3} s" : BasisEditorLocalization.Get("editor.mediaPlayer.debug.durationUnknown"));
        Set("C_SyncPpm", _target.SyncRatePpm != 0 ? $"{_target.SyncRatePpm} ppm" : "1x");
        Set("C_MaxDivergence", _target.maxDivergenceMs > 0 ? $"{_target.maxDivergenceMs} ms" : BasisEditorLocalization.Get("editor.mediaPlayer.debug.maxDivergenceDefault"));

        // 6. Audio
        Set("AD_Format", _target.AudioSampleRate > 0
            ? $"{_target.AudioSampleRate} Hz × {_target.AudioChannels}"
            : "—");
        Set("AO_Consumed", _target.AudioFramesPulled.ToString());
        Set("AO_PullRate", live ? $"{_pullRate:F0} Hz" : "—");
        AudioSettings.GetDSPBufferSize(out int dspLength, out int dspCount);
        Set("AO_Dsp", $"{AudioSettings.outputSampleRate} Hz, {dspLength} × {dspCount}");
        SetPill("AO_ListenerPaused", BasisEditorLocalization.Get(AudioListener.pause ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no"), AudioListener.pause ? Tone.Warn : Tone.Good);
        BasisMediaPlayerAudio audio = _target.AudioComponent;
        Set("AO_Outputs", audio == null
            ? BasisEditorLocalization.Get("editor.mediaPlayer.debug.noAudioComponent")
            : audio.IsAnyOutputPlaying
                ? BasisEditorLocalization.Get("editor.mediaPlayer.debug.outputsPlaying", audio.BoundOutputCount)
                : BasisEditorLocalization.Get("editor.mediaPlayer.debug.outputsStopped", audio.BoundOutputCount));
        Set("AO_Level", audio == null || !live
            ? "—"
            : BasisEditorLocalization.Get("editor.mediaPlayer.debug.level", audio.LastPcmPeak, audio.LastPcmRms));
        Set("AO_Latency", audio == null ? "—" : $"{audio.EstimatedOutputLatencyUs / 1000.0:F1} ms");

        // 7. Captions and subtitles
        bool captionsOn = BasisMediaSettings.CaptionsEnabled.RawValue;
        SetPill("CS_Enabled", BasisEditorLocalization.Get(captionsOn ? "editor.mediaPlayer.debug.on" : "editor.mediaPlayer.debug.off"), captionsOn ? Tone.Good : Tone.Neutral);
        Set("CS_Track", _target.SelectedSubtitleTrackIndex < 0
            ? BasisEditorLocalization.Get("editor.mediaPlayer.debug.trackInBand")
            : $"#{_target.SelectedSubtitleTrackIndex}");
        Set("CS_TrackCount", _target.SubtitleTracks.Count.ToString());
        Set("CS_Current", string.IsNullOrEmpty(_target.CurrentCaption) ? BasisEditorLocalization.Get("editor.mediaPlayer.debug.noCaption") : _target.CurrentCaption);

        // 8. Engine
        BmCapabilitySet caps = BasisMediaPlayer.EngineCapabilities;
        Set("E_Platform", caps?.platform ?? BasisEditorLocalization.Get("editor.mediaPlayer.debug.platformUnavailable"));
        Set("E_Version", caps != null ? caps.version.ToString() : "—");
        Set("E_Video", DescribeVideo(caps));
        Set("E_Audio", DescribeAudio(caps));
        Set("E_Transports", DescribeTransports(caps));

        // Diagnostics row
        BasisMediaPlayerDiagnostics diagnostics = Diagnostics();
        Set(_diagPath, BasisEditorLocalization.Get("editor.mediaPlayer.debug.diagPath", diagnostics == null
            ? BasisEditorLocalization.Get("editor.mediaPlayer.debug.noLogger")
            : diagnostics.ResolvedLogPath));
        Set(_diagRows, BasisEditorLocalization.Get("editor.mediaPlayer.debug.diagRows", diagnostics == null ? "—" : diagnostics.RowsWritten.ToString()));
        Show(_diagAttach, diagnostics == null);
        Show(_diagStart, diagnostics != null && live && !diagnostics.IsLogging);
        Show(_diagStop, diagnostics != null && live && diagnostics.IsLogging);
        Show(_diagFlush, diagnostics != null && live && diagnostics.IsLogging);
    }

    void UpdateRates(bool live)
    {
        if (!live)
        {
            _decodedRate = _presentedRate = _pullRate = _editorFrameRate = 0f;
            return;
        }
        double now = EditorApplication.timeSinceStartup;
        double elapsed = now - _rateLastTime;
        if (elapsed < 0.2)
            return;
        if (_rateLastTime > 0 && elapsed < 5)
        {
            _decodedRate = (float)((_target.FramesDecoded - _rateLastDecoded) / elapsed);
            _presentedRate = (float)((_target.PresentedFrameCount - _rateLastPresented) / elapsed);
            _pullRate = (float)((_target.AudioFramesPulled - _rateLastPulled) / elapsed);
            _editorFrameRate = (float)((Time.frameCount - _rateLastFrame) / elapsed);
        }
        _rateLastTime = now;
        _rateLastDecoded = _target.FramesDecoded;
        _rateLastPresented = _target.PresentedFrameCount;
        _rateLastPulled = _target.AudioFramesPulled;
        _rateLastFrame = Time.frameCount;
    }

    static string DescribeVideo(BmCapabilitySet caps)
    {
        if (caps?.video == null || caps.video.Length == 0) return BasisEditorLocalization.Get("editor.mediaPlayer.debug.video", BasisEditorLocalization.Get("editor.mediaPlayer.debug.noneParen"));
        var text = new StringBuilder();
        for (int i = 0; i < caps.video.Length; i++)
        {
            BmVideoCap cap = caps.video[i];
            if (i > 0) text.Append(", ");
            text.Append(cap.codec).Append(' ').Append(cap.route)
                .Append(" ≤").Append(cap.max_width).Append('×').Append(cap.max_height);
        }
        return BasisEditorLocalization.Get("editor.mediaPlayer.debug.video", text.ToString());
    }

    static string DescribeAudio(BmCapabilitySet caps)
    {
        if (caps?.audio == null || caps.audio.Length == 0) return BasisEditorLocalization.Get("editor.mediaPlayer.debug.audio", BasisEditorLocalization.Get("editor.mediaPlayer.debug.noneParen"));
        var text = new StringBuilder();
        for (int i = 0; i < caps.audio.Length; i++)
        {
            if (i > 0) text.Append(", ");
            text.Append(caps.audio[i].codec).Append(" ≤").Append(caps.audio[i].max_channels).Append("ch");
        }
        return BasisEditorLocalization.Get("editor.mediaPlayer.debug.audio", text.ToString());
    }

    static string DescribeTransports(BmCapabilitySet caps)
    {
        if (caps?.transports == null || caps.transports.Length == 0) return BasisEditorLocalization.Get("editor.mediaPlayer.debug.transports", BasisEditorLocalization.Get("editor.mediaPlayer.debug.noneParen"));
        var text = new StringBuilder();
        for (int i = 0; i < caps.transports.Length; i++)
        {
            if (i > 0) text.Append(", ");
            text.Append(caps.transports[i].scheme);
        }
        return BasisEditorLocalization.Get("editor.mediaPlayer.debug.transports", text.ToString());
    }

    // Query strings on resolved CDN URLs carry signed tokens, and this
    // window is the kind of thing that ends up in a screenshot.
    static string Redact(string url)
        => string.IsNullOrEmpty(url) ? "—" : BasisMediaUrlRouter.Redact(url);

    enum Tone { Neutral, Good, Warn, Bad }

    static Tone StateTone(BmState state) => state switch
    {
        BmState.Playing => Tone.Good,
        BmState.Error => Tone.Bad,
        BmState.Buffering or BmState.Opening => Tone.Warn,
        _ => Tone.Neutral,
    };

    void Set(string name, string value) => Set(rootVisualElement.Q<Label>(name), value);

    static void Set(Label label, string value)
    {
        if (label != null) label.text = value;
    }

    void SetPill(string name, string value, Tone tone)
    {
        Label label = rootVisualElement.Q<Label>(name);
        if (label == null) return;
        label.RemoveFromClassList("bvp-pill-neutral");
        label.RemoveFromClassList("bvp-pill-good");
        label.RemoveFromClassList("bvp-pill-warn");
        label.RemoveFromClassList("bvp-pill-bad");
        label.AddToClassList(tone switch
        {
            Tone.Good => "bvp-pill-good",
            Tone.Warn => "bvp-pill-warn",
            Tone.Bad => "bvp-pill-bad",
            _ => "bvp-pill-neutral",
        });
        label.text = value;
    }

    void Fill(string name, float fraction, Tone tone)
    {
        VisualElement bar = rootVisualElement.Q<VisualElement>(name);
        if (bar == null) return;
        bar.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100f);
        bar.RemoveFromClassList("bvp-bar-fill-good");
        bar.RemoveFromClassList("bvp-bar-fill-warn");
        bar.RemoveFromClassList("bvp-bar-fill-bad");
        if (tone == Tone.Good) bar.AddToClassList("bvp-bar-fill-good");
        else if (tone == Tone.Warn) bar.AddToClassList("bvp-bar-fill-warn");
        else if (tone == Tone.Bad) bar.AddToClassList("bvp-bar-fill-bad");
    }

    static void Show(VisualElement element, bool show)
    {
        if (element != null) element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
