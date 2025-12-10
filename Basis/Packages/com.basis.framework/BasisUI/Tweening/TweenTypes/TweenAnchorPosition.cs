using UnityEngine;

namespace Basis.BTween
{
    public static class TweenAnchorPositionExtensions
    {
        public static TweenAnchorPosition TweenAnchorPosition(
            this RectTransform rectTransform,
            float duration,
            Vector2 endPosition)
        {
            TweenAnchorPosition tween = BTween.TweenAnchorPosition.GetAvailableTween()
                .SetTarget(rectTransform)
                .Start(duration, endPosition);
            return tween;
        }

        public static TweenAnchorPosition TweenAnchorPosition(
            this RectTransform rectTransform,
            float duration,
            Vector2 startPosition,
            Vector2 endPosition)
        {
            TweenAnchorPosition tween = BTween.TweenAnchorPosition.GetAvailableTween()
                .SetTarget(rectTransform)
                .Start(duration, startPosition, endPosition);
            return tween;
        }
    }

    public class TweenAnchorPosition : BaseTweenVector2<TweenAnchorPosition>
    {

        public RectTransform Target;

        public TweenAnchorPosition SetTarget(RectTransform target)
        {
            Target = target;
            return this;
        }

        public override bool Process(float currentTime)
        {
            if (base.Process(currentTime)) return true;

            float blend = BlendValue(currentTime);
            Target.anchoredPosition = Vector2.Lerp(StartValue, EndValue, blend);
            return false;
        }

        public override void Finish()
        {
            Target.anchoredPosition = EndValue;
            base.Finish();
        }

    }
}
