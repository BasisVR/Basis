using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class BasisRemotePlayerDebugWindow : EditorWindow
{
    private const string UssPath = "Packages/com.basis.framework.editor/Editor/StyleSheets/BasisRemotePlayerDebug.uss";

    private Label _subtitle;
    private Label _emptyState;
    private Label _noMatch;
    private VisualElement _controls;
    private VisualElement _detailScroll;

    private TextField _search;
    private SliderInt _slider;
    private Label _position;
    private Label _currentName;
    private Label _currentSub;

    private readonly Dictionary<string, Label> _v = new();
    private readonly Dictionary<string, Label> _p = new();
    private VisualElement _interpBarFill;
    private Label _perfReason;
    private Label _avatarError;

    private readonly List<ushort> _filtered = new();
    private readonly Dictionary<ushort, BasisNetworkReceiver> _byId = new();
    private int _cursor;
    private string _query = string.Empty;
    private uint _lastVersion = uint.MaxValue;
    private bool _dirty = true;
    private bool _jumpToFirst;

    [MenuItem("Basis/Debug/Remote Players", false, 604)]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisRemotePlayerDebugWindow>(BasisEditorLocalization.Get("editor.remotePlayerDebug.title"));
        w.minSize = new Vector2(420, 560);
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.AddToClassList("brp-root");

        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (sheet != null) root.styleSheets.Add(sheet);

        var header = new VisualElement();
        header.AddToClassList("brp-card");
        header.AddToClassList("brp-header-card");
        var title = new Label(BasisEditorLocalization.Get("editor.remotePlayerDebug.title"));
        title.AddToClassList("brp-header-title");
        _subtitle = new Label(BasisEditorLocalization.Get("editor.remotePlayerDebug.notPlaying"));
        _subtitle.AddToClassList("brp-header-subtitle");
        header.Add(title);
        header.Add(_subtitle);
        root.Add(header);

        _emptyState = new Label(BasisEditorLocalization.Get("editor.remotePlayerDebug.enterPlayMode"));
        _emptyState.AddToClassList("brp-help-info");
        root.Add(_emptyState);

        BuildControls(root);

        _noMatch = new Label();
        _noMatch.AddToClassList("brp-help-info");
        _noMatch.style.display = DisplayStyle.None;
        root.Add(_noMatch);

        BuildDetail(root);

        root.schedule.Execute(Refresh).Every(250);
        Refresh();
    }

    private void BuildControls(VisualElement root)
    {
        _controls = new VisualElement();
        _controls.AddToClassList("brp-card");

        _search = new TextField(BasisEditorLocalization.Get("editor.remotePlayerDebug.find"));
        _search.AddToClassList("brp-search");
        _search.RegisterValueChangedCallback(evt =>
        {
            _query = evt.newValue ?? string.Empty;
            _dirty = true;
            _jumpToFirst = true;
            Refresh();
        });
        _controls.Add(_search);

        var navRow = new VisualElement();
        navRow.AddToClassList("brp-controls-row");

        var prev = new Button(() => Step(-1)) { text = "◀" };
        prev.AddToClassList("brp-button");
        navRow.Add(prev);

        _slider = new SliderInt(0, 0) { showInputField = true };
        _slider.AddToClassList("brp-slider");
        _slider.RegisterValueChangedCallback(evt =>
        {
            _cursor = evt.newValue;
            UpdateSelectedNow();
        });
        navRow.Add(_slider);

        var next = new Button(() => Step(1)) { text = "▶" };
        next.AddToClassList("brp-button");
        navRow.Add(next);

        _controls.Add(navRow);

        _position = new Label("-");
        _position.AddToClassList("brp-position");
        _controls.Add(_position);

        root.Add(_controls);
    }

    private void BuildDetail(VisualElement root)
    {
        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.AddToClassList("brp-scroll");
        _detailScroll = scroll;
        VisualElement c = scroll.contentContainer;

        var current = new VisualElement();
        current.AddToClassList("brp-card");
        _currentName = new Label("-");
        _currentName.AddToClassList("brp-current-name");
        _currentSub = new Label("-");
        _currentSub.AddToClassList("brp-current-sub");
        current.Add(_currentName);
        current.Add(_currentSub);
        c.Add(current);

        VisualElement s;

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionIdentity"));
        AddValue(s, "name", BasisEditorLocalization.Get("editor.remotePlayerDebug.displayName"));
        AddValue(s, "uuid", BasisEditorLocalization.Get("editor.remotePlayerDebug.uuid"));
        AddValue(s, "platform", BasisEditorLocalization.Get("sdk.buildReport.platform"));
        AddValue(s, "pid", BasisEditorLocalization.Get("editor.remotePlayerDebug.playerId"));
        AddValue(s, "dindex", BasisEditorLocalization.Get("editor.remotePlayerDebug.dataIndex"));
        AddPill(s, "destroyed", BasisEditorLocalization.Get("editor.remotePlayerDebug.destroyed"));

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionAvatar"));
        AddPill(s, "hasavatar", BasisEditorLocalization.Get("editor.remotePlayerDebug.hasAvatar"));
        AddPill(s, "fallback", BasisEditorLocalization.Get("editor.remotePlayerDebug.fallbackAvatar"));
        AddPill(s, "loading", BasisEditorLocalization.Get("editor.remotePlayerDebug.loading"));
        AddValue(s, "loadmode", BasisEditorLocalization.Get("editor.remotePlayerDebug.loadMode"));
        AddPill(s, "inrange", BasisEditorLocalization.Get("editor.remotePlayerDebug.inAvatarRange"));
        AddValue(s, "lod", BasisEditorLocalization.Get("editor.remotePlayerDebug.lodLevel"));
        AddValue(s, "poseskip", BasisEditorLocalization.Get("editor.remotePlayerDebug.poseSkipCounter"));
        AddPill(s, "perfblock", BasisEditorLocalization.Get("editor.remotePlayerDebug.perfBlocked"));
        _perfReason = AddHelp(s, "brp-help-warn");
        _avatarError = AddHelp(s, "brp-help-error");

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionNetwork"));
        AddPill(s, "hasdata", BasisEditorLocalization.Get("editor.remotePlayerDebug.hasRequiredData"));
        AddPill(s, "holds", BasisEditorLocalization.Get("editor.remotePlayerDebug.bufferHolds"));
        AddPill(s, "cur", BasisEditorLocalization.Get("editor.remotePlayerDebug.hasCurrentBuffer"));
        AddPill(s, "next", BasisEditorLocalization.Get("editor.remotePlayerDebug.hasNextBuffer"));
        AddValue(s, "staged", BasisEditorLocalization.Get("editor.remotePlayerDebug.stagedCount"));
        AddValue(s, "interp", BasisEditorLocalization.Get("editor.remotePlayerDebug.interpolationTime"));
        _interpBarFill = AddBar(s);
        AddValue(s, "rate", BasisEditorLocalization.Get("editor.remotePlayerDebug.playbackRate"));
        AddValue(s, "jitter", BasisEditorLocalization.Get("editor.remotePlayerDebug.jitterDepth"));
        AddValue(s, "seq", BasisEditorLocalization.Get("editor.remotePlayerDebug.highestSequence"));
        AddValue(s, "seen", BasisEditorLocalization.Get("editor.remotePlayerDebug.seenPackets"));
        AddValue(s, "scale", BasisEditorLocalization.Get("editor.remotePlayerDebug.humanScale"));

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionPose"));
        AddValue(s, "pos", BasisEditorLocalization.Get("editor.remotePlayerDebug.position"));
        AddValue(s, "rot", BasisEditorLocalization.Get("editor.remotePlayerDebug.rotationEuler"));
        AddValue(s, "scl", BasisEditorLocalization.Get("editor.remotePlayerDebug.scale"));

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionFace"));
        AddPill(s, "facevis", BasisEditorLocalization.Get("editor.remotePlayerDebug.faceVisible"));
        AddPill(s, "eyebones", BasisEditorLocalization.Get("editor.remotePlayerDebug.hasEyeBones"));
        AddPill(s, "blink", BasisEditorLocalization.Get("editor.remotePlayerDebug.blinking"));
        AddValue(s, "eyeL", BasisEditorLocalization.Get("editor.remotePlayerDebug.eyeLeft"));
        AddValue(s, "eyeR", BasisEditorLocalization.Get("editor.remotePlayerDebug.eyeRight"));
        AddValue(s, "mouth", BasisEditorLocalization.Get("editor.remotePlayerDebug.mouthViseme"));
        AddValue(s, "facegen", BasisEditorLocalization.Get("editor.remotePlayerDebug.faceGeneration"));

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionVoice"));
        AddValue(s, "talk", BasisEditorLocalization.Get("editor.remotePlayerDebug.talkMode"));
        AddPill(s, "selfmute", BasisEditorLocalization.Get("editor.remotePlayerDebug.selfMuted"));
        AddPill(s, "typing", BasisEditorLocalization.Get("editor.remotePlayerDebug.chatTyping"));
        AddPill(s, "audiomod", BasisEditorLocalization.Get("editor.remotePlayerDebug.audioModule"));

        s = Section(c, BasisEditorLocalization.Get("editor.remotePlayerDebug.sectionVisibility"));
        AddPill(s, "blocked", BasisEditorLocalization.Get("editor.remotePlayerDebug.blocked"));
        AddPill(s, "tempblock", BasisEditorLocalization.Get("editor.remotePlayerDebug.tempBlocked"));
        AddPill(s, "effblock", BasisEditorLocalization.Get("editor.remotePlayerDebug.effectivelyBlocked"));
        AddPill(s, "oor", BasisEditorLocalization.Get("editor.remotePlayerDebug.outOfRange"));

        root.Add(scroll);
    }

    // ---------------- navigation / selection ----------------

    private void Step(int delta)
    {
        if (_filtered.Count == 0) return;
        _cursor = Mathf.Clamp(_cursor + delta, 0, _filtered.Count - 1);
        _slider.SetValueWithoutNotify(_cursor);
        UpdateSelectedNow();
    }

    private void Refresh()
    {
        bool playing = Application.isPlaying;
        if (!playing)
        {
            _subtitle.text = BasisEditorLocalization.Get("editor.remotePlayerDebug.notPlaying");
            ShowEmpty(BasisEditorLocalization.Get("editor.remotePlayerDebug.enterPlayMode"));
            return;
        }

        uint version = BasisNetworkPlayers.SnapshotVersion;
        if (version != _lastVersion)
        {
            _lastVersion = version;
            _dirty = true;
        }

        int total = BasisNetworkPlayers.ReceiverCount;
        _subtitle.text = BasisEditorLocalization.Get("editor.remotePlayerDebug.connectedCount", total);

        if (total == 0)
        {
            ShowEmpty(BasisEditorLocalization.Get("editor.remotePlayerDebug.noPlayers"));
            return;
        }

        _emptyState.style.display = DisplayStyle.None;
        _controls.style.display = DisplayStyle.Flex;

        if (_dirty) RebuildWorkingSet();

        if (_filtered.Count == 0)
        {
            _detailScroll.style.display = DisplayStyle.None;
            _noMatch.text = BasisEditorLocalization.Get("editor.remotePlayerDebug.noMatch", _query);
            _noMatch.style.display = DisplayStyle.Flex;
            _position.text = BasisEditorLocalization.Get("editor.remotePlayerDebug.positionNone", total);
            return;
        }

        _noMatch.style.display = DisplayStyle.None;
        _detailScroll.style.display = DisplayStyle.Flex;
        UpdateSelectedNow();
    }

    private void ShowEmpty(string message)
    {
        _emptyState.text = message;
        _emptyState.style.display = DisplayStyle.Flex;
        _controls.style.display = DisplayStyle.None;
        _noMatch.style.display = DisplayStyle.None;
        _detailScroll.style.display = DisplayStyle.None;
    }

    private void RebuildWorkingSet()
    {
        ushort prev = (_cursor >= 0 && _cursor < _filtered.Count) ? _filtered[_cursor] : (ushort)0;
        bool hadSelection = _filtered.Count > 0;

        _byId.Clear();
        _filtered.Clear();

        BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;
        if (snapshot != null)
        {
            for (int i = 0; i < count && i < snapshot.Length; i++)
            {
                BasisNetworkReceiver r = snapshot[i];
                if (r == null) continue;
                _byId[r.playerId] = r;
                if (MatchesQuery(r)) _filtered.Add(r.playerId);
            }
        }

        _slider.highValue = Mathf.Max(0, _filtered.Count - 1);

        if (_jumpToFirst)
        {
            _cursor = 0;
            _jumpToFirst = false;
        }
        else if (hadSelection)
        {
            int idx = _filtered.IndexOf(prev);
            _cursor = idx >= 0 ? idx : Mathf.Min(_cursor, Mathf.Max(0, _filtered.Count - 1));
        }
        else
        {
            _cursor = 0;
        }

        _slider.SetValueWithoutNotify(Mathf.Clamp(_cursor, 0, Mathf.Max(0, _filtered.Count - 1)));
        _dirty = false;
    }

    private bool MatchesQuery(BasisNetworkReceiver r)
    {
        if (string.IsNullOrEmpty(_query)) return true;

        BasisRemotePlayer rp = r.RemotePlayer;
        string name = rp != null && !string.IsNullOrEmpty(rp.DisplayName) ? rp.DisplayName : r.displayName;
        if (!string.IsNullOrEmpty(name) && name.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        string uuid = rp != null ? rp.UUID : null;
        if (!string.IsNullOrEmpty(uuid) && uuid.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        return r.playerId.ToString().IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void UpdateSelectedNow()
    {
        if (_filtered.Count == 0) return;
        _cursor = Mathf.Clamp(_cursor, 0, _filtered.Count - 1);

        ushort id = _filtered[_cursor];
        if (!_byId.TryGetValue(id, out BasisNetworkReceiver receiver) || receiver == null)
        {
            _dirty = true;
            return;
        }

        bool filtering = !string.IsNullOrEmpty(_query);
        _position.text = filtering
            ? BasisEditorLocalization.Get("editor.remotePlayerDebug.positionMatch", _cursor + 1, _filtered.Count, BasisNetworkPlayers.ReceiverCount)
            : BasisEditorLocalization.Get("editor.remotePlayerDebug.positionPlayer", _cursor + 1, _filtered.Count);

        UpdateDetail(receiver);
    }

    // ---------------- detail construction ----------------

    private static VisualElement Section(VisualElement parent, string title)
    {
        var fold = new Foldout { text = title, value = true };
        fold.AddToClassList("brp-foldout");
        parent.Add(fold);
        return fold.contentContainer;
    }

    private void AddValue(VisualElement parent, string key, string label)
    {
        var r = new VisualElement();
        r.AddToClassList("brp-status-row");
        var l = new Label(label);
        l.AddToClassList("brp-status-label");
        var v = new Label("-");
        v.AddToClassList("brp-status-value");
        r.Add(l);
        r.Add(v);
        parent.Add(r);
        _v[key] = v;
    }

    private void AddPill(VisualElement parent, string key, string label)
    {
        var r = new VisualElement();
        r.AddToClassList("brp-status-row");
        var l = new Label(label);
        l.AddToClassList("brp-status-label");
        var p = new Label("-");
        p.AddToClassList("brp-status-pill");
        p.AddToClassList("brp-pill-neutral");
        r.Add(l);
        r.Add(p);
        parent.Add(r);
        _p[key] = p;
    }

    private static Label AddHelp(VisualElement parent, string cssClass)
    {
        var l = new Label();
        l.AddToClassList(cssClass);
        l.style.display = DisplayStyle.None;
        parent.Add(l);
        return l;
    }

    private static VisualElement AddBar(VisualElement parent)
    {
        var track = new VisualElement();
        track.AddToClassList("brp-bar-track");
        var fill = new VisualElement();
        fill.AddToClassList("brp-bar-fill");
        track.Add(fill);
        parent.Add(track);
        return fill;
    }

    // ---------------- detail update ----------------

    private void UpdateDetail(BasisNetworkReceiver receiver)
    {
        BasisRemotePlayer rp = receiver.RemotePlayer;

        string name = rp != null && !string.IsNullOrEmpty(rp.DisplayName)
            ? rp.DisplayName
            : (string.IsNullOrEmpty(receiver.displayName) ? BasisEditorLocalization.Get("editor.common.unknown") : receiver.displayName);
        _currentName.text = $"[{receiver.playerId}] {name}";
        string uuid = rp != null && !string.IsNullOrEmpty(rp.UUID) ? rp.UUID : "-";
        string platform = rp != null && !string.IsNullOrEmpty(rp.PlayerPlatform) ? rp.PlayerPlatform : "-";
        _currentSub.text = $"{uuid}   ·   {platform}";

        SetValue("name", name);
        SetValue("uuid", uuid);
        SetValue("platform", platform);
        SetValue("pid", receiver.playerId.ToString());
        SetValue("dindex", rp != null ? rp.RemotePlayerDataIndex.ToString() : "-");
        PillAlert("destroyed", rp != null && rp.IsDestroyed);

        PillGoodBad("hasavatar", rp != null && rp.BasisAvatar != null);
        PillNote("fallback", rp != null && rp.IsConsideredFallBackAvatar);
        PillNote("loading", rp != null && rp.IsLoadingAnAvatar);
        SetValue("loadmode", rp == null ? "-" : (rp.AlwaysRequestedMode == 1 ? BasisEditorLocalization.Get("editor.remotePlayerDebug.loadModeLocal") : BasisEditorLocalization.Get("editor.remotePlayerDebug.loadModeRemote")));
        PillGoodBad("inrange", rp != null && rp.InAvatarRange);
        SetValue("lod", rp != null ? rp.CurrentLodLevel.ToString() : "-");
        SetValue("poseskip", rp != null ? rp.PoseSkipCounter.ToString() : "-");
        bool perfBlocked = rp != null && rp.IsBlockedByPerformance;
        PillAlert("perfblock", perfBlocked);
        SetHelp(_perfReason, perfBlocked && !string.IsNullOrEmpty(rp.PerformanceBlockReason),
            perfBlocked ? rp.PerformanceBlockReason : null);
        bool hasErr = rp != null && (!string.IsNullOrEmpty(rp.AvatarLoadErrorMessage) || rp.HasFailedAvatarLoadGlobally);
        string errText = rp == null
            ? null
            : (rp.HasFailedAvatarLoadGlobally ? BasisEditorLocalization.Get("editor.remotePlayerDebug.avatarLoadFailed") + " " : string.Empty) + rp.AvatarLoadErrorMessage;
        SetHelp(_avatarError, hasErr, errText);

        PillGoodBad("hasdata", receiver.hasRequiredData);
        PillGoodBad("holds", receiver.HasBufferHolds);
        PillGoodBad("cur", receiver.HasCurrentBuffer);
        PillGoodBad("next", receiver.HasNextBuffer);
        SetValue("staged", receiver.StagedCount.ToString());
        double interp = receiver.InterpolationTimeDebug;
        SetValue("interp", interp.ToString("F2"));
        if (_interpBarFill != null)
            _interpBarFill.style.width = new Length(Mathf.Clamp01((float)interp) * 100f, LengthUnit.Percent);
        SetValue("rate", receiver.LastPlaybackRate.ToString("F2"));
        SetValue("jitter", receiver.DynamicJitterDepth.ToString("F2"));
        SetValue("seq", receiver.HighestSequence.ToString());
        SetValue("seen", receiver.SeenPackets.ToString());
        SetValue("scale", receiver.CachedHumanScaleDebug.ToString("F3"));

        receiver.GetLatestNetworkPose(out float3 pos, out quaternion rot, out float3 scl);
        SetValue("pos", $"{pos.x:F2}, {pos.y:F2}, {pos.z:F2}");
        Vector3 e = ((Quaternion)rot).eulerAngles;
        SetValue("rot", $"{e.x:F0}, {e.y:F0}, {e.z:F0}");
        SetValue("scl", $"{scl.x:F2}, {scl.y:F2}, {scl.z:F2}");

        BasisRemoteFaceDriver fd = rp != null ? rp.RemoteFaceDriver : null;
        PillGoodBad("facevis", rp != null && rp.FaceIsVisible);
        PillGoodBad("eyebones", fd != null && fd.HasEyeBones);
        PillGoodBad("blink", fd != null && fd.BlinkingEnabled);
        float[] ea = receiver.EyesAndMouth;
        if (ea != null && ea.Length >= 6)
        {
            SetValue("eyeL", BasisEditorLocalization.Get("editor.remotePlayerDebug.eyeValue", ea[1], ea[0]));
            SetValue("eyeR", BasisEditorLocalization.Get("editor.remotePlayerDebug.eyeValue", ea[3], ea[2]));
            SetValue("mouth", $"{ea[4]:F2}, {ea[5]:F2}");
        }
        SetValue("facegen", fd != null ? fd.FaceGeneration.ToString() : "-");

        SetValue("talk", rp != null ? rp.TalkMode.ToString() : "-");
        PillNote("selfmute", rp != null && rp.IsSelfMuted);
        PillNote("typing", rp != null && rp.IsChatTyping);
        PillGoodBad("audiomod", receiver.AudioReceiverModule != null);

        PillAlert("blocked", rp != null && rp.IsBlocked);
        PillAlert("tempblock", rp != null && rp.TempBlocked);
        PillAlert("effblock", rp != null && rp.IsEffectivelyBlocked);
        PillNote("oor", rp != null && rp.OutOfRangeFromLocal);
    }

    // ---------------- setters ----------------

    private void SetValue(string key, string text)
    {
        if (_v.TryGetValue(key, out Label l)) l.text = text;
    }

    private static string YesNo(bool on) => on ? BasisEditorLocalization.Get("sdk.cilbox.state.yes") : BasisEditorLocalization.Get("sdk.cilbox.state.no");

    private void PillGoodBad(string key, bool on) =>
        SetPill(key, YesNo(on), on ? "brp-pill-good" : "brp-pill-bad");

    private void PillAlert(string key, bool on) =>
        SetPill(key, YesNo(on), on ? "brp-pill-bad" : "brp-pill-neutral");

    private void PillNote(string key, bool on) =>
        SetPill(key, YesNo(on), on ? "brp-pill-warn" : "brp-pill-neutral");

    private void SetPill(string key, string text, string cssClass)
    {
        if (!_p.TryGetValue(key, out Label l)) return;
        l.text = text;
        l.RemoveFromClassList("brp-pill-good");
        l.RemoveFromClassList("brp-pill-bad");
        l.RemoveFromClassList("brp-pill-warn");
        l.RemoveFromClassList("brp-pill-neutral");
        l.AddToClassList(cssClass);
    }

    private static void SetHelp(Label l, bool show, string text)
    {
        if (l == null) return;
        l.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        if (show) l.text = text;
    }
}
