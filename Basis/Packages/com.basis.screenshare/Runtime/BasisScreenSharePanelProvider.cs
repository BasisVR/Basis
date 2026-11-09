using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Basis.BasisUI;
using UnityEngine;

[AutoStaticsCleanup]
public partial class BasisScreenSharePanelProvider : BasisMenuActionProvider<BasisMainMenu>
{
    public const string StaticTitleKey = "menu.provider.screenShare";
    public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);
    static BasisScreenSharePanelProvider instance;
    static bool quitting;
    public override string Title => StaticTitle;
    public override string IconAddress => AddressableAssets.Sprites.Computer;
    public override int Order => 9;
    public override bool Hidden => BasisScreenShareClient.Count == 0;
    BasisMenuPanel panel;
    PanelElementDescriptor statusGroup, viewingGroup;
    PanelDropdown sourceDropdown;
    PanelButton startButton, showHiddenButton;
    readonly List<BasisScreenShareNative.Source> sources = new List<BasisScreenShareNative.Source>();
    BasisScreenShare watched;
    readonly StringBuilder builder = new StringBuilder(256);

    [RuntimeInitializeOnLoadMethod]
    static void AddToMenu()
    {
        quitting = false;
        instance = new BasisScreenSharePanelProvider();
        BasisMenuBase<BasisMainMenu>.AddProvider(instance);
        BasisScreenShareClient.Changed -= RefreshMainMenu;
        BasisScreenShareClient.Changed += RefreshMainMenu;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
        SettingsProviderAdminTab.LockSectionBuilders.Remove(BuildAdminLock);
        SettingsProviderAdminTab.LockSectionBuilders.Add(BuildAdminLock);
    }

    static void BuildAdminLock(RectTransform container)
    {
        PanelToggle toggle = PanelToggle.CreateNewEntry(container);
        toggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.title.lockScreenShare"));
        toggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.title.lockScreenShare.tooltip"));
        toggle.SetValueWithoutNotify(BasisScreenShareClient.Locked);
        toggle.OnValueChanged += value =>
        {
            if (!BasisScreenShareClient.SendLock(value)) toggle.SetValueWithoutNotify(BasisScreenShareClient.Locked);
        };
        System.Action sync = null;
        sync = () =>
        {
            if (toggle == null)
            {
                BasisScreenShareClient.Changed -= sync;
                return;
            }
            toggle.SetValueWithoutNotify(BasisScreenShareClient.Locked);
        };
        BasisScreenShareClient.Changed += sync;
    }

    static void OnQuitting()
    {
        quitting = true;
        BasisScreenShareClient.Changed -= RefreshMainMenu;
    }

    static void RefreshMainMenu()
    {
        if (quitting) return;
        if (BasisMenuBase<BasisMainMenu>.Instance) BasisMenuBase<BasisMainMenu>.Instance.BindProvidersToButtons();
        if (BasisMainMenu.ActiveMenuTitle != StaticTitle || instance == null) return;
        if (BasisScreenShareClient.Count == 0)
        {
            BasisMainMenu.CloseActivePanel();
            return;
        }
        instance.Watch(BasisScreenShareClient.Local);
        instance.Refresh();
    }

    public static void OpenPanel()
    {
        if (instance == null || BasisMainMenu.ActiveMenuTitle == StaticTitle) return;
        if (!BasisMenuBase<BasisMainMenu>.Instance) return;
        instance.RunAction();
    }

    public override void RunAction()
    {
        if (BasisMainMenu.ActiveMenuTitle == Title)
        {
            BasisMainMenu.CloseActivePanel();
            return;
        }
        panel = BasisMainMenu.CreateActiveMenu(BasisMenuPanel.PanelData.Standard(Title), BasisMenuPanel.PanelStyles.Page);
        BoundButton?.BindActiveStateToAddressablesInstance(panel);
        panel.OnInstanceReleased += OnPanelClosed;
        RectTransform container = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, panel.Descriptor.ContentParent).ContentParent;
        BuildShareSection(container);
        BuildViewingSection(container);
        Watch(BasisScreenShareClient.Local);
        Refresh();
    }

    void BuildShareSection(RectTransform container)
    {
        statusGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        statusGroup.SetTitle(BasisLocalization.Get("screenShare.yourShare"));
        if (BasisScreenShareClient.Local == null) return;
        if (!BasisScreenShareNative.CanCapture) return;
        RectTransform group = statusGroup.ContentParent;
        sourceDropdown = PanelDropdown.CreateNewEntry(group);
        sourceDropdown.Descriptor.SetTitle(BasisLocalization.Get("screenShare.source"));
        sourceDropdown.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.source.tooltip"));
        ReloadSources();
        sourceDropdown.OnValueChanged = _ => OnSourceChanged();
        PanelButton refresh = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, group);
        refresh.Descriptor.SetTitle(BasisLocalization.Get("screenShare.refreshSources"));
        refresh.OnClicked += ReloadSources;
        PanelDropdown resolution = PanelDropdown.CreateNewEntry(group);
        resolution.Descriptor.SetTitle(BasisLocalization.Get("screenShare.resolution"));
        resolution.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.resolution.tooltip"));
        resolution.AssignLocalizedEntries(new List<string>(BasisScreenShareSettings.Resolutions), new List<string> { "screenShare.resolution.native", "screenShare.resolution.1440", "screenShare.resolution.1080", "screenShare.resolution.720", "screenShare.resolution.480" });
        resolution.AssignBinding(BasisScreenShareSettings.Resolution);
        PanelDropdown fps = PanelDropdown.CreateNewEntry(group);
        fps.Descriptor.SetTitle(BasisLocalization.Get("screenShare.frameRate"));
        fps.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.frameRate.tooltip"));
        fps.AssignLocalizedEntries(new List<string>(BasisScreenShareSettings.FrameRates), new List<string> { "screenShare.frameRate.60", "screenShare.frameRate.30", "screenShare.frameRate.15", "screenShare.frameRate.5" });
        fps.AssignBinding(BasisScreenShareSettings.FrameRate);
        PanelDropdown quality = PanelDropdown.CreateNewEntry(group);
        quality.Descriptor.SetTitle(BasisLocalization.Get("screenShare.quality"));
        quality.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.quality.tooltip"));
        quality.AssignLocalizedEntries(new List<string>(BasisScreenShareSettings.Qualities), new List<string> { "screenShare.quality.auto", "screenShare.quality.low", "screenShare.quality.high", "screenShare.quality.max" });
        quality.AssignBinding(BasisScreenShareSettings.Quality);
        PanelToggle audio = PanelToggle.CreateNewEntry(group);
        audio.Descriptor.SetTitle(BasisLocalization.Get("screenShare.audio"));
        audio.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.audio.tooltip"));
        audio.AssignBinding(BasisScreenShareSettings.ShareAudio);
        PanelDropdown audioSource = PanelDropdown.CreateNewEntry(group);
        audioSource.Descriptor.SetTitle(BasisLocalization.Get("screenShare.audioSource"));
        audioSource.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.audioSource.tooltip"));
        audioSource.AssignLocalizedEntries(new List<string>(BasisScreenShareSettings.AudioSources), new List<string> { "screenShare.audioSource.auto", "screenShare.audioSource.application", "screenShare.audioSource.system" });
        audioSource.AssignBinding(BasisScreenShareSettings.AudioSource);
        PanelToggle cursor = PanelToggle.CreateNewEntry(group);
        cursor.Descriptor.SetTitle(BasisLocalization.Get("screenShare.cursor"));
        cursor.AssignBinding(BasisScreenShareSettings.ShowCursor);
        startButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, group);
        startButton.OnClicked += OnStartClicked;
    }

    void BuildViewingSection(RectTransform container)
    {
        viewingGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        viewingGroup.SetTitle(BasisLocalization.Get("screenShare.viewing"));
        RectTransform group = viewingGroup.ContentParent;
        PanelToggle receive = PanelToggle.CreateNewEntry(group);
        receive.Descriptor.SetTitle(BasisLocalization.Get("screenShare.receive"));
        receive.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.receive.tooltip"));
        receive.AssignBinding(BasisScreenShareSettings.Receive);
        PanelSlider distance = PanelSlider.CreateNew(group);
        distance.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("screenShare.viewDistance"), 5f, 200f, true, 0, ValueDisplayMode.Raw));
        distance.Descriptor.SetTooltip(BasisLocalization.Get("screenShare.viewDistance.tooltip"));
        distance.SetValueWithoutNotify(BasisScreenShareSettings.ViewDistance.RawValue);
        distance.OnValueChanged = v => BasisScreenShareSettings.ViewDistance.SetValue(v);
        PanelSlider volume = PanelSlider.CreateNew(group);
        volume.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("screenShare.volume")));
        volume.SetValueWithoutNotify(Mathf.Clamp01(BasisScreenShareSettings.Volume.RawValue) * 100f);
        volume.OnValueChanged = v => BasisScreenShareSettings.Volume.SetValue(Mathf.Clamp01(v / 100f));
        showHiddenButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, group);
        showHiddenButton.Descriptor.SetTitle(BasisLocalization.Get("screenShare.showHidden"));
        showHiddenButton.OnClicked += BasisScreenShareClient.ShowHidden;
    }

    void ReloadSources()
    {
        if (sourceDropdown == null) return;
        sources.Clear();
        sources.AddRange(BasisScreenShare.Sources());
        List<string> ids = new List<string>(sources.Count), labels = new List<string>(sources.Count);
        foreach (BasisScreenShareNative.Source source in sources)
        {
            ids.Add(source.id);
            string label = source.IsPortal ? BasisLocalization.Get("screenShare.source.portal") : source.IsMonitor ? BasisLocalization.Get("screenShare.source.display", source.name, source.width, source.height) : source.name;
            labels.Add(label.Length > 60 ? label.Substring(0, 57) + "..." : label);
        }
        if (ids.Count == 0)
        {
            ids.Add(string.Empty);
            labels.Add(BasisLocalization.Get("screenShare.source.none"));
        }
        string keep = BasisScreenShareClient.Local != null && !string.IsNullOrEmpty(BasisScreenShareClient.Local.SourceId) ? BasisScreenShareClient.Local.SourceId : sourceDropdown.Value;
        sourceDropdown.AssignEntries(ids, labels);
        string pick = ids.Contains(keep) ? keep : ids[PrimaryIndex()];
        sourceDropdown.SetValueWithoutNotify(pick);
    }

    int PrimaryIndex()
    {
        for (int i = 0; i < sources.Count; i++) if (sources[i].primary) return i;
        return 0;
    }

    BasisScreenShareNative.Source Selected()
    {
        string id = sourceDropdown != null ? sourceDropdown.Value : null;
        foreach (BasisScreenShareNative.Source source in sources) if (source.id == id) return source;
        return sources.Count > 0 ? sources[PrimaryIndex()] : null;
    }

    void OnStartClicked()
    {
        BasisScreenShare local = BasisScreenShareClient.Local;
        if (local == null) return;
        if (local.State == BasisScreenShare.ShareState.Idle || local.State == BasisScreenShare.ShareState.Error) local.StartSharing(Selected());
        else local.StopSharing();
        Refresh();
    }

    void OnSourceChanged()
    {
        BasisScreenShare local = BasisScreenShareClient.Local;
        BasisScreenShareNative.Source next = Selected();
        if (local == null || next == null || !local.IsSharing || next.id == local.SourceId) return;
        local.Recast(next);
        Refresh();
    }

    void Watch(BasisScreenShare local)
    {
        if (watched == local) return;
        if (watched != null) watched.Changed -= Refresh;
        watched = local;
        if (watched != null) watched.Changed += Refresh;
    }

    void Refresh()
    {
        if (panel == null || statusGroup == null) return;
        BasisScreenShare local = BasisScreenShareClient.Local;
        statusGroup.SetDescription(Describe(local));
        if (startButton != null)
        {
            bool idle = local == null || local.State == BasisScreenShare.ShareState.Idle || local.State == BasisScreenShare.ShareState.Error;
            startButton.Descriptor.SetTitle(BasisLocalization.Get(idle ? "screenShare.start" : "screenShare.stop"));
        }
        if (viewingGroup != null) viewingGroup.SetDescription(DescribeRemotes());
        if (showHiddenButton != null) showHiddenButton.gameObject.SetActive(BasisScreenShareClient.HiddenCount > 0);
    }

    string Describe(BasisScreenShare local)
    {
        if (local == null) return BasisLocalization.Get("screenShare.status.none");
        if (!BasisScreenShareNative.CanCapture) return BasisLocalization.Get(BasisScreenShareNative.UnsupportedKey);
        if (!BasisScreenShareNative.PluginCurrent) return BasisLocalization.Get(Application.isEditor ? "screenShare.error.restart" : "screenShare.error.pluginMissing");
        switch (local.State)
        {
            case BasisScreenShare.ShareState.Error: return BasisLocalization.Get("screenShare.status.error", local.StatusError);
            case BasisScreenShare.ShareState.Starting: return BasisLocalization.Get(!BasisScreenShareClient.Available && Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected ? "screenShare.status.unsupportedServer" : BasisScreenShareNative.IsLinux && local.CaptureStatus != null && !local.CaptureStatus.Running ? "screenShare.status.choose" : "screenShare.status.starting");
            case BasisScreenShare.ShareState.Idle when BasisScreenShareClient.LockedForMe: return BasisLocalization.Get("screenShare.status.locked");
            case BasisScreenShare.ShareState.Idle: return BasisLocalization.Get(BasisScreenShareClient.Available ? "screenShare.status.idle" : Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected ? "screenShare.status.unsupportedServer" : "screenShare.status.offline");
        }
        BasisScreenShareNative.Status status = local.CaptureStatus;
        builder.Clear();
        builder.Append(BasisLocalization.Get(local.State == BasisScreenShare.ShareState.Live ? "screenShare.status.live" : "screenShare.status.waiting", local.SourceName, local.TotalViewers));
        if (status != null)
        {
            builder.Append('\n').Append(status.width.ToString(CultureInfo.InvariantCulture)).Append('×').Append(status.height.ToString(CultureInfo.InvariantCulture));
            builder.Append(" · ").Append((local.CurrentKbps / 1000f).ToString("0.0", CultureInfo.InvariantCulture)).Append(" Mb/s");
            builder.Append(" · ").Append(status.encoder);
            if (!status.audio && BasisScreenShareSettings.ShareAudio.RawValue && !string.IsNullOrEmpty(status.audio_error)) builder.Append('\n').Append(BasisLocalization.Get("screenShare.status.noAudio", status.audio_error));
        }
        return builder.ToString();
    }

    string DescribeRemotes()
    {
        int count = BasisScreenShareClient.RemoteCount, hidden = BasisScreenShareClient.HiddenCount;
        if (count == 0) return BasisLocalization.Get("screenShare.viewing.none");
        string text = BasisLocalization.Get("screenShare.viewing.count", count);
        return hidden > 0 ? text + "\n" + BasisLocalization.Get("screenShare.viewing.hidden", hidden) : text;
    }

    void OnPanelClosed()
    {
        Watch(null);
        panel = null;
        statusGroup = viewingGroup = null;
        sourceDropdown = null;
        startButton = showHiddenButton = null;
    }
}

public class BasisScreenShareRunner : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Create()
    {
        GameObject go = new GameObject("Basis Screen Share") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<BasisScreenShareRunner>();
    }

    void Update() => BasisScreenShareClient.Pump();
}
