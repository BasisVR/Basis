using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public sealed class BasisWorldPanelBackdrop : RawImage
    {
        protected override void OnEnable()
        {
            base.OnEnable();
            FitTexture();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            FitTexture();
        }

        public void FitTexture()
        {
            Texture current = texture;
            Rect rect = rectTransform.rect;
            if (current == null || rect.width <= 0f || rect.height <= 0f || current.width <= 0 || current.height <= 0) return;
            float rectAspect = rect.width / rect.height;
            float textureAspect = (float)current.width / current.height;
            Rect fitted = rectAspect >= textureAspect
                ? new Rect(0f, 0.5f - 0.5f * textureAspect / rectAspect, 1f, textureAspect / rectAspect)
                : new Rect(0.5f - 0.5f * rectAspect / textureAspect, 0f, rectAspect / textureAspect, 1f);
            if (uvRect != fitted) uvRect = fitted;
        }
    }
}
