using NUnit.Framework;
using UnityEngine;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeGazeTests
    {
        private static readonly Vector2 Left = new Vector2(0.40f, 0.50f), Right = new Vector2(0.50f, 0.50f), Upper = new Vector2(0.45f, 0.48f), Lower = new Vector2(0.45f, 0.52f);
        [Test]
        public void ACentredIrisLooksStraightAhead()
        {
            Assert.IsTrue(MediaPipeGaze.TryMeasure(Left, Right, Upper, Lower, new Vector2(0.45f, 0.50f), 1f, out Vector2 gaze));
            Assert.That(gaze.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(gaze.y, Is.EqualTo(0f).Within(1e-4f));
        }
        [Test]
        public void AnIrisTowardTheImageRightCornerIsPositiveX()
        {
            Assert.IsTrue(MediaPipeGaze.TryMeasure(Left, Right, Upper, Lower, new Vector2(0.475f, 0.50f), 1f, out Vector2 gaze));
            Assert.That(gaze.x, Is.EqualTo(0.5f).Within(1e-3f), "half way from the centre to the corner");
        }
        [Test]
        public void AnIrisAboveTheCentreIsPositiveY()
        {
            Assert.IsTrue(MediaPipeGaze.TryMeasure(Left, Right, Upper, Lower, new Vector2(0.45f, 0.49f), 1f, out Vector2 gaze));
            Assert.That(gaze.y, Is.EqualTo(0.2f).Within(1e-3f), "image y runs down, so a smaller y is up");
        }
        [Test]
        public void AClosedEyeReportsNoGaze()
        {
            Assert.IsFalse(MediaPipeGaze.TryMeasure(Left, Right, new Vector2(0.45f, 0.498f), new Vector2(0.45f, 0.502f), new Vector2(0.45f, 0.50f), 1f, out _));
        }
        [Test]
        public void HorizontalGazeIsIndependentOfTheFrameAspect()
        {
            Assert.IsTrue(MediaPipeGaze.TryMeasure(Left, Right, Upper, Lower, new Vector2(0.475f, 0.50f), 16f / 9f, out Vector2 wide));
            Assert.IsTrue(MediaPipeGaze.TryMeasure(Left, Right, Upper, Lower, new Vector2(0.475f, 0.50f), 4f / 3f, out Vector2 narrow));
            Assert.That(wide.x, Is.EqualTo(narrow.x).Within(1e-4f));
        }
        [Test]
        public void ARolledHeadStillMeasuresAlongTheEyeLine()
        {
            Vector2 a = new Vector2(0.45f, 0.45f), b = new Vector2(0.45f, 0.55f), upper = new Vector2(0.43f, 0.50f), lower = new Vector2(0.47f, 0.50f);
            Assert.IsTrue(MediaPipeGaze.TryMeasure(a, b, upper, lower, new Vector2(0.45f, 0.525f), 1f, out Vector2 gaze));
            Assert.That(gaze.x, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(gaze.y, Is.EqualTo(0f).Within(1e-3f));
        }
        [Test]
        public void DegenerateCornersAreRejected()
        {
            Assert.IsFalse(MediaPipeGaze.TryMeasure(Left, Left, Upper, Lower, Left, 1f, out _));
        }
    }
}
