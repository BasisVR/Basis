// Largely based on the work of https://easings.net/#

using System;
using UnityEngine;

namespace Basis.BTween
{
    public enum Easing
    {
        InSine,
        OutSine,
        InOutSine,
        InQuad,
        OutQuad,
        InOutQuad,
        InCubic,
        OutCubic,
        InOutCubic,
        InQuart,
        OutQuart,
        InOutQuart,
        InQuint,
        OutQuint,
        InOutQuint,
        InExpo,
        OutExpo,
        InOutExpo,
        InCirc,
        OutCirc,
        InOutCirc,
        InBack,
        OutBack,
        InOutBack,
        InElastic,
        OutElastic,
        InOutElastic,
        InBounce,
        OutBounce,
        InOutBounce,
    }

    public static class EaseTypes
    {
        public const float c1  = 1.70158f;
        public const float c2  = c1 * 1.525f;
        public const float c3  = c1 + 1;
        public const float c4  = (2 * Mathf.PI) / 3;
        public const float c5  = (2 * Mathf.PI) / 4.5f;
        public const float n1  = 7.5625f;
        public const float d1  = 2.75f;

        public static double PerformEase(Easing ease, double x)
        {
            switch (ease)
            {
                case Easing.InSine:
                    return 1 - Math.Cos((x * Math.PI) / 2);

                case Easing.OutSine:
                    return Math.Sin((x * Math.PI) / 2);

                case Easing.InOutSine:
                    return -(Math.Cos(Math.PI * x) - 1) / 2;

                case Easing.InQuad:
                    return x * x;

                case Easing.OutQuad:
                    return 1 - (1 - x) * (1 - x);

                case Easing.InOutQuad:
                    return x < 0.5
                        ? 2 * x * x
                        : 1 - Math.Pow(-2 * x + 2, 2) / 2;

                case Easing.InCubic:
                    return x * x * x;

                case Easing.OutCubic:
                    return 1 - Math.Pow(1 - x, 3);

                case Easing.InOutCubic:
                    return x < 0.5
                        ? 4 * x * x * x
                        : 1 - Math.Pow(-2 * x + 2, 3) / 2;

                case Easing.InQuart:
                    return x * x * x * x;

                case Easing.OutQuart:
                    return 1 - Math.Pow(1 - x, 4);

                case Easing.InOutQuart:
                    return x < 0.5
                        ? 8 * x * x * x * x
                        : 1 - Math.Pow(-2 * x + 2, 4) / 2;

                case Easing.InQuint:
                    return x * x * x * x * x;

                case Easing.OutQuint:
                    return 1 - Math.Pow(1 - x, 5);

                case Easing.InOutQuint:
                    return x < 0.5
                        ? 16 * x * x * x * x * x
                        : 1 - Math.Pow(-2 * x + 2, 5) / 2;

                case Easing.InExpo:
                    return x == 0 ? 0 : Math.Pow(2, 10 * x - 10);

                case Easing.OutExpo:
                    return x == 1 ? 1 : 1 - Math.Pow(2, -10 * x);

                case Easing.InOutExpo:
                    return x == 0
                        ? 0
                        : x == 1
                            ? 1
                            : x < 0.5
                                ? Math.Pow(2, 20 * x - 10) / 2
                                : (2 - Math.Pow(2, -20 * x + 10)) / 2;

                case Easing.InCirc:
                    return 1 - Math.Sqrt(1 - Math.Pow(x, 2));

                case Easing.OutCirc:
                    return Math.Sqrt(1 - Math.Pow(x - 1, 2));

                case Easing.InOutCirc:
                    return x < 0.5
                        ? (1 - Math.Sqrt(1 - Math.Pow(2 * x, 2))) / 2
                        : (Math.Sqrt(1 - Math.Pow(-2 * x + 2, 2)) + 1) / 2;

                case Easing.InBack:
                    return c3 * x * x * x - c1 * x * x;

                case Easing.OutBack:
                    return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);

                case Easing.InOutBack:
                    return x < 0.5
                        ? (Math.Pow(2 * x, 2) * ((c2 + 1) * 2 * x - c2)) / 2
                        : (Math.Pow(2 * x - 2, 2) * ((c2 + 1) * (x * 2 - 2) + c2) + 2) / 2;

                case Easing.InElastic:
                    return x == 0
                        ? 0
                        : x == 1
                            ? 1
                            : -Math.Pow(2, 10 * x - 10) * Math.Sin((x * 10 - 10.75) * c4);

                case Easing.OutElastic:
                    return x == 0
                        ? 0
                        : x == 1
                            ? 1
                            : Math.Pow(2, -10 * x) * Math.Sin((x * 10 - 0.75) * c4) + 1;

                case Easing.InOutElastic:
                    return x == 0
                        ? 0
                        : x == 1
                            ? 1
                            : x < 0.5
                                ? -(Math.Pow(2, 20 * x - 10) * Math.Sin((20 * x - 11.125) * c5)) / 2
                                : (Math.Pow(2, -20 * x + 10) * Math.Sin((20 * x - 11.125) * c5)) / 2 + 1;

                case Easing.InBounce:
                    return 1 - PerformEase(Easing.OutBounce, 1 - x);

                case Easing.OutBounce:
                    if (x < 1 / d1)
                        return n1 * x * x;

                    if (x < 2 / d1)
                        return n1 * (x -= 1.5 / d1) * x + 0.75;

                    if (x < 2.5 / d1)
                        return n1 * (x -= 2.25 / d1) * x + 0.9375;

                    return n1 * (x -= 2.625 / d1) * x + 0.984375;

                case Easing.InOutBounce:
                    return x < 0.5
                        ? (1 - PerformEase(Easing.OutBounce, 1 - 2 * x)) / 2
                        : (1 + PerformEase(Easing.OutBounce, 2 * x - 1)) / 2;

                default:
                    Debug.LogWarning($"Ease type {ease} not implemented.");
                    return x;
            }
        }
    }
}
