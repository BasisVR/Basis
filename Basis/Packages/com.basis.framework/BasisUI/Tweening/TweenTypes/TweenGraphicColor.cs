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
                .Start(duration, image.color, endPosition);
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

        public override bool Process(double currentTime)
        {
            if (base.Process(currentTime))
                return true;

            double t = BlendValue(currentTime);

            double r = StartValue.r + (EndValue.r - StartValue.r) * t;
            double g = StartValue.g + (EndValue.g - StartValue.g) * t;
            double b = StartValue.b + (EndValue.b - StartValue.b) * t;
            double a = StartValue.a + (EndValue.a - StartValue.a) * t;

            Target.color = new Color(
                (float)r,
                (float)g,
                (float)b,
                (float)a
            );

            return false;
        }

        public override void Finish()
        {
            Target.color = EndValue;
            base.Finish();
        }
    }
}
