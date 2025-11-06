using System;
    using UnityEngine;

    namespace Basis.Scripts.BasisSdk.Interactions
    {
        public static class BasisEasing
        {
            public enum EasingType
            {
                Linear = 0,
                SmoothStep = 1,
                EaseInQuad = 2,
                EaseOutQuad = 3,
                EaseInOutQuad = 4,
                EaseInCubic = 5,
                EaseOutCubic = 6,
                EaseInOutCubic = 7
            }

            public static float ApplyEasing(float t, EasingType e)
            {
                switch (e)
                {
                    case EasingType.SmoothStep:
                        return Mathf.SmoothStep(0f, 1f, t);
                    case EasingType.EaseInQuad:
                        return t * t;
                    case EasingType.EaseOutQuad:
                        return t * (2f - t);
                    case EasingType.EaseInOutQuad:
                        return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
                    case EasingType.EaseInCubic:
                        return t * t * t;
                    case EasingType.EaseOutCubic:
                        return 1f - Mathf.Pow(1f - t, 3f);
                    case EasingType.EaseInOutCubic:
                        return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
                    case EasingType.Linear:
                    default:
                        return t;
                }
            }
        }
    }
