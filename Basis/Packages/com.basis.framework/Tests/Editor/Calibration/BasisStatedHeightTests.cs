using NUnit.Framework;

namespace Basis.Tests.Calibration
{
    /// <summary>
    /// The height the player types in is the most trusted number in the pipeline, so the parser has to
    /// take it however they think about their own height and never silently misread it. The dangerous
    /// failures are the quiet ones: reading "5'10" as five metres, or "178" as 178 metres, would poison
    /// every measurement that then gets validated against it.
    /// </summary>
    public class BasisStatedHeightTests
    {
        const float Eps = 0.005f;

        [TestCase("178", 1.78f)]
        [TestCase("178cm", 1.78f)]
        [TestCase("178 cm", 1.78f)]
        [TestCase("1.78", 1.78f)]
        [TestCase("1.78m", 1.78f)]
        [TestCase("1,78", 1.78f)]
        [TestCase("165", 1.65f)]
        [TestCase(" 190 ", 1.90f)]
        public void MetricIsUnderstood(string typed, float expected)
        {
            Assert.IsTrue(BasisStatedHeight.TryParse(typed, out float meters), typed);
            Assert.AreEqual(expected, meters, Eps, typed);
        }

        [TestCase("5'10", 1.778f)]
        [TestCase("5' 10\"", 1.778f)]
        [TestCase("5ft 10in", 1.778f)]
        [TestCase("6'", 1.829f)]
        [TestCase("5 10", 1.778f)]
        [TestCase("6'2", 1.880f)]
        public void ImperialIsUnderstood(string typed, float expected)
        {
            Assert.IsTrue(BasisStatedHeight.TryParse(typed, out float meters), typed);
            Assert.AreEqual(expected, meters, Eps, typed);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("tall")]
        [TestCase("0")]
        [TestCase("500")]
        [TestCase("12")]
        [TestCase("-178")]
        public void NonsenseIsRefusedRatherThanGuessed(string typed)
        {
            Assert.IsFalse(BasisStatedHeight.TryParse(typed, out float meters), typed);
            Assert.AreEqual(0f, meters, "a refused parse must not leave a value behind");
        }

        [Test]
        public void EveryAcceptedHeightIsAPlausibleHuman()
        {
            // The band is the last line of defence: whatever the parser accepts becomes the yardstick
            // every measured value is judged against.
            string[] inputs = { "178", "1.78", "5'10", "100", "240", "2.4", "1.0" };
            foreach (string input in inputs)
            {
                if (BasisStatedHeight.TryParse(input, out float meters))
                {
                    Assert.GreaterOrEqual(meters, BasisStatedHeight.MinMeters, input);
                    Assert.LessOrEqual(meters, BasisStatedHeight.MaxMeters, input);
                }
            }
        }

        [Test]
        public void TheEditableEchoRoundTripsBackThroughTheParser()
        {
            // The field echoes the parsed height back at the player; that echo has to survive being
            // read again, or editing an existing value would drift it.
            Assert.IsTrue(BasisStatedHeight.TryParse("178", out float original));
            string shown = BasisStatedHeight.FormatCompact(original);
            Assert.IsTrue(BasisStatedHeight.TryParse(shown, out float reparsed), shown);
            Assert.AreEqual(original, reparsed, Eps, shown);
        }

        [Test]
        public void TheDualUnitDisplayIsAlsoReadableBack()
        {
            // It carries both an apostrophe and a metric unit, so the parser has to decide which one
            // means it — getting this backwards reads 178 cm as 178 feet.
            Assert.IsTrue(BasisStatedHeight.TryParse("178", out float original));
            string shown = BasisStatedHeight.Format(original);
            Assert.IsTrue(BasisStatedHeight.TryParse(shown, out float reparsed), shown);
            Assert.AreEqual(original, reparsed, Eps, shown);
        }

        [Test]
        public void FormattingShowsBothSystems()
        {
            string shown = BasisStatedHeight.Format(1.778f);
            StringAssert.Contains("178 cm", shown);
            StringAssert.Contains("5'", shown);
        }

        [Test]
        public void UnsetHeightVetoesNothing()
        {
            // With no stated height every measurement has to pass through untouched, or a player who
            // never fills this in would find their readings silently rejected.
            Assert.IsTrue(BasisStatedHeight.IsPlausibleEye(1.60f));
            Assert.IsTrue(BasisStatedHeight.IsPlausibleEye(2.60f));
        }
    }
}
