using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class BasisRemoteAudioDebugWindow : EditorWindow
{
    private Vector2 _scrollPos;
    private bool _showGlobal = true;
    private readonly Dictionary<ushort, bool> _playerFoldouts = new();

    private static readonly Color BarColor = new Color(0.2f, 0.7f, 1f, 0.8f);
    private static readonly Color BarBgColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    private static readonly Color GoodColor = new Color(0.3f, 0.9f, 0.3f);
    private static readonly Color WarnColor = new Color(1f, 0.8f, 0.2f);
    private static readonly Color ErrorColor = new Color(1f, 0.3f, 0.3f);
    private static readonly Color SilenceColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
    private static readonly Color LoudColor = new Color(1f, 0.3f, 0.1f, 0.9f);

    [MenuItem("Basis/Debug/Remote Audio Debug")]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisRemoteAudioDebugWindow>("Remote Audio Debug");
        w.minSize = new Vector2(450, 400);
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

        EditorGUILayout.LabelField("Remote Player Audio Debug", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Network -> Jitter Buffer -> Opus Decode -> Ring Buffer -> OnAudioFilterRead -> Output",
            EditorStyles.miniLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to see live data.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.Space(4);
        _showGlobal = DrawSection("Global Audio State", _showGlobal, DrawGlobalSection);
        EditorGUILayout.Space(4);

        DrawAllPlayers();

        EditorGUILayout.EndScrollView();
    }

    private void DrawGlobalSection()
    {
        float mainVol = SMModuleAudio.ActiveMainVolume;
        float voiceVol = SMModuleAudio.ActiveVoiceVolume;
        int remoteCount = BasisNetworkPlayers.ReceiverCount;
        int outputRate = BasisAudioReceiver.outputSampleRate;

        EditorGUILayout.LabelField("Remote Players", remoteCount.ToString());

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Main Volume", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect mainBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(mainBar, mainVol, $"{mainVol:P0}");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Voice Volume", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect voiceBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(voiceBar, voiceVol, $"{voiceVol:P0}");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Output Sample Rate", $"{outputRate} Hz");
        EditorGUILayout.LabelField("Opus Config",
            $"{RemoteOpusSettings.NetworkSampleRate} Hz, {RemoteOpusSettings.Channels}ch, " +
            $"Frame: {RemoteOpusSettings.FrameSize} ({RemoteOpusSettings.FrameSize * 1000f / RemoteOpusSettings.NetworkSampleRate:F1}ms)");
    }

    private void DrawAllPlayers()
    {
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;

        if (count == 0)
        {
            EditorGUILayout.HelpBox("No remote players connected.", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField($"Remote Players ({count})", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        for (int i = 0; i < count && i < snapshot.Length; i++)
        {
            BasisNetworkReceiver receiver = snapshot[i];
            if (receiver == null) continue;

            ushort id = receiver.playerId;
            string name = receiver.displayName;
            string label = $"[{id}] {(string.IsNullOrEmpty(name) ? "Unknown" : name)}";

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
            EditorGUILayout.HelpBox("AudioReceiverModule is NULL", MessageType.Error);
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
        EditorGUILayout.LabelField("Audio Source", EditorStyles.boldLabel);

        bool hasSource = audio.HasAudioSource;
        AudioSource src = audio.audioSource;

        StatusLabel("Has Source", hasSource);

        if (src == null)
        {
            EditorGUILayout.LabelField("AudioSource", "NULL");
            return;
        }

        StatusLabel("Enabled", src.enabled);
        StatusLabel("Playing", src.isPlaying);

        EditorGUILayout.LabelField("Spatial Blend", $"{src.spatialBlend:F2} ({(src.spatialBlend > 0.5f ? "3D" : "2D")})");
        EditorGUILayout.LabelField("Max Distance", $"{src.maxDistance:F1}m");
        EditorGUILayout.LabelField("Rolloff", src.rolloffMode.ToString());
        EditorGUILayout.LabelField("Spatialize", $"{src.spatialize} (Post: {src.spatializePostEffects})");
        EditorGUILayout.LabelField("Doppler", $"{src.dopplerLevel:F2}");
    }

    private void DrawVolumeChain(BasisAudioReceiver audio)
    {
        EditorGUILayout.LabelField("Volume Chain", EditorStyles.boldLabel);

        AudioSource src = audio.audioSource;
        float srcVol = src != null ? src.volume : 0f;
        float dampen = audio.DirectionalDampeningMultiplier;
        float mainVol = SMModuleAudio.ActiveMainVolume;
        float effective = srcVol * dampen * mainVol;

        // Source volume
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Source Volume", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect srcBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(srcBar, srcVol, $"{srcVol:F2}");
        EditorGUILayout.EndHorizontal();

        // Directional dampening
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Dir. Dampening", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect dampBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        Color dampColor = dampen < 0.3f ? ErrorColor : dampen < 0.7f ? WarnColor : GoodColor;
        DrawBar(dampBar, dampen, $"{dampen:F3}", dampColor);
        EditorGUILayout.EndHorizontal();

        if (dampen < 0.5f)
        {
            EditorGUILayout.HelpBox("Player is behind listener - volume reduced by directional dampening.", MessageType.Info);
        }

        // Main volume
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Main Volume", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect mainBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
        DrawBar(mainBar, mainVol, $"{mainVol:F2}");
        EditorGUILayout.EndHorizontal();

        // Effective output
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Effective Output", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect effBar = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        Color effColor = effective < 0.1f ? ErrorColor : effective > 0.9f ? LoudColor : BarColor;
        DrawBar(effBar, effective, $"{srcVol:F2} x {dampen:F2} x {mainVol:F2} = {effective:F3}", effColor);
        EditorGUILayout.EndHorizontal();

        if (effective < 0.01f && srcVol > 0f)
        {
            EditorGUILayout.HelpBox("Effective volume is near zero! Check dampening and main volume.", MessageType.Warning);
        }
    }

    private void DrawRingBuffer(BasisAudioReceiver audio)
    {
        EditorGUILayout.LabelField("Decoded PCM Queue", EditorStyles.boldLabel);

        BasisVoiceBuffer buf = audio.VoiceBuffer;
        if (buf == null)
        {
            EditorGUILayout.LabelField("Voice Buffer", "NULL");
            return;
        }

        int frames = buf.DecodedFrameCount;
        int cap = buf.DecodedFrameCapacity;
        float fillPct = cap > 0 ? (float)frames / cap : 0f;
        int samples = buf.SampleCount;
        float bufferedMs = samples * 1000f / RemoteOpusSettings.NetworkSampleRate;

        StatusLabel("Has Real Audio", buf.HasRealAudio);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Fill Level", GUILayout.Width(EditorGUIUtility.labelWidth));
        Rect fillBar = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        Color fillColor = frames >= cap ? ErrorColor : fillPct > 0.8f ? WarnColor : BarColor;
        DrawBar(fillBar, fillPct, $"{frames}/{cap} frames ({fillPct:P0})", fillColor);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Buffered Audio", $"{bufferedMs:F1}ms");
        string state = buf.IsEmpty ? "EMPTY" : frames >= cap ? "FULL" : "Streaming";
        EditorGUILayout.LabelField("State", state);
        EditorGUILayout.LabelField("PLC Count", audio.PlcCount.ToString());
        EditorGUILayout.LabelField("Silence Skipped", audio.SilenceInjectedCount.ToString());
    }

    private void DrawJitterBuffer(BasisAudioReceiver audio)
    {
        EditorGUILayout.LabelField("Encoded Packets (Jitter)", EditorStyles.boldLabel);

        BasisVoiceBuffer buf = audio.VoiceBuffer;
        if (buf == null)
        {
            EditorGUILayout.LabelField("Voice Buffer", "NULL");
            return;
        }

        StatusLabel("Started", buf.Started);

        int buffered = buf.EncodedBufferedCount;
        int received = buf.ReceivedSinceStart;
        int initDepth = buf.InitialBufferDepth;

        EditorGUILayout.LabelField("Buffered Packets", buffered.ToString());
        EditorGUILayout.LabelField("Received Since Start", received.ToString());
        EditorGUILayout.LabelField("Initial Depth Required", initDepth.ToString());

        if (buf.Started)
        {
            if (received < initDepth)
            {
                float fillPct = initDepth > 0 ? (float)received / initDepth : 0f;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Filling", GUILayout.Width(EditorGUIUtility.labelWidth));
                Rect fillBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
                DrawBar(fillBar, fillPct, $"{received}/{initDepth} - NOT YET PLAYING", WarnColor);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    $"Jitter buffer is filling ({received}/{initDepth}). " +
                    "Audio won't play until enough packets arrive to absorb network jitter.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField("Status", "Playing (past initial fill)");
            }
        }
    }

    private void DrawSilenceTracking(BasisAudioReceiver audio)
    {
        EditorGUILayout.LabelField("Silence Tracking", EditorStyles.boldLabel);

        int silentUnits = audio._silentUnits20ms;
        float silentMs = silentUnits * 20f;

        EditorGUILayout.LabelField("Silent Units (20ms each)", silentUnits.ToString());
        EditorGUILayout.LabelField("Silent Duration", $"{silentMs:F0}ms");

        if (silentMs > 0)
        {
            float silentSec = silentMs / 1000f;
            // Cap bar at 5 seconds for visual
            float barFill = Mathf.Clamp01(silentSec / 5f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Silence", GUILayout.Width(EditorGUIUtility.labelWidth));
            Rect silBar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            Color silColor = silentMs > 2000f ? ErrorColor : silentMs > 500f ? WarnColor : SilenceColor;
            DrawBar(silBar, barFill, $"{silentMs:F0}ms", silColor);
            EditorGUILayout.EndHorizontal();
        }

        if (silentMs > 2000f)
        {
            EditorGUILayout.HelpBox("Prolonged silence (>2s). Player may have stopped speaking, or packets are not arriving.", MessageType.Warning);
        }
    }

    private void DrawViseme(BasisAudioReceiver audio)
    {
        EditorGUILayout.LabelField("Viseme Driver", EditorStyles.boldLabel);
        StatusLabel("Remote Audio Driver", audio.BasisRemoteVisemeAudioDriver != null);

        if (audio.BasisRemoteVisemeAudioDriver != null)
        {
            StatusLabel("Initialized", audio.BasisRemoteVisemeAudioDriver.Initalized);
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
}
