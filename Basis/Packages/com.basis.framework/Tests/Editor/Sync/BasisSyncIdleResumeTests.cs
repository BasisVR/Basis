using Basis.Scripts.Networking.Sync;
using NUnit.Framework;
using static Basis.Tests.Sync.BasisSyncTestSupport;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Regression tests for the "grabbed pickup lags for seconds after sitting idle" bug: an idle sender's
    /// backed-off keyframes used to stack up as multi-second interpolation windows that playback then crossed
    /// in real time (capped at 1.35x), so the first motion after a grab trailed far behind the freshest
    /// received data before rushing to catch up. Covers the static-window skip, the gap-window clamp (with
    /// extrapolation suppressed across it), the idle depth-ratchet guard, and the receiver reset that
    /// <see cref="BasisSyncedObject.OnOwnershipTransfer"/> performs so a new owner's unrelated sequence
    /// numbering is accepted immediately instead of landing in the anti-reorder drop zone.
    /// </summary>
    public class BasisSyncIdleResumeTests
    {
        // Unclamped Current->Next lerp, mirroring what InterpolateSyncObjectsJob renders.
        static float Output(BasisSyncReceiver r)
        {
            float from = r.CurrentValues.Cont[0];
            float to = r.NextValues.Cont[0];
            return from + (to - from) * r.InterpTime;
        }

        [Test]
        public void Receiver_GrabAfterIdle_ResumesWithinNominalLatency()
        {
            var (s, v) = Single(BasisSyncFieldType.Float);
            var r = new BasisSyncReceiver(s);
            r.Configure(true, 0.2, false, 0f, 0, 0); // pickups run with extrapolation on

            byte seq = 0;
            for (int i = 0; i < 20; i++) // active motion at 20 Hz
            {
                v.Cont[0] = i * 0.1f;
                FeedKeyframe(r, s, v, ++seq, ms: 50);
                r.Advance(Dt);
            }
            for (int k = 0; k < 4; k++) // at rest: unchanged keyframes with maxed-out backoff stamps
            {
                FeedKeyframe(r, s, v, ++seq, ms: 8000);
                Advance(r, 576); // ~8 s of wall time between refreshes
            }

            // Grab: the first packet after the idle stretch carries a raw-gap stamp (a pre-fix sender
            // re-owning the object stamped up to 65535 ms), then motion streams at 20 Hz again.
            float val = 5f;
            v.Cont[0] = val;
            FeedKeyframe(r, s, v, ++seq, ms: 65535);
            for (int i = 0; i < 9; i++)
            {
                Advance(r, 4);
                val += 0.05f;
                v.Cont[0] = val;
                FeedKeyframe(r, s, v, ++seq, ms: 50);
            }
            Advance(r, 4);

            Assert.Greater(Output(r), 4.99f,
                "~0.5 s after the grab the output must track the fresh motion, not still be crossing the idle-era windows");
        }

        [Test]
        public void Receiver_IdenticalIdleFrames_CollapseInsteadOfQueueing()
        {
            var (s, v) = Single(BasisSyncFieldType.Float);
            var r = new BasisSyncReceiver(s);

            v.Cont[0] = 1f;
            for (byte i = 1; i <= 6; i++) FeedKeyframe(r, s, v, i, ms: 8000);
            r.Advance(Dt);
            Assert.AreEqual(0, r.BufferedFrameCount, "identical frames collapse instead of queueing as playback backlog");

            v.Cont[0] = 3f;
            FeedKeyframe(r, s, v, 7, ms: 50);
            Advance(r, 8);
            Assert.AreEqual(3f, Output(r), 0.05f, "motion after an idle stretch plays immediately, not behind the idle frames");
        }

        [Test]
        public void Receiver_HugeIntervalStamp_CrossesAtPacketPace_WithoutOvershoot()
        {
            var (s, v) = Single(BasisSyncFieldType.Float);
            var r = new BasisSyncReceiver(s);
            r.Configure(true, 0.2, false, 0f, 0, 0);

            v.Cont[0] = 1f; FeedKeyframe(r, s, v, 1, ms: 50); r.Advance(Dt);
            v.Cont[0] = 10f; FeedKeyframe(r, s, v, 2, ms: 65535);

            float peak = float.MinValue;
            for (int i = 0; i < 36; i++)
            {
                r.Advance(Dt);
                float o = Output(r);
                if (o > peak) peak = o;
            }

            Assert.AreEqual(10f, Output(r), 0.05f, "a resume gap is crossed at packet pace, not replayed in real time");
            Assert.LessOrEqual(peak, 10.01f, "extrapolation must not run along a collapsed gap window's garbage slope");
        }

        [Test]
        public void Receiver_IdleStarvation_DoesNotInflateJitterDepth()
        {
            var (s, v) = Single(BasisSyncFieldType.Float);
            var r = new BasisSyncReceiver(s);

            byte seq = 0;
            for (int i = 0; i < 8; i++) // brief motion, then rest
            {
                v.Cont[0] = i * 0.1f;
                FeedKeyframe(r, s, v, ++seq, ms: 50);
                Advance(r, 4);
            }

            // Rest: the sender goes quiet, then refreshes unchanged keyframes on the real backoff ramp.
            // Arrival gaps match the stamps, so the receiver starves between refreshes.
            int[] stampMs = { 500, 1000, 2000, 4000 };
            foreach (int ms in stampMs)
            {
                Advance(r, (int)(ms / 1000f / Dt));
                FeedKeyframe(r, s, v, ++seq, ms: (ushort)ms);
                r.Advance(Dt);
            }

            Assert.LessOrEqual(r.DynamicDepth, 2.01f,
                "idle refreshes are not network jitter; the depth target must relax to its floor instead of ratcheting toward 4");
        }

        [Test]
        public void Receiver_ResetForNewOwner_AcceptsNewSequenceImmediately()
        {
            var (s, v) = Single(BasisSyncFieldType.Float);
            var stale = new BasisSyncReceiver(s); // still keyed to the previous owner's sequence
            var reset = new BasisSyncReceiver(s); // what OnOwnershipTransfer now does on observers

            v.Cont[0] = 1f;
            for (byte i = 1; i <= 200; i++)
            {
                FeedKeyframe(stale, s, v, i);
                FeedKeyframe(reset, s, v, i);
                stale.Advance(Dt);
                reset.Advance(Dt);
            }

            reset.Reset();
            v.Cont[0] = 42f; // new owner's stream starts at its own unrelated sequence number
            FeedKeyframe(stale, s, v, 100);
            FeedKeyframe(reset, s, v, 100);
            stale.Advance(Dt);
            reset.Advance(Dt);

            Assert.AreEqual(1f, stale.NextValues.Cont[0], 1e-3f,
                "without the reset the new owner's sequence lands in the anti-reorder drop zone and is discarded");
            Assert.IsTrue(reset.HasData);
            Assert.AreEqual(42f, reset.NextValues.Cont[0], 1e-3f,
                "after the ownership reset the new owner's stream seeds immediately");
        }
    }
}
