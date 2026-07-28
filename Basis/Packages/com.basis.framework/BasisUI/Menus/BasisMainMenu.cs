using Basis.BasisUI.Styling;
using Basis.BTween;
using Basis.Scripts.Drivers;
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

        public override Component ProviderButtonParent => HorizontalLayout ? HorizontalLayout.ContentParent : null;

        public BasisMainMenu()
        {
            HotbarMenu = BasisMenuPanel.CreateNew(BasisMenuPanel.PanelData.Toolbar(MenuTitle), MenuObjectInstance.PanelRoot);

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
            AnimateMenuEntrance();
        }

        private void AnimateMenuEntrance()
        {
            // Fade in the hotbar panel
           // UIAnimations.FadeIn(HotbarMenu, 0.2f, 0f, Easing.OutCubic);

            // Stagger the hotbar buttons with fade + slide up
            if (ProviderButtons.Count > 0)
            {
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
        /// </summary>
        public static void ShowTooltip(string text)
        {
            if (!Instance) return;
            BasisMainMenu menu = (BasisMainMenu)Instance;
            if (menu._tooltipLabel == null || menu._tooltipCanvasGroup == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                HideTooltip();
                return;
            }

            menu.KillTooltipTween();
            menu._tooltipCanvasGroup.gameObject.SetActive(true);
            if (menu._tooltipEdge != null) menu._tooltipEdge.color = menu._tooltipEdgeColor;
            if (menu._tooltipBackgroundStyle != null) menu._tooltipBackgroundStyle.ApplyActiveStyle();
            menu._tooltipLabel.enabled = true;
            menu._tooltipLabel.text = text;
            menu._tooltipTween = menu._tooltipCanvasGroup
                .TweenAlpha(TooltipFadeDuration, menu._tooltipCanvasGroup.alpha, 1f)
                .SetEase(Easing.OutCubic);
        }

        /// <summary>
        /// Fade the hover tooltip bar back out.
        /// </summary>
        public static void HideTooltip()
        {
            if (!Instance) return;
            BasisMainMenu menu = (BasisMainMenu)Instance;
            if (menu._tooltipCanvasGroup == null)
            {
                return;
            }

            menu.KillTooltipTween();
            if (menu._tooltipEdge != null) menu._tooltipEdge.color = new Color(0f, 0f, 0f, menu._tooltipEdgeColor.a);
            if (menu._tooltipBackgroundStyle != null && menu._tooltipBackgroundStyle.Image != null)
            {
                menu._tooltipBackgroundStyle.Image.color = Color.black;
            }
            if (menu._tooltipLabel != null) menu._tooltipLabel.enabled = false;
            CanvasGroup tooltipGroup = menu._tooltipCanvasGroup;
            menu._tooltipTween = tooltipGroup
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
            BasisUIManagement.CloseAllMenus();

            if (Instance)
            {
                Instance.Release();
            }

            Instance = new BasisMainMenu();
            BasisCursorManagement.UnlockCursor(nameof(BasisMainMenu));
            SetMicrophoneIconHudVisible(false);
            BasisMenuStateMemory.WasOpen = true;
        }
        public static void OpenWithProvider(string ProviderTitle)
        {
            Open();
            int count = BasisMainMenu.Providers.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = BasisMainMenu.Providers[Index];
                if (provider.Title == ProviderTitle)
                {
                    provider.RunAction();
                    return;
                }
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

            Instance.Release();
            Instance = null;
            BasisCursorManagement.LockCursor(nameof(BasisMainMenu));
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

            // Animate content panel entrance
            UIAnimations.PanelIn(Instance.ActiveMenu);

            return Instance.ActiveMenu;
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
