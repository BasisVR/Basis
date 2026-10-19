using Basis.Editor.Localization;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public sealed class BasisRemoteAudioTab : BasisEditorTabPage
{
    public override string Title => BasisEditorLocalization.Get("editor.remoteAudio.title");
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.remoteAudio.subtitle");

    private bool _showGlobal = true;
    private readonly Dictionary<ushort, bool> _playerFoldouts = new();

    private static readonly Color BarColor = new Color(0.2f, 0.7f, 1f, 0.8f);
    private static readonly Color BarBgColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    private static readonly Color GoodColor = new Color(0.3f, 0.9f, 0.3f);
    private static readonly Color WarnColor = new Color(1f, 0.8f, 0.2f);
    private static readonly Color ErrorColor = new Color(1f, 0.3f, 0.3f);
    private static readonly Color SilenceColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
    private static readonly Color LoudColor = new Color(1f, 0.3f, 0.1f, 0.9f);

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
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.header"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.flow"),
            EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.remoteAudio.playModeOnly"), MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.Space(4);
        _showGlobal = DrawSection(BasisEditorLocalization.Get("editor.remoteAudio.section.global"), _showGlobal, DrawGlobalSection);
        EditorGUILayout.Space(4);

        DrawAllPlayers();

    }

    private void DrawGlobalSection()
    {
        float mainVol = SMModuleAudio.ActiveMainVolume;
        float voiceVol = SMModuleAudio.ActiveVoiceVolume;
        int remoteCount = BasisNetworkPlayers.ReceiverCount;
        int outputRate = BasisAudioReceiver.outputSampleRate;

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.remotePlayers"), remoteCount.ToString());

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.mainVolume"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect mainBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(mainBar, mainVol, $"{mainVol:P0}");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.voiceVolume"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect voiceBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(voiceBar, voiceVol, $"{voiceVol:P0}");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.outputSampleRate"), $"{outputRate} Hz");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.opusConfig"),
            BasisEditorLocalization.Get("editor.remoteAudio.opusConfigValue", RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels,
                RemoteOpusSettings.FrameSize, RemoteOpusSettings.FrameSize * 1000f / RemoteOpusSettings.NetworkSampleRate));
    }

    private void DrawAllPlayers()
    {
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;

        if (count == 0)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.remoteAudio.noPlayers"), MessageType.Info);
            return;
        }

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.players", count));
        EditorGUILayout.Space(2);

        for (int i = 0; i < count && i < snapshot.Length; i++)
        {
            BasisNetworkReceiver receiver = snapshot[i];
            if (receiver == null) continue;

            ushort id = receiver.playerId;
            string name = receiver.displayName;
            string label = $"[{id}] {(string.IsNullOrEmpty(name) ? BasisEditorLocalization.Get("editor.common.unknown") : name)}";

            if (!_playerFoldouts.ContainsKey(id))
                _playerFoldouts[id] = true;

            _playerFoldouts[id] = EditorGUILayout.BeginFoldoutHeaderGroup(_playerFoldouts[id], label);
            if (_playerFoldouts[id])
            {
                EditorGUI.indentLevel++;
                DrawPlayerAudio(receiver);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }
    }

    private void DrawPlayerAudio(BasisNetworkReceiver receiver)
    {
        BasisAudioReceiver audio = receiver.AudioReceiverModule;
        if (audio == null)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.remoteAudio.moduleNull"), MessageType.Error);
            return;
        }

        DrawAudioSourceSection(audio);
        EditorGUILayout.Space(2);
        DrawVolumeChain(audio);
        EditorGUILayout.Space(2);
        DrawRingBuffer(audio);
        EditorGUILayout.Space(2);
        DrawJitterBuffer(audio);
        EditorGUILayout.Space(2);
        DrawSilenceTracking(audio);
        EditorGUILayout.Space(2);
        DrawViseme(audio);
    }

    private void DrawAudioSourceSection(BasisAudioReceiver audio)
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.audioSource"));

        bool hasSource = audio.HasAudioSource;
        AudioSource src = audio.audioSource;

        StatusLabel(BasisEditorLocalization.Get("editor.remoteAudio.hasSource"), hasSource);

        if (src == null)
        {
            EditorGUILayout.LabelField("AudioSource", "NULL");
            return;
        }

        StatusLabel(BasisEditorLocalization.Get("editor.common.enabled"), src.enabled);
        StatusLabel(BasisEditorLocalization.Get("editor.remoteAudio.playing"), src.isPlaying);

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.spatialBlend"), $"{src.spatialBlend:F2} ({(src.spatialBlend > 0.5f ? "3D" : "2D")})");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.maxDistance"), $"{src.maxDistance:F1}m");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.rolloff"), src.rolloffMode.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.spatialize"), BasisEditorLocalization.Get("editor.remoteAudio.spatializeValue", src.spatialize, src.spatializePostEffects));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.doppler"), $"{src.dopplerLevel:F2}");
    }

    private void DrawVolumeChain(BasisAudioReceiver audio)
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.volumeChain"));

        AudioSource src = audio.audioSource;
        float srcVol = src != null ? src.volume : 0f;
        float dampen = audio.DirectionalDampeningMultiplier;
        float mainVol = SMModuleAudio.ActiveMainVolume;
        float effective = srcVol * dampen * mainVol;

        // Source volume
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.sourceVolume"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect srcBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(srcBar, srcVol, $"{srcVol:F2}");
        EditorGUILayout.EndHorizontal();

        // Directional dampening
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.dirDampening"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect dampBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        Color dampColor = dampen < 0.3f ? ErrorColor : dampen < 0.7f ? WarnColor : GoodColor;
        DrawBar(dampBar, dampen, $"{dampen:F3}", dampColor);
        EditorGUILayout.EndHorizontal();

        if (dampen < 0.5f)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.remoteAudio.dampeningHelp"), MessageType.Info);
        }

        // Main volume
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.mainVolume"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect mainBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(mainBar, mainVol, $"{mainVol:F2}");
        EditorGUILayout.EndHorizontal();

        // Effective output
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.effectiveOutput"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect effBar = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        Color effColor = effective < 0.1f ? ErrorColor : effective > 0.9f ? LoudColor : BarColor;
        DrawBar(effBar, effective, $"{srcVol:F2} x {dampen:F2} x {mainVol:F2} = {effective:F3}", effColor);
        EditorGUILayout.EndHorizontal();

        if (effective < 0.01f && srcVol > 0f)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.remoteAudio.effectiveHelp"), MessageType.Warning);
        }
    }

    private void DrawRingBuffer(BasisAudioReceiver audio)
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.decodedQueue"));

        BasisVoiceBuffer buf = audio.VoiceBuffer;
        if (buf == null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.voiceBuffer"), "NULL");
            return;
        }

        int frames = buf.DecodedFrameCount;
        int cap = buf.DecodedFrameCapacity;
        float fillPct = cap > 0 ? (float)frames / cap : 0f;
        int samples = buf.SampleCount;
        float bufferedMs = samples * 1000f / RemoteOpusSettings.NetworkSampleRate;

        StatusLabel(BasisEditorLocalization.Get("editor.remoteAudio.hasRealAudio"), buf.HasRealAudio);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.fillLevel"), GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect fillBar = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        Color fillColor = frames >= cap ? ErrorColor : fillPct > 0.8f ? WarnColor : BarColor;
        DrawBar(fillBar, fillPct, BasisEditorLocalization.Get("editor.remoteAudio.fillValue", frames, cap, fillPct), fillColor);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.bufferedAudio"), $"{bufferedMs:F1}ms");
        string state = BasisEditorLocalization.Get(buf.IsEmpty ? "editor.remoteAudio.state.empty" : frames >= cap ? "editor.remoteAudio.state.full" : "editor.remoteAudio.state.streaming");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.state"), state);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.plcCount"), audio.PlcCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.silenceSkipped"), audio.SilenceInjectedCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.genuineUnderruns"), buf.GenuineUnderruns.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.lateSalvaged"), buf.LateSalvagedCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.starveBridged"), audio.StarvePlcCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.catchUpTrimmed"), audio.TrimmedQuietFrames.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.catchUpAccelerated"), audio.AcceleratedFrames.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.backlogFlushed"), audio.FlushedPackets.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.expandInserted"), audio.ExpandInsertedFrames.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.standingDepth"), BasisEditorLocalization.Get("editor.remoteAudio.standingDepthValue", buf.StandingBufferedFrames));
    }

    private void DrawJitterBuffer(BasisAudioReceiver audio)
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.jitterBuffer"));

        BasisVoiceBuffer buf = audio.VoiceBuffer;
        if (buf == null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.voiceBuffer"), "NULL");
            return;
        }

        StatusLabel(BasisEditorLocalization.Get("editor.remoteAudio.started"), buf.Started);

        int buffered = buf.EncodedBufferedCount;
        int received = buf.ReceivedSinceStart;
        int initDepth = buf.InitialBufferDepth;

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.bufferedPackets"), buffered.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.receivedSinceStart"), received.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.initialDepthRequired"), initDepth.ToString());

        if (buf.Started)
        {
            if (received < initDepth)
            {
                float fillPct = initDepth > 0 ? (float)received / initDepth : 0f;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.filling"), GUILayout.Width(EditorGUIUtility.labelWidth));
                Rect fillBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
                DrawBar(fillBar, fillPct, BasisEditorLocalization.Get("editor.remoteAudio.fillingValue", received, initDepth), WarnColor);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    BasisEditorLocalization.Get("editor.remoteAudio.fillingHelp", received, initDepth),
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.status"), BasisEditorLocalization.Get("editor.remoteAudio.playingPastFill"));
            }
        }
    }

    private void DrawSilenceTracking(BasisAudioReceiver audio)
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.silenceTracking"));

        int silentUnits = audio._silentUnits20ms;
        float silentMs = silentUnits * 20f;

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.silentUnits"), silentUnits.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.silentDuration"), $"{silentMs:F0}ms");

        if (silentMs > 0)
        {
            float silentSec = silentMs / 1000f;
            // Cap bar at 5 seconds for visual
            float barFill = Mathf.Clamp01(silentSec / 5f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.remoteAudio.silence"), GUILayout.Width(EditorGUIUtility.labelWidth));
            Rect silBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            Color silColor = silentMs > 2000f ? ErrorColor : silentMs > 500f ? WarnColor : SilenceColor;
            DrawBar(silBar, barFill, $"{silentMs:F0}ms", silColor);
            EditorGUILayout.EndHorizontal();
        }

        if (silentMs > 2000f)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.remoteAudio.prolongedSilence"), MessageType.Warning);
        }
    }

    private void DrawViseme(BasisAudioReceiver audio)
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.remoteAudio.visemeDriver"));
        StatusLabel(BasisEditorLocalization.Get("editor.remoteAudio.remoteAudioDriver"), audio.BasisRemoteVisemeAudioDriver != null);

        if (audio.BasisRemoteVisemeAudioDriver != null)
        {
            StatusLabel(BasisEditorLocalization.Get("editor.remoteAudio.initialized"), audio.BasisRemoteVisemeAudioDriver.Initialized);
        }
    }

    // --- Helpers ---

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
        return expanded;
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
}
