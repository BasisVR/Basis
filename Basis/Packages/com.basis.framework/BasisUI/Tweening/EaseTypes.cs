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
                {
                    double m = 1 - x;
                    return 1 - m * m;
                }

                case Easing.InOutQuad:
                    if (x < 0.5)
                        return 2 * x * x;
                    else
                    {
                        double m = -2 * x + 2;
                        return 1 - m * m / 2;
                    }

                case Easing.InCubic:
                    return x * x * x;

                case Easing.OutCubic:
                {
                    double m = 1 - x;
                    return 1 - m * m * m;
                }

                case Easing.InOutCubic:
                    if (x < 0.5)
                        return 4 * x * x * x;
                    else
                    {
                        double m = -2 * x + 2;
                        return 1 - m * m * m / 2;
                    }

                case Easing.InQuart:
                    return x * x * x * x;

                case Easing.OutQuart:
                {
                    double m = 1 - x;
                    double m2 = m * m;
                    return 1 - m2 * m2;
                }

                case Easing.InOutQuart:
                    if (x < 0.5)
                        return 8 * x * x * x * x;
                    else
                    {
                        double m = -2 * x + 2;
                        double m2 = m * m;
                        return 1 - m2 * m2 / 2;
                    }

                case Easing.InQuint:
                    return x * x * x * x * x;

                case Easing.OutQuint:
                {
                    double m = 1 - x;
                    double m2 = m * m;
                    return 1 - m2 * m2 * m;
                }

                case Easing.InOutQuint:
                    if (x < 0.5)
                        return 16 * x * x * x * x * x;
                    else
                    {
                        double m = -2 * x + 2;
                        double m2 = m * m;
                        return 1 - m2 * m2 * m / 2;
                    }

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
                    return 1 - Math.Sqrt(1 - x * x);

                case Easing.OutCirc:
                {
                    double m = x - 1;
                    return Math.Sqrt(1 - m * m);
                }

                case Easing.InOutCirc:
                    if (x < 0.5)
                        return (1 - Math.Sqrt(1 - 4 * x * x)) / 2;
                    else
                    {
                        double m = -2 * x + 2;
                        return (Math.Sqrt(1 - m * m) + 1) / 2;
                    }

                case Easing.InBack:
                    return c3 * x * x * x - c1 * x * x;

                case Easing.OutBack:
                {
                    double m = x - 1;
                    return 1 + m * m * (c3 * m + c1);
                }

                case Easing.InOutBack:
                    if (x < 0.5)
                    {
                        double m = 2 * x;
                        return (m * m * ((c2 + 1) * m - c2)) / 2;
                    }
                    else
                    {
                        double m = 2 * x - 2;
                        return (m * m * ((c2 + 1) * m + c2) + 2) / 2;
                    }

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
