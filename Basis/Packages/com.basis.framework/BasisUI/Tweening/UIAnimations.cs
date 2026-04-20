using UnityEngine;

namespace Basis.BTween
{
    /// <summary>
    /// Reusable UI animation patterns built on the BTween system.
    /// </summary>
    public static class UIAnimations
    {
        /// <summary>
        /// Fade a CanvasGroup alpha from 0 to 1. Adds a CanvasGroup if one doesn't exist.
        /// </summary>
        public static TweenCanvasGroupAlpha FadeIn(Component target, float duration = 0.2f, float delay = 0f, Easing ease = Easing.OutCubic)
        {
            if (!Application.isPlaying || target == null) return null;

            CanvasGroup cg = GetOrAddCanvasGroup(target.gameObject);
            cg.alpha = 0f;
            return cg.TweenAlpha(duration, 0f, 1f).SetEase(ease).SetDelay(delay);
        }

        /// <summary>
        /// Scale a transform from a smaller size up to Vector3.one.
        /// </summary>
        public static TweenScale ScaleIn(Transform target, float duration = 0.25f, float startScale = 0.97f, float delay = 0f, Easing ease = Easing.OutCubic)
        {
            if (!Application.isPlaying || target == null) return null;

            target.localScale = Vector3.one * startScale;
            return target.TweenScale(duration, Vector3.one * startScale, Vector3.one).SetEase(ease).SetDelay(delay);
        }

        /// <summary>
        /// Pop-in animation: fade + scale with overshoot (OutBack) for emphasis.
        /// Great for dialogs and important UI elements.
        /// </summary>
        public static void PopIn(Component target, float duration = 0.3f, float delay = 0f)
        {
            if (!Application.isPlaying || target == null) return;

           // FadeIn(target, duration, delay, Easing.OutCubic);
            ScaleIn(target.transform, duration, 0.9f, delay, Easing.OutBack);
        }

        /// <summary>
        /// Panel entrance animation: subtle fade + scale for content panels.
        /// </summary>
        public static void PanelIn(Component target, float delay = 0f)
        {
            if (!Application.isPlaying || target == null) return;

          //  FadeIn(target, 0.2f, delay, Easing.OutCubic);
            ScaleIn(target.transform, 0.25f, 0.97f, delay, Easing.OutCubic);
        }

        /// <summary>
        /// Slide a RectTransform in from an offset position.
        /// </summary>
        public static TweenAnchorPosition SlideIn(RectTransform target, Vector2 offset, float duration = 0.25f, float delay = 0f, Easing ease = Easing.OutCubic)
        {
            if (!Application.isPlaying || target == null) return null;

            Vector2 endPos = target.anchoredPosition;
            target.anchoredPosition = endPos + offset;
            return target.TweenAnchorPosition(duration, target.anchoredPosition, endPos).SetEase(ease).SetDelay(delay);
        }

        /// <summary>
        /// Quick scale punch: shrink then spring back. Great for button press feedback.
        /// </summary>
        public static void PunchScale(Transform target, float punchScale = 0.92f, float downDuration = 0.06f, float upDuration = 0.12f)
        {
            if (!Application.isPlaying || target == null) return;

            target.TweenScale(downDuration, target.localScale, Vector3.one * punchScale)
                .SetEase(Easing.OutCubic)
                .AddCallback(() =>
                {
                    if (target != null)
                    {
                        target.TweenScale(upDuration, Vector3.one * punchScale, Vector3.one)
                            .SetEase(Easing.OutBack);
                    }
                });
        }

        /// <summary>
        /// Staggered entrance for a group of UI elements: fade + slide up.
        /// </summary>
        public static void StaggerEntrance(RectTransform[] targets, float staggerDelay = 0.04f, float duration = 0.2f, float slideOffset = -15f)
        {
            if (!Application.isPlaying || targets == null) return;

            for (int i = 0; i < targets.Length; i++)
            {
                RectTransform target = targets[i];
                if (target == null) continue;

                float delay = i * staggerDelay;
             //  FadeIn(target, duration, delay, Easing.OutCubic);
                SlideIn(target, new Vector2(0, slideOffset), duration + 0.05f, delay, Easing.OutCubic);
            }
        }

        /// <summary>
        /// Hover lift: scale up for a 3D pop-out feel in VR.
        /// Uses scale instead of position so it works inside layout groups.
        /// </summary>
        public static void HoverLift(Transform target, float scaleAmount = 1.0125f, float duration = 0.15f)
        {
            if (!Application.isPlaying || target == null) return;

            target.TweenScale(duration, Vector3.one * scaleAmount).SetEase(Easing.OutCubic);
        }

        /// <summary>
        /// Reset hover: return to original scale.
        /// </summary>
        public static void HoverReset(Transform target, float duration = 0.15f)
        {
            if (!Application.isPlaying || target == null) return;

            target.TweenScale(duration, Vector3.one).SetEase(Easing.OutCubic);
        }

        private static CanvasGroup GetOrAddCanvasGroup(GameObject go)
        {
            if (!go.TryGetComponent<CanvasGroup>(out CanvasGroup cg))
            {
                cg = go.AddComponent<CanvasGroup>();
            }
            return cg;
        }
    }
}
