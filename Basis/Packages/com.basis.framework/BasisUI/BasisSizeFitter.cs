
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
#endif

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Basis.BasisUI
{

#if UNITY_EDITOR
    [CustomEditor(typeof(BasisSizeFitter))]
    public class BasisSizeFitterEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            VisualElement root = new VisualElement();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            return root;
        }
    }
#endif

    /// <summary>
    /// ContentSizeFitter variant that can floor its fitted size at the parent rect's width
    /// (both axes compare against the parent WIDTH — long-standing behaviour other layouts
    /// depend on). Unlike the stock fitter it computes the final size first and writes it once,
    /// only when it actually changed: the stock write-then-correct pattern re-dirtied the canvas
    /// on every rebuild and looped through DelayedSetDirty whenever the parent was larger than
    /// the content.
    /// </summary>
    public class BasisSizeFitter : ContentSizeFitter
    {

        public bool UseParentAsPreferredSize = true;

        private DrivenRectTransformTracker _tracker;

        public override void SetLayoutHorizontal()
        {
            _tracker.Clear();
            FitSelf(0);
        }

        public override void SetLayoutVertical()
        {
            FitSelf(1);
        }

        protected override void OnDisable()
        {
            _tracker.Clear();
            base.OnDisable();
        }

        private void FitSelf(int axis)
        {
            RectTransform rect = (RectTransform)transform;
            FitMode fitting = axis == 0 ? horizontalFit : verticalFit;

            if (fitting == FitMode.Unconstrained)
            {
                _tracker.Add(this, rect, DrivenTransformProperties.None);
                return;
            }

            _tracker.Add(this, rect, axis == 0 ? DrivenTransformProperties.SizeDeltaX : DrivenTransformProperties.SizeDeltaY);

            float target = fitting == FitMode.MinSize
                ? LayoutUtility.GetMinSize(rect, axis)
                : LayoutUtility.GetPreferredSize(rect, axis);

            if (fitting == FitMode.PreferredSize && UseParentAsPreferredSize && transform.parent is RectTransform rectParent)
            {
                target = Mathf.Max(target, rectParent.rect.width);
            }

            float current = axis == 0 ? rect.rect.width : rect.rect.height;
            if (Mathf.Abs(current - target) > 0.01f)
            {
                rect.SetSizeWithCurrentAnchors((RectTransform.Axis)axis, target);
            }
        }
    }
}
