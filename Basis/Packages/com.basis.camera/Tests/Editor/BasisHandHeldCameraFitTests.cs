using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Geometry for the desktop screen fit. The prop is a fixed-size world object held a fixed
    /// distance from the eye, so whether it fits the window is pure frustum maths — and getting it
    /// wrong is invisible in code review but fills the player's screen with camera.
    ///
    /// Reference geometry: the Player Held Camera root rect is 3500 x 2500 local units.
    /// </summary>
    public class BasisHandHeldCameraFitTests
    {
        private static readonly Vector2 RootRect = new Vector2(3500f, 2500f);
        private const float Fraction = 0.85f;
        private const float Fov = 60f;
        private const float Aspect = 16f / 9f;

        private static float FitAt(float distance) =>
            BasisHandHeldCameraInteractable.ComputeDesktopFitScale(RootRect, distance, Fov, Aspect, Fraction);

        private static float FrustumHeight(float distance, float fov) =>
            2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

        [Test]
        public void FitScale_MakesTheRectCoverExactlyTheRequestedFraction()
        {
            const float distance = 0.5f;

            float scale = FitAt(distance);
            float renderedHeight = RootRect.y * scale;

            Assert.That(renderedHeight / FrustumHeight(distance, Fov), Is.EqualTo(Fraction).Within(1e-3f),
                "At a 16:9 aspect the height is the binding axis, so the fit must land on the fraction.");
        }

        [Test]
        public void FitScale_IsProportionalToDistance()
        {
            // The prop keeps a constant screen fraction, so twice as far means twice the scale.
            Assert.That(FitAt(1f), Is.EqualTo(FitAt(0.5f) * 2f).Within(1e-5f));
        }

        [Test]
        public void FitScale_ShrinksAsTheCameraGetsCloser()
        {
            Assert.That(FitAt(0.2f), Is.LessThan(FitAt(0.4f)));
            Assert.That(FitAt(0.4f), Is.LessThan(FitAt(0.8f)));
        }

        [Test]
        public void NarrowAspect_BecomesWidthBound()
        {
            // Below the rect's own aspect the horizontal frustum runs out first and must win,
            // otherwise the prop overflows the sides of a tall window.
            const float distance = 0.5f;
            const float narrow = 0.5f;

            float scale = BasisHandHeldCameraInteractable.ComputeDesktopFitScale(
                RootRect, distance, Fov, narrow, Fraction);

            float renderedWidth = RootRect.x * scale;
            float frustumWidth = FrustumHeight(distance, Fov) * narrow;

            Assert.That(renderedWidth / frustumWidth, Is.EqualTo(Fraction).Within(1e-3f));
        }

        [Test]
        public void WiderAspectAlone_DoesNotChangeAHeightBoundFit()
        {
            // An ultrawide monitor adds horizontal frustum only. Height already binds at 16:9, so
            // the scale must not grow just because the window got wider.
            const float distance = 0.5f;

            float wide = BasisHandHeldCameraInteractable.ComputeDesktopFitScale(
                RootRect, distance, Fov, 32f / 9f, Fraction);

            Assert.That(wide, Is.EqualTo(FitAt(distance)).Within(1e-5f));
        }

        [Test]
        public void NarrowerFov_AllowsALargerProp()
        {
            // Zooming in shrinks the frustum, so a constant screen fraction needs a smaller prop.
            float zoomedIn = BasisHandHeldCameraInteractable.ComputeDesktopFitScale(
                RootRect, 0.5f, 30f, Aspect, Fraction);

            Assert.That(zoomedIn, Is.LessThan(FitAt(0.5f)));
        }

        [Test]
        public void FitFraction_ScalesTheResultLinearly()
        {
            float half = BasisHandHeldCameraInteractable.ComputeDesktopFitScale(
                RootRect, 0.5f, Fov, Aspect, Fraction * 0.5f);

            Assert.That(half, Is.EqualTo(FitAt(0.5f) * 0.5f).Within(1e-5f));
        }

        [Test]
        public void DegenerateInput_ReturnsInfinitySoTheCallerLeavesScaleAlone()
        {
            // ApplyCameraScale treats +infinity as "no constraint". Returning 0 instead would
            // collapse the prop to nothing the moment any of these went bad.
            Assert.That(BasisHandHeldCameraInteractable.ComputeDesktopFitScale(RootRect, 0f, Fov, Aspect, Fraction),
                Is.EqualTo(float.PositiveInfinity), "Zero distance.");
            Assert.That(BasisHandHeldCameraInteractable.ComputeDesktopFitScale(RootRect, -1f, Fov, Aspect, Fraction),
                Is.EqualTo(float.PositiveInfinity), "Camera behind the eye.");
            Assert.That(BasisHandHeldCameraInteractable.ComputeDesktopFitScale(Vector2.zero, 0.5f, Fov, Aspect, Fraction),
                Is.EqualTo(float.PositiveInfinity), "Rect with no size.");
            Assert.That(BasisHandHeldCameraInteractable.ComputeDesktopFitScale(RootRect, 0.5f, Fov, 0f, Fraction),
                Is.EqualTo(float.PositiveInfinity), "Aspect not yet initialised.");
            Assert.That(BasisHandHeldCameraInteractable.ComputeDesktopFitScale(RootRect, 0.5f, 0f, Aspect, Fraction),
                Is.EqualTo(float.PositiveInfinity), "Degenerate field of view.");
            Assert.That(BasisHandHeldCameraInteractable.ComputeDesktopFitScale(RootRect, 0.5f, 180f, Aspect, Fraction),
                Is.EqualTo(float.PositiveInfinity), "Field of view with no finite frustum.");
        }
    }
}
