using NUnit.Framework;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The depth-of-field readout the camera gizmos draw. This is the one part of the debug
    /// visualisation that asserts something about the world rather than just drawing state, so it
    /// is the part that can be quietly wrong: a sign slip in the thin-lens formula still produces
    /// plausible-looking planes. Reference values are the standard photographic ones for a
    /// full-frame sensor (circle of confusion 0.03mm).
    /// </summary>
    public class BasisHandHeldCameraGizmoTests
    {
        private const float FullFrameCoC = 0.03f;

        [Test]
        public void DepthOfField_MatchesTheTextbookBandFor50mmAtF2Point8()
        {
            BasisHandHeldCameraGizmos.ComputeDepthOfField(50f, 2.8f, 5f, FullFrameCoC,
                out float near, out float far, out float hyperfocal);

            Assert.That(hyperfocal, Is.EqualTo(29.81f).Within(0.05f));
            Assert.That(near, Is.EqualTo(4.29f).Within(0.02f));
            Assert.That(far, Is.EqualTo(6.00f).Within(0.02f));
        }

        [Test]
        public void DepthOfField_FocusAtHyperfocalReachesInfinity()
        {
            BasisHandHeldCameraGizmos.ComputeDepthOfField(50f, 2.8f, 5f, FullFrameCoC,
                out _, out _, out float hyperfocal);

            BasisHandHeldCameraGizmos.ComputeDepthOfField(50f, 2.8f, hyperfocal, FullFrameCoC,
                out float near, out float far, out _);

            Assert.That(float.IsPositiveInfinity(far), Is.True,
                "Focusing at the hyperfocal distance is exactly the point where the far limit runs to infinity.");
            Assert.That(near, Is.EqualTo(hyperfocal * 0.5f).Within(0.05f),
                "The near limit at hyperfocal focus is half the hyperfocal distance.");
        }

        [Test]
        public void DepthOfField_StoppingDownWidensTheBand()
        {
            BasisHandHeldCameraGizmos.ComputeDepthOfField(50f, 1.4f, 3f, FullFrameCoC,
                out float wideNear, out float wideFar, out _);
            BasisHandHeldCameraGizmos.ComputeDepthOfField(50f, 11f, 3f, FullFrameCoC,
                out float stoppedNear, out float stoppedFar, out _);

            Assert.That(stoppedFar - stoppedNear, Is.GreaterThan(wideFar - wideNear));
            Assert.That(stoppedNear, Is.LessThan(wideNear));
        }

        [Test]
        public void DepthOfField_LongerLensNarrowsTheBand()
        {
            BasisHandHeldCameraGizmos.ComputeDepthOfField(24f, 4f, 4f, FullFrameCoC,
                out float shortNear, out float shortFar, out _);
            BasisHandHeldCameraGizmos.ComputeDepthOfField(85f, 4f, 4f, FullFrameCoC,
                out float longNear, out float longFar, out _);

            Assert.That(longFar - longNear, Is.LessThan(shortFar - shortNear));
        }

        [Test]
        public void DepthOfField_BandBracketsTheFocusDistance()
        {
            const float focus = 2.5f;
            BasisHandHeldCameraGizmos.ComputeDepthOfField(35f, 5.6f, focus, FullFrameCoC,
                out float near, out float far, out _);

            Assert.That(near, Is.LessThan(focus));
            Assert.That(far, Is.GreaterThan(focus));
        }

        [TestCase(0f, 2.8f, 5f, FullFrameCoC)]
        [TestCase(50f, 0f, 5f, FullFrameCoC)]
        [TestCase(50f, 2.8f, 0f, FullFrameCoC)]
        [TestCase(50f, 2.8f, 5f, 0f)]
        public void DepthOfField_DegenerateInputsDoNotProduceNonsense(float focal, float aperture, float focus, float coc)
        {
            BasisHandHeldCameraGizmos.ComputeDepthOfField(focal, aperture, focus, coc,
                out float near, out float far, out float hyperfocal);

            Assert.That(near, Is.EqualTo(0f));
            Assert.That(float.IsPositiveInfinity(far), Is.True);
            Assert.That(float.IsPositiveInfinity(hyperfocal), Is.True);
        }

        [Test]
        public void Layers_ToggleIndependentlyAndStartOff()
        {
            BasisHandHeldCameraGizmos gizmos = new BasisHandHeldCameraGizmos();

            Assert.That(gizmos.AnyLayerActive, Is.False);

            gizmos.SetLayerEnabled(BasisCameraGizmoLayers.Follow, true);
            gizmos.SetLayerEnabled(BasisCameraGizmoLayers.Readouts, true);

            Assert.That(gizmos.IsLayerEnabled(BasisCameraGizmoLayers.Follow), Is.True);
            Assert.That(gizmos.IsLayerEnabled(BasisCameraGizmoLayers.Readouts), Is.True);
            Assert.That(gizmos.IsLayerEnabled(BasisCameraGizmoLayers.Frustum), Is.False);

            gizmos.SetLayerEnabled(BasisCameraGizmoLayers.Follow, false);

            Assert.That(gizmos.IsLayerEnabled(BasisCameraGizmoLayers.Follow), Is.False);
            Assert.That(gizmos.IsLayerEnabled(BasisCameraGizmoLayers.Readouts), Is.True,
                "Turning one layer off must not disturb the others — each drives its own handle set.");
            Assert.That(gizmos.AnyLayerActive, Is.True);
        }

        [Test]
        public void Layers_RedundantWritesAreHarmless()
        {
            BasisHandHeldCameraGizmos gizmos = new BasisHandHeldCameraGizmos();

            gizmos.SetLayerEnabled(BasisCameraGizmoLayers.PinState, false);
            gizmos.SetLayerEnabled(BasisCameraGizmoLayers.PinState, true);
            gizmos.SetLayerEnabled(BasisCameraGizmoLayers.PinState, true);

            Assert.That(gizmos.IsLayerEnabled(BasisCameraGizmoLayers.PinState), Is.True);
            Assert.That(gizmos.Layers, Is.EqualTo(BasisCameraGizmoLayers.PinState));
        }
    }
}
