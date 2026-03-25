using UnityEngine;

namespace Basis.BTween
{
    public static class TweenScaleExtensions
    {
        public static TweenScale TweenScale(
            this Transform transform,
            float duration,
            Vector3 endScale)
        {
            TweenScale tween = BTween.TweenScale.GetAvailableTween()
                .SetTarget(transform)
                .Start(duration, transform.localScale, endScale);
            return tween;
        }

        public static TweenScale TweenScale(
            this Transform transform,
            float duration,
            Vector3 startScale,
            Vector3 endScale)
        {
            TweenScale tween = BTween.TweenScale.GetAvailableTween()
                .SetTarget(transform)
                .Start(duration, startScale, endScale);
            return tween;
        }
    }

    public class TweenScale : BaseTweenVector3<TweenScale>
    {
        public Transform Target;

        public TweenScale SetTarget(Transform target)
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

            double x = StartValue.x + (EndValue.x - StartValue.x) * t;
            double y = StartValue.y + (EndValue.y - StartValue.y) * t;
            double z = StartValue.z + (EndValue.z - StartValue.z) * t;

            Target.localScale = new Vector3(
                (float)x,
                (float)y,
                (float)z
            );

            return false;
        }

        public override void Finish()
        {
            if (Target != null) Target.localScale = EndValue;
            base.Finish();
        }

        public override void Reset()
        {
            base.Reset();
            Target = null;
        }
    }
}
