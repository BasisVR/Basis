using NUnit.Framework;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeExposureTests
    {
        private static MediaPipeExposure Metered(byte level, int samples = 1000)
        {
            MediaPipeExposure exposure = new MediaPipeExposure();
            Meter(exposure, level, samples);
            return exposure;
        }
        private static void Meter(MediaPipeExposure exposure, byte level, int samples = 1000)
        {
            exposure.Begin();
            for (int i = 0; i < samples; i++) exposure.Add(level, level, level);
            Assert.IsTrue(exposure.End());
        }
        [Test]
        public void Lut_StartsAsIdentity()
        {
            MediaPipeExposure exposure = new MediaPipeExposure();
            for (int i = 0; i < 256; i++) Assert.AreEqual(i, exposure.Lut[i]);
            Assert.IsFalse(exposure.Active);
        }
        [Test]
        public void DarkFrame_IsBoostedTowardAUsableExposure()
        {
            MediaPipeExposure exposure = Metered(30);
            exposure.Update(true);
            Assert.IsTrue(exposure.Active);
            Assert.Greater(exposure.Gain, 1.5f);
            Assert.Greater(exposure.Lut[30], 90, "a face sitting at 12% grey must come out bright enough for the detector");
            Assert.Greater(exposure.Boost, 1.5f);
            Assert.That(exposure.Level, Is.EqualTo(30f / 255f).Within(1e-3f), "the reported light level is what the camera actually delivered, before any boost");
            for (int i = 1; i < 256; i++) Assert.GreaterOrEqual(exposure.Lut[i], exposure.Lut[i - 1], "the curve must never reorder tones");
            Assert.AreEqual(255, exposure.Lut[255]);
        }
        [Test]
        public void VeryDarkFrame_AlsoLiftsTheMidtones()
        {
            MediaPipeExposure exposure = Metered(8);
            exposure.Update(true);
            Assert.Less(exposure.Gamma, 0.99f, "gain alone tops out; the rest has to come from a gamma lift");
            Assert.GreaterOrEqual(exposure.Gamma, MediaPipeExposure.MinGamma - 1e-4f);
            Assert.LessOrEqual(exposure.Gain, MediaPipeExposure.MaxGain + 1e-4f);
            Assert.Greater(exposure.Lut[8], 40);
        }
        [Test]
        public void BrightFrame_IsLeftAlone()
        {
            MediaPipeExposure exposure = Metered(140);
            exposure.Update(true);
            Assert.IsFalse(exposure.Active);
            for (int i = 0; i < 256; i++) Assert.AreEqual(i, exposure.Lut[i]);
            Assert.That(exposure.Boost, Is.EqualTo(1f).Within(0.02f));
        }
        [Test]
        public void BoostOff_StillMetersButNeverTouchesPixels()
        {
            MediaPipeExposure exposure = Metered(20);
            exposure.Update(false);
            Assert.IsFalse(exposure.Active);
            Assert.That(exposure.Level, Is.EqualTo(20f / 255f).Within(1e-3f));
            for (int i = 0; i < 256; i++) Assert.AreEqual(i, exposure.Lut[i]);
        }
        [Test]
        public void ChangesSettleGraduallyAfterTheFirstFrame()
        {
            MediaPipeExposure exposure = Metered(30);
            exposure.Update(true);
            float dark = exposure.Gain;
            Meter(exposure, 140);
            exposure.Update(true);
            Assert.Greater(exposure.Gain, 1f + (dark - 1f) * 0.5f, "one bright frame must not slam the curve back, or the landmarks flicker with it");
            for (int i = 0; i < 60; i++)
            {
                Meter(exposure, 140);
                exposure.Update(true);
            }
            Assert.IsFalse(exposure.Active, "but a lasting change does settle");
        }
        [Test]
        public void EmptyMeterReportsNothing()
        {
            MediaPipeExposure exposure = new MediaPipeExposure();
            exposure.Begin();
            Assert.IsFalse(exposure.End());
            Assert.Less(exposure.Level, 0f);
            exposure.Update(true);
            Assert.IsFalse(exposure.Active);
        }
        [Test]
        public void Quality_MapsDarkToLowAndLitToFull()
        {
            Assert.AreEqual(1f, MediaPipeExposure.Quality(-1f));
            Assert.AreEqual(0f, MediaPipeExposure.Quality(0.02f));
            Assert.AreEqual(1f, MediaPipeExposure.Quality(0.5f));
            Assert.Greater(MediaPipeExposure.Quality(0.2f), 0.3f);
            Assert.Less(MediaPipeExposure.Quality(0.2f), 0.9f);
        }
    }
}
