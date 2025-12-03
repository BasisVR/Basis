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
            BTweenManager.RegisterGroup(ProcessGroup);
        }

        private static void ProcessGroup(float currentTime)
        {
            List<T> list = Tweens;
            foreach (T tween in list)
            {
                if (!tween.Active) continue;
                tween.Process(currentTime);
            }
        }

        public static T GetAvailableTween()
        {
            foreach (T tween in Tweens)
            {
                if (tween.Active) continue;
                return tween;
            }

            T newTween = new();
            Tweens.Add(newTween);
            return newTween;
        }

        protected float BlendValue(float currentTime) =>
            EaseTypes.PerformEase(Ease, Mathf.InverseLerp(StartTime, EndTime, currentTime));


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

        /// <summary>
        /// Returns true when completed.
        /// </summary>
        public virtual bool Process(float currentTime)
        {
            float percentage = BlendValue(currentTime);
            if (percentage >= 1)
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
