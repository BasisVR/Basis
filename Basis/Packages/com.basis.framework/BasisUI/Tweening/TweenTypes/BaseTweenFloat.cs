using UnityEngine;

namespace Basis.BTween
{
    public abstract class BaseTweenFloat<T> : BaseTween<T>
        where T: BaseTweenFloat<T>, new()
    {
        public float StartValue;
        public float EndValue;

        public T Start(float duration, float startValue, float endValue)
        {
            AssignTimes(duration);
            AssignRange(startValue, endValue);
            return (T)this;
        }

        public void AssignRange(float startValue, float endValue)
        {
            StartValue = startValue;
            EndValue = endValue;
        }

        public T Start(float duration, float endValue)
        {
            AssignTimes(duration);
            StartValue = 0;
            EndValue = endValue;
            return (T)this;
        }

        public override void Reset()
        {
            base.Reset();
            StartValue = 0;
            EndValue = 0;
        }
    }
}
