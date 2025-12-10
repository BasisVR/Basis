using UnityEngine;

namespace Basis.BTween
{
    public abstract class BaseTweenVector2<T> : BaseTween<T>
        where T: BaseTweenVector2<T>, new()
    {
        public Vector2 StartValue;
        public Vector2 EndValue;

        public T Start(float duration, Vector2 startValue, Vector2 endValue)
        {
            AssignTimes(duration);
            AssignRange(startValue, endValue);
            return (T)this;
        }

        public void AssignRange(Vector2 startValue, Vector2 endValue)
        {
            StartValue = startValue;
            EndValue = endValue;
        }

        public T Start(float duration, Vector2 endValue)
        {
            AssignTimes(duration);
            StartValue = Vector2.zero;
            EndValue = endValue;
            return (T)this;
        }

        public override void Reset()
        {
            base.Reset();
            StartValue = Vector2.zero;
            EndValue = Vector2.zero;
        }
    }
}
