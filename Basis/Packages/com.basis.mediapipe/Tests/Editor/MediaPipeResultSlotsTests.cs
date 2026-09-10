using System.Collections.Generic;
using NUnit.Framework;
namespace Basis.MediaPipe.Tests
{
    public class MediaPipeResultSlotsTests
    {
        [Test]
        public void NothingToTakeBeforeAPublish()
        {
            MediaPipeResultSlots slots = new MediaPipeResultSlots();
            Assert.IsFalse(slots.TryTake(out _));
            slots.BeginWrite().Result.TimestampMs = 5;
            Assert.IsFalse(slots.TryTake(out _), "a slot being written is not visible yet");
            slots.Publish();
            Assert.IsTrue(slots.TryTake(out BasisMediaPipeResult taken));
            Assert.AreEqual(5, taken.TimestampMs);
            Assert.IsFalse(slots.TryTake(out _), "each publish is taken once");
        }
        [Test]
        public void TheWriterNeverGetsTheSlotTheReaderHolds()
        {
            MediaPipeResultSlots slots = new MediaPipeResultSlots();
            slots.BeginWrite().Result.TimestampMs = 1;
            slots.Publish();
            Assert.IsTrue(slots.TryTake(out _));
            int held = slots.Consumed;
            for (int i = 0; i < 40; i++)
            {
                MediaPipeResultSlot writing = slots.BeginWrite();
                Assert.AreNotEqual(held, slots.Writing, "the reader is still using its arrays");
                Assert.AreNotEqual(slots.Published, slots.Writing, "and a published, untaken result must not be overwritten");
                writing.Result.TimestampMs = i + 2;
                slots.Publish();
                if (i % 3 == 0)
                {
                    Assert.IsTrue(slots.TryTake(out BasisMediaPipeResult taken));
                    Assert.AreEqual(i + 2, taken.TimestampMs, "the reader always gets the newest publish");
                    held = slots.Consumed;
                }
            }
        }
        [Test]
        public void SlotsAreRecycledNotAllocated()
        {
            MediaPipeResultSlots slots = new MediaPipeResultSlots();
            HashSet<MediaPipeResultSlot> seen = new HashSet<MediaPipeResultSlot>();
            for (int i = 0; i < 60; i++)
            {
                seen.Add(slots.BeginWrite());
                slots.Publish();
                if (i % 2 == 0) slots.TryTake(out _);
            }
            Assert.AreEqual(MediaPipeResultSlots.Count, seen.Count);
        }
        [Test]
        public void ArraysAreSizedForTheModels()
        {
            MediaPipeResultSlot slot = new MediaPipeResultSlots().BeginWrite();
            Assert.AreEqual(MediaPipeSpace.PoseCount, slot.PoseWorld.Length);
            Assert.AreEqual(MediaPipeSpace.PoseCount, slot.PoseVisibility.Length);
            Assert.AreEqual(MediaPipeSpace.HandCount, slot.HandImage[1].Length);
            Assert.AreEqual((int)MediaPipeArkitBlendshape.Count, slot.Blendshapes.Length);
        }
    }
}
