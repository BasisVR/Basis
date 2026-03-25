using UnityEngine;

namespace Basis.BTween
{
    public static class TweenCanvasGroupAlphaExtensions
    {
        public static TweenCanvasGroupAlpha TweenAlpha(
            this CanvasGroup canvasGroup,
            float duration,
            float endAlpha)
        {
            TweenCanvasGroupAlpha tween = TweenCanvasGroupAlpha.GetAvailableTween()
                .SetTarget(canvasGroup)
                .Start(duration, canvasGroup.alpha, endAlpha);
            return tween;
        }

        public static TweenCanvasGroupAlpha TweenAlpha(
            this CanvasGroup canvasGroup,
            float duration,
            float startAlpha,
            float endAlpha)
        {
            TweenCanvasGroupAlpha tween = TweenCanvasGroupAlpha.GetAvailableTween()
                .SetTarget(canvasGroup)
                .Start(duration, startAlpha, endAlpha);
            return tween;
        }
    }

    public class TweenCanvasGroupAlpha : BaseTweenFloat<TweenCanvasGroupAlpha>
    {
        public CanvasGroup Target;

        public TweenCanvasGroupAlpha SetTarget(CanvasGroup target)
        {
            Target = target;
            return this;
        }

        public override bool Process(double currentTime)
        {
            if (Target == null)
            {
                Reset();
                return false;
            }

            if (base.Process(currentTime))
                return true;

            double t = BlendValue(currentTime);
            Target.alpha = (float)(StartValue + (EndValue - StartValue) * t);
            return false;
        }

        public override void Finish()
        {
            if (Target != null) Target.alpha = EndValue;
            base.Finish();
        }

        public override void Reset()
        {
            base.Reset();
            Target = null;
        }
    }
}
