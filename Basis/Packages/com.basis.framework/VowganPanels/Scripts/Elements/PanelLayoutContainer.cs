using System;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

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
        public LayoutContainerOptions ChildLayoutOptions;

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
            ChildLayoutOptions = options;
            ApplyLayoutOptions();
        }

        public void ApplyLayoutOptions()
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;

            if (ChildLayoutOptions.Constrained)
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

            LayoutGroup.childAlignment = ChildLayoutOptions.Alignment;
            LayoutGroup.childControlWidth = ChildLayoutOptions.StretchItemWidth;
            LayoutGroup.childControlHeight = ChildLayoutOptions.StretchItemHeight;
            LayoutGroup.childForceExpandWidth = ChildLayoutOptions.SpreadItemWidth;
            LayoutGroup.childForceExpandHeight = ChildLayoutOptions.SpreadItemHeight;
        }


#if UNITY_EDITOR
        protected override void OnValidate()
        {
            EditorApplication.delayCall += EditorApplyLayoutOptions;
        }

        private void EditorApplyLayoutOptions()
        {
            if (!this || !gameObject) return;
            if (PrefabUtility.IsPartOfPrefabAsset(gameObject)) return;

            ApplyLayoutOptions();
            if (rectTransform) LayoutRebuilder.MarkLayoutForRebuild(rectTransform);

            EditorUtility.SetDirty(this);
            if (rectTransform) EditorUtility.SetDirty(rectTransform);
            if (LayoutGroup)   EditorUtility.SetDirty(LayoutGroup);
            if (ContentFitter) EditorUtility.SetDirty(ContentFitter);
        }
#endif
    }
}
