using HVR.Basis.Comms.OSC;
using NUnit.Framework;

namespace HVR.Basis.Comms.Tests
{
    public class BasisEyeLidPlausibilityTests
    {
        private const float Step = 1f / 60f;

        private static void Run(BasisEyeLidPlausibility sut, ref float now, float seconds, System.Func<float, float> lidAt, bool rightDead = false)
        {
            float end = now + seconds;
            while (now < end)
            {
                float lid = lidAt(now);
                sut.LidSample(now, true, !rightDead, lid);
                if (rightDead) sut.LidSample(now, false, true, 0f);
                sut.Tick(now, Step);
                now += Step;
            }
        }

        private static float RealTracker(float t) => t % 4f < 0.2f ? 0f : 0.75f;

        [Test]
        public void It_should_accept_a_tracker_that_supplies_real_lids()
        {
            var sut = new BasisEyeLidPlausibility();
            float now = 0f;
            Run(sut, ref now, 10f, RealTracker);
            Assert.That(sut.Valid, Is.True);
        }

        [Test]
        public void It_should_keep_real_lids_through_a_long_eye_close()
        {
            var sut = new BasisEyeLidPlausibility();
            float now = 0f;
            Run(sut, ref now, 20f, RealTracker);
            Run(sut, ref now, 1.5f, _ => 0f);
            Assert.That(sut.Valid, Is.True);
        }

        [Test]
        public void It_should_reject_the_recorded_frame_pattern()
        {
            var sut = new BasisEyeLidPlausibility();
            float now = 0f;
            float[] openLeft = { 0f, 0f, 0.4f, 0f, 0.07f, 0f, 0.16f, 0f, 0.15f, 0.3f, 0.29f, 0.33f, 0.02f, 0.21f, 0f, 0f, 0f, 0f, 0.6f, 0.54f, 0.55f, 0.48f, 0f, 0.62f, 0.57f, 0.56f, 0.58f, 0.53f, 0.64f, 0.64f, 0.8f, 0.63f, 0.26f, 0.22f, 0.24f, 0.52f, 0f, 0f, 0.04f, 0.28f, 0.18f, 0f, 0.25f, 0.29f, 0.29f, 0.24f, 0.22f, 0.04f };
            bool everValid = false;
            for (int pass = 0; pass < 3; pass++)
            {
                Run(sut, ref now, openLeft.Length * 0.4f, t => openLeft[(int)(t / 0.4f) % openLeft.Length] * 0.75f, rightDead: true);
                everValid |= sut.Valid;
            }
            Assert.That(everValid, Is.False);
        }

        [Test]
        public void It_should_reject_lids_stuck_closed()
        {
            var sut = new BasisEyeLidPlausibility();
            float now = 0f;
            Run(sut, ref now, 30f, _ => 0f);
            Assert.That(sut.Valid, Is.False);
        }

        [Test]
        public void It_should_stay_invalid_when_the_source_sends_no_lids()
        {
            var sut = new BasisEyeLidPlausibility();
            for (float now = 0f; now < 30f; now += Step)
            {
                sut.FaceSample(now);
                sut.Tick(now, Step);
            }
            Assert.That(sut.Valid, Is.False);
        }

        [Test]
        public void It_should_start_over_after_the_source_goes_silent()
        {
            var sut = new BasisEyeLidPlausibility();
            float now = 0f;
            Run(sut, ref now, 10f, RealTracker);
            Assert.That(sut.Valid, Is.True);
            now += BasisEyeLidPlausibility.SourceSilenceSeconds + 0.1f;
            sut.Tick(now, Step);
            Assert.That(sut.Valid, Is.False);
            Run(sut, ref now, 30f, _ => 0f);
            Assert.That(sut.Valid, Is.False);
        }
    }
}
