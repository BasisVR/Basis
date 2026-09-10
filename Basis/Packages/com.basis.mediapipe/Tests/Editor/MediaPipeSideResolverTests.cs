using NUnit.Framework;
using UnityEngine;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeSideResolverTests
    {
        [Test]
        public void SideLatch_FirstCallDecidesImmediately()
        {
            MediaPipeSideLatch latch = default;
            Assert.IsTrue(latch.Update(1f));
        }
        [Test]
        public void SideLatch_OneContradictingFrameIsIgnored()
        {
            MediaPipeSideLatch latch = default;
            latch.Update(-1f);
            Assert.IsFalse(latch.Update(1f), "a single frame is a blown shoulder landmark, not a person turning round");
            Assert.IsFalse(latch.Update(-1f));
            Assert.IsFalse(latch.Update(1f));
            Assert.IsFalse(latch.Update(1f));
        }
        [Test]
        public void SideLatch_APersistentContradictionFlips()
        {
            MediaPipeSideLatch latch = default;
            latch.Update(-1f);
            latch.Update(1f);
            latch.Update(1f);
            Assert.IsTrue(latch.Update(1f));
        }
        [Test]
        public void SideLatch_EdgeOnHoldsTheLastCall()
        {
            MediaPipeSideLatch latch = default;
            latch.Update(1f);
            Assert.IsTrue(latch.Update(0f));
            Assert.IsTrue(latch.Update(0f));
        }
        private static Vector3[] Pose(Vector2 leftWrist, Vector2 rightWrist)
        {
            Vector3[] pose = new Vector3[MediaPipeSpace.PoseCount];
            pose[MediaPipeSpace.LeftWrist] = leftWrist;
            pose[MediaPipeSpace.RightWrist] = rightWrist;
            return pose;
        }
        private static Vector3[] Hand(Vector2 wrist)
        {
            Vector3[] hand = new Vector3[MediaPipeSpace.HandCount];
            hand[MediaPipeSpace.HandWrist] = wrist;
            return hand;
        }
        [Test]
        public void HandSides_ThePoseWristsWinOverTheLabel()
        {
            MediaPipeHandSideResolver resolver = new MediaPipeHandSideResolver();
            Vector3[] pose = Pose(new Vector2(0.7f, 0.5f), new Vector2(0.3f, 0.5f));
            Assert.IsTrue(resolver.Resolve(pose, Hand(new Vector2(0.72f, 0.5f)), null, false, 1));
            Assert.IsFalse(resolver.Resolve(pose, Hand(new Vector2(0.28f, 0.5f)), null, true, 1));
        }
        [Test]
        public void HandSides_TwoHandsAreAssignedAsAPair()
        {
            MediaPipeHandSideResolver resolver = new MediaPipeHandSideResolver();
            Vector3[] pose = Pose(new Vector2(0.7f, 0.5f), new Vector2(0.3f, 0.5f));
            Assert.IsFalse(resolver.Resolve(pose, Hand(new Vector2(0.31f, 0.5f)), Hand(new Vector2(0.69f, 0.5f)), true, 2));
            Assert.IsTrue(resolver.Resolve(pose, Hand(new Vector2(0.69f, 0.5f)), Hand(new Vector2(0.31f, 0.5f)), false, 2));
        }
        [Test]
        public void HandSides_AnAmbiguousPoseFallsBackToWhereTheHandWasLastFrame()
        {
            MediaPipeHandSideResolver resolver = new MediaPipeHandSideResolver();
            Vector3[] pose = Pose(new Vector2(0.7f, 0.5f), new Vector2(0.3f, 0.5f));
            Assert.IsTrue(resolver.Resolve(pose, Hand(new Vector2(0.7f, 0.5f)), null, false, 1));
            Vector3[] crossed = Pose(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            Assert.IsTrue(resolver.Resolve(crossed, Hand(new Vector2(0.68f, 0.52f)), null, false, 1), "the pose cannot tell, but this hand is where the left hand was a frame ago");
        }
        [Test]
        public void HandSides_NoPoseAndNoHistoryTrustsTheLabel()
        {
            MediaPipeHandSideResolver resolver = new MediaPipeHandSideResolver();
            Assert.IsTrue(resolver.Resolve(null, Hand(new Vector2(0.5f, 0.5f)), null, true, 1));
            Assert.IsFalse(resolver.Resolve(null, Hand(new Vector2(0.1f, 0.1f)), null, false, 1));
        }
        [Test]
        public void HandSides_HistoryExpires()
        {
            MediaPipeHandSideResolver resolver = new MediaPipeHandSideResolver();
            Vector3[] pose = Pose(new Vector2(0.7f, 0.5f), new Vector2(0.3f, 0.5f));
            resolver.Resolve(pose, Hand(new Vector2(0.7f, 0.5f)), null, false, 1);
            for (int i = 0; i < MediaPipeHandSideResolver.ForgetAfter + 1; i++) resolver.Resolve(null, Hand(new Vector2(0.05f, 0.05f)), null, false, 1);
            Assert.IsFalse(resolver.Resolve(null, Hand(new Vector2(0.7f, 0.5f)), null, false, 1), "a memory that old says nothing about a hand appearing there now");
        }
    }
}
