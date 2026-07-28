using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Shared accent tinting for panel elements — used to show at a glance that a control is in a
    /// raised state (Performance Mode is cutting, a volume is boosted past normal, and so on).
    ///
    /// <para>The element's own colours are captured once up front and every tint is a blend toward
    /// the accent, so the styling the prefab shipped with is never lost and clearing always lands
    /// back exactly where it started. Transitions run through the tween system.</para>
    /// </summary>
    public static class BasisPanelTint
    {
        public const float Strength = 0.28f;
        public const float Duration = 0.25f;

        public static readonly Color Calm = new Color(0.45f, 0.82f, 0.55f, 1f);
        public static readonly Color Caution = new Color(1f, 0.78f, 0.35f, 1f);
        public static readonly Color Hot = new Color(1f, 0.48f, 0.35f, 1f);

        public sealed class Handle
        {
            internal PanelElementDescriptor Element;
            internal Color PlainBackground;
            internal Color PlainTitle;
            internal bool HasBackground;
            internal bool HasTitle;
            internal bool Tinted;
            internal Color Accent;
            internal TweenGraphicColor BackgroundTween;
            internal TweenGraphicColor TitleTween;
        }

        /// <summary>
        /// Snapshots an element's untinted colours. Call once, before any tint is applied.
        /// </summary>
        public static Handle Capture(PanelElementDescriptor element)
        {
            Handle handle = new Handle { Element = element };
            if (element == null)
            {
                return handle;
            }

            handle.HasBackground = element.HasElementBaseImage;
            handle.HasTitle = element.HasTitle;
            handle.PlainBackground = handle.HasBackground ? element.ElementBaseImage.color : Color.white;
            handle.PlainTitle = handle.HasTitle ? element.TitleLabel.color : Color.white;
            return handle;
        }

        /// <summary>
        /// Blends the element toward <paramref name="accent"/>. Repeat calls with the same accent
        /// are ignored, so this is safe to drive from a slider that fires every frame.
        /// </summary>
        public static void Apply(Handle handle, Color accent, bool animate = true)
        {
            if (handle?.Element == null)
            {
                return;
            }

            if (handle.Tinted && handle.Accent == accent)
            {
                return;
            }

            handle.Tinted = true;
            handle.Accent = accent;

            Color background = Color.Lerp(handle.PlainBackground, accent, Strength);
            background.a = handle.PlainBackground.a;

            Color title = accent;
            title.a = handle.PlainTitle.a;

            Transition(handle, background, title, animate);
        }

        /// <summary>
        /// Returns the element to the colours <see cref="Capture"/> recorded.
        /// </summary>
        public static void Clear(Handle handle, bool animate = true)
        {
            if (handle?.Element == null || !handle.Tinted)
            {
                return;
            }

            handle.Tinted = false;
            Transition(handle, handle.PlainBackground, handle.PlainTitle, animate);
        }

        private static void Transition(Handle handle, Color background, Color title, bool animate)
        {
            PanelElementDescriptor element = handle.Element;

            if (handle.HasBackground && element.ElementBaseImage != null)
            {
                if (handle.BackgroundTween && handle.BackgroundTween.Active
                    && handle.BackgroundTween.Target == element.ElementBaseImage)
                {
                    handle.BackgroundTween.Reset();
                }

                if (animate && Application.isPlaying)
                {
                    handle.BackgroundTween = element.ElementBaseImage
                        .TweenColor(Duration, element.ElementBaseImage.color, background)
                        .SetEase(Easing.OutCubic);
                }
                else
                {
                    element.ElementBaseImage.color = background;
                }
            }

            if (handle.HasTitle && element.TitleLabel != null)
            {
                if (handle.TitleTween && handle.TitleTween.Active
                    && handle.TitleTween.Target == element.TitleLabel)
                {
                    handle.TitleTween.Reset();
                }

                if (animate && Application.isPlaying)
                {
                    handle.TitleTween = element.TitleLabel
                        .TweenColor(Duration, element.TitleLabel.color, title)
                        .SetEase(Easing.OutCubic);
                }
                else
                {
                    element.TitleLabel.color = title;
                }
            }
        }
    }
}
