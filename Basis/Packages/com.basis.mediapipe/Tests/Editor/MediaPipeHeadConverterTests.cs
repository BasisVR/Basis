using NUnit.Framework;
using UnityEngine;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeHeadConverterTests
    {
        private const float Render = 1f / 60f, Sample = 1f / 15f;
        private static BasisMediaPipeResult Face(Quaternion rotation, Vector3 translationCm) => new BasisMediaPipeResult { HasFace = true, FaceTransform = Matrix4x4.TRS(translationCm, rotation, Vector3.one) };
        private static MediaPipeHeadConverter Converter() => new MediaPipeHeadConverter { Smoothing = 0f, InvertPitch = false, InvertYaw = false, InvertRoll = false };
        private static bool Run(MediaPipeHeadConverter converter, in BasisMediaPipeResult result, int samples, out Quaternion rotation, out Vector3 position)
        {
            rotation = Quaternion.identity;
            position = Vector3.zero;
            bool tracked = false;
            for (int s = 0; s < samples; s++)
            {
                for (int r = 0; r < 4; r++) tracked = converter.TryGetHeadOffset(in result, new MediaPipeTiming(Render, Sample, r == 0), out rotation, out position);
            }
            return tracked;
        }
        [Test]
        public void TurningYourHead_TurnsTheOffsetTheSameWay()
        {
            MediaPipeHeadConverter converter = Converter();
            BasisMediaPipeResult neutral = Face(Quaternion.identity, Vector3.zero);
            converter.Calibrate(neutral);
            Run(converter, neutral, 5, out _, out _);
            Assert.IsTrue(Run(converter, Face(Quaternion.AngleAxis(20f, Vector3.up), new Vector3(5f, 0f, 0f)), 30, out Quaternion rotation, out Vector3 position));
            Assert.Less(Quaternion.Angle(rotation, Quaternion.AngleAxis(20f, Vector3.up)), 1f);
            Assert.That(position.x, Is.EqualTo(-0.05f).Within(0.002f), "5 cm of camera-space head shift is 5 cm of mirrored player-local shift");
            Assert.That(position.y, Is.EqualTo(0f).Within(1e-4f));
        }
        [Test]
        public void LosingTheFace_HoldsThenFadesBackToNeutralInsteadOfSnapping()
        {
            MediaPipeHeadConverter converter = Converter();
            converter.Calibrate(Face(Quaternion.identity, Vector3.zero));
            Run(converter, Face(Quaternion.AngleAxis(20f, Vector3.up), new Vector3(5f, 0f, 0f)), 30, out Quaternion tracked, out Vector3 trackedPosition);
            BasisMediaPipeResult lost = default;
            Assert.IsTrue(Run(converter, lost, 5, out Quaternion held, out Vector3 heldPosition));
            Assert.Less(Quaternion.Angle(held, tracked), 0.5f, "a brief dropout must not move the head at all");
            Assert.Less(Vector3.Distance(heldPosition, trackedPosition), 1e-3f);
            float previousAngle = Quaternion.Angle(held, Quaternion.identity), previousShift = heldPosition.magnitude;
            bool alive = true;
            int frames = 0;
            for (; frames < 240 && alive; frames++)
            {
                alive = converter.TryGetHeadOffset(in lost, new MediaPipeTiming(Render, Sample, frames % 4 == 0), out Quaternion fading, out Vector3 shift);
                float angle = Quaternion.Angle(fading, Quaternion.identity);
                Assert.LessOrEqual(angle, previousAngle + 0.01f, "the return to neutral must be a fade, never a jump");
                Assert.LessOrEqual(shift.magnitude, previousShift + 1e-4f);
                previousAngle = angle;
                previousShift = shift.magnitude;
            }
            Assert.IsFalse(alive, "after the fade the tracker is released");
            Assert.Greater(frames, 20, "and not before the hold has run out");
        }
        [Test]
        public void TheFaceComingBack_EasesInInsteadOfSnapping()
        {
            MediaPipeHeadConverter converter = Converter();
            converter.Calibrate(Face(Quaternion.identity, Vector3.zero));
            BasisMediaPipeResult turned = Face(Quaternion.AngleAxis(30f, Vector3.up), Vector3.zero);
            Run(converter, turned, 10, out _, out _);
            Run(converter, default, 60, out _, out _);
            Assert.IsTrue(converter.TryGetHeadOffset(in turned, new MediaPipeTiming(Render, Sample, true), out Quaternion first, out _));
            Assert.Less(Quaternion.Angle(first, Quaternion.identity), 15f, "re-acquisition must ramp back in");
            Run(converter, turned, 30, out Quaternion settled, out _);
            Assert.Less(Quaternion.Angle(settled, Quaternion.AngleAxis(30f, Vector3.up)), 1f);
        }
        [Test]
        public void AOneFrameHeadJump_IsRejected()
        {
            MediaPipeHeadConverter converter = Converter();
            BasisMediaPipeResult neutral = Face(Quaternion.identity, Vector3.zero);
            converter.Calibrate(neutral);
            Run(converter, neutral, 10, out _, out _);
            converter.TryGetHeadOffset(Face(Quaternion.AngleAxis(120f, Vector3.up), Vector3.zero), new MediaPipeTiming(Render, Sample, true), out Quaternion spike, out _);
            Assert.Less(Quaternion.Angle(spike, Quaternion.identity), 1f, "a 120 degree turn between two camera frames is a glitch");
            Run(converter, neutral, 3, out Quaternion after, out _);
            Assert.Less(Quaternion.Angle(after, Quaternion.identity), 1f);
            Assert.AreEqual(1, converter.RejectedSamples);
        }
        [Test]
        public void GlitchRejectionOff_LetsTheJumpThrough()
        {
            MediaPipeHeadConverter converter = Converter();
            converter.RejectGlitches = false;
            BasisMediaPipeResult neutral = Face(Quaternion.identity, Vector3.zero);
            converter.Calibrate(neutral);
            Run(converter, neutral, 10, out _, out _);
            converter.TryGetHeadOffset(Face(Quaternion.AngleAxis(120f, Vector3.up), Vector3.zero), new MediaPipeTiming(Render, Sample, true), out Quaternion spike, out _);
            Assert.Greater(Quaternion.Angle(spike, Quaternion.identity), 10f);
            Assert.AreEqual(0, converter.RejectedSamples);
        }
        [Test]
        public void ADegenerateFaceTransform_CountsAsNoFace()
        {
            MediaPipeHeadConverter converter = Converter();
            BasisMediaPipeResult broken = new BasisMediaPipeResult { HasFace = true };
            Assert.IsFalse(converter.TryGetHeadOffset(in broken, new MediaPipeTiming(Render, Sample, true), out _, out _));
        }
    }
}
