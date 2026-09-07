using System.Collections.Generic;
using Basis.BasisUI;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Mirrors
{
    public class BasisMirrorPanelProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        public const string StaticTitleKey = "menu.provider.mirror";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);

        private static readonly int[] ResolutionPresets = { 256, 512, 1024, 2048, 4096, 8192 };
        private static readonly int[] MsaaSampleCounts = { 1, 2, 4, 8 };
        private static readonly int[] DepthBitOptions = { 16, 24 };

        private static BasisMirrorPanelProvider _instance;
        private static bool _quitting;

        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Mirror;
        public override int Order => 9;
        public override bool Hidden => BasisMirrorRegistry.Count == 0;

        private BasisMenuPanel _panel;
        private PanelTabGroup _tabGroup;
        private RectTransform _navColumn;
        private RectTransform _tabColumn;
        private readonly List<RectTransform> _pageContents = new List<RectTransform>();
        private static int _lastTabIndex;

        private PanelDropdown _selector;
        private PanelElementDescriptor _emptyState;
        private PanelElementDescriptor _statusGroup;

        private PanelDropdown _presetDropdown;
        private PanelSlider _widthSlider;
        private PanelSlider _heightSlider;
        private PanelElementDescriptor _placementGroup;
        private PanelToggle _grabbableToggle;
        private PanelToggle _moveWithPlayspaceToggle;
        private PanelDropdown _msaaDropdown;
        private PanelDropdown _depthDropdown;
        private PanelSlider _viewerCapSlider;

        private PanelSlider _nearClipSlider;
        private PanelSlider _farClipSlider;
        private PanelSlider _clipOffsetSlider;

        private readonly Dictionary<int, PanelToggle> _layerToggles = new Dictionary<int, PanelToggle>();
        private PanelElementDescriptor _layerGroup;

        private PanelToggle _cutoutToggle;
        private bool _cutoutUnavailable;
        private PanelDropdown _clearFlagsDropdown;
        private PanelElementDescriptor _clearColorGroup;
        private PanelImage _clearColorPreview;
        private PanelTextField _clearColorHex;
        private PanelSlider _clearColorRed;
        private PanelSlider _clearColorGreen;
        private PanelSlider _clearColorBlue;

        private PanelSlider _updateIntervalSlider;
        private PanelSlider _fullRateSlider;
        private PanelSlider _halfRateSlider;
        private PanelSlider _cullDistanceSlider;
        // private PanelToggle _postProcessingToggle;
        private PanelToggle _occlusionToggle;
        private PanelToggle _shadowsToggle;

        private BasisSDKMirror _activeMirror;
        private readonly List<BasisSDKMirror> _entries = new List<BasisSDKMirror>();

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            _quitting = false;
            _instance = new BasisMirrorPanelProvider();
            BasisMenuBase<BasisMainMenu>.AddProvider(_instance);

            BasisMirrorRegistry.OnChanged -= RefreshMainMenu;
            BasisMirrorRegistry.OnChanged += RefreshMainMenu;
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
        }

        private static void OnQuitting()
        {
            _quitting = true;
            BasisMirrorRegistry.OnChanged -= RefreshMainMenu;
        }

        private static void RefreshMainMenu()
        {
            if (_quitting) return;
            if (BasisMenuBase<BasisMainMenu>.Instance) BasisMenuBase<BasisMainMenu>.Instance.BindProvidersToButtons();
            if (BasisMainMenu.ActiveMenuTitle != StaticTitle || _instance == null) return;
            if (BasisMirrorRegistry.Count == 0)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }
            _instance.RebuildSelector();
        }

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            _panel = panel;

            panel.OnInstanceReleased += OnPanelClosed;

            _tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);
            _navColumn = _tabGroup.ExtrasContainer;
            _tabColumn = _navColumn.parent as RectTransform;
            _pageContents.Clear();

            _selector = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, PickerColumn);
            _selector.Descriptor.SetSize(new Vector2(60, 80));
            _selector.transform.SetSiblingIndex(0);
            FitToNavColumn(_selector.Descriptor, releaseControlSlot: false);
            _selector.OnValueChanged = _ => OnSelectionChanged();

            _emptyState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, PickerColumn);
            _emptyState.transform.SetSiblingIndex(1);
            _emptyState.SetTitle(BasisLocalization.Get("mirror.noMirrors"));
            _emptyState.SetDescription(BasisLocalization.Get("mirror.noMirrors.description"));
            FitToNavColumn(_emptyState, releaseControlSlot: true);

            _statusGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, _navColumn);
            _statusGroup.SetTitle(BasisLocalization.Get("mirror.status"));
            FitToNavColumn(_statusGroup, releaseControlSlot: true);

            AddTab("mirror.size", BuildSizeTab);
            AddTab("mirror.resolution", BuildResolutionTab);
            AddTab("mirror.clipping", BuildClippingTab);
            AddTab("mirror.layers", BuildLayersTab);
            AddTab("mirror.background", BuildBackgroundTab);
            AddTab("mirror.performance", BuildPerformanceTab);

            RebuildSelector();

            if (_lastTabIndex > 0 && _lastTabIndex < _tabGroup.SelectionButtons.Count &&
                _tabGroup.SelectionButtons[_lastTabIndex] != null &&
                _tabGroup.SelectionButtons[_lastTabIndex].gameObject.activeSelf)
            {
                _tabGroup.SelectionButtons[_lastTabIndex].OnClicked?.Invoke();
            }
        }

        private int AddTab(string tabKey, System.Action<RectTransform> build)
        {
            PanelTabPage page = PanelTabPage.CreateVertical(_tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = page.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Mirror);
            descriptor.SetTitle(BasisLocalization.Get(tabKey));

            ClampScrollViewport(descriptor.ContentParent);
            build(descriptor.ContentParent);
            _pageContents.Add(descriptor.ContentParent);

            int index = _tabGroup.Pages.Count;
            PanelScrollMemory.Attach(descriptor.ContentParent, "mirror/" + tabKey);
            _tabGroup.AddTab(BasisLocalization.Get(tabKey), () => _lastTabIndex = index, page);
            return index;
        }

        private static void ClampScrollViewport(RectTransform content)
        {
            if (content == null) return;

            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null) return;

            RectTransform viewport = scroll.viewport;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-25f, 0f);
            if (!viewport.TryGetComponent(out RectMask2D _))
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }
        }

        private static void FitToNavColumn(PanelElementDescriptor element, bool releaseControlSlot)
        {
            if (element == null) return;

            if (element.IconBackground != null) element.IconBackground.SetActive(false);
            if (!releaseControlSlot || element.Header == null) return;

            Transform slot = element.Header.Find("Title/Element");
            if (slot != null) slot.gameObject.SetActive(false);
        }

        private RectTransform PickerColumn => _tabColumn != null ? _tabColumn : _navColumn;

        private RectTransform ActivePageContent()
        {
            if (_tabGroup == null || _pageContents.Count == 0) return null;
            return _pageContents[Mathf.Clamp(_tabGroup.Value, 0, _pageContents.Count - 1)];
        }

        private void RebuildPage(PanelElementDescriptor group)
        {
            if (group == null) return;
            PanelElementDescriptor.RebuildLayoutChain(group.ContentParent, ActivePageContent());
        }

        private void RebuildNavColumn()
        {
            if (_navColumn == null) return;

            RectTransform from = _statusGroup != null && _statusGroup.Header != null
                ? _statusGroup.Header
                : _navColumn;
            PanelElementDescriptor.RebuildLayoutChain(from, PickerColumn);
        }

        private void OnPanelClosed()
        {
            _panel = null;
            _tabGroup = null;
            _navColumn = null;
            _tabColumn = null;
            _pageContents.Clear();
            _selector = null;
            _emptyState = null;
            _statusGroup = null;
            _presetDropdown = null;
            _widthSlider = null;
            _heightSlider = null;
            _placementGroup = null;
            _grabbableToggle = null;
            _moveWithPlayspaceToggle = null;
            _msaaDropdown = null;
            _depthDropdown = null;
            _viewerCapSlider = null;
            _nearClipSlider = null;
            _farClipSlider = null;
            _clipOffsetSlider = null;
            _layerToggles.Clear();
            _layerGroup = null;
            _cutoutToggle = null;
            _cutoutUnavailable = false;
            _clearFlagsDropdown = null;
            _clearColorGroup = null;
            _clearColorPreview = null;
            _clearColorHex = null;
            _clearColorRed = null;
            _clearColorGreen = null;
            _clearColorBlue = null;
            _updateIntervalSlider = null;
            _fullRateSlider = null;
            _halfRateSlider = null;
            _cullDistanceSlider = null;
            // _postProcessingToggle = null;
            _occlusionToggle = null;
            _shadowsToggle = null;
            _activeMirror = null;
            _entries.Clear();
        }

        public override void OnReleaseEvent() => OnPanelClosed();

        private void BuildSizeTab(RectTransform parent)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(BasisLocalization.Get("mirror.size"));
            group.SetDescription(BasisLocalization.Get("mirror.size.description"));
            RectTransform content = group.ContentParent;

            _widthSlider = PanelSlider.CreateNew(content);
            _widthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.width"),
                BasisSDKMirror.MinSurfaceSize, BasisSDKMirror.MaxSurfaceSize, false, 2, ValueDisplayMode.Meters));
            _widthSlider.SetResetDefault(1f);
            _widthSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.SurfaceWidth = v;
                Persist();
                RefreshSizeReadouts();
            };

            _heightSlider = PanelSlider.CreateNew(content);
            _heightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.height"),
                BasisSDKMirror.MinSurfaceSize, BasisSDKMirror.MaxSurfaceSize, false, 2, ValueDisplayMode.Meters));
            _heightSlider.SetResetDefault(1f);
            _heightSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.SurfaceHeight = v;
                Persist();
                RefreshSizeReadouts();
            };

            _placementGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            _placementGroup.SetTitle(BasisLocalization.Get("mirror.placement"));
            _placementGroup.SetDescription(BasisLocalization.Get("mirror.placement.description"));
            RectTransform placementContent = _placementGroup.ContentParent;

            _grabbableToggle = PanelToggle.CreateNewEntry(placementContent);
            _grabbableToggle.Descriptor.SetTitle(BasisLocalization.Get("mirror.grabbable"));
            _grabbableToggle.Descriptor.SetDescription(BasisLocalization.Get("mirror.grabbable.description"));
            _grabbableToggle.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                BasisMirrorSettingsStore.SetPersonalMirrorGrabbable(_activeMirror, v);
                Persist();
            };

            _moveWithPlayspaceToggle = PanelToggle.CreateNewEntry(placementContent);
            _moveWithPlayspaceToggle.Descriptor.SetTitle(BasisLocalization.Get("mirror.moveWithPlayspace"));
            _moveWithPlayspaceToggle.Descriptor.SetDescription(BasisLocalization.Get("mirror.moveWithPlayspace.description"));
            _moveWithPlayspaceToggle.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                BasisMirrorSettingsStore.SetPersonalMirrorMovesWithPlayspace(_activeMirror, v);
                Persist();
            };
        }

        private void BuildResolutionTab(RectTransform parent)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(BasisLocalization.Get("mirror.resolution"));
            group.SetDescription(BasisLocalization.Get("mirror.resolution.description"));
            RectTransform content = group.ContentParent;

            _presetDropdown = PanelDropdown.CreateNewEntry(content);
            _presetDropdown.Descriptor.SetTitle(BasisLocalization.Get("mirror.preset"));
            _presetDropdown.Descriptor.SetDescription(BasisLocalization.Get("mirror.preset.description"));
            _presetDropdown.AssignEntries(BuildPresetLabels());
            _presetDropdown.OnValueChanged = _ =>
            {
                if (_activeMirror == null || _presetDropdown == null) return;
                int index = _presetDropdown.Index;
                if (index < 0 || index >= ResolutionPresets.Length) return;

                int size = ResolutionPresets[index];
                _activeMirror.ReflectionWidth = size;
                _activeMirror.ReflectionHeight = size;
                Persist();
                ApplyActiveMirrorToControls();
            };

            _msaaDropdown = PanelDropdown.CreateNewEntry(content);
            _msaaDropdown.Descriptor.SetTitle(BasisLocalization.Get("mirror.msaa"));
            _msaaDropdown.Descriptor.SetDescription(BasisLocalization.Get("mirror.msaa.description"));
            _msaaDropdown.AssignEntries(BuildMsaaLabels());
            _msaaDropdown.OnValueChanged = _ =>
            {
                if (_activeMirror == null || _msaaDropdown == null) return;
                int index = _msaaDropdown.Index;
                if (index < 0 || index >= MsaaSampleCounts.Length) return;
                _activeMirror.MsaaSamples = MsaaSampleCounts[index];
                Persist();
                RefreshStatus();
            };

            _depthDropdown = PanelDropdown.CreateNewEntry(content);
            _depthDropdown.Descriptor.SetTitle(BasisLocalization.Get("mirror.depthBits"));
            _depthDropdown.Descriptor.SetDescription(BasisLocalization.Get("mirror.depthBits.description"));
            _depthDropdown.AssignEntries(BuildDepthLabels());
            _depthDropdown.OnValueChanged = _ =>
            {
                if (_activeMirror == null || _depthDropdown == null) return;
                int index = _depthDropdown.Index;
                if (index < 0 || index >= DepthBitOptions.Length) return;
                _activeMirror.DepthBits = DepthBitOptions[index];
                Persist();
            };

            _viewerCapSlider = PanelSlider.CreateNew(content);
            _viewerCapSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.secondaryViewerCap"),
                BasisSDKMirror.MinResolution, BasisSDKMirror.MaxResolution, true, 0, ValueDisplayMode.Raw));
            _viewerCapSlider.Descriptor.SetDescription(BasisLocalization.Get("mirror.secondaryViewerCap.description"));
            _viewerCapSlider.SetResetDefault(1024f);
            _viewerCapSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.SecondaryViewerResolutionCap = Mathf.RoundToInt(v);
                Persist();
            };
        }

        private void BuildClippingTab(RectTransform parent)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(BasisLocalization.Get("mirror.clipping"));
            group.SetDescription(BasisLocalization.Get("mirror.clipping.description"));
            RectTransform content = group.ContentParent;

            _nearClipSlider = PanelSlider.CreateNew(content);
            _nearClipSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.nearClip"),
                BasisSDKMirror.MinNearClip, BasisSDKMirror.MaxNearClip, false, 3, ValueDisplayMode.Meters));
            _nearClipSlider.SetResetDefault(0.01f);
            _nearClipSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.NearClip = v;
                Persist();
            };

            _farClipSlider = PanelSlider.CreateNew(content);
            _farClipSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.farClip"),
                BasisSDKMirror.MinFarClip, BasisSDKMirror.MaxFarClip, false, 1, ValueDisplayMode.Meters));
            _farClipSlider.SetResetDefault(25f);
            _farClipSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.FarClip = v;
                Persist();
            };

            _clipOffsetSlider = PanelSlider.CreateNew(content);
            _clipOffsetSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.clipPlaneOffset"),
                BasisSDKMirror.MinClipPlaneOffset, BasisSDKMirror.MaxClipPlaneOffset, false, 3, ValueDisplayMode.Meters));
            _clipOffsetSlider.Descriptor.SetDescription(BasisLocalization.Get("mirror.clipPlaneOffset.description"));
            _clipOffsetSlider.SetResetDefault(0.05f);
            _clipOffsetSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.SurfaceClipOffset = v;
                Persist();
            };
        }

        private void BuildLayersTab(RectTransform parent)
        {
            _layerGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            _layerGroup.SetTitle(BasisLocalization.Get("mirror.layers"));
            _layerGroup.SetDescription(BasisLocalization.Get("mirror.layers.description"));
            RectTransform content = _layerGroup.ContentParent;

            _layerToggles.Clear();
            for (int Layer = 0; Layer < 32; Layer++)
            {
                string layerName = LayerMask.LayerToName(Layer);
                if (string.IsNullOrEmpty(layerName)) continue;

                int captured = Layer;
                PanelToggle toggle = PanelToggle.CreateNewEntry(content);
                toggle.Descriptor.SetTitle(layerName);
                toggle.OnValueChanged = v =>
                {
                    if (_activeMirror == null) return;
                    int mask = _activeMirror.ReflectionLayers.value;
                    mask = v ? (mask | (1 << captured)) : (mask & ~(1 << captured));
                    _activeMirror.ReflectionLayers = mask;
                    Persist();
                };
                _layerToggles.Add(captured, toggle);
            }
        }

        private void BuildBackgroundTab(RectTransform parent)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(BasisLocalization.Get("mirror.background"));
            group.SetDescription(BasisLocalization.Get("mirror.background.description"));
            RectTransform content = group.ContentParent;

            _cutoutToggle = PanelToggle.CreateNewEntry(content);
            _cutoutToggle.Descriptor.SetTitle(BasisLocalization.Get("mirror.cutout"));
            _cutoutToggle.Descriptor.SetDescription(BasisLocalization.Get("mirror.cutout.description"));
            _cutoutToggle.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;

                bool applied = _activeMirror.SetCutout(v);
                _cutoutUnavailable = v && !applied;
                if (_cutoutUnavailable) _cutoutToggle?.SetValueWithoutNotify(false);

                Persist();
                ApplyClearControlsVisibility();
                RefreshClearColorControls();
                RefreshStatus();
            };

            _clearFlagsDropdown = PanelDropdown.CreateNewEntry(content);
            _clearFlagsDropdown.Descriptor.SetTitle(BasisLocalization.Get("mirror.clearFlags"));
            _clearFlagsDropdown.AssignLocalizedEntries(ClearFlagEntries(), ClearFlagKeys());
            _clearFlagsDropdown.OnValueChanged = _ =>
            {
                if (_activeMirror == null || _clearFlagsDropdown == null) return;
                int index = _clearFlagsDropdown.Index;
                if (index < 0) return;
                _activeMirror.ClearFlags = (BasisSDKMirror.MirrorClearFlags)index;
                Persist();
                ApplyClearControlsVisibility();
            };

            _clearColorGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            _clearColorGroup.SetTitle(BasisLocalization.Get("mirror.clearColor"));
            RectTransform colorContent = _clearColorGroup.ContentParent;

            _clearColorPreview = PanelImage.CreateNew(colorContent);
            _clearColorPreview.SetSize(new Vector2(200, 30));

            _clearColorHex = PanelTextField.CreateNewEntry(colorContent);
            _clearColorHex.Descriptor.SetTitle(BasisLocalization.Get("mirror.clearColor.hex"));
            _clearColorHex.OnValueChanged = hex =>
            {
                if (_activeMirror == null) return;
                if (!TryParseColor(hex, out Color parsed)) return;
                _activeMirror.ClearColor = parsed;
                Persist();
                RefreshClearColorControls();
            };

            _clearColorRed = CreateChannelSlider(colorContent, "mirror.clearColor.red", 0);
            _clearColorGreen = CreateChannelSlider(colorContent, "mirror.clearColor.green", 1);
            _clearColorBlue = CreateChannelSlider(colorContent, "mirror.clearColor.blue", 2);
        }

        private PanelSlider CreateChannelSlider(RectTransform parent, string titleKey, int channel)
        {
            PanelSlider slider = PanelSlider.CreateNew(parent);
            slider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get(titleKey), 0f, 255f, true, 0, ValueDisplayMode.Raw));
            slider.SetResetDefault(0f);
            slider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                Color color = _activeMirror.ConfiguredClearColor;
                color[channel] = Mathf.Clamp01(v / 255f);
                _activeMirror.ClearColor = color;
                Persist();
                RefreshClearColorControls();
            };
            return slider;
        }

        private void BuildPerformanceTab(RectTransform parent)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(BasisLocalization.Get("mirror.performance"));
            group.SetDescription(BasisLocalization.Get("mirror.performance.description"));
            RectTransform content = group.ContentParent;

            _updateIntervalSlider = PanelSlider.CreateNew(content);
            _updateIntervalSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.updateInterval"),
                BasisSDKMirror.MinUpdateInterval, BasisSDKMirror.MaxUpdateInterval, true, 0, ValueDisplayMode.Raw));
            _updateIntervalSlider.Descriptor.SetDescription(BasisLocalization.Get("mirror.updateInterval.description"));
            _updateIntervalSlider.SetResetDefault(1f);
            _updateIntervalSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.UpdateInterval = Mathf.RoundToInt(v);
                Persist();
            };

            _cullDistanceSlider = PanelSlider.CreateNew(content);
            _cullDistanceSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.cullDistance"),
                0f, BasisSDKMirror.MaxRateDistance, false, 1, ValueDisplayMode.Meters));
            _cullDistanceSlider.Descriptor.SetDescription(BasisLocalization.Get("mirror.cullDistance.description"));
            _cullDistanceSlider.SetResetDefault(25f);
            _cullDistanceSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.CullRange = v;
                Persist();
            };

            _fullRateSlider = PanelSlider.CreateNew(content);
            _fullRateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.fullRateDistance"),
                0f, BasisSDKMirror.MaxRateDistance, false, 1, ValueDisplayMode.Meters));
            _fullRateSlider.Descriptor.SetDescription(BasisLocalization.Get("mirror.fullRateDistance.description"));
            _fullRateSlider.SetResetDefault(4f);
            _fullRateSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.FullRateRange = v;
                Persist();
            };

            _halfRateSlider = PanelSlider.CreateNew(content);
            _halfRateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("mirror.halfRateDistance"),
                0f, BasisSDKMirror.MaxRateDistance, false, 1, ValueDisplayMode.Meters));
            _halfRateSlider.SetResetDefault(10f);
            _halfRateSlider.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.HalfRateRange = v;
                Persist();
            };

            // Post processing is deliberately not offered: the mirror camera running it stacks a
            // second pass on top of the one the player camera already applied.
            // _postProcessingToggle = PanelToggle.CreateNewEntry(content);
            // _postProcessingToggle.Descriptor.SetTitle(BasisLocalization.Get("mirror.postProcessing"));
            // _postProcessingToggle.OnValueChanged = v =>
            // {
            //     if (_activeMirror == null) return;
            //     _activeMirror.UsePostProcessing = v;
            //     Persist();
            // };

            _occlusionToggle = PanelToggle.CreateNewEntry(content);
            _occlusionToggle.Descriptor.SetTitle(BasisLocalization.Get("mirror.occlusionCulling"));
            _occlusionToggle.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.UseOcclusionCulling = v;
                Persist();
            };

            _shadowsToggle = PanelToggle.CreateNewEntry(content);
            _shadowsToggle.Descriptor.SetTitle(BasisLocalization.Get("mirror.shadows"));
            _shadowsToggle.OnValueChanged = v =>
            {
                if (_activeMirror == null) return;
                _activeMirror.RenderShadows = v;
                Persist();
            };

            RectTransform actions = PanelElementDescriptor.BuildActionRow(content, "MirrorActions");
            PanelButton reset = PanelButton.CreateNew(actions);
            reset.Descriptor.SetTitle(BasisLocalization.Get("mirror.reset"));
            reset.OnClicked += () =>
            {
                if (_activeMirror == null) return;
                BasisMirrorSettingsStore.ResetToDefaults(_activeMirror);
                ApplyActiveMirrorToControls();
            };
        }

        private void RebuildSelector()
        {
            if (_selector == null) return;

            _entries.Clear();
            List<string> labels = new List<string>();
            for (int Index = 0; Index < BasisMirrorRegistry.Mirrors.Count; Index++)
            {
                BasisSDKMirror mirror = BasisMirrorRegistry.Mirrors[Index];
                if (mirror == null) continue;
                _entries.Add(mirror);
                labels.Add($"{_entries.Count}. {mirror.DisplayName}");
            }

            _selector.AssignEntries(labels);

            if (_entries.Count == 0)
            {
                _selector.gameObject.SetActive(false);
                _emptyState?.SetActive(true);
                _statusGroup?.SetActive(false);
                _activeMirror = null;
                RebuildNavColumn();
                return;
            }

            _selector.gameObject.SetActive(true);
            _emptyState?.SetActive(false);
            _statusGroup?.SetActive(true);

            int index = _activeMirror != null ? _entries.IndexOf(_activeMirror) : 0;
            if (index < 0) index = 0;
            _activeMirror = _entries[index];
            _selector.SetValueWithoutNotify(labels[index]);

            ApplyActiveMirrorToControls();
            RebuildNavColumn();
        }

        private void OnSelectionChanged()
        {
            if (_selector == null) return;
            int index = _selector.Index;
            if (index < 0 || index >= _entries.Count) return;

            _activeMirror = _entries[index];
            ApplyActiveMirrorToControls();
        }

        private void ApplyActiveMirrorToControls()
        {
            if (_activeMirror == null) return;

            _cutoutUnavailable = false;

            if (_activeMirror.HasSurfaceSize)
            {
                Vector2 surface = _activeMirror.SurfaceSize;
                _widthSlider?.SetValueWithoutNotify(surface.x);
                _heightSlider?.SetValueWithoutNotify(surface.y);
            }

            bool personalMirror = BasisMirrorSettingsStore.IsPersonalMirror(_activeMirror);
            _placementGroup?.SetActive(personalMirror);
            if (personalMirror)
            {
                _grabbableToggle?.SetValueWithoutNotify(BasisMirrorSettingsStore.PersonalMirrorGrabbable(_activeMirror));
                _moveWithPlayspaceToggle?.SetValueWithoutNotify(BasisMirrorSettingsStore.PersonalMirrorMovesWithPlayspace(_activeMirror));
            }

            _viewerCapSlider?.SetValueWithoutNotify(_activeMirror.SecondaryViewerResolutionCap);
            string presetLabel = CurrentPresetLabel();
            if (presetLabel != null) _presetDropdown?.SetValueWithoutNotify(presetLabel);
            _msaaDropdown?.SetValueWithoutNotify(MsaaLabel(_activeMirror.MsaaSamples));
            _depthDropdown?.SetValueWithoutNotify(DepthLabel(_activeMirror.DepthBits));

            _nearClipSlider?.SetValueWithoutNotify(_activeMirror.NearClip);
            _farClipSlider?.SetValueWithoutNotify(_activeMirror.FarClip);
            _clipOffsetSlider?.SetValueWithoutNotify(_activeMirror.SurfaceClipOffset);

            int mask = _activeMirror.ReflectionLayers.value;
            foreach (KeyValuePair<int, PanelToggle> pair in _layerToggles)
            {
                pair.Value?.SetValueWithoutNotify((mask & (1 << pair.Key)) != 0);
            }

            _cutoutToggle?.SetValueWithoutNotify(_activeMirror.CutoutEnabled);
            _clearFlagsDropdown?.SetValueWithoutNotify(((int)_activeMirror.ConfiguredClearFlags).ToString());
            RefreshClearColorControls();
            ApplyClearControlsVisibility();

            _updateIntervalSlider?.SetValueWithoutNotify(_activeMirror.UpdateInterval);
            _fullRateSlider?.SetValueWithoutNotify(_activeMirror.FullRateRange);
            _halfRateSlider?.SetValueWithoutNotify(_activeMirror.HalfRateRange);
            _cullDistanceSlider?.SetValueWithoutNotify(_activeMirror.CullRange);

            // _postProcessingToggle?.SetValueWithoutNotify(_activeMirror.UsePostProcessing);
            _occlusionToggle?.SetValueWithoutNotify(_activeMirror.UseOcclusionCulling);
            _shadowsToggle?.SetValueWithoutNotify(_activeMirror.RenderShadows);

            RefreshStatus();
        }

        private void RefreshResolutionReadouts()
        {
            string preset = CurrentPresetLabel();
            if (preset != null) _presetDropdown?.SetValueWithoutNotify(preset);
            RefreshStatus();
        }

        /// <summary>
        /// Width and height are clamped and applied through the transform, so a rejected or adjusted
        /// value has to be reflected back rather than leaving the handle where the user dropped it.
        /// </summary>
        private void RefreshSizeReadouts()
        {
            if (_activeMirror == null || !_activeMirror.HasSurfaceSize) return;

            Vector2 surface = _activeMirror.SurfaceSize;
            _widthSlider?.SetValueWithoutNotify(surface.x);
            _heightSlider?.SetValueWithoutNotify(surface.y);
            RefreshStatus();
        }

        private void RefreshClearColorControls()
        {
            if (_activeMirror == null) return;

            Color color = _activeMirror.ConfiguredClearColor;
            if (_clearColorPreview != null && _clearColorPreview.Image != null)
            {
                _clearColorPreview.Image.color = color;
            }
            _clearColorRed?.SetValueWithoutNotify(Mathf.Round(Mathf.Clamp01(color.r) * 255f));
            _clearColorGreen?.SetValueWithoutNotify(Mathf.Round(Mathf.Clamp01(color.g) * 255f));
            _clearColorBlue?.SetValueWithoutNotify(Mathf.Round(Mathf.Clamp01(color.b) * 255f));
            _clearColorHex?.SetValueWithoutNotify(ColorUtility.ToHtmlStringRGBA(color));
        }

        /// <summary>
        /// The cutout owns the clear while it is on — it forces a fully transparent Solid Color —
        /// so the manual clear controls are taken away rather than left showing a value the mirror
        /// is not using.
        /// </summary>
        private void ApplyClearControlsVisibility()
        {
            if (_activeMirror == null) return;

            bool cutout = _activeMirror.CutoutEnabled;
            bool showColor = !cutout &&
                _activeMirror.ConfiguredClearFlags == BasisSDKMirror.MirrorClearFlags.Color;

            bool changed = false;
            if (_clearFlagsDropdown != null && _clearFlagsDropdown.gameObject.activeSelf == cutout)
            {
                _clearFlagsDropdown.gameObject.SetActive(!cutout);
                changed = true;
            }
            if (_clearColorGroup != null && _clearColorGroup.gameObject.activeSelf != showColor)
            {
                _clearColorGroup.SetActive(showColor);
                changed = true;
            }

            if (changed) RebuildPage(_clearColorGroup);
        }

        private void RefreshStatus()
        {
            if (_statusGroup == null || _activeMirror == null) return;

            Vector2Int effective = _activeMirror.EffectiveResolution;
            string body = string.Format(
                BasisLocalization.Get("mirror.status.effectiveResolution"), effective.x, effective.y);

            if (_activeMirror.HasSurfaceSize)
            {
                Vector2 surface = _activeMirror.SurfaceSize;
                body += "\n" + string.Format(
                    BasisLocalization.Get("mirror.status.surfaceSize"),
                    surface.x.ToString("0.##"), surface.y.ToString("0.##"));
            }

            if (BasisSDKMirror.ResolutionIsOverriddenGlobally)
            {
                body += "\n" + BasisLocalization.Get("mirror.status.globalOverride");
            }
            if (!BasisMirrorSettingsStore.IsPersisted(_activeMirror))
            {
                body += "\n" + BasisLocalization.Get("mirror.status.notPersisted");
            }
            if (_cutoutUnavailable)
            {
                body += "\n" + BasisLocalization.Get("mirror.cutout.unavailable");
            }

            _statusGroup.SetDescription(body);
            RebuildNavColumn();
        }

        private void Persist()
        {
            BasisMirrorSettingsStore.CaptureFrom(_activeMirror);
        }

        private static bool TryParseColor(string hex, out Color color)
        {
            if (string.IsNullOrEmpty(hex))
            {
                color = default;
                return false;
            }
            if (!hex.StartsWith("#")) hex = "#" + hex;
            return ColorUtility.TryParseHtmlString(hex, out color);
        }

        private static List<string> BuildPresetLabels()
        {
            List<string> labels = new List<string>(ResolutionPresets.Length);
            for (int Index = 0; Index < ResolutionPresets.Length; Index++)
            {
                labels.Add(PresetLabel(ResolutionPresets[Index]));
            }
            return labels;
        }

        private static string PresetLabel(int size) => $"{size} x {size}";

        private string CurrentPresetLabel()
        {
            if (_activeMirror == null) return null;

            int width = _activeMirror.ReflectionWidth;
            if (width != _activeMirror.ReflectionHeight) return null;

            for (int Index = 0; Index < ResolutionPresets.Length; Index++)
            {
                if (ResolutionPresets[Index] == width) return PresetLabel(width);
            }
            return null;
        }

        private static List<string> BuildMsaaLabels()
        {
            List<string> labels = new List<string>(MsaaSampleCounts.Length);
            for (int Index = 0; Index < MsaaSampleCounts.Length; Index++)
            {
                labels.Add(MsaaLabel(MsaaSampleCounts[Index]));
            }
            return labels;
        }

        private static string MsaaLabel(int samples)
        {
            return samples <= 1 ? BasisLocalization.Get("mirror.msaa.off") : $"{samples}x";
        }

        private static List<string> BuildDepthLabels()
        {
            List<string> labels = new List<string>(DepthBitOptions.Length);
            for (int Index = 0; Index < DepthBitOptions.Length; Index++)
            {
                labels.Add(DepthLabel(DepthBitOptions[Index]));
            }
            return labels;
        }

        private static string DepthLabel(int bits)
        {
            return bits >= 24 ? "24" : "16";
        }

        private static List<string> ClearFlagEntries()
        {
            return new List<string> { "0", "1", "2", "3", "4" };
        }

        private static List<string> ClearFlagKeys()
        {
            return new List<string>
            {
                "mirror.clearFlags.fromReferenceCamera",
                "mirror.clearFlags.skybox",
                "mirror.clearFlags.color",
                "mirror.clearFlags.depth",
                "mirror.clearFlags.nothing",
            };
        }
    }
}
