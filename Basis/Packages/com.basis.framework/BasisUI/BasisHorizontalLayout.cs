using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class BasisHorizontalLayout : HorizontalLayoutGroup
    {
        [ContextMenu("Set Alignment Left")]
        public void SetAlignmentLeft() => SetAlignment(TextAlignment.Left);
        [ContextMenu("Set Alignment Center")]
        public void SetAlignmentCenter() => SetAlignment(TextAlignment.Center);
        [ContextMenu("Set Alignment Right")]
        public void SetAlignmentRight() => SetAlignment(TextAlignment.Right);


        public void SetAlignment(TextAlignment alignment)
        {
#if UNITY_EDITOR
            Undo.RecordObjects(new UnityEngine.Object[] { this, rectTransform }, "Updated alignment of layout.");
#endif

            switch (alignment)
            {
                case TextAlignment.Left:
                    childAlignment = TextAnchor.MiddleLeft;
                    rectTransform.anchorMin = new Vector2(0, 0);
                    rectTransform.anchorMax = new Vector2(0, 1);
                    rectTransform.pivot = new Vector2(0, 0.5f);
                    break;
                case TextAlignment.Center:
                    childAlignment = TextAnchor.MiddleCenter;
                    rectTransform.anchorMin = new Vector2(0.5f, 0);
                    rectTransform.anchorMax = new Vector2(0.5f, 1);
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case TextAlignment.Right:
                    childAlignment = TextAnchor.MiddleRight;
                    rectTransform.anchorMin = new Vector2(1, 0);
                    rectTransform.anchorMax = new Vector2(1, 1);
                    rectTransform.pivot = new Vector2(1, 0.5f);
                    break;
            }
        }
    }
}
