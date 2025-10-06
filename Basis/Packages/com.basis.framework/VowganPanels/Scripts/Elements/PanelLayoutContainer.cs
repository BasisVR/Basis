using System;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI
{
    public class PanelLayoutContainer : PanelElement
    {
        public static class Styles
        {
            public static string Vertical => "VowganUI/Elements/LayoutContainerVertical";
            public static string Horizontal => "VowganUI/Elements/LayoutContainerHorizontal";
        }

        public HorizontalOrVerticalLayoutGroup LayoutGroup;
        public ContentSizeFitter ContentFitter;

        /// <summary>
        /// Any changes to this will only be applied by calling ApplyLayoutOptions();
        /// </summary>
        public LayoutContainerOptions ChildLayout;

        /// <summary>
        /// Direction is handled via Horizontal/Vertical Layout Groups and cannot be changed at runtime.
        /// </summary>
        public LayoutDirection Direction => _direction;
        protected LayoutDirection _direction;

        public static PanelLayoutContainer CreateNew(Component parent, LayoutDirection direction)
        {
            PanelLayoutContainer element;
            switch (direction)
            {
                case LayoutDirection.Vertical:
                    element = CreateNew<PanelLayoutContainer>(Styles.Vertical, parent);
                    element._direction = LayoutDirection.Vertical;
                    return element;

                case LayoutDirection.Horizontal:
                    element = CreateNew<PanelLayoutContainer>(Styles.Horizontal, parent);
                    element._direction = LayoutDirection.Horizontal;
                    return element;

                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ApplyLayoutOptions();
        }

        public void CopyLayoutOptions(LayoutContainerOptions options)
        {
            ChildLayout = options;
            ApplyLayoutOptions();
        }

        public void ApplyLayoutOptions()
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;

            if (ChildLayout.Constrained)
            {
                switch (Direction)
                {
                    case LayoutDirection.Vertical:
                        ContentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                        ContentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                        break;
                    case LayoutDirection.Horizontal:
                        ContentFitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                        ContentFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            LayoutGroup.childAlignment = ChildLayout.Alignment;
            LayoutGroup.childControlWidth = ChildLayout.StretchItemWidth;
            LayoutGroup.childControlHeight = ChildLayout.StretchItemHeight;
            LayoutGroup.childForceExpandWidth = ChildLayout.SpreadItemWidth;
            LayoutGroup.childForceExpandHeight = ChildLayout.SpreadItemHeight;
        }

        protected override void OnValidate()
        {
            ApplyLayoutOptions();
        }
    }
}
