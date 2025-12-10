using UnityEngine;

namespace Basis.BTween
{
    public abstract class BaseTweenVector3<T> : BaseTween<T>
        where T: BaseTweenVector3<T>, new()
    {
        public Vector3 StartValue;
        public Vector3 EndValue;

        public T Start(float duration, Vector3 startValue, Vector3 endValue)
        {
            AssignTimes(duration);
            AssignRange(startValue, endValue);
            return (T)this;
        }

        public void AssignRange(Vector3 startValue, Vector3 endValue)
        {
            StartValue = startValue;
            EndValue = endValue;
        }

        public T Start(float duration, Vector3 endValue)
        {
            AssignTimes(duration);
            StartValue = Vector3.zero;
            EndValue = endValue;
            return (T)this;
        }

        public override void Reset()
        {
            base.Reset();
            StartValue = Vector3.zero;
            EndValue = Vector3.zero;
        }
    }
}
