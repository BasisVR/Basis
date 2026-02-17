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

        public override bool Process(double currentTime)
        {
            if (Target == null)
            {
                return false;
            }

            if (base.Process(currentTime))
                return true;

            double t = BlendValue(currentTime);

            double x = StartValue.x + (EndValue.x - StartValue.x) * t;
            double y = StartValue.y + (EndValue.y - StartValue.y) * t;

            Target.anchoredPosition = new Vector2((float)x, (float)y);
            return false;
        }

        public override void Finish()
        {
            Target.anchoredPosition = EndValue;
            base.Finish();
        }

    }
}
