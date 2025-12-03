using UnityEngine;
using UnityEngine.UI;

namespace Basis.BTween
{
    public static class TweenGraphicColorExtensions
    {
        public static TweenGraphicColor TweenColor(
            this Graphic image,
            float duration,
            Color endPosition)
        {
            TweenGraphicColor tween = TweenGraphicColor.GetAvailableTween()
                .SetTarget(image)
                .Start(duration, endPosition);
            return tween;
        }

        public static TweenGraphicColor TweenColor(
            this Graphic image,
            float duration,
            Color startPosition,
            Color endPosition)
        {
            TweenGraphicColor tween = TweenGraphicColor.GetAvailableTween()
                .SetTarget(image)
                .Start(duration, startPosition, endPosition);
            return tween;
        }
    }


    public class TweenGraphicColor : BaseTweenColor<TweenGraphicColor>
    {
        public Graphic Target;

        public TweenGraphicColor SetTarget(Graphic target)
        {
            Target = target;
            return this;
        }

        public override bool Process(float currentTime)
        {
            if (base.Process(currentTime)) return true;

            float blend = BlendValue(currentTime);
            Target.color = Color.Lerp(StartValue, EndValue, blend);
            return false;
        }

        public override void Finish()
        {
            Target.color = EndValue;
            base.Finish();
        }
    }
}
