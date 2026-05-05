using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BTween
{
    [Serializable]
    public abstract class BaseTween<T> where T: BaseTween<T>, new()
    {

        public static implicit operator bool(BaseTween<T> tween) => tween != null;


        public bool Active;
        public Easing Ease = Easing.OutSine;
        public float StartTime;
        public float EndTime;
        public Action OnComplete;

        private const int MAX_TWEEN_COUNT = 64;
        public static List<T> Tweens = new(MAX_TWEEN_COUNT);


        static BaseTween()
        {
            BasisTweenManager.RegisterGroup(ProcessGroup);
        }

        private static void ProcessGroup(double currentTime)
        {
            List<T> list = Tweens;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                T tween = list[i];
                if (!tween.Active) continue;
                tween.Process(currentTime);
            }
        }

        public static T GetAvailableTween()
        {
            List<T> list = Tweens;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                T tween = list[i];
                if (tween.Active) continue;
                return tween;
            }

            T newTween = new();
            list.Add(newTween);
            return newTween;
        }

        protected double BlendValue(double currentTime)
        {
            double t = InverseLerp(StartTime, EndTime, currentTime);
            return EaseTypes.PerformEase(Ease, t);
        }

        static double InverseLerp(double a, double b, double value)
        {
            if (a == b)
                return 0.0;

            double t = (value - a) / (b - a);
            return Math.Clamp(t, 0.0, 1.0);
        }

        protected void AssignTimes(float duration)
        {
            Active = true;
            StartTime = Time.realtimeSinceStartup;
            EndTime = StartTime + duration;
        }


        public T SetEase(Easing easing)
        {
            Ease = easing;
            return (T)this;
        }

        public T AddCallback(Action callback)
        {
            OnComplete += callback;
            return (T)this;
        }

        public T SetDelay(float delay)
        {
            StartTime += delay;
            EndTime += delay;
            return (T)this;
        }

        /// <summary>
        /// Returns true when completed.
        /// </summary>
        public virtual bool Process(double currentTime)
        {
            if (currentTime >= EndTime)
            {
                Finish();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Complete a tween, applying its End value.
        /// </summary>
        public virtual void Finish()
        {
            // Debug.Log("Finished!");
            OnComplete?.Invoke();
            Reset();
        }

        public virtual void Reset()
        {
            Active = false;
            StartTime = 0;
            EndTime = 0;
            OnComplete = null;
        }
    }
}
