using System;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Basis.BasisUI
{
    [Obsolete]
    public class PanelLayoutContainer : PanelElementDescriptor
    {
        public static class LayoutStyles
        {
            public static string Vertical => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Layout Container Vertical.prefab";
            public static string Horizontal => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Layout Container Horizontal.prefab";
        }

        public HorizontalOrVerticalLayoutGroup LayoutGroup;
        public ContentSizeFitter ContentFitter;

        /// <summary>
        /// Layout options must be applied immediately after through ApplyLayoutOptions().
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
                    element = CreateNew<PanelLayoutContainer>(LayoutStyles.Vertical, parent);
                    element._direction = LayoutDirection.Vertical;
                    return element;

                case LayoutDirection.Horizontal:
                    element = CreateNew<PanelLayoutContainer>(LayoutStyles.Horizontal, parent);
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
