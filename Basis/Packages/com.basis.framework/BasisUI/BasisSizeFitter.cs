
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

    public class BasisSizeFitter : ContentSizeFitter
    {

        public bool UseParentAsPreferredSize = true;

        public override void SetLayoutHorizontal()
        {
            base.SetLayoutHorizontal();
            HandleSelfFittingAlongAxis(0);
        }

        public override void SetLayoutVertical()
        {
            base.SetLayoutVertical();
            HandleSelfFittingAlongAxis(1);
        }

        private void HandleSelfFittingAlongAxis(int axis)
        {
            if (!UseParentAsPreferredSize) return;

            RectTransform rectTransform = (RectTransform)transform;
            RectTransform rectParent = (RectTransform)transform.parent;

            FitMode fitting = (axis == 0 ? horizontalFit : verticalFit);

            if (fitting == FitMode.PreferredSize)
            {
                float parentWidth = rectParent.rect.width;
                rectTransform.SetSizeWithCurrentAnchors((RectTransform.Axis)axis,
                    Mathf.Max(LayoutUtility.GetPreferredSize(rectTransform, axis), parentWidth));
            }
        }
    }
}
