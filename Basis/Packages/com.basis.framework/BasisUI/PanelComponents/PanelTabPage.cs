using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTabPage : PanelComponent
    {
        private const float FadeDuration = 0.15f;

        public static class TabPageStyles
        {
            public static string Default =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Tab Page.prefab";
        }

        private CanvasGroup _canvasGroup;
        private TweenCanvasGroupAlpha _fadeTween;

        public static PanelTabPage CreateNew(Component parent) =>
            CreateNew<PanelTabPage>(TabPageStyles.Default, parent);

        public static PanelTabPage CreateNew(string style, Component parent) =>
            CreateNew<PanelTabPage>(style, parent);

        /// <summary>
        /// Create a TabPage with a vertical layout predefined for the content parent.
        /// </summary>
        public static PanelTabPage CreateVertical(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }

        public static PanelTabPage CreateVerticalAlternate(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVerticalLibrary, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }

        /// <summary>
        /// Create a TabPage with a Horizontal layout predefined for the content parent.
        /// </summary>
        public static PanelTabPage CreateHorizontal(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewHorizontal, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }
        /// <summary>
        /// Create a TabPage with a Grid layout predefined for the content parent.
        /// </summary>
        public static PanelTabPage CreateGrid(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewGrid, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }

        private CanvasGroup GetCanvasGroup()
        {
            if (!_canvasGroup)
            {
                if (!TryGetComponent(out _canvasGroup))
                {
                    _canvasGroup = gameObject.AddComponent<CanvasGroup>();
                }
            }
            return _canvasGroup;
        }

        public void ShowPage(bool value)
        {
            if (!Application.isPlaying)
            {
                gameObject.SetActive(value);
                return;
            }

            CanvasGroup cg = GetCanvasGroup();

            // Cancel any active tween without completing it
            if (_fadeTween != null && _fadeTween.Active) _fadeTween.Reset();

            if (value)
            {
                gameObject.SetActive(true);
                cg.alpha = 0f;
                cg.interactable = true;
                cg.blocksRaycasts = true;
                _fadeTween = cg.TweenAlpha(FadeDuration, 0f, 1f).SetEase(Easing.OutCubic);
            }
            else
            {
                cg.interactable = false;
                cg.blocksRaycasts = false;
                _fadeTween = cg.TweenAlpha(FadeDuration, cg.alpha, 0f)
                    .SetEase(Easing.OutCubic)
                    .AddCallback(() =>
                    {
                        if (this != null) gameObject.SetActive(false);
                    });
            }
        }
    }
}
