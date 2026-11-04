using Basis.Shims;
using NUnit.Framework;

namespace Basis.Tests.Sync
{
    public sealed class BasisSharedRandomShimTests
    {
        [Test]
        public void SameSeedAndIndex_GiveTheSameNumber()
        {
            for (long index = -1000; index < 1000; index += 7)
            {
                Assert.AreEqual(BasisSharedRandomShim.Value(42, index), BasisSharedRandomShim.Value(42, index));
                Assert.AreEqual(BasisSharedRandomShim.RangeInt(42, index, 0, 52), BasisSharedRandomShim.RangeInt(42, index, 0, 52));
            }
        }

        [Test]
        public void NeighbouringSeeds_AreNotTheSameSequence()
        {
            int same = 0;
            for (long index = 0; index < 1000; index++)
            {
                if (BasisSharedRandomShim.RangeInt(1, index, 0, 1000) == BasisSharedRandomShim.RangeInt(2, index, 0, 1000)) same++;
            }
            Assert.Less(same, 10);
        }

        [Test]
        public void Value_StaysInZeroToOne_AndSpreadsOut()
        {
            int[] buckets = new int[10];
            for (long index = 0; index < 100000; index++)
            {
                float value = BasisSharedRandomShim.Value(7, index);
                Assert.GreaterOrEqual(value, 0f);
                Assert.Less(value, 1f);
                buckets[(int)(value * 10)]++;
            }
            foreach (int count in buckets) Assert.That(count, Is.InRange(9000, 11000));
        }

        [Test]
        public void RangeInt_StaysInBounds_IncludingExtremes()
        {
            for (long index = 0; index < 10000; index++)
            {
                int small = BasisSharedRandomShim.RangeInt(3, index, -2, 3);
                Assert.That(small, Is.InRange(-2, 2));
                int wide = BasisSharedRandomShim.RangeInt(3, index, int.MinValue, int.MaxValue);
                Assert.Less(wide, int.MaxValue);
            }
            Assert.AreEqual(5, BasisSharedRandomShim.RangeInt(3, 0, 5, 5));
            Assert.AreEqual(5, BasisSharedRandomShim.RangeInt(3, 0, 5, -5));
        }

        [Test]
        public void Range_MapsOntoMinMax()
        {
            for (long index = 0; index < 1000; index++)
            {
                float value = BasisSharedRandomShim.Range(9, index, -4f, 4f);
                Assert.That(value, Is.InRange(-4f, 4f));
            }
        }

        [Test]
        public void WindowIndex_BadPeriod_IsZero()
        {
            Assert.AreEqual(0, BasisSharedRandomShim.WindowIndex(0d));
            Assert.AreEqual(0, BasisSharedRandomShim.WindowIndex(-5d));
            Assert.AreEqual(0, BasisSharedRandomShim.WindowIndex(double.NaN));
            Assert.AreEqual(0, BasisSharedRandomShim.WindowIndex(double.PositiveInfinity));
        }
    }
}
