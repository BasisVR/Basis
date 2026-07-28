using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Issue #638: on desktop (Eye mode) the menu compensated for vertical FOV only, so narrowing
    /// the window kept the menu's world width and it overflowed the screen horizontally. The fix
    /// shrinks the menu with the window aspect once it drops below EYE_WIDTH_FIT_ASPECT.
    /// Geometry mirrors "Basis Menu Instance.prefab": widest panel group 1500px
    /// (BasisMenuPanel.PanelData), RootScale 0.0005, HeadOffset z 0.4.
    /// </summary>
    public class BasisMenuAspectFitTests
    {
        const float RootScale = 0.0005f;
        const float HeadOffsetZ = 0.4f;
        const float SixteenByNine = 16f / 9f;

        static float PanelWidthPx => BasisMenuPanel.PanelData.Standard("width probe").PanelSize.x;

        static float TanHalf(float fovDegrees) => Mathf.Tan(0.5f * Mathf.Deg2Rad * fovDegrees);

        static float ScreenWidthFraction(float fovDegrees, float aspect)
        {
            float scaleFactor = BasisMenuMover.GetEyeModeScaleFactor(fovDegrees, aspect);
            float menuHalfWidth = 0.5f * PanelWidthPx * RootScale * scaleFactor;
            float frustumHalfWidth = HeadOffsetZ * TanHalf(fovDegrees) * aspect;
            return menuHalfWidth / frustumHalfWidth;
        }

        [Test]
        public void DesignFovAtSixteenByNine_IsUnitScale()
        {
            Assert.That(BasisMenuMover.GetEyeModeScaleFactor(BasisMenuMover.EYE_DESIGN_FOV, SixteenByNine),
                Is.EqualTo(1f).Within(1e-5f),
                "the menu must render at its designed size on a standard 16:9 window.");
        }

        [Test]
        public void FovCompensation_UnchangedAboveFitAspect([Values(50f, 60f, 75f, 80f, 100f)] float fov)
        {
            float expected = TanHalf(fov) / TanHalf(BasisMenuMover.EYE_DESIGN_FOV);
            Assert.That(BasisMenuMover.GetEyeModeScaleFactor(fov, SixteenByNine),
                Is.EqualTo(expected).Within(1e-5f),
                "third-person FOV zoom compensation must be untouched on wide windows.");
        }

        [Test]
        public void NarrowWindow_ShrinksProportionallyToAspect([Values(1.1f, 1.0f, 0.75f, 0.5f)] float aspect)
        {
            float expected = aspect / BasisMenuMover.EYE_WIDTH_FIT_ASPECT;
            Assert.That(BasisMenuMover.GetEyeModeScaleFactor(BasisMenuMover.EYE_DESIGN_FOV, aspect),
                Is.EqualTo(expected).Within(1e-5f),
                "below the fit aspect the menu must track the window width 1:1.");
        }

        [Test]
        public void MenuFitsHorizontally_AtAnyAspectAndFov(
            [Values(0.4f, 0.8f, 1.0f, 1.2f, 4f / 3f, 16f / 9f, 21f / 9f, 32f / 9f)] float aspect,
            [Values(50f, 60f, 80f, 100f)] float fov)
        {
            Assert.That(ScreenWidthFraction(fov, aspect), Is.LessThanOrEqualTo(1f),
                $"the widest panel group overflows the window at fov {fov}, aspect {aspect:0.00} — this is issue #638.");
        }

        [Test]
        public void WidthFit_HoldsConstantScreenFractionBelowFitAspect()
        {
            float atFit = ScreenWidthFraction(BasisMenuMover.EYE_DESIGN_FOV, BasisMenuMover.EYE_WIDTH_FIT_ASPECT);
            float below = ScreenWidthFraction(BasisMenuMover.EYE_DESIGN_FOV, 0.9f);
            float farBelow = ScreenWidthFraction(BasisMenuMover.EYE_DESIGN_FOV, 0.5f);

            Assert.That(atFit, Is.LessThanOrEqualTo(1f));
            Assert.That(below, Is.EqualTo(atFit).Within(1e-4f));
            Assert.That(farBelow, Is.EqualTo(atFit).Within(1e-4f));
        }

        [Test]
        public void FitAspect_HasMarginOverExactFit()
        {
            float exactFitAspect = (0.5f * PanelWidthPx * RootScale) / (HeadOffsetZ * TanHalf(BasisMenuMover.EYE_DESIGN_FOV));
            Assert.That(BasisMenuMover.EYE_WIDTH_FIT_ASPECT, Is.GreaterThan(exactFitAspect),
                "the fit aspect must leave a margin so the menu edge never sits exactly on the window edge.");
        }

        [Test]
        public void DegenerateAspect_KeepsDesignScale()
        {
            Assert.That(BasisMenuMover.GetEyeModeScaleFactor(BasisMenuMover.EYE_DESIGN_FOV, 0f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(BasisMenuMover.GetEyeModeScaleFactor(BasisMenuMover.EYE_DESIGN_FOV, -1f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(BasisMenuMover.GetEyeModeScaleFactor(BasisMenuMover.EYE_DESIGN_FOV, float.NaN), Is.EqualTo(1f).Within(1e-5f));
        }
    }
}
