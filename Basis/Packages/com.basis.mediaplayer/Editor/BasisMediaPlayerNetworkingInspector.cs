using Basis.Editor.Localization;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisMediaPlayerNetworking))]
public class BasisMediaPlayerNetworkingInspector : Editor
{
    private const string UxmlPath = "Packages/com.basis.mediaplayer/Editor/StyleSheets/MediaPlayerNetworkingSDK.uxml";
    private const string UssPath = "Packages/com.basis.mediaplayer/Editor/StyleSheets/MediaPlayerSDK.uss";

    private BasisMediaPlayerNetworking _target;
    private VisualElement _root;
    private Label _canControl, _hasPlayer, _editHint;
    // The two cards that only do anything in Play Mode. Left visible they
    // read as authored settings, the URL box especially, since it sits a
    // component away from the player's own URL field.
    private VisualElement _urlCard, _actionsCard;

    public override VisualElement CreateInspectorGUI()
    {
        _target = (BasisMediaPlayerNetworking)target;
        _root = new VisualElement();

        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (tree == null)
        {
            _root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.inspector.uxmlMissing", "MediaPlayerNetworkingSDK.uxml"), HelpBoxMessageType.Error));
            return _root;
        }
        tree.CloneTree(_root);
        BasisEditorLocalization.LocalizeTree(_root, "editor.mediaPlayer.uxml.networking.");
        if (sheet != null) _root.styleSheets.Add(sheet);

        BindByName("AdminOnlyField", "AdminOnly");
        BindByName("AllowAnyoneField", "AllowAnyoneToTakeControl");
        BindByName("AnyoneCanControlField", "AnyoneCanControl");
        BindByName("HeartbeatField", "PositionHeartbeatSeconds");
        BindByName("VerboseLoggingField", "VerboseLogging");
        _root.Bind(serializedObject);

        _canControl = _root.Q<Label>("StatusCanControl");
        _hasPlayer = _root.Q<Label>("StatusHasPlayer");
        _editHint = _root.Q<Label>("StatusEditModeHint");
        _urlCard = _root.Q<VisualElement>("UrlCard");
        _actionsCard = _root.Q<VisualElement>("ActionsCard");

        var playBtn = _root.Q<Button>("ActPlayButton");
        if (playBtn != null) playBtn.clicked += () => { if (PlayModeOnly()) _ = _target.Play(); };
        var pauseBtn = _root.Q<Button>("ActPauseButton");
        if (pauseBtn != null) pauseBtn.clicked += () => { if (PlayModeOnly()) _ = _target.Pause(); };
        var resumeBtn = _root.Q<Button>("ActResumeButton");
        if (resumeBtn != null) resumeBtn.clicked += () => { if (PlayModeOnly()) _ = _target.Resume(); };
        var stopBtn = _root.Q<Button>("ActStopButton");
        if (stopBtn != null) stopBtn.clicked += () => { if (PlayModeOnly()) _ = _target.Stop(); };
        var resyncLocalBtn = _root.Q<Button>("ActResyncLocalButton");
        if (resyncLocalBtn != null) resyncLocalBtn.clicked += () => { if (PlayModeOnly()) _target.ResyncLocal(); };
        var resyncEveryoneBtn = _root.Q<Button>("ActResyncEveryoneButton");
        if (resyncEveryoneBtn != null) resyncEveryoneBtn.clicked += () => { if (PlayModeOnly()) _ = _target.ResyncEveryone(); };

        var urlField = _root.Q<TextField>("UrlField");
        var loadBtn = _root.Q<Button>("ActLoadUrlButton");
        if (loadBtn != null)
        {
            loadBtn.clicked += () =>
            {
                if (!PlayModeOnly()) return;
                string url = urlField != null ? urlField.value : null;
                if (string.IsNullOrWhiteSpace(url)) return;
                _ = _target.SetUrl(url.Trim());
            };
        }

        _root.schedule.Execute(RefreshStatus).Every(250);
        // Once up front as well, or the Play Mode cards are drawn visible
        // and only hide a quarter of a second later.
        RefreshStatus();
        return _root;
    }

    private bool PlayModeOnly()
    {
        if (Application.isPlaying && _target != null) return true;
        Debug.LogWarning("BasisMediaPlayerNetworking actions only run in Play Mode.");
        return false;
    }

    private void RefreshStatus()
    {
        if (_target == null) _target = (BasisMediaPlayerNetworking)target;
        if (_target == null) return;

        bool live = Application.isPlaying;
        Show(_editHint, !live);
        // Every control on these two needs a running session and
        // ownership, and warns to the console if clicked without them.
        Show(_urlCard, live);
        Show(_actionsCard, live);

        SetPill(_canControl, _target.CanLocallyControl, live);
        SetPill(_hasPlayer, _target.MediaPlayer != null, live);
    }

    private static void Show(VisualElement element, bool show)
    {
        if (element != null) element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private static void SetPill(Label l, bool value, bool live)
    {
        if (l == null) return;
        l.RemoveFromClassList("bvp-pill-neutral");
        l.RemoveFromClassList("bvp-pill-good");
        l.RemoveFromClassList("bvp-pill-bad");
        if (!live) { l.text = "—"; l.AddToClassList("bvp-pill-neutral"); return; }
        l.text = BasisEditorLocalization.Get(value ? "sdk.cilbox.state.yes" : "sdk.cilbox.state.no");
        l.AddToClassList(value ? "bvp-pill-good" : "bvp-pill-bad");
    }

    private void BindByName(string name, string property)
    {
        if (_root.Q<VisualElement>(name) is IBindable bindable) bindable.bindingPath = property;
    }
}
