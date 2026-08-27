using NUnit.Framework;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// Whether a camera that does not render every frame can keep a temporal accumulation.
    ///
    /// It could not. The gate between one render and the next was a fixed window of two rendered frames,
    /// which every camera that renders EVERY frame passes and no other camera ever can. The handheld
    /// camera has a render rate limit - a shipped setting, and one the panel offers precisely because the
    /// camera is expensive - and at 30Hz on a 90Hz headset it renders one frame in three. The gate failed
    /// on every single render, so the temporal filter was discarded on every single render, and its feed
    /// stayed a one sample per pixel trace beside a direct view of the same room that was accumulating
    /// normally. Nothing named it: a render rate limiter and a denoiser have nothing to do with each other
    /// until you notice they are counting in the same units.
    ///
    /// The budget is now counted in the camera's OWN renders. Camera motion survives any gap - the
    /// reprojection reads a stored view projection, not a delta - so what the window really protects is
    /// scene motion, and that is measured per render of the camera doing the reprojecting.
    /// </summary>
    public class BasisGlobalIlluminationCameraCadenceTests
    {
        private static BasisGlobalIlluminationHistory History()
        {
            return new BasisGlobalIlluminationHistory();
        }

        [Test]
        public void ACameraRenderingEveryFrameIsUnchanged()
        {
            BasisGlobalIlluminationHistory history = History();
            history.RecordFrame(10);
            Assert.IsTrue(history.Contiguous(11), "a camera that rendered last frame lost its accumulation");
            history.RecordFrame(11);
            Assert.IsTrue(history.Contiguous(12));
        }

        [Test]
        public void NothingIsContiguousBeforeTheFirstRender()
        {
            BasisGlobalIlluminationHistory history = History();
            Assert.IsFalse(history.Contiguous(0), "a history that has never been written was reprojected through");
            Assert.IsFalse(history.SpecularContiguous(0));
        }

        [Test]
        public void AThirtyHertzCameraOnANinetyHertzDisplayKeepsItsAccumulation()
        {
            BasisGlobalIlluminationHistory history = History();
            history.RecordFrame(0);

            // The first strided render is the one that measures the stride, so it still resets - once.
            Assert.IsFalse(history.Contiguous(3));
            history.RecordFrame(3);

            for (int frame = 6; frame <= 90; frame += 3)
            {
                Assert.IsTrue(history.Contiguous(frame),
                    "the handheld camera limited to 30Hz threw its accumulation away at frame " + frame +
                    ", so its feed is a raw trace while the player's own view is denoised");
                history.RecordFrame(frame);
            }
        }

        [Test]
        public void TheRateLimitersJitterDoesNotResetTheHistory()
        {
            // BasisRenderRateLimiter carries its remainder frame to frame, so a cadence that does not
            // divide the frame rate alternates - three, three, four - and a window pinned to the last gap
            // exactly would fail on every fourth render.
            BasisGlobalIlluminationHistory history = History();
            int frame = 0;
            history.RecordFrame(frame);
            int[] gaps = { 3, 3, 4, 3, 3, 4, 3, 4, 3 };
            for (int index = 0; index < gaps.Length; index++)
            {
                frame += gaps[index];
                if (index > 0)
                {
                    Assert.IsTrue(history.Contiguous(frame),
                        "a one frame wobble in the render rate limiter reset the accumulation at frame " + frame);
                }
                history.RecordFrame(frame);
            }
        }

        [Test]
        public void ADroppedRenderStillResets()
        {
            BasisGlobalIlluminationHistory history = History();
            history.RecordFrame(0);
            history.RecordFrame(3);
            history.RecordFrame(6);
            Assert.IsTrue(history.Contiguous(9), "the established cadence should hold");
            Assert.IsFalse(history.Contiguous(12),
                "a whole missed render was reprojected through, which is what the window exists to stop");
        }

        [Test]
        public void ACameraThatStoppedAndCameBackReseeds()
        {
            // The render gate switches the capture camera off entirely when nothing is showing the feed.
            // Whatever it was pointing at while it was off, the history is a second stale by the time it
            // comes back, and reprojecting through a second of scene motion is a smear.
            BasisGlobalIlluminationHistory history = History();
            history.RecordFrame(0);
            history.RecordFrame(9);
            Assert.IsFalse(history.Contiguous(9 + BasisGlobalIlluminationHistory.MaxGap + 1),
                "a camera that stopped for a second reprojected through the second");
        }

        [Test]
        public void TheAllowedGapIsBoundedAtBothEnds()
        {
            Assert.AreEqual(2, BasisGlobalIlluminationHistory.AllowedGap(0),
                "a camera with no measured cadence should behave exactly as it did before");
            Assert.AreEqual(2, BasisGlobalIlluminationHistory.AllowedGap(1));
            Assert.AreEqual(4, BasisGlobalIlluminationHistory.AllowedGap(3));
            Assert.AreEqual(BasisGlobalIlluminationHistory.MaxGap,
                BasisGlobalIlluminationHistory.AllowedGap(BasisGlobalIlluminationHistory.MaxGap * 4),
                "the ceiling is what stops a stalled camera reprojecting through the stall");
        }

        [Test]
        public void ReflectionsKeepTheirOwnCadence()
        {
            // The two passes run at different points in the frame and either can be off, so a camera can
            // be accumulating reflections while its diffuse gather is switched off entirely.
            BasisGlobalIlluminationHistory history = History();
            history.RecordSpecularFrame(0);
            history.RecordSpecularFrame(3);
            Assert.IsTrue(history.SpecularContiguous(6),
                "reflections on a rate limited camera threw their accumulation away every render");
            Assert.IsFalse(history.Contiguous(6),
                "the diffuse gather never ran on this camera, so it has no cadence to inherit");
        }

        [Test]
        public void AStillCaptureDoesNotCostThePreviewItsCadence()
        {
            // TakeScreenshot brackets an explicit Render() at the end of a frame the live preview has
            // already drawn, so the camera renders twice under one frame count.
            BasisGlobalIlluminationHistory history = History();
            history.RecordFrame(0);
            history.RecordFrame(3);
            history.RecordFrame(3);
            Assert.AreEqual(3, history.Stride,
                "a photo read as a zero length stride, so the next preview render threw its accumulation away");
            Assert.IsTrue(history.Contiguous(6));
        }

        [Test]
        public void ReleasingTheTargetsForgetsTheCadence()
        {
            BasisGlobalIlluminationHistory history = History();
            history.RecordFrame(0);
            history.RecordFrame(3);
            history.Release();
            Assert.AreEqual(0, history.Stride,
                "a released history kept a cadence measured against targets that no longer exist");
            Assert.AreEqual(0, history.SpecularStride);
        }
    }
}
