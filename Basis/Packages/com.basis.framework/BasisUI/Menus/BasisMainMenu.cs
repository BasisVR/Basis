using Basis.BasisUI.Styling;
using Basis.BTween;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class BasisMainMenu : BasisMenuBase<BasisMainMenu>
    {

        public const string MenuTitleKey = "menu.main.title";
        public static string MenuTitle => BasisLocalization.Get(MenuTitleKey);

        public static string ActiveMenuTitle
        {
            get
            {
                if (!Instance || !Instance.ActiveMenu)
                {
                    return string.Empty;
                }

                return Instance.ActiveMenu.Data.Title;
            }
        }

        public BasisMenuPanel HotbarMenu;
        public PanelElementDescriptor HorizontalLayout;

        private TextMeshProUGUI _tooltipLabel;
        private CanvasGroup _tooltipCanvasGroup;
        private Image _tooltipEdge;
        private Color _tooltipEdgeColor;
        private UiStyleImage _tooltipBackgroundStyle;
        private TweenCanvasGroupAlpha _tooltipTween;
        private const float TooltipFadeDuration = 0.15f;

        private RectTransform _tooltipProgressFill;
        private const float TooltipProgressHeight = 8f;

        private string _hoverTooltipText;
        private string _progressDisplay = string.Empty;
        private float _progressPercentage;
        private bool _progressActive;

        public override Component ProviderButtonParent => HorizontalLayout ? HorizontalLayout.ContentParent : null;

        public BasisMainMenu()
        {
            HotbarMenu = BasisMenuPanel.CreateNew(BasisMenuPanel.PanelData.Toolbar(MenuTitle), MenuObjectInstance.PanelRoot);
            HotbarMenu.SetLayer(BasisMenuPanel.PanelLayer.MainMenu);

            HorizontalLayout = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewHorizontal, HotbarMenu.Descriptor.ContentParent);
            if(HorizontalLayout.ContentParent.TryGetComponent(out BasisHorizontalLayout Layout))
            {
                Layout.spacing = 0;
            }
            else
            {
                BasisDebug.LogError("Unable to find Horizontal Spacing!");
            }
            BindProvidersToButtons();
            CreateTooltipArea();
            BasisUILoadingBar.OnDisplayChanged += OnLoadingProgressChanged;
            OnLoadingProgressChanged(BasisUILoadingBar.CurrentDisplay, BasisUILoadingBar.CurrentPercentage, BasisUILoadingBar.HasDisplay);
            AnimateMenuEntrance();
        }

        public override void Release()
        {
            BasisUILoadingBar.OnDisplayChanged -= OnLoadingProgressChanged;
            base.Release();
        }

        private void AnimateMenuEntrance()
        {
            // Fade in the hotbar panel
           // UIAnimations.FadeIn(HotbarMenu, 0.2f, 0f, Easing.OutCubic);

            // Stagger the hotbar buttons with fade + slide up. SlideIn captures each button's
            // CURRENT anchoredPosition as its destination, and the buttons were created this
            // frame — the layout group has not placed them yet, so without settling the layout
            // first every destination is the prefab default and the bar animates into a stack.
            if (ProviderButtons.Count > 0)
            {
                PanelElementDescriptor.RebuildLayoutChain(HorizontalLayout.ContentParent, HotbarMenu.Descriptor.rectTransform);
                RectTransform[] buttonTransforms = new RectTransform[ProviderButtons.Count];
                for (int i = 0; i < ProviderButtons.Count; i++)
                {
                    buttonTransforms[i] = ProviderButtons[i].rectTransform;
                }
                UIAnimations.StaggerEntrance(buttonTransforms, 0.04f, 0.2f, -15f);
            }
        }

        /// <summary>
        /// Builds the hover tooltip bar directly below the hotbar button bar. It stays hidden
        /// until a setting is hovered (see ShowTooltip), then fades in. Raise AreaHeight to grow it.
        /// </summary>
        private void CreateTooltipArea()
        {
            if (!HotbarMenu)
            {
                return;
            }

            // Panel-local units (panels run at localScale 1, so these are 1:1 with the bar).
            float barWidth = HotbarMenu.Data.PanelSize.x;
            const float AreaHeight = 50f;

            RectTransform barRect = (RectTransform)HotbarMenu.transform;

            // Source the live Edge / material / sprite / style off the hotbar's own panel so the
            // section matches the active theme instead of hard-coding asset references.
            Transform sourceEdge = HotbarMenu.transform.Find("Edge");
            UiStyleImage sourceStyle = HotbarMenu.GetComponentInChildren<UiStyleImage>(true);
            BasisImageBackground sourceBackgroundImage = HotbarMenu.GetComponentInChildren<BasisImageBackground>(true);

            // Container, parented to the bar so it shares the bar's canvas and is released with the
            // menu. Sits below the bar, never over the buttons.
            GameObject areaObject = new GameObject("Tooltip Area", typeof(RectTransform));
            areaObject.layer = HotbarMenu.gameObject.layer;

            RectTransform areaRect = (RectTransform)areaObject.transform;
            areaRect.SetParent(barRect, false);
            areaRect.localScale = Vector3.one;
            areaRect.localRotation = Quaternion.identity;
            areaRect.anchorMin = new Vector2(0.5f, 0.5f);
            areaRect.anchorMax = new Vector2(0.5f, 0.5f);
            areaRect.pivot = new Vector2(0.5f, 1f); // top-center: grows downward from the anchored position
            areaRect.sizeDelta = new Vector2(barWidth, AreaHeight);
            areaRect.anchoredPosition = new Vector2(0f, -120f);

            // Fades in/out as a hover tooltip; starts hidden and never intercepts input.
            _tooltipCanvasGroup = areaObject.AddComponent<CanvasGroup>();
            _tooltipCanvasGroup.alpha = 0f;
            _tooltipCanvasGroup.interactable = false;
            _tooltipCanvasGroup.blocksRaycasts = false;

            // Edge: faint Circle 512 border that peeks out behind the panel, exactly like the Main Menu.
            if (sourceEdge && sourceEdge.TryGetComponent(out Image sourceEdgeImage))
            {
                GameObject edgeObject = new GameObject("Edge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                edgeObject.layer = areaObject.layer;

                RectTransform edgeRect = (RectTransform)edgeObject.transform;
                edgeRect.SetParent(areaRect, false);
                edgeRect.localScale = Vector3.one;
                edgeRect.localRotation = Quaternion.identity;
                edgeRect.anchorMin = Vector2.zero;
                edgeRect.anchorMax = Vector2.one;
                edgeRect.pivot = new Vector2(0.5f, 0.5f);
                edgeRect.sizeDelta = ((RectTransform)sourceEdge).sizeDelta; // bleeds a few px past the panel
                edgeRect.anchoredPosition = Vector2.zero;

                Image edge = edgeObject.GetComponent<Image>();
                edge.material = sourceEdgeImage.material;
                edge.sprite = sourceEdgeImage.sprite;
                edge.color = sourceEdgeImage.color;
                edge.type = sourceEdgeImage.type;
                edge.fillCenter = sourceEdgeImage.fillCenter;
                edge.pixelsPerUnitMultiplier = sourceEdgeImage.pixelsPerUnitMultiplier;
                edge.raycastTarget = false;
                _tooltipEdge = edge;
                _tooltipEdgeColor = edge.color;
            }

            // Background: solid themed panel, drawn over the Edge so the Edge rims it. Uses the menu
            // Background's image settings + material plus a Ui Style Image for theme colouring.
            GameObject backgroundObject = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            backgroundObject.layer = areaObject.layer;

            RectTransform backgroundRect = (RectTransform)backgroundObject.transform;
            backgroundRect.SetParent(areaRect, false);
            backgroundRect.localScale = Vector3.one;
            backgroundRect.localRotation = Quaternion.identity;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            Image background = backgroundObject.GetComponent<Image>();
            background.raycastTarget = false;
            if (sourceStyle && sourceStyle.Image)
            {
                Image source = sourceStyle.Image;
                background.material = source.material;
                background.sprite = source.sprite;
                background.color = source.color;
                background.type = source.type;
                background.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
            }

            // Ui Style Image keeps the panel coloured by the active theme.
            UiStyleImage backgroundStyle = backgroundObject.AddComponent<UiStyleImage>();
            backgroundStyle.SetStyle(sourceStyle ? sourceStyle.ColorStyle : "Background");
            _tooltipBackgroundStyle = backgroundStyle;

            // Mask children (overlay + text) to the rounded panel shape, like the menu Background.
            Mask backgroundMask = backgroundObject.AddComponent<Mask>();
            backgroundMask.showMaskGraphic = true;

            // "Background Image" overlay (BasisImageBackground), layered behind the text.
            if (sourceBackgroundImage)
            {
                GameObject backgroundImageObject = new GameObject("Background Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(BasisImageBackground));
                backgroundImageObject.layer = areaObject.layer;

                RectTransform backgroundImageRect = (RectTransform)backgroundImageObject.transform;
                backgroundImageRect.SetParent(backgroundRect, false);
                backgroundImageRect.localScale = Vector3.one;
                backgroundImageRect.localRotation = Quaternion.identity;
                backgroundImageRect.anchorMin = Vector2.zero;
                backgroundImageRect.anchorMax = Vector2.one;
                backgroundImageRect.offsetMin = Vector2.zero;
                backgroundImageRect.offsetMax = Vector2.zero;

                BasisImageBackground backgroundImage = backgroundImageObject.GetComponent<BasisImageBackground>();
                backgroundImage.material = sourceBackgroundImage.material;
                backgroundImage.sprite = sourceBackgroundImage.sprite;
                backgroundImage.color = sourceBackgroundImage.color;
                backgroundImage.type = sourceBackgroundImage.type;
                backgroundImage.preserveAspect = sourceBackgroundImage.preserveAspect;
                backgroundImage.raycastTarget = false;
            }

            // Loading progress fill, hidden unless the loading bar is reporting into this area.
            // Created before Content so the label always draws over it.
            GameObject progressObject = new GameObject("Progress Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            progressObject.layer = areaObject.layer;

            RectTransform progressRect = (RectTransform)progressObject.transform;
            progressRect.SetParent(backgroundRect, false);
            progressRect.localScale = Vector3.one;
            progressRect.localRotation = Quaternion.identity;
            progressRect.anchorMin = Vector2.zero;
            progressRect.anchorMax = Vector2.zero;
            progressRect.pivot = Vector2.zero;
            progressRect.sizeDelta = new Vector2(0f, TooltipProgressHeight);
            progressRect.anchoredPosition = Vector2.zero;

            Image progressImage = progressObject.GetComponent<Image>();
            progressImage.raycastTarget = false;
            progressObject.AddComponent<UiStyleImage>().SetStyle("Menu Element");

            _tooltipProgressFill = progressRect;
            progressObject.SetActive(false);

            // Content holder inside the masked Background, padded like the menu panels.
            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.layer = areaObject.layer;

            RectTransform contentRect = (RectTransform)contentObject.transform;
            contentRect.SetParent(backgroundRect, false);
            contentRect.localScale = Vector3.one;
            contentRect.localRotation = Quaternion.identity;
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.sizeDelta = new Vector2(-32f, -32f);
            contentRect.anchoredPosition = Vector2.zero;

            // Centered tooltip label inside Content, masked to the rounded panel like menu content.
            GameObject labelObject = new GameObject("Tooltip Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelObject.layer = areaObject.layer;

            RectTransform labelRect = (RectTransform)labelObject.transform;
            labelRect.SetParent(contentRect, false);
            labelRect.localScale = Vector3.one;
            labelRect.localRotation = Quaternion.identity;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            _tooltipLabel = labelObject.GetComponent<TextMeshProUGUI>();
            _tooltipLabel.text = string.Empty;
            _tooltipLabel.color = Color.white;
            _tooltipLabel.alignment = TextAlignmentOptions.Center;
            _tooltipLabel.enableAutoSizing = false;
            _tooltipLabel.fontSize = 18f;
            _tooltipLabel.raycastTarget = false;

            // Start fully hidden (inactive) so the masked background never lingers as white.
            areaObject.SetActive(false);
        }

        /// <summary>
        /// Show the hover tooltip bar with the given text, fading it in. Empty text fades it out.
        /// A hovered tooltip always outranks the loading progress that otherwise occupies the bar.
        /// </summary>
        public static void ShowTooltip(string text)
        {
            if (!Instance) return;
            BasisMainMenu menu = (BasisMainMenu)Instance;
            menu._hoverTooltipText = string.IsNullOrEmpty(text) ? null : text;
            menu.RefreshTooltip();
        }

        /// <summary>
        /// Clear the hovered tooltip. The bar falls back to loading progress if something is
        /// still loading, otherwise it fades back out.
        /// </summary>
        public static void HideTooltip()
        {
            if (!Instance) return;
            BasisMainMenu menu = (BasisMainMenu)Instance;
            menu._hoverTooltipText = null;
            menu.RefreshTooltip();
        }

        private void OnLoadingProgressChanged(string display, float percentage, bool active)
        {
            _progressDisplay = display;
            _progressPercentage = percentage;
            _progressActive = active;
            RefreshTooltip();
        }

        private void RefreshTooltip()
        {
            if (!string.IsNullOrEmpty(_hoverTooltipText))
            {
                ApplyTooltip(_hoverTooltipText, -1f);
            }
            else if (_progressActive)
            {
                ApplyTooltip(BasisUILoadingBar.FormatDisplay(_progressPercentage, _progressDisplay), _progressPercentage);
            }
            else
            {
                FadeTooltipOut();
            }
        }

        private void ApplyTooltip(string text, float progressPercentage)
        {
            if (_tooltipLabel == null || _tooltipCanvasGroup == null)
            {
                return;
            }

            KillTooltipTween();
            _tooltipCanvasGroup.gameObject.SetActive(true);
            if (_tooltipEdge != null) _tooltipEdge.color = _tooltipEdgeColor;
            if (_tooltipBackgroundStyle != null) _tooltipBackgroundStyle.ApplyActiveStyle();
            _tooltipLabel.enabled = true;
            _tooltipLabel.text = text;
            SetTooltipProgressFill(progressPercentage);
            _tooltipTween = _tooltipCanvasGroup
                .TweenAlpha(TooltipFadeDuration, _tooltipCanvasGroup.alpha, 1f)
                .SetEase(Easing.OutCubic);
        }

        private void SetTooltipProgressFill(float progressPercentage)
        {
            if (_tooltipProgressFill == null)
            {
                return;
            }

            if (progressPercentage < 0f)
            {
                _tooltipProgressFill.gameObject.SetActive(false);
                return;
            }

            _tooltipProgressFill.gameObject.SetActive(true);
            _tooltipProgressFill.anchorMax = new Vector2(Mathf.Clamp01(progressPercentage / 100f), 0f);
        }

        private void FadeTooltipOut()
        {
            if (_tooltipCanvasGroup == null || !_tooltipCanvasGroup.gameObject.activeSelf)
            {
                return;
            }

            KillTooltipTween();
            SetTooltipProgressFill(-1f);
            if (_tooltipEdge != null) _tooltipEdge.color = new Color(0f, 0f, 0f, _tooltipEdgeColor.a);
            if (_tooltipBackgroundStyle != null && _tooltipBackgroundStyle.Image != null)
            {
                _tooltipBackgroundStyle.Image.color = Color.black;
            }
            if (_tooltipLabel != null) _tooltipLabel.enabled = false;
            CanvasGroup tooltipGroup = _tooltipCanvasGroup;
            _tooltipTween = tooltipGroup
                .TweenAlpha(TooltipFadeDuration, tooltipGroup.alpha, 0f)
                .SetEase(Easing.OutCubic)
                .AddCallback(() =>
                {
                    // Once fully faded out, deactivate so the masked background doesn't linger.
                    tooltipGroup.gameObject.SetActive(false);
                });
        }

        /// <summary>
        /// Stops the current tooltip fade (and its pending deactivate callback) so a new fade
        /// can take over cleanly — e.g. re-showing while a fade-out is still mid-flight.
        /// </summary>
        private void KillTooltipTween()
        {
            if (_tooltipTween != null && _tooltipTween.Active && _tooltipTween.Target == _tooltipCanvasGroup)
            {
                _tooltipTween.Reset();
            }
            _tooltipTween = null;
        }

        public static void Open()
        {
            BasisUILoadingBar.SetHudSuppressed(true);
            BasisUIManagement.CloseAllMenus();

            if (Instance)
            {
                // Re-opening rebuilds the menu, taking the active page and the virtual
                // keyboard with it. Snapshot them so an unsolicited prompt that forced the
                // menu open can put them back when it closes.
                BasisMenuPromptRestore.Capture();
                Instance.Release();
            }

            Instance = new BasisMainMenu();
            BasisMenuPromptRestore.SetOwner(Instance.MenuObjectInstance);
            BasisCursorManagement.UnlockCursor(nameof(BasisMainMenu));
            SetMicrophoneIconHudVisible(false);
            BasisMenuStateMemory.WasOpen = true;
        }
        public static void OpenWithProvider(string ProviderTitle) => OpenWithProvider(FindProvider(ProviderTitle));

        public static void OpenWithProvider(BasisMenuActionProvider<BasisMainMenu> provider)
        {
            Open();
            // Landing on a named provider is deliberate navigation, not a prompt displacing a
            // page, so there is nothing to put back. Dropping the snapshot Open just took stops
            // the next in-menu confirmation from being restored onto the page we came from.
            BasisMenuPromptRestore.Clear();
            provider?.RunAction();
        }

        public static BasisMenuActionProvider<BasisMainMenu> FindProvider(string title)
        {
            int count = Providers.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = Providers[Index];
                if (provider != null && provider.Title == title)
                {
                    return provider;
                }
            }
            return null;
        }

        public static BasisMenuActionProvider<BasisMainMenu> ActivePanelProvider
        {
            get
            {
                if (!Instance || !Instance.ActiveMenu)
                {
                    return null;
                }
                return Instance.ActiveProvider ?? FindProvider(Instance.ActiveMenu.Data.Title);
            }
        }

        public static void Toggle()
        {
            if (Instance)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public static void Close()
        {
            if (!Instance)
            {
                return;
            }

            // Closing outright is the user's own decision — there is nothing left to put back.
            BasisMenuPromptRestore.Clear();
            Instance.Release();
            Instance = null;
            BasisCursorManagement.LockCursor(nameof(BasisMainMenu));
            BasisUILoadingBar.SetHudSuppressed(false);
            SetMicrophoneIconHudVisible(true);
            BasisMenuStateMemory.WasOpen = false;
        }

        private static void SetMicrophoneIconHudVisible(bool visible)
        {
#if !BASIS_DISABLE_MICROPHONE
            if (BasisLocalCameraDriver.Instance != null)
            {
                BasisLocalCameraDriver.Instance.microphoneIconDriver.HardEnableVisuals(visible);
            }
#endif
        }

        public static BasisMenuPanel CreateActiveMenu(BasisMenuPanel.PanelData data, string style, BasisMenuActionProvider<BasisMainMenu> provider = null)
        {
            if (Instance.Dialogue)
            {
                Instance.Dialogue.ReleaseInstance();
            }
            if (Instance.ActiveMenu)
            {
                if (Instance.ActiveMenu.Data.Title == data.Title)
                {
                    return Instance.ActiveMenu;
                }
                else
                {
                    // Notify the previous provider that its panel is being released
                    if (Instance.ActiveProvider != null)
                    {
                        Instance.ActiveProvider.OnReleaseEvent();
                    }

                    Instance.ActiveMenu.ReleaseInstance();
                }
            }

            Instance.ActiveMenu = BasisMenuPanel.CreateNew(data, Instance.MenuObjectInstance.PanelRoot, style);
            Instance.ActiveProvider = provider;
            BasisMenuStateMemory.ActiveProviderTitle = data.Title;

            // Every provider panel gets the move handle, which also restores where the user last
            // dragged this one to.
            BasisPanelMoveHandle.Attach(Instance.ActiveMenu, ResolvePanelOffsetKey(data.Title, provider));

            // Animate content panel entrance
            UIAnimations.PanelIn(Instance.ActiveMenu);

            return Instance.ActiveMenu;
        }

        /// <summary>
        /// Stable per-provider key for remembered panel placement. Most providers do not hand
        /// themselves to <see cref="CreateActiveMenu"/>, so the one whose title built this panel is
        /// looked up instead — its type name survives a language change, which the localized title
        /// on <see cref="BasisMenuPanel.PanelData"/> would not.
        /// </summary>
        private static string ResolvePanelOffsetKey(string title, BasisMenuActionProvider<BasisMainMenu> provider)
        {
            provider ??= FindProvider(title);
            return provider != null ? provider.GetType().Name : title;
        }

        public static void CloseActivePanel()
        {
            if (!Instance || !Instance.ActiveMenu)
            {
                return;
            }

            if (Instance.ActiveProvider != null)
            {
                Instance.ActiveProvider.OnReleaseEvent();
            }

            Instance.ActiveMenu.ReleaseInstance();
            Instance.ActiveMenu = null;
            Instance.ActiveProvider = null;
            BasisMenuStateMemory.ActiveProviderTitle = string.Empty;
        }
    }
}
