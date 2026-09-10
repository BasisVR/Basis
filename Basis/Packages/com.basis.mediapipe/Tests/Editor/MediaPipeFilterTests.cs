using NUnit.Framework;
using UnityEngine;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeFilterTests
    {
        private const float Dt = 1f / 15f;
        private static float Limit => MediaPipeFilterMath.PositionLimit(MediaPipeFilterMath.MaxHandSpeed, Dt);
        private static float TurnLimit => MediaPipeFilterMath.TurnLimit(MediaPipeFilterMath.MaxTurnDegPerSec, Dt);
        [Test]
        public void SpikeGate_OneFrameSpikeNeverGetsThrough()
        {
            MediaPipeSpikeGateVec3 gate = default;
            Vector3 rest = new Vector3(0.1f, 0.2f, -0.3f);
            for (int i = 0; i < 5; i++) gate.Apply(rest + new Vector3(0.005f * (i % 2), 0f, 0f), Limit, Dt);
            Vector3 duringSpike = gate.Apply(rest + new Vector3(0.4f, 0f, 0f), Limit, Dt);
            Vector3 after = gate.Apply(rest, Limit, Dt);
            Assert.Less(Vector3.Distance(duringSpike, rest), 0.01f, "the spike sample must be held back");
            Assert.Less(Vector3.Distance(after, rest), 1e-4f, "and the next honest sample passes straight through");
            Assert.AreEqual(1, gate.Rejected);
        }
        [Test]
        public void SpikeGate_FastRealMoveArrivesOneSampleLateThenTracks()
        {
            MediaPipeSpikeGateVec3 gate = default;
            Vector3 start = Vector3.zero, step = new Vector3(0.25f, 0f, 0f);
            gate.Apply(start, Limit, Dt);
            Vector3 first = gate.Apply(start + step, Limit, Dt);
            Vector3 second = gate.Apply(start + step * 2f, Limit, Dt);
            Vector3 third = gate.Apply(start + step * 3f, Limit, Dt);
            Assert.Less(Vector3.Distance(first, start), 1e-4f, "the first sample of a big move is held for one frame");
            Assert.Less(Vector3.Distance(second, start + step * 2f), 1e-4f, "a move that persists is accepted on the second frame");
            Assert.Less(Vector3.Distance(third, start + step * 3f), 1e-4f, "and predicted velocity keeps a sustained move flowing");
            Assert.AreEqual(1, gate.Rejected);
        }
        [Test]
        public void SpikeGate_OrdinaryMotionIsUntouched()
        {
            MediaPipeSpikeGateVec3 gate = default;
            Vector3 p = Vector3.zero;
            gate.Apply(p, Limit, Dt);
            for (int i = 0; i < 30; i++)
            {
                p += new Vector3(0.05f, 0.02f, -0.03f);
                Assert.AreEqual(p, gate.Apply(p, Limit, Dt));
            }
            Assert.AreEqual(0, gate.Rejected);
        }
        [Test]
        public void SpikeGate_DisabledIsTransparent()
        {
            MediaPipeSpikeGateVec3 gate = default;
            Vector3 a = Vector3.zero, b = new Vector3(5f, 5f, 5f);
            gate.Apply(a, 0f, Dt);
            Assert.AreEqual(b, gate.Apply(b, 0f, Dt));
            Assert.AreEqual(0, gate.Rejected);
        }
        [Test]
        public void RotationGate_ChiralityFlipIsHeldUntilItPersists()
        {
            MediaPipeSpikeGateQuat gate = default;
            Quaternion rest = Quaternion.AngleAxis(10f, Vector3.up), flipped = rest * Quaternion.AngleAxis(180f, Vector3.forward);
            gate.Apply(rest, TurnLimit);
            Quaternion held = gate.Apply(flipped, TurnLimit);
            Quaternion back = gate.Apply(rest, TurnLimit);
            Assert.Less(Quaternion.Angle(held, rest), 0.01f, "a single-frame 180 degree roll is a mislabelled hand, not a wrist flip");
            Assert.Less(Quaternion.Angle(back, rest), 0.01f);
            gate.Apply(flipped, TurnLimit);
            Quaternion confirmed = gate.Apply(flipped, TurnLimit);
            Assert.Less(Quaternion.Angle(confirmed, flipped), 0.01f, "a flip that persists is real and must be accepted");
            Assert.AreEqual(2, gate.Rejected);
        }
        [Test]
        public void RotationGate_OrdinaryTurnPassesUntouched()
        {
            MediaPipeSpikeGateQuat gate = default;
            Quaternion q = Quaternion.identity;
            gate.Apply(q, TurnLimit);
            for (int i = 0; i < 10; i++)
            {
                q = q * Quaternion.AngleAxis(40f, Vector3.up);
                Assert.Less(Quaternion.Angle(gate.Apply(q, TurnLimit), q), 0.01f);
            }
            Assert.AreEqual(0, gate.Rejected);
        }
        private static float RunScalar(float renderRate, int samplesAfterStep)
        {
            MediaPipeScalarFilter filter = default;
            float value = 0f;
            int perSample = Mathf.RoundToInt(renderRate * Dt);
            for (int s = 0; s < 20 + samplesAfterStep; s++)
            {
                float target = s < 20 ? 0f : 1f;
                for (int r = 0; r < perSample; r++) value = filter.Apply(target, new MediaPipeTiming(1f / renderRate, Dt, r == 0), 3f, 1f);
            }
            return value;
        }
        [Test]
        public void ScalarFilter_SettlesTheSameAtAnyRenderRate()
        {
            float at60 = RunScalar(60f, 30), at240 = RunScalar(240f, 30);
            Assert.That(at60, Is.EqualTo(1f).Within(0.02f));
            Assert.That(at240, Is.EqualTo(at60).Within(0.02f), "smoothing lives on the camera clock, so the render rate must not change where it settles");
        }
        [Test]
        public void ScalarFilter_MovesAtTheSameSpeedAtAnyRenderRate()
        {
            float at60 = RunScalar(60f, 3), at240 = RunScalar(240f, 3);
            Assert.Greater(at60, 0.2f);
            Assert.Less(at60, 0.98f, "three camera samples in, the value should still be on its way");
            Assert.That(at240, Is.EqualTo(at60).Within(0.1f), "a faster render loop must not make the response faster");
        }
        [Test]
        public void ScalarFilter_RelaxEasesTowardTheTargetAndCarriesOnFromThere()
        {
            MediaPipeScalarFilter filter = default;
            MediaPipeTiming sample = new MediaPipeTiming(1f / 60f, Dt, true);
            for (int i = 0; i < 30; i++) filter.Apply(1f, sample, 3f, 1f);
            Assert.That(filter.Carried, Is.EqualTo(1f).Within(0.02f));
            float relaxed = filter.Relax(0f, 0.5f);
            Assert.That(relaxed, Is.EqualTo(0.5f).Within(0.02f));
            for (int i = 0; i < 20; i++) relaxed = filter.Relax(0f, 0.5f);
            Assert.Less(relaxed, 0.01f);
            float next = filter.Apply(1f, sample, 3f, 1f);
            Assert.Less(next, 0.9f, "a fresh sample continues from the relaxed value instead of snapping back to the old one");
            Assert.Greater(next, 0.05f);
        }
        [Test]
        public void ScalarFilter_RelaxWithNoHistoryStartsAtTheTarget()
        {
            MediaPipeScalarFilter filter = default;
            Assert.AreEqual(0.6f, filter.Relax(0.6f, 0.3f));
            Assert.IsTrue(filter.HasSample);
        }
        [Test]
        public void ScalarFilter_CarryWithoutASampleIsZero()
        {
            MediaPipeScalarFilter filter = default;
            Assert.AreEqual(0f, filter.Carry(new MediaPipeTiming(1f / 60f, Dt, false)));
        }
        [Test]
        public void PositionFilter_HoldsStillWhenTheSampleHoldsStill()
        {
            MediaPipePositionFilter filter = default;
            Vector3 p = new Vector3(0.3f, -0.1f, 0.2f);
            Vector3 last = Vector3.zero;
            for (int s = 0; s < 40; s++) for (int r = 0; r < 4; r++) last = filter.Apply(p, new MediaPipeTiming(1f / 60f, Dt, r == 0), 3f, 3.25f, 0.4f, MediaPipeFilterMath.MaxHandSpeed);
            Assert.Less(Vector3.Distance(last, p), 1e-3f);
            Assert.AreEqual(0, filter.Rejected);
        }
        [Test]
        public void PresenceFade_SnapsInFirstThenHoldsThenFades()
        {
            MediaPipePresenceFade fade = default;
            Assert.AreEqual(1f, fade.Step(true, 1f / 60f, 0.5f, 6f, 4f), "first acquisition is immediate");
            float during = 1f;
            for (int i = 0; i < 24; i++) during = fade.Step(false, 1f / 60f, 0.5f, 6f, 4f);
            Assert.AreEqual(1f, during, "a loss shorter than the hold changes nothing");
            float faded = during;
            for (int i = 0; i < 120; i++)
            {
                float next = fade.Step(false, 1f / 60f, 0.5f, 6f, 4f);
                Assert.LessOrEqual(next, faded + 1e-6f, "the fade must be monotonic");
                faded = next;
            }
            Assert.AreEqual(0f, faded, "and end at zero");
            float back = fade.Step(true, 1f / 60f, 0.5f, 6f, 4f);
            Assert.Greater(back, 0f);
            Assert.Less(back, 0.5f, "re-acquiring after a fade eases back in instead of snapping");
        }
        [Test]
        public void PresenceFade_ABriefFlickerNeverShows()
        {
            MediaPipePresenceFade fade = default;
            fade.Step(true, 1f / 60f, 0.5f, 6f, 4f);
            for (int i = 0; i < 6; i++) Assert.AreEqual(1f, fade.Step(false, 1f / 60f, 0.5f, 6f, 4f));
            Assert.AreEqual(1f, fade.Step(true, 1f / 60f, 0.5f, 6f, 4f));
        }
    }
}
