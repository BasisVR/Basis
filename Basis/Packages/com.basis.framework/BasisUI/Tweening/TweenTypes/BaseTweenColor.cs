using UnityEngine;

namespace Basis.BTween
{
    public abstract class BaseTweenColor<T> : BaseTween<T>
        where T: BaseTweenColor<T>, new()
    {
        public Color StartValue;
        public Color EndValue;

        public T Start(float duration, Color startValue, Color endValue)
        {
            AssignTimes(duration);
            AssignRange(startValue, endValue);
            return (T)this;
        }

        public void AssignRange(Color startValue, Color endValue)
        {
            StartValue = startValue;
            EndValue = endValue;
        }

        public T Start(float duration, Color endValue)
        {
            AssignTimes(duration);
            StartValue = Color.white;
            EndValue = endValue;
            return (T)this;
        }

        public override void Reset()
        {
            base.Reset();
            StartValue = Color.white;
            EndValue = Color.white;
        }
    }
}
