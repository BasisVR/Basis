using NUnit.Framework;
using UnityEngine;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeNeutralSamplerTests
    {
        [Test]
        public void AStillHeadProducesANeutralAfterEnoughSamples()
        {
            MediaPipeNeutralSampler sampler = new MediaPipeNeutralSampler();
            Quaternion held = Quaternion.Euler(-8f, 3f, 1f);
            Vector3 at = new Vector3(2f, -1f, 45f);
            for (int i = 0; i < MediaPipeNeutralSampler.Required - 1; i++) Assert.IsFalse(sampler.Add(held, at));
            Assert.IsTrue(sampler.Add(held, at));
            Assert.Less(Quaternion.Angle(sampler.Rotation, held), 0.01f);
            Assert.Less(Vector3.Distance(sampler.Position, at), 1e-4f);
            Assert.AreEqual(0, sampler.Count, "ready to sample again");
        }
        [Test]
        public void MovingTheHeadStartsOver()
        {
            MediaPipeNeutralSampler sampler = new MediaPipeNeutralSampler();
            for (int i = 0; i < 10; i++) sampler.Add(Quaternion.identity, Vector3.zero);
            Assert.AreEqual(10, sampler.Count);
            sampler.Add(Quaternion.Euler(0f, 10f, 0f), Vector3.zero);
            Assert.AreEqual(1, sampler.Count, "a turn throws the run away and keeps only the new sample");
            for (int i = 0; i < 10; i++) sampler.Add(Quaternion.Euler(0f, 10f, 0f), Vector3.zero);
            sampler.Add(Quaternion.Euler(0f, 10f, 0f), new Vector3(5f, 0f, 0f));
            Assert.AreEqual(1, sampler.Count, "so does sliding sideways");
        }
        [Test]
        public void SmallJitterAveragesOut()
        {
            MediaPipeNeutralSampler sampler = new MediaPipeNeutralSampler();
            bool ready = false;
            for (int i = 0; i < MediaPipeNeutralSampler.Required; i++)
            {
                float wobble = (i % 2 == 0 ? 1f : -1f) * 0.8f;
                ready = sampler.Add(Quaternion.Euler(0f, wobble, 0f), new Vector3(wobble * 0.5f, 0f, 0f));
            }
            Assert.IsTrue(ready);
            Assert.Less(Quaternion.Angle(sampler.Rotation, Quaternion.identity), 0.2f);
            Assert.Less(Mathf.Abs(sampler.Position.x), 0.1f);
        }
    }
}
