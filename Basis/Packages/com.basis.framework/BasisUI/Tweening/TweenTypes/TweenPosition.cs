using UnityEngine;

namespace Basis.BTween
{
    public static class TweenPositionExtensions
    {
        public static TweenPosition TweenPosition(
            this Transform transform,
            float duration,
            Vector3 endPosition)
        {
            TweenPosition tween = BTween.TweenPosition.GetAvailableTween()
                .SetTarget(transform)
                .Start(duration, endPosition);
            return tween;
        }

        public static TweenPosition TweenPosition(
            this Transform transform,
            float duration,
            Vector3 startPosition,
            Vector3 endPosition)
        {
            TweenPosition tween = BTween.TweenPosition.GetAvailableTween()
                .SetTarget(transform)
                .Start(duration, startPosition, endPosition);
            return tween;
        }
    }


    public class TweenPosition : BaseTweenVector3<TweenPosition>
    {

        public Transform Target;

        public TweenPosition SetTarget(Transform target)
        {
            Target = target;
            return this;
        }

        public override bool Process(float currentTime)
        {
            if (base.Process(currentTime)) return true;

            float blend = BlendValue(currentTime);
            Target.position = Vector3.Lerp(StartValue, EndValue, blend);
            return false;
        }

        public override void Finish()
        {
            Target.position = EndValue;
            base.Finish();
        }
    }
}
