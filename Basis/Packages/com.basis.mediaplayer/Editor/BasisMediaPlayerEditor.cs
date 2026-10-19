using Basis.Editor.Localization;
using System;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Inspector for <see cref="BasisMediaPlayer"/>: the authored source and
/// buffering settings, what a resolver reported for the current media, a live
/// session readout, and transport controls while playing. Also carries the
/// scene-setup menu items, so a test player is one click away in any project
/// referencing the package.
/// </summary>
[CustomEditor(typeof(BasisMediaPlayer))]
public class BasisMediaPlayerEditor : Editor
{
    BasisMediaPlayer _target;
    VisualElement _root;
    Label _title, _uploader, _source, _duration, _nowPlayingHint, _capturePath;
    Label _state, _position, _banked, _video, _frames, _audio, _sync, _error, _editHint;
    VisualElement _syncRow;
    Button _open, _play, _pause, _back, _forward, _close;

    static string PackagePath => UnityEditor.PackageManager.PackageInfo
        .FindForAssembly(typeof(BasisMediaPlayerEditor).Assembly)?.assetPath;

    public override VisualElement CreateInspectorGUI()
    {
        _target = (BasisMediaPlayer)target;
        _root = new VisualElement();

        string package = PackagePath;
        if (string.IsNullOrEmpty(package))
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.packagePathMissing"), HelpBoxMessageType.Error));
            return _root;
        }

        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uxml").Replace('\\', '/'));
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
            Path.Combine(package, "Editor/StyleSheets/MediaPlayerSDK.uss").Replace('\\', '/'));
        if (tree == null)
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "MediaPlayerSDK.uxml"), HelpBoxMessageType.Error));
            return _root;
        }
        tree.CloneTree(_root);
        BasisEditorLocalization.LocalizeTree(_root, "editor.mediaPlayer.uxml.main.");
        if (sheet != null) _root.styleSheets.Add(sheet);

        BindByName("PerPlatformUrlsField", "perPlatformUrls");
        BindByName("UrlField", "url");
        BindByName("AndroidUrlField", "androidUrl");
        BindByName("LivenessField", "liveness");
        BindByName("PlayOnStartField", "playOnStart");
        BindByName("MaxDivergenceField", "maxDivergenceMs");
        BindByName("EngineCaptureField", "engineCapture");
        BindByName("EngineCaptureFileField", "engineCaptureFileName");
        BindByName("EngineCaptureAppendField", "engineCaptureAppend");
        BindByName("AllowLocalField", "allowLocalAddresses");
        BindByName("DisplayNameField", "DisplayName");
        BindByName("AutoPlayField", "AutoPlayOnSourceAssigned");
        BindByName("LoopField", "Loop");
        BindByName("LoopRestartDelayField", "LoopRestartDelaySeconds");
        BindByName("StopAfterField", "StopAfterSeconds");
        BindByName("StopOnDisableField", "StopOnDisable");
        BindByName("VolumeField", "Volume");
        BindByName("MuteField", "Mute");
        BindByName("VerboseLoggingField", "VerboseLogging");
        BindByName("FlipScreenshotField", "FlipVerticallyForScreenshot");
        _root.Bind(serializedObject);
        SetUpPerPlatformUrls();

        _title = _root.Q<Label>("NowPlayingTitle");
        _uploader = _root.Q<Label>("NowPlayingUploader");
        _source = _root.Q<Label>("NowPlayingSource");
        _duration = _root.Q<Label>("NowPlayingDuration");
        _nowPlayingHint = _root.Q<Label>("NowPlayingHint");
        _capturePath = _root.Q<Label>("EngineCapturePath");

        _state = _root.Q<Label>("StatusState");
        _position = _root.Q<Label>("StatusPosition");
        _banked = _root.Q<Label>("StatusBanked");
        _video = _root.Q<Label>("StatusVideo");
        _frames = _root.Q<Label>("StatusFrames");
        _audio = _root.Q<Label>("StatusAudio");
        _sync = _root.Q<Label>("StatusSync");
        _syncRow = _root.Q<VisualElement>("SyncRow");
        _error = _root.Q<Label>("StatusError");
        _editHint = _root.Q<Label>("StatusEditModeHint");

        _open = _root.Q<Button>("ActOpenButton");
        _play = _root.Q<Button>("ActPlayButton");
        _pause = _root.Q<Button>("ActPauseButton");
        _back = _root.Q<Button>("ActBackButton");
        _forward = _root.Q<Button>("ActForwardButton");
        _close = _root.Q<Button>("ActCloseButton");

        // Through the router, so an authored page URL resolves here the same
        // way it would at Start rather than failing to open.
        Wire(_open, () => _target.LoadUrl(_target.ResolvedUrl));
        Wire(_play, () => _target.Play());
        Wire(_pause, () => _target.Pause());
        Wire(_back, () => _target.Seek(_target.PositionSeconds - 10.0));
        Wire(_forward, () => _target.Seek(_target.PositionSeconds + 10.0));
        Wire(_close, () => _target.Stop());

        var debug = _root.Q<Button>("OpenDebugWindowButton");
        if (debug != null) debug.clicked += BasisMediaPlayerDebugWindow.ShowWindow;

        _root.schedule.Execute(Refresh).Every(250);
        Refresh();
        return _root;
    }

    void Wire(Button button, Action action)
    {
        if (button == null) return;
        button.clicked += () =>
        {
            if (_target == null) return;
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[BasisMedia] transport controls only run in play mode.");
                return;
            }
            action();
        };
    }

    void Refresh()
    {
        if (_target == null) _target = (BasisMediaPlayer)target;
        if (_target == null) return;

        bool live = Application.isPlaying;
        Show(_editHint, !live);
        Show(_root.Q<VisualElement>("TransportCard"), live);

        BasisResolvedMedia media = _target.Media;
        bool described = media != null;
        Show(_nowPlayingHint, !described);
        SetText(_title, described ? Or(media.Title, "—") : "—");
        SetText(_uploader, described ? Or(media.Uploader, "—") : "—");
        SetText(_source, described ? Or(media.SourceUrl, media.Url) : "—");
        SetText(_duration, described && media.Duration.HasValue
            ? Clock(media.Duration.Value.TotalSeconds)
            : "—");

        SetState(_state, live ? _target.State : (BmState?)null);
        SetText(_position, live
            ? (_target.DurationSeconds > 0
                ? $"{Clock(_target.PositionSeconds)} / {Clock(_target.DurationSeconds)}"
                : Clock(_target.PositionSeconds))
            : "—");
        SetText(_banked, live ? $"{_target.BankedMilliseconds} ms" : "—");
        SetText(_video, live && _target.VideoSize.x > 0
            ? $"{_target.VideoSize.x}×{_target.VideoSize.y}"
            : "—");
        SetText(_frames, live
            ? BasisEditorLocalization.Get("editor.mediaPlayer.player.frames", _target.PresentedFrameCount, _target.FramesDecoded)
            : "—");
        SetText(_audio, live ? BasisEditorLocalization.Get("editor.mediaPlayer.player.audioFrames", _target.AudioFramesPulled) : "—");

        // Only meaningful while a sync target is being converged on.
        int ppm = live ? _target.SyncRatePpm : 0;
        Show(_syncRow, ppm != 0);
        SetText(_sync, $"{ppm} ppm");

        string capture = _target.EngineCapturePath;
        Show(_capturePath, !string.IsNullOrEmpty(capture));
        SetText(_capturePath, capture);

        bool failed = live && _target.State == BmState.Error;
        Show(_error, failed);
        if (failed && _error != null) _error.text = BasisEditorLocalization.Get("editor.mediaPlayer.player.engineError", _target.ErrorCode);
    }

    static string Or(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "—";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    static void Show(VisualElement element, bool show)
    {
        if (element != null) element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
    }

    static void SetText(Label label, string value)
    {
        if (label != null) label.text = value;
    }

    static void SetState(Label label, BmState? state)
    {
        if (label == null) return;
        label.RemoveFromClassList("bvp-pill-neutral");
        label.RemoveFromClassList("bvp-pill-good");
        label.RemoveFromClassList("bvp-pill-bad");
        if (!state.HasValue) { label.text = "—"; label.AddToClassList("bvp-pill-neutral"); return; }
        label.text = state.Value.ToString();
        label.AddToClassList(state.Value switch
        {
            BmState.Playing => "bvp-pill-good",
            BmState.Error => "bvp-pill-bad",
            _ => "bvp-pill-neutral",
        });
    }

    void BindByName(string name, string property)
    {
        if (_root.Q<VisualElement>(name) is IBindable bindable) bindable.bindingPath = property;
    }

    /// <summary>
    /// One URL, or one per platform. Off shows a single "URL"; on relabels it
    /// "Windows URL" and reveals the Android one beside it, so the common case
    /// is not asked to look at a field it will never fill in.
    /// </summary>
    void SetUpPerPlatformUrls()
    {
        var toggle = _root.Q<Toggle>("PerPlatformUrlsField");
        var urlField = _root.Q<TextField>("UrlField");
        var androidField = _root.Q<TextField>("AndroidUrlField");
        if (toggle == null || urlField == null || androidField == null) return;

        // A player authored before this toggle existed, or by a script, states
        // its intent by carrying an Android URL at all. Adopt that rather than
        // presenting it as switched off while it is plainly in effect.
        var player = (BasisMediaPlayer)target;
        if (!player.perPlatformUrls && !string.IsNullOrEmpty(player.androidUrl))
        {
            serializedObject.FindProperty("perPlatformUrls").boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            toggle.SetValueWithoutNotify(true);
        }

        Apply(toggle.value);
        toggle.RegisterValueChangedCallback(evt =>
        {
            // Switching back to one URL drops the Android one, so a value
            // cannot linger out of sight and still decide what plays there.
            if (!evt.newValue)
            {
                SerializedProperty android = serializedObject.FindProperty("androidUrl");
                if (!string.IsNullOrEmpty(android.stringValue))
                {
                    android.stringValue = string.Empty;
                    serializedObject.ApplyModifiedProperties();
                }
            }
            Apply(evt.newValue);
        });

        void Apply(bool perPlatform)
        {
            urlField.label = BasisEditorLocalization.Get(perPlatform ? "editor.mediaPlayer.player.windowsUrl" : "editor.mediaPlayer.uxml.main.urlField.label");
            androidField.style.display = perPlatform ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
