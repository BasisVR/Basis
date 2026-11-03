using Basis.Scripts.Drivers;
using NUnit.Framework;

namespace Basis.Tests
{
    public class BasisJobWorkerLimitTests
    {
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(8, 8)]
        [TestCase(16, 16)]
        [TestCase(32, 32)]
        public void EveryCoreGetsAWorker(int cores, int expected)
        {
            Assert.AreEqual(expected, BasisJobWorkerLimit.TargetWorkers(cores));
        }

        [Test]
        public void NeverExceedsTheJobSystemMaximum()
        {
            Assert.LessOrEqual(BasisJobWorkerLimit.Limit(4), 4);
        }
    }
}
