using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The link from the lens to the stabilizer. A long lens magnifies the same shake, so the damp
    /// times are stretched as the camera zooms in and released as it zooms back out — all of it in
    /// the pure solver, which is the only way it can be asserted without a headset and a hand.
    /// </summary>
    public class BasisHandHeldCameraZoomStabilizationTests
    {
        private const float Response = 1f;
        private const float MinScale = 0.35f;
        private const float MaxScale = 4f;

        private const float WideFov = BasisHandHeldCameraUI.MaxFov;
        private const float LongFov = BasisHandHeldCameraUI.MinFov;
        private const float ReferenceFov = BasisHandHeldCameraInteractable.StabilizationReferenceFov;

        private static float Scale(float fieldOfView, float response = Response,
            float minScale = MinScale, float maxScale = MaxScale)
            => BasisHandHeldCameraInteractable.SolveZoomStabilizationScale(
                fieldOfView, response, minScale, maxScale);

        [Test]
        public void TheSlidersReadAsWrittenAtTheFieldOfViewTheCameraOpensAt()
        {
            Assert.That(Scale(ReferenceFov), Is.EqualTo(1f).Within(1e-4f),
                "the damping sliders have to mean what they say before the lens is touched");
        }

        [Test]
        public void ZoomingInStabilizesHarderAndZoomingOutLetsGo()
        {
            Assert.That(Scale(LongFov), Is.GreaterThan(1f), "a long lens has to hold the camera harder");
            Assert.That(Scale(WideFov), Is.LessThan(1f), "a wide lens has to hand the camera back");
        }

        [Test]
        public void ItMovesOneWayAcrossTheWholeZoomRange()
        {
            float previous = float.MaxValue;

            for (float fov = LongFov; fov <= WideFov; fov += 1f)
            {
                float scale = Scale(fov);

                Assert.That(scale, Is.LessThanOrEqualTo(previous),
                    $"stabilization went back up while zooming out, at {fov}°");
                previous = scale;
            }
        }

        [Test]
        public void NeitherEndOfTheZoomRunsTheDampingAwayFromWhatWasAskedFor()
        {
            for (float fov = 1f; fov <= 179f; fov += 1f)
            {
                float scale = Scale(fov, BasisHandHeldCameraInteractable.MaxZoomStabilizationResponse);

                Assert.That(scale, Is.InRange(MinScale, MaxScale), $"at {fov}° the clamp did not hold");
            }
        }

        [Test]
        public void APairOfLimitsTheWrongWayRoundStillLandsBetweenThem()
        {
            // The two are separate sliders and a settings file is text on disk, so nothing stops a
            // floor above the ceiling arriving. Mathf.Clamp would return the floor for everything.
            float scale = Scale(LongFov, Response, minScale: 2f, maxScale: 0.5f);

            Assert.That(scale, Is.InRange(0.5f, 2f));
        }

        [Test]
        public void RaisingTheResponseExaggeratesTheLensRatherThanInvertingIt()
        {
            float floor = BasisHandHeldCameraInteractable.MinZoomStabilizationScale;
            float ceiling = BasisHandHeldCameraInteractable.MaxZoomStabilizationScale;

            Assert.That(Scale(LongFov, 2f, floor, ceiling), Is.GreaterThan(Scale(LongFov, 1f, floor, ceiling)));
            Assert.That(Scale(WideFov, 2f, floor, ceiling), Is.LessThan(Scale(WideFov, 1f, floor, ceiling)),
                "the wide end has to fall further as well, or the response is only half a control");
        }

        [Test]
        public void ADegenerateFieldOfViewIsStillANumber()
        {
            foreach (float fov in new[] { 0f, -10f, 180f, 400f })
            {
                float scale = Scale(fov);

                Assert.That(float.IsNaN(scale), Is.False, $"{fov}° produced a NaN damp time");
                Assert.That(scale, Is.InRange(MinScale, MaxScale));
            }
        }

        [Test]
        public void ALongLensRemovesLessOfTheShakeEachFrameThanAWideOne()
        {
            // What the scale is actually for: it multiplies the damp time the stabilizer runs at,
            // and a longer damp time takes a smaller bite out of the residual every frame.
            const float damping = 0.2f;
            const float frame = 1f / 90f;

            float longLens = BasisCameraDamping.Fraction(damping * Scale(LongFov), frame);
            float reference = BasisCameraDamping.Fraction(damping * Scale(ReferenceFov), frame);
            float wideLens = BasisCameraDamping.Fraction(damping * Scale(WideFov), frame);

            Assert.That(longLens, Is.LessThan(reference));
            Assert.That(wideLens, Is.GreaterThan(reference));
        }
    }
}
