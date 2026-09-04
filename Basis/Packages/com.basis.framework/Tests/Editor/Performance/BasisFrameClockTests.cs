using Basis.Scripts.Drivers;
using NUnit.Framework;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// The shared per-frame pump that panels, mirrors and the dolly manager subscribe to instead of
    /// each running their own Update. It is refcounted on purpose: the moment nothing needs it, it
    /// stops ticking entirely rather than firing an empty event every frame, so an idle client pays
    /// nothing for features nobody has open.
    ///
    /// The refcount is what these tests are really about. A leaked AddRequest keeps the whole chain
    /// alive forever, and a double RemoveRequest would let one closed panel silence another that is
    /// still open.
    /// </summary>
    public class BasisFrameClockTests
    {
        int ticks;

        void CountTick() => ticks++;

        [SetUp]
        public void Drain()
        {
            // Nothing else runs in EditMode, but the refcount is a static shared with the whole
            // player, so start from a known floor rather than assuming it.
            for (int index = 0; index < 64; index++)
            {
                BasisFrameClock.RemoveRequest();
            }
            ticks = 0;
            BasisFrameClock.OnTick += CountTick;
        }

        [TearDown]
        public void Release()
        {
            BasisFrameClock.OnTick -= CountTick;
            for (int index = 0; index < 64; index++)
            {
                BasisFrameClock.RemoveRequest();
            }
        }

        [Test]
        public void WithNoSubscribersItDoesNotTick()
        {
            BasisFrameClock.Tick(1f / 60f);
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.Zero, "an idle client must not pay for a pump nothing is using.");
        }

        [Test]
        public void OneRequestStartsTheTick()
        {
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(1));
        }

        [Test]
        public void TheTickStopsOnlyWhenTheLastRequestIsReleased()
        {
            BasisFrameClock.AddRequest();
            BasisFrameClock.AddRequest();

            BasisFrameClock.RemoveRequest();
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(1), "one panel closing must not silence another still open.");

            BasisFrameClock.RemoveRequest();
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(1));
        }

        [Test]
        public void ExtraReleasesCannotDriveTheCountNegative()
        {
            // Otherwise a double-release would need matching extra AddRequests before the
            // clock ever started again, which reads as "the panel is dead" to the user.
            BasisFrameClock.RemoveRequest();
            BasisFrameClock.RemoveRequest();
            BasisFrameClock.RemoveRequest();

            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(1));
        }

        [Test]
        public void TheFirstSampleSeedsTheAverageInsteadOfEasingIntoIt()
        {
            // Easing up from zero would show a wrong frame rate for the first second of
            // every panel that opens.
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 90f);

            Assert.That(BasisFrameClock.SmoothedFramesPerSecond, Is.EqualTo(90f).Within(0.01f));
        }

        [Test]
        public void LaterSamplesAreSmoothed()
        {
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 100f);
            BasisFrameClock.Tick(1f / 10f);

            // One tenth of the way from 10 ms toward 100 ms.
            float expectedDelta = 0.01f + (0.1f - 0.01f) * 0.1f;
            Assert.That(BasisFrameClock.SmoothedFramesPerSecond, Is.EqualTo(1f / expectedDelta).Within(0.05f),
                "a single long frame must not make the readout collapse.");
        }

        [Test]
        public void RepeatedSamplesConvergeOnTheRealRate()
        {
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 100f);
            for (int index = 0; index < 200; index++)
            {
                BasisFrameClock.Tick(1f / 30f);
            }

            Assert.That(BasisFrameClock.SmoothedFramesPerSecond, Is.EqualTo(30f).Within(0.1f));
        }

        [Test]
        public void ANonPositiveDeltaIsIgnoredButStillTicksSubscribers()
        {
            // The editor hands out a zero delta on the frame a domain reload finishes.
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 60f);
            float seeded = BasisFrameClock.SmoothedFramesPerSecond;

            BasisFrameClock.Tick(0f);
            BasisFrameClock.Tick(-1f);

            Assert.That(BasisFrameClock.SmoothedFramesPerSecond, Is.EqualTo(seeded).Within(1e-4f));
            Assert.That(ticks, Is.EqualTo(3), "subscribers still need their frame even when the delta is unusable.");
        }

        [Test]
        public void ReopeningAfterEveryoneLeftStartsFromTheNewRate()
        {
            // The smoothing is reset on release, so a panel opened after a long pause does not
            // inherit whatever the frame rate happened to be when the last one closed.
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 20f);
            BasisFrameClock.RemoveRequest();

            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 144f);

            Assert.That(BasisFrameClock.SmoothedFramesPerSecond, Is.EqualTo(144f).Within(0.05f));
        }
    }
}
